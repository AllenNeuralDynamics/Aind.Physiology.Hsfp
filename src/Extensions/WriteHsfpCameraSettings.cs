using AllenNeuralDynamics.HamamatsuCamera;
using AllenNeuralDynamics.HamamatsuCamera.API;
using Bonsai;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;

/// <summary>The camera that is acquiring, and a readout of everything it has applied.</summary>
static class HsfpCameraSettings
{
    static readonly object Gate = new object();
    static HsfpCameraCapture openCamera;

    public static void SetOpenCamera(HsfpCameraCapture camera)
    {
        lock (Gate) openCamera = camera;
    }

    public static void ClearOpenCamera(HsfpCameraCapture camera)
    {
        lock (Gate)
        {
            if (ReferenceEquals(openCamera, camera)) openCamera = null;
        }
    }

    static readonly KeyValuePair<string, DCAMIDSTR>[] DeviceStrings =
    {
        new KeyValuePair<string, DCAMIDSTR>("vendor", DCAMIDSTR.VENDOR),
        new KeyValuePair<string, DCAMIDSTR>("model", DCAMIDSTR.MODEL),
        new KeyValuePair<string, DCAMIDSTR>("series_name", DCAMIDSTR.CAMERA_SERIESNAME),
        new KeyValuePair<string, DCAMIDSTR>("camera_id", DCAMIDSTR.CAMERAID),
        new KeyValuePair<string, DCAMIDSTR>("camera_version", DCAMIDSTR.CAMERAVERSION),
        new KeyValuePair<string, DCAMIDSTR>("driver_version", DCAMIDSTR.DRIVERVERSION),
        new KeyValuePair<string, DCAMIDSTR>("module_version", DCAMIDSTR.MODULEVERSION),
        new KeyValuePair<string, DCAMIDSTR>("dcam_api_version", DCAMIDSTR.DCAMAPIVERSION),
        new KeyValuePair<string, DCAMIDSTR>("bus", DCAMIDSTR.BUS),
    };

    /// <summary>
    /// Reads the device strings and every DCAM property of the acquiring camera, with the attributes of each
    /// (unit, range, step, default, access), and the software settings it applies.
    /// </summary>
    public static Dictionary<string, object> Read()
    {
        HsfpCameraCapture camera;
        lock (Gate) camera = openCamera;
        var props = camera != null && camera.CameraProps != null ? camera.CameraProps.ToList() : null;
        if (props == null || props.Count == 0)
        {
            throw new InvalidOperationException("The camera settings cannot be read: the camera is not acquiring.");
        }

        var device = new Dictionary<string, object>();
        var manager = new DCAM_MANAGER { m_hdcam = props[0].m_hdcam };
        foreach (var item in DeviceStrings)
        {
            string text = null;
            try { text = manager.dev_getstring(item.Value); }
            catch (Exception) { }
            device[item.Key] = string.IsNullOrEmpty(text) ? null : text;
        }

        var properties = new List<object>();
        var errors = new List<object>();
        foreach (var prop in props)
        {
            double value = 0;
            var id = prop.m_idProp.getidprop();
            var name = prop.getname();
            if (!prop.getvalue(ref value))
            {
                errors.Add(new Dictionary<string, object> { { "id", id }, { "name", name }, { "error", "DCAM getvalue failed" } });
                continue;
            }
            var attr = prop.m_attr;
            var attribute = new DCAMPROPATTRIBUTE(attr.attribute);
            properties.Add(new Dictionary<string, object>
            {
                { "id", id },
                { "name", name },
                { "value", value },
                { "value_text", attribute.has_attr(DCAMPROPATTRIBUTE.HASVALUETEXT) ? prop.getvaluetext(value) : null },
                { "unit_code", attr.iUnit },
                { "group", attr.iGroup },
                { "min", attribute.has_attr(DCAMPROPATTRIBUTE.HASRANGE) ? (object)attr.valuemin : null },
                { "max", attribute.has_attr(DCAMPROPATTRIBUTE.HASRANGE) ? (object)attr.valuemax : null },
                { "step", attribute.has_attr(DCAMPROPATTRIBUTE.HASSTEP) ? (object)attr.valuestep : null },
                { "default", attribute.has_attr(DCAMPROPATTRIBUTE.HASDEFAULT) ? (object)attr.valuedefault : null },
                { "writable", attribute.has_attr(DCAMPROPATTRIBUTE.WRITABLE) },
                { "readable", attribute.has_attr(DCAMPROPATTRIBUTE.READABLE) },
                { "volatile", attribute.has_attr(DCAMPROPATTRIBUTE.VOLATILE) },
                { "attribute", attr.attribute },
                { "attribute2", attr.attribute2 },
            });
        }

        return new Dictionary<string, object>
        {
            { "source", "dcam_applied_readback" },
            { "device_name", camera.DeviceName },
            { "captured_at", DateTimeOffset.UtcNow.ToString("o") },
            { "camera_assembly", typeof(C13440).Assembly.FullName },
            { "device", device },
            { "properties", properties },
            { "readback_errors", errors },
            { "settings_path", camera.SettingsPath },
            { "lookup_table_path", camera.LookupTablePath },
            { "lookup_table_points", camera.PointsOfInterest },
            { "software_regions", camera.Regions },
            { "crop_mode", camera.CropMode.ToString() },
        };
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Sink)]
[Description("For each path received, reads everything the acquiring camera has applied (its device strings, every DCAM property with its attributes, and its software settings) and writes it to a new JSON file at that path. Emits the path. Raises an error if the camera is not acquiring.")]
public class WriteHsfpCameraSettings
{
    public IObservable<string> Process(IObservable<string> source)
    {
        return source.Do(path =>
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("The path of the camera settings file is empty.");
            var settings = HsfpCameraSettings.Read();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(JsonConvert.SerializeObject(settings, Formatting.Indented));
                writer.Flush();
                file.Flush(true);
            }
        });
    }
}
