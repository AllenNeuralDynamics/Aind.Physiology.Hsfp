using Bonsai;
using Bonsai.Design;
using AllenNeuralDynamics.HamamatsuCamera;
using AllenNeuralDynamics.HamamatsuCamera.API;
using AllenNeuralDynamics.HamamatsuCamera.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[Editor(typeof(HsfpCameraCaptureEditor), typeof(ComponentEditor))]
[Description("Hamamatsu camera. While it acquires, WriteHsfpCameraSettings can write everything it has applied.")]
public class HsfpCameraCapture : C13440
{
    [Description("Name of the camera, as it appears in the instrument metadata.")]
    public string DeviceName { get; set; }

    [Description("Region of the sensor that is cropped and recorded. If set, it replaces the configured regions.")]
    public AindPhysiologyHsfp.CameraRegion Region { get; set; }

    internal bool EditingSettings;

    public HsfpCameraCapture()
    {
        DeviceName = "Hamamatsu Orca Flash 4 v3";
    }

    public override IObservable<IFrameContainer> Generate()
    {
        // The settings editor subscribes to Generate for its live preview, which must not be read as the session's.
        if (EditingSettings) return base.Generate();

        // Property mappings are applied on subscription, so all properties are read lazily.
        return Observable.Defer(() =>
        {
            // The camera package logs a failed start and keeps running without a camera, then crashes on stop.
            // Check first, so the workflow stops with a clear error instead.
            var problem = HsfpDcam.FindCameraProblem();
            if (problem != null) return Observable.Throw<IFrameContainer>(new InvalidOperationException(problem));

            if (Region != null)
            {
                Regions = new List<RegionOfInterest>
                {
                    new RegionOfInterest { X = Region.X, Y = Region.Y, Width = Region.Width, Height = Region.Height }
                };
            }

            // The camera is open and configured once its first frame arrives; from then on its settings can be read.
            var opened = false;
            return base.Generate()
                .Do(frame =>
                {
                    if (opened) return;
                    opened = true;
                    HsfpCameraSettings.SetOpenCamera(this);
                })
                .Finally(() => HsfpCameraSettings.ClearOpenCamera(this));
        }).Publish().RefCount();
    }
}

// Delegates to the camera settings editor, flagging its preview subscription.
public class HsfpCameraCaptureEditor : WorkflowComponentEditor
{
    public override bool EditComponent(ITypeDescriptorContext context, object component, IServiceProvider provider, IWin32Window owner)
    {
        var camera = (HsfpCameraCapture)component;
        var editor = (WorkflowComponentEditor)TypeDescriptor.GetEditor(typeof(C13440), typeof(ComponentEditor));
        if (editor == null) throw new InvalidOperationException("Hamamatsu camera settings editor is unavailable.");
        camera.EditingSettings = true;
        try { return editor.EditComponent(context, camera, provider, owner); }
        finally { camera.EditingSettings = false; }
    }
}

/// <summary>Checks that DCAM-API finds a camera, before the camera package opens it.</summary>
static class HsfpDcam
{
    [DllImport("dcamapi")]
    static extern DCAMERR dcamapi_init(ref DCAMAPI_INIT param);

    [DllImport("dcamapi")]
    static extern DCAMERR dcamapi_uninit();

    /// <summary>
    /// Initializes DCAM-API, reads the number of cameras and releases it again. Returns why the camera cannot be used,
    /// or null if a camera was found. Call it only while no camera is open in this process: uninitializing DCAM-API
    /// closes every camera the process holds.
    /// </summary>
    public static string FindCameraProblem()
    {
        DCAMAPI_INIT param = new DCAMAPI_INIT(0);
        DCAMERR error;
        try
        {
            error = dcamapi_init(ref param);
        }
        catch (DllNotFoundException)
        {
            return "Hamamatsu camera: DCAM-API (dcamapi.dll) is not installed.";
        }
        if (error.failed())
        {
            return string.Format(
                "Hamamatsu camera: DCAM-API found no usable camera ({0}). Check that the camera is on and connected, " +
                "and that no other program (HCImage, another Bonsai) is using it.", error.text());
        }
        try
        {
            if (param.iDeviceCount < 1)
            {
                return "Hamamatsu camera: no camera found. Check that the camera is on and connected.";
            }
            return null;
        }
        finally
        {
            dcamapi_uninit();
        }
    }
}
