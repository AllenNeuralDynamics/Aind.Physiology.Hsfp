using Bonsai;
using Bonsai.Harp;
using AllenNeuralDynamics.Cuttlefish;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Xml;
using System.Xml.Serialization;

[Combinator]
[Description("Creates the messages that configure the cuTTLefish PWM outputs from the rig's channels and trigger timing: the camera trigger on channel 0 and each light source on its channel, phase-locked in integer microseconds.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateCuttlefishConfiguration
{
    public CreateCuttlefishConfiguration()
    {
        Interval = TimeSpan.FromMilliseconds(50);
    }

    [XmlIgnore]
    [Description("Time between consecutive messages.")]
    public TimeSpan Interval { get; set; }

    [Browsable(false)]
    [XmlElement("Interval")]
    public string IntervalXml
    {
        get { return XmlConvert.ToString(Interval); }
        set { Interval = XmlConvert.ToTimeSpan(value); }
    }

    public IObservable<HarpMessage> Process(IObservable<AindPhysiologyHsfp.AindPhysioHsfpRig> source)
    {
        return source.SelectMany(rig =>
        {
            var messages = CreateMessages(rig);
            return messages.ToObservable().Zip(Observable.Interval(Interval), (message, _) => message);
        });
    }

    /// <summary>
    /// Disables the PWM outputs, which may still run from a previous session, enables rising-edge events on all 8
    /// channels, then configures channel 0 (camera) and each channel with a
    /// light source, all derived from the rig's trigger timing by <see cref="HsfpTriggerSchedule"/>. Unused
    /// channels get no PWM settings.
    /// </summary>
    public static IList<HarpMessage> CreateMessages(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        var camera = HsfpTriggerSchedule.Camera(rig);
        var channels = HsfpChannels.Get(rig);
        const Pins AllPins = Pins.Pin0 | Pins.Pin1 | Pins.Pin2 | Pins.Pin3 | Pins.Pin4 | Pins.Pin5 | Pins.Pin6 | Pins.Pin7;

        var messages = new List<HarpMessage>();
        messages.Add(PwmState.FromPayload(MessageType.Write, EnableFlag.Disabled));
        messages.Add(EnableRisingEdgeEvents.FromPayload(MessageType.Write, AllPins));
        messages.Add(CreatePwmSettings(HsfpChannels.CameraChannel, camera));
        foreach (var channel in channels)
        {
            messages.Add(CreatePwmSettings(channel.Channel, HsfpTriggerSchedule.LightSource(rig, channel)));
        }
        return messages;
    }

    private static HarpMessage CreatePwmSettings(int pin, PwmSettingsPayload payload)
    {
        switch (pin)
        {
            case 0: return PwmSettings0.FromPayload(MessageType.Write, payload);
            case 1: return PwmSettings1.FromPayload(MessageType.Write, payload);
            case 2: return PwmSettings2.FromPayload(MessageType.Write, payload);
            case 3: return PwmSettings3.FromPayload(MessageType.Write, payload);
            case 4: return PwmSettings4.FromPayload(MessageType.Write, payload);
            case 5: return PwmSettings5.FromPayload(MessageType.Write, payload);
            case 6: return PwmSettings6.FromPayload(MessageType.Write, payload);
            case 7: return PwmSettings7.FromPayload(MessageType.Write, payload);
            default: throw new ArgumentOutOfRangeException("pin");
        }
    }
}
