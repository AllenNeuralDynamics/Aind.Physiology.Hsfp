using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Xml.Serialization;
using AllenNeuralDynamics.Cuttlefish;
using AllenNeuralDynamics.HamamatsuCamera.Models;
using Bonsai;
using OpenCV.Net;

/// <summary>A camera frame, tagged with the acquisition channel (laser) it was acquired under.</summary>
public class HsfpFrame
{
    public HsfpFrame(IplImage image, int channel, long frameCounter, double timestamp, double seconds)
    {
        Image = image;
        Channel = channel;
        FrameCounter = frameCounter;
        Timestamp = timestamp;
        Seconds = seconds;
    }

    /// <summary>Raw camera image.</summary>
    public IplImage Image { get; private set; }

    /// <summary>Acquisition channel: the index of the laser in the rig.</summary>
    public int Channel { get; private set; }

    /// <summary>Frame counter of the camera.</summary>
    public long FrameCounter { get; private set; }

    /// <summary>Timestamp of the camera.</summary>
    public double Timestamp { get; private set; }

    /// <summary>Harp time (s) of the laser trigger that the frame was acquired under.</summary>
    public double Seconds { get; private set; }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Tags each camera frame with its acquisition channel, from the cuTTLefish rising-edge event paired with it. Laser N is on pin N + 1.")]
public class ToHsfpFrame
{
    public IObservable<HsfpFrame> Process(IObservable<Tuple<Frame, Tuple<Pins, double>>> source)
    {
        return source.Select(value =>
        {
            var frame = value.Item1;
            var pins = (int)value.Item2.Item1 & 0xFE; // pin 0 triggers the camera
            if (pins == 0) throw new InvalidOperationException("Camera frame paired with an event that has no laser pin.");
            var channel = 0;
            while ((pins & (1 << (channel + 1))) == 0) channel++;
            return new HsfpFrame(frame.Image, channel, (long)frame.FrameCounter, frame.Timestamp, value.Item2.Item2);
        });
    }
}

/// <summary>Mean fluorescence of each ROI in one frame.</summary>
public class HsfpActivity
{
    public HsfpActivity(int channel, double seconds, int[] fiberNumbers, double[] values)
    {
        Channel = channel;
        Seconds = seconds;
        FiberNumbers = fiberNumbers;
        Values = values;
    }

    public int Channel { get; private set; }

    public double Seconds { get; private set; }

    /// <summary>Fiber number of each value.</summary>
    public int[] FiberNumbers { get; private set; }

    /// <summary>Mean pixel value inside each fiber's region, for this frame's channel.</summary>
    public double[] Values { get; private set; }
}

/// <summary>Helpers to read ROI regions from images.</summary>
static class HsfpRoi
{
    /// <summary>Region of a fiber for a channel, clipped to the image. Returns false if empty.</summary>
    public static bool TryGetRect(AindPhysiologyHsfp.FiberRoi roi, int channel, Size imageSize, out Rect rect)
    {
        rect = default(Rect);
        if (roi == null || roi.Regions == null || roi.Regions.Count == 0) return false;
        var region = roi.Regions[Math.Min(channel, roi.Regions.Count - 1)];
        var x0 = Math.Max(0, Math.Min(region.X, imageSize.Width));
        var y0 = Math.Max(0, Math.Min(region.Y, imageSize.Height));
        var x1 = Math.Max(x0, Math.Min(region.X + region.Width, imageSize.Width));
        var y1 = Math.Max(y0, Math.Min(region.Y + region.Height, imageSize.Height));
        rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        return rect.Width > 0 && rect.Height > 0;
    }

    public static double Mean(IplImage image, Rect rect)
    {
        using (var sub = image.GetSubRect(rect))
        {
            return CV.Avg(sub).Val0;
        }
    }

