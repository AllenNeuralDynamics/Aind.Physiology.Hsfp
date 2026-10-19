using Bonsai;
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

[Description("Uses a launcher-reserved segment root; never generates an independent acquisition name.")]
public class HsfpRootPath : Source<string>
{
    public string AcquisitionRoot { get; set; }
    public string SegmentId { get; set; }
    public string Subject { get; set; }

    public override IObservable<string> Generate()
    {
        return Observable.Defer(() => {
            if (string.IsNullOrWhiteSpace(AcquisitionRoot) || string.IsNullOrWhiteSpace(SegmentId))
                throw new InvalidOperationException("Launch through Aind.Experiment.Hsfp to reserve acquisition and segment paths.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(SegmentId, "^segment_[0-9]+$"))
                throw new InvalidOperationException("Invalid segment identifier.");
            var root = Path.GetFullPath(AcquisitionRoot);
            var segment = Path.Combine(root, "fib", "segments", SegmentId);
            if (!File.Exists(Path.Combine(segment, "segment_input.json")))
                throw new InvalidOperationException("Segment sources must be saved before starting Bonsai.");
            if (Directory.Exists(Path.Combine(segment, "fib")))
                throw new IOException("Segment already contains recording data. Reserve a new segment; never overwrite.");
            Directory.CreateDirectory(Path.Combine(segment, "fib"));
            HsfpRecordingContext.Root = root;
            HsfpRecordingContext.SegmentId = SegmentId;
            HsfpRecordingContext.SegmentPath = segment;
            HsfpRecordingContext.Started = HsfpRecordingContext.Stopped = false;
            return Observable.Return(segment);
        });
    }
}

[Description("Hamamatsu camera with an applied DCAM snapshot on its first emitted frame.")]
public class HsfpCamera : C13440
{
    public HsfpCamera()
    {
        DeviceName = "Hamamatsu Orca Flash 4 v3";
    }

    public string DeviceName { get; set; }

    public override IObservable<IFrameContainer> Generate()
    {
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
