using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;

/// <summary>A cuTTLefish channel with a light source, i.e. one acquisition channel.</summary>
public class HsfpChannel
{
    public HsfpChannel(int channel, int index, AindPhysiologyHsfp.LightSource lightSource)
    {
        Channel = channel;
        Index = index;
        LightSource = lightSource;
    }

    /// <summary>cuTTLefish channel (1-7) that triggers the light source. Also the acquisition channel number.</summary>
    public int Channel { get; private set; }

    /// <summary>Position of the light source in the firing order (0 to L - 1), skipping unused channels.</summary>
    public int Index { get; private set; }

    public AindPhysiologyHsfp.LightSource LightSource { get; private set; }
}

/// <summary>
/// The light sources of the rig, by cuTTLefish channel. Mirrors <c>CuttlefishChannels.light_sources</c> in the
/// Python package. Channel 0 is reserved for the camera; channels 1-7 each hold a light source or nothing.
/// </summary>
public static class HsfpChannels
{
    /// <summary>cuTTLefish channel reserved for the camera trigger.</summary>
    public const int CameraChannel = 0;

    /// <summary>Number of cuTTLefish channels that can trigger a light source (1-7).</summary>
    public const int LightSourceChannelCount = 7;

    /// <summary>Used channels, in firing order. Empty if the rig is not set.</summary>
    public static IList<HsfpChannel> Get(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        var result = new List<HsfpChannel>();
        var channels = rig != null ? rig.Channels : null;
        if (channels == null) return result;
        var sources = new[]
        {
            channels.Channel1, channels.Channel2, channels.Channel3, channels.Channel4,
            channels.Channel5, channels.Channel6, channels.Channel7,
        };
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] != null) result.Add(new HsfpChannel(i + 1, result.Count, sources[i]));
        }
        return result;
    }

    /// <summary>Firing-order index of a cuTTLefish channel, or -1 if the channel has no light source.</summary>
    public static int IndexOf(AindPhysiologyHsfp.AindPhysioHsfpRig rig, int channel)
    {
        foreach (var item in Get(rig))
        {
            if (item.Channel == channel) return item.Index;
        }
        return -1;
    }

    /// <summary>The light source on a cuTTLefish channel, or null.</summary>
    public static AindPhysiologyHsfp.LightSource Find(AindPhysiologyHsfp.AindPhysioHsfpRig rig, int channel)
    {
        foreach (var item in Get(rig))
        {
            if (item.Channel == channel) return item.LightSource;
        }
        return null;
    }

    /// <summary>Short label of a channel: its number and the wavelength, or else the name, of its light source.</summary>
    public static string Label(AindPhysiologyHsfp.AindPhysioHsfpRig rig, int channel)
    {
        var source = Find(rig, channel);
        if (source == null) return "Ch " + channel;
        return source.Wavelength.HasValue
            ? string.Format("Ch {0} - {1} nm", channel, source.Wavelength.Value)
            : string.Format("Ch {0} - {1}", channel, source.Name);
    }
}

namespace AindPhysiologyHsfp
{
    partial class AindPhysioHsfpRig
    {
        /// <summary>Number of cuTTLefish channels with a light source, i.e. the number of acquisition channels.</summary>
        [Browsable(false)]
        [XmlIgnore]
        [Newtonsoft.Json.JsonIgnore]
        public int LightSourceCount
        {
            get { return HsfpChannels.Get(this).Count; }
        }
    }
}