    /// <summary>Mean of each column of the region, i.e. the emission spectrum along the sensor x axis.</summary>
    public static double[] ColumnMeans(IplImage image, Rect rect)
    {
        using (var sub = image.GetSubRect(rect))
        using (var reduced = new Mat(1, rect.Width, Depth.F64, 1))
        {
            CV.Reduce(sub, reduced, 0, ReduceOperation.Avg);
            var values = new double[rect.Width];
            for (int i = 0; i < values.Length; i++) values[i] = reduced.GetReal(i);
            return values;
        }
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Computes the mean fluorescence inside each enabled ROI of each frame, using the region of the frame's channel.")]
public class ComputeRoiActivity
{
    [XmlIgnore]
    [Description("The regions of interest. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.RoiSettings RoiSettings { get; set; }

    public IObservable<HsfpActivity> Process(IObservable<HsfpFrame> source)
    {
        return source.Select(frame =>
        {
            var settings = RoiSettings;
            var rois = settings == null || settings.Rois == null
                ? new List<AindPhysiologyHsfp.FiberRoi>()
                : settings.Rois.Where(roi => roi.Enabled).ToList();
            var fibers = new int[rois.Count];
            var values = new double[rois.Count];
            for (int i = 0; i < rois.Count; i++)
            {
                Rect rect;
                fibers[i] = rois[i].FiberNumber;
                values[i] = HsfpRoi.TryGetRect(rois[i], frame.Channel, frame.Image.Size, out rect)
                    ? HsfpRoi.Mean(frame.Image, rect)
                    : double.NaN;
            }
            return new HsfpActivity(frame.Channel, frame.Seconds, fibers, values);
        });
    }
}

/// <summary>Running health statistics of the acquisition.</summary>
public class HsfpHealth
{
    public double FrameRate { get; set; }

    public long Frames { get; set; }

    /// <summary>Frames missing from the camera frame counter.</summary>
    public long DroppedFrames { get; set; }

    /// <summary>Frames whose channel does not follow the previous one, i.e. frames assigned to the wrong laser.</summary>
    public long SequenceErrors { get; set; }

    public long[] FramesPerChannel { get; set; }

    public double[] ChannelRates { get; set; }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Computes running health statistics of the acquisition: frame rate, dropped frames and channel sequence errors.")]
public class ComputeAcquisitionHealth
{
    public ComputeAcquisitionHealth()
    {
        Window = TimeSpan.FromSeconds(1);
    }

    [Description("Number of acquisition channels (lasers). Set via PropertyMapping from the rig.")]
    public int ChannelCount { get; set; }

    [XmlIgnore]
    [Description("Time window over which rates are computed.")]
    public TimeSpan Window { get; set; }

    [Browsable(false)]
    [XmlElement("Window")]
    public string WindowXml
    {
        get { return System.Xml.XmlConvert.ToString(Window); }
        set { Window = System.Xml.XmlConvert.ToTimeSpan(value); }
    }

    public IObservable<HsfpHealth> Process(IObservable<HsfpFrame> source)
    {
        return Observable.Defer(() =>
        {
            long frames = 0, dropped = 0, sequenceErrors = 0;
            long? lastCounter = null;
            int? lastChannel = null;
            var perChannel = new long[0];
            var recent = new Queue<Tuple<double, int>>();
            return source.Select(frame =>
            {
                var channelCount = Math.Max(ChannelCount, frame.Channel + 1);
                if (perChannel.Length < channelCount) Array.Resize(ref perChannel, channelCount);
                frames++;
                perChannel[frame.Channel]++;
                if (lastCounter.HasValue && frame.FrameCounter > lastCounter.Value + 1)
                {
                    dropped += frame.FrameCounter - lastCounter.Value - 1;
                }
                if (lastChannel.HasValue && frame.Channel != (lastChannel.Value + 1) % channelCount)
                {
                    sequenceErrors++;
                }
                lastCounter = frame.FrameCounter;
                lastChannel = frame.Channel;

                recent.Enqueue(Tuple.Create(frame.Seconds, frame.Channel));
                var window = Window.TotalSeconds;
                while (recent.Count > 0 && recent.Peek().Item1 < frame.Seconds - window) recent.Dequeue();
                var span = recent.Count > 1 ? frame.Seconds - recent.Peek().Item1 : 0;
                var rates = new double[channelCount];
                if (span > 0)
                {
                    foreach (var item in recent) rates[item.Item2] += 1;
                    for (int i = 0; i < rates.Length; i++) rates[i] = (rates[i] - (i == recent.Peek().Item2 ? 1 : 0)) / span;
                }
                return new HsfpHealth
                {
                    FrameRate = span > 0 ? (recent.Count - 1) / span : 0,
                    Frames = frames,
                    DroppedFrames = dropped,
                    SequenceErrors = sequenceErrors,
                    FramesPerChannel = (long[])perChannel.Clone(),
                    ChannelRates = rates,
                };
            });
        });
    }
}
