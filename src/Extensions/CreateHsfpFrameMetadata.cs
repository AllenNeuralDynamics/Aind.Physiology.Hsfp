using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;

/// <summary>One row of frame_metadata.csv: the timing and channel of one frame.</summary>
public class HsfpFrameMetadata
{
    public HsfpFrameMetadata(long frameCounter, double timestamp, double seconds, int channel, long channelFrameCounter)
    {
        FrameCounter = frameCounter;
        Timestamp = timestamp;
        Seconds = seconds;
        Channel = channel;
        ChannelFrameCounter = channelFrameCounter;
    }

    /// <summary>Frame counter of the camera.</summary>
    public long FrameCounter { get; private set; }

    /// <summary>Timestamp of the camera.</summary>
    public double Timestamp { get; private set; }

    /// <summary>Harp time (s) of the light source trigger that the frame was acquired under.</summary>
    public double Seconds { get; private set; }

    /// <summary>Acquisition channel: the cuTTLefish channel (1-7) of the light source.</summary>
    public int Channel { get; private set; }

    /// <summary>
    /// Index of the frame among the frames of its channel, from 0 at the first frame logged: its index in the TIFF
    /// stack of the channel.
    /// </summary>
    public long ChannelFrameCounter { get; private set; }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Creates the frame_metadata.csv row of each frame, numbering the frames of each channel from 0 (ChannelFrameCounter), from the first frame of each subscription, i.e. of each logged epoch.")]
public class CreateHsfpFrameMetadata
{
    public IObservable<HsfpFrameMetadata> Process(IObservable<HsfpFrame> source)
    {
        return Observable.Defer(() =>
        {
            var counters = new Dictionary<int, long>();
            return source.Select(frame =>
            {
                long count;
                counters.TryGetValue(frame.Channel, out count);
                counters[frame.Channel] = count + 1;
                return new HsfpFrameMetadata(frame.FrameCounter, frame.Timestamp, frame.Seconds, frame.Channel, count);
            });
        });
    }
}
