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
[Description("Creates the messages that configure the cuTTLefish PWM outputs from the rig: the camera trigger on pin 0 and laser N on pin N + 1.")]
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

    private static IList<HarpMessage> CreateMessages(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        if (rig.Lasers.Count < 1 || rig.Lasers.Count > 7)
        {
            throw new ArgumentException("The rig must define between 1 and 7 lasers.");
        }

        var laserPins = Pins.None;
        for (int i = 0; i < rig.Lasers.Count; i++)
        {
            laserPins |= (Pins)(1 << (i + 1));
        }

        var messages = new List<HarpMessage>();
        messages.Add(EnableRisingEdgeEvents.FromPayload(MessageType.Write, Pins.Pin0 | laserPins));
        messages.Add(CreatePwmSettings(0, rig.Camera.Trigger));
        for (int i = 0; i < rig.Lasers.Count; i++)
        {
            messages.Add(CreatePwmSettings(i + 1, rig.Lasers[i].Pwm));
        }
        return messages;
    }

    private static HarpMessage CreatePwmSettings(int pin, AindPhysiologyHsfp.PwmSettings settings)
    {
        var payload = new PwmSettingsPayload(
            (uint)settings.OffsetUs,
            (uint)settings.OnDurationUs,
            (uint)settings.OffDurationUs,
            (uint)settings.Cycles,
            settings.Invert);
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
