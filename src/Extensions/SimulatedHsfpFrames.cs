using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using OpenCV.Net;

[Description("Generates synthetic HSFP frames for testing the UI without hardware: one bright band per fiber, whose spectrum shifts with the channel and whose brightness fluctuates over time.")]
public class SimulatedHsfpFrames : Source<HsfpFrame>
{
    public SimulatedHsfpFrames()
    {
        ChannelCount = 5;
        FiberCount = 2;
        Width = 1976;
        Height = 236;
        FrameRate = 150;
    }

    [Description("Number of acquisition channels (lasers).")]
    public int ChannelCount { get; set; }

    [Description("Number of fibers imaged on the sensor.")]
    public int FiberCount { get; set; }

    [Description("Image width (px).")]
    public int Width { get; set; }

    [Description("Image height (px).")]
    public int Height { get; set; }

    [Description("Camera frame rate (Hz).")]
    public double FrameRate { get; set; }

    public override IObservable<HsfpFrame> Generate()
    {
        return Observable.Defer(() =>
        {
            var random = new Random(0);
            var channelCount = Math.Max(1, ChannelCount);
            var fiberCount = Math.Max(1, FiberCount);
            var width = Math.Max(16, Width);
            var height = Math.Max(16, Height);
            var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, FrameRate));
            return Observable.Interval(period).Select(counter =>
            {
                var channel = (int)(counter % channelCount);
                var seconds = counter * period.TotalSeconds;
                var image = new IplImage(new Size(width, height), IplDepth.U16, 1);
                image.SetZero();
                var row = new ushort[width];
                for (int f = 0; f < fiberCount; f++)
                {
                    var centerY = height * (f + 0.5) / fiberCount;
                    var halfHeight = height * 0.25 / fiberCount;
                    var peakX = width * (0.15 + 0.7 * (channel + 0.5) / channelCount);
                    var gain = 1 + 0.3 * Math.Sin(seconds * (0.8 + 0.3 * f) + channel) + 0.05 * random.NextDouble();
                    for (int x = 0; x < width; x++)
                    {
                        var dx = (x - peakX) / (width * 0.06);
                        row[x] = (ushort)Math.Min(65535, 200 + 20000 * gain * Math.Exp(-dx * dx));
                    }
                    for (int y = (int)(centerY - halfHeight); y < (int)(centerY + halfHeight); y++)
                    {
                        if (y < 0 || y >= height) continue;
                        var offset = image.ImageData + y * image.WidthStep;
                        var shorts = new short[width];
                        Buffer.BlockCopy(row, 0, shorts, 0, width * 2);
                        System.Runtime.InteropServices.Marshal.Copy(shorts, 0, offset, width);
                    }
                }
                return new HsfpFrame(image, channel, counter, seconds, seconds);
            });
        });
    }
}
