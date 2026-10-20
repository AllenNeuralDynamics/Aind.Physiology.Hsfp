using Bonsai;
using Bonsai.Design;
using AllenNeuralDynamics.HamamatsuCamera;
using AllenNeuralDynamics.HamamatsuCamera.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Windows.Forms;

// Recording context is process-local. A launcher run reserves exactly one segment.
internal static class HsfpRecordingContext
{
    internal static string Root;
    internal static string SegmentId;
    internal static string SegmentPath;
    internal static bool Started;
    internal static bool Stopped;
    private static readonly object Gate = new object();

    internal static void Journal(string kind, DateTimeOffset? observedAt = null)
    {
        lock (Gate)
        {
            if (Root == null) throw new InvalidOperationException("HSFP root is not initialized.");
            if (kind == "recording_start" && Started) return;
            if (kind == "recording_stop" && (!Started || Stopped)) return;
            var record = new {
                segment_id = SegmentId, @event = kind,
                timestamp = (observedAt ?? DateTimeOffset.UtcNow).ToString("o"), source = "bonsai",
                details = new { boundary = kind == "recording_start" ? "first camera frame accepted" : "recording pipeline completed" }
            };
            string path = Path.Combine(Root, "fib", "recording_segments.jsonl");
            using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record) + "\n");
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (kind == "recording_start") Started = true;
            if (kind == "recording_stop") Stopped = true;
        }
    }
}

[Description("Uses the launcher-reserved segment, or a new standalone recording when both root properties are empty.")]
public class HsfpRootPath : Source<string>
{
    public string AcquisitionRoot { get; set; }
    public string SegmentId { get; set; }
    public string Subject { get; set; }

    public override IObservable<string> Generate()
    {
        return Observable.Defer(() => {
            if (string.IsNullOrWhiteSpace(AcquisitionRoot) || string.IsNullOrWhiteSpace(SegmentId))
            {
                if (!string.IsNullOrWhiteSpace(AcquisitionRoot) || !string.IsNullOrWhiteSpace(SegmentId))
                    throw new InvalidOperationException("Supply both AcquisitionRoot and SegmentId, or leave both empty for standalone recording.");
                var parent = Environment.GetEnvironmentVariable("HSFP_MANUAL_OUTPUT_DIRECTORY");
                if (string.IsNullOrWhiteSpace(parent)) parent = @"C:\output_data";
                if (string.IsNullOrWhiteSpace(Subject) || Subject.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Subject.Contains("_") || Subject.EndsWith(".") || Subject.EndsWith(" "))
                    throw new InvalidOperationException("Set Subject to a nonempty filename-safe subject ID without underscores before standalone recording.");
                var name = Subject + "_" + DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N");
                var manualRoot = Path.GetFullPath(Path.Combine(parent, name));
                var manualSegment = Path.Combine(manualRoot, "fib", "segment_0001");
                Directory.CreateDirectory(manualSegment);
                // This is a hardware recording, not a launcher/animal metadata contract.
                using (var file = new FileStream(Path.Combine(manualSegment, "segment_input.json"), FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(file))
                    writer.Write(JsonConvert.SerializeObject(new { recording_mode = "standalone", subject_label = Subject }, Formatting.Indented));
                return Initialize(manualRoot, "segment_0001", manualSegment);
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(SegmentId, "^segment_[0-9]+$"))
                throw new InvalidOperationException("Invalid segment identifier.");
            var root = Path.GetFullPath(AcquisitionRoot);
            var segment = Path.Combine(root, "fib", SegmentId);
            if (!File.Exists(Path.Combine(segment, "segment_input.json")))
                throw new InvalidOperationException("Segment sources must be saved before starting Bonsai.");
            if (Directory.Exists(Path.Combine(segment, "fib")))
                throw new IOException("Segment already contains recording data. Reserve a new segment; never overwrite.");
            return Initialize(root, SegmentId, segment);
        });
    }

    private static IObservable<string> Initialize(string root, string segmentId, string segment)
    {
        Directory.CreateDirectory(Path.Combine(segment, "fib"));
        HsfpRecordingContext.Root = root;
        HsfpRecordingContext.SegmentId = segmentId;
        HsfpRecordingContext.SegmentPath = segment;
        HsfpRecordingContext.Started = HsfpRecordingContext.Stopped = false;
        Console.WriteLine("HSFP recording directory: " + root);
        return Observable.Return(segment);
    }
}

[Editor(typeof(HsfpCameraEditor), typeof(ComponentEditor))]
[Description("Hamamatsu camera with an applied DCAM snapshot on its first recorded frame; settings preview records no metadata.")]
public class HsfpCamera : C13440
{
    public HsfpCamera()
    {
        DeviceName = "Hamamatsu Orca Flash 4 v3";
    }

