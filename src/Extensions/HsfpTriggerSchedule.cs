using AllenNeuralDynamics.Cuttlefish;
using System;
using System.Collections.Generic;

/// <summary>
/// Derives the cuTTLefish PWM outputs from the rig, in integer microseconds. Mirrors
/// <c>TriggerTiming.camera_schedule</c> and <c>TriggerTiming.light_source_schedule</c> in the Python package.
/// </summary>
/// <remarks>
/// The camera is triggered once per frame on channel 0. The light sources fire in turn, one per frame, in channel
/// order, skipping unused channels: with L light sources, the k-th one fires in frames k, k + L, k + 2L... so its
/// period is exactly L camera periods. Every value is an integer number of microseconds, and the firmware schedules
/// edges on absolute deadlines, so the light sources stay phase-locked to the camera for the whole session.
/// </remarks>
public static class HsfpTriggerSchedule
{
    /// <summary>PWM output of the camera trigger (channel 0).</summary>
    public static PwmSettingsPayload Camera(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        var timing = Validate(rig);
        return Create(0, timing.CameraTriggerDurationUs, timing.FramePeriodUs);
    }

    /// <summary>PWM output of the light source on a used cuTTLefish channel.</summary>
    public static PwmSettingsPayload LightSource(AindPhysiologyHsfp.AindPhysioHsfpRig rig, HsfpChannel channel)
    {
        var timing = Validate(rig);
        var count = HsfpChannels.Get(rig).Count;
        if (channel == null || channel.Index < 0 || channel.Index >= count)
        {
            throw new ArgumentOutOfRangeException("channel", "The channel has no light source in the rig.");
        }
        var frame = (long)timing.FramePeriodUs;
        return Create(channel.Index * frame + timing.LightSourceDelayUs, timing.LightSourceOnDurationUs, count * frame);
    }

    /// <summary>Camera frame rate (Hz), or 0 if the rig does not define the timing.</summary>
    public static double FrameRate(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        if (rig == null || rig.Timing == null || rig.Timing.FramePeriodUs <= 0) return 0;
        return 1e6 / rig.Timing.FramePeriodUs;
    }

    static AindPhysiologyHsfp.TriggerTiming Validate(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        if (rig == null) throw new ArgumentNullException("rig");
        if (HsfpChannels.Get(rig).Count == 0)
        {
            throw new ArgumentException("At least one cuTTLefish channel must have a light source.", "rig");
        }
        var timing = rig.Timing;
        if (timing == null) throw new ArgumentException("The rig does not define the trigger timing.", "rig");
        if (timing.FramePeriodUs <= 0 || timing.CameraTriggerDurationUs <= 0 || timing.LightSourceOnDurationUs <= 0 || timing.LightSourceDelayUs < 0)
        {
            throw new ArgumentException("The trigger timing must be positive.", "rig");
        }
        if (timing.CameraTriggerDurationUs >= timing.FramePeriodUs)
        {
            throw new ArgumentException("The camera trigger must be shorter than the frame period.", "rig");
        }
        if ((long)timing.LightSourceDelayUs + timing.LightSourceOnDurationUs > timing.FramePeriodUs)
        {
            throw new ArgumentException("The light source pulse must end within its frame, or it would overlap the next light source.", "rig");
        }
        return timing;
    }

    static PwmSettingsPayload Create(long offsetUs, long onDurationUs, long periodUs)
    {
        if (offsetUs > uint.MaxValue || periodUs > uint.MaxValue)
        {
            throw new ArgumentException("The trigger timing exceeds the range of the cuTTLefish registers.");
        }
        return new PwmSettingsPayload((uint)offsetUs, (uint)onDurationUs, (uint)(periodUs - onDurationUs), 0, false);
    }
}