    public string DeviceName { get; set; }

    internal bool EditingSettings;

    public override IObservable<IFrameContainer> Generate()
    {
        // The inherited settings editor subscribes to Generate for live preview.
        // Never treat that preview as a recorded segment, even after a prior run.
        if (EditingSettings) return base.Generate();
        return Observable.Create<IFrameContainer>(observer => {
            bool captured = false;
            var subscription = base.Generate().Do(frame => {
                if (captured) return;
                if (HsfpRecordingContext.SegmentPath == null)
                    throw new InvalidOperationException("Recording root must initialize before camera output.");
                var observedAt = DateTimeOffset.UtcNow;
                var properties = new List<object>();
                var errors = new List<object>();
                foreach (var prop in CameraProps)
                {
                    double value = 0;
                    if (!prop.getvalue(ref value)) {
                        errors.Add(new { id = prop.m_idProp.getidprop(), name = prop.getname(), error = "DCAM getvalue failed" });
                        continue;
                    }
                    properties.Add(new { id = prop.m_idProp.getidprop(), name = prop.getname(),
                        value = value, value_text = prop.getvaluetext(value), unit_code = prop.m_attr.iUnit });
                }
                var snapshot = new { source = "dcam_applied_readback", device_name = DeviceName,
                    captured_at = DateTimeOffset.UtcNow.ToString("o"),
                    camera_assembly = typeof(C13440).Assembly.FullName,
                    bonsai_core_assembly = typeof(Source<>).Assembly.FullName,
                    dotnet_runtime = Environment.Version.ToString(),
                    properties = properties, readback_errors = errors,
                    software_regions = Regions, crop_mode = CropMode.ToString(), lookup_table_path = LookupTablePath };
                var path = Path.Combine(HsfpRecordingContext.SegmentPath, "camera_applied.json");
                // Exclusive source capture; a repeat run must use a different segment.
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(file))
                {
                    writer.Write(JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                    writer.Flush(); file.Flush(true);
                }
                HsfpRecordingContext.Journal("recording_start", observedAt);
                captured = true;
            }).Subscribe(observer.OnNext, observer.OnError, observer.OnCompleted);
            return subscription;
        }).Publish().RefCount();
    }
}

[Combinator]
[Description("Writes a normal stop only when the recording pipeline completes; errors/disposal do not fabricate a stop.")]
public class HsfpRecordingEnd
{
    public IObservable<T> Process<T>(IObservable<T> source)
    {
        return source.Do(_ => { }, () => HsfpRecordingContext.Journal("recording_stop"));
    }
}

// Delegate to the pinned camera editor, marking its preview subscription explicitly.
public class HsfpCameraEditor : WorkflowComponentEditor
{
    public override bool EditComponent(ITypeDescriptorContext context, object component, IServiceProvider provider, IWin32Window owner)
    {
        var camera = (HsfpCamera)component;
        var editor = (WorkflowComponentEditor)TypeDescriptor.GetEditor(typeof(C13440), typeof(ComponentEditor));
        if (editor == null) throw new InvalidOperationException("Hamamatsu camera settings editor is unavailable.");
        camera.EditingSettings = true;
        try { return editor.EditComponent(context, camera, provider, owner); }
        finally { camera.EditingSettings = false; }
    }
}
