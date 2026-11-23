using System;
using System.ComponentModel;
using System.Numerics;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Hexa.NET.ImGui;

/// <summary>Keeps the latest value of a stream, for a visualizer to draw on its own thread.</summary>
class Latest<T> where T : class
{
    volatile T value;

    public T Value { get { return value; } }

    public void Set(T newValue)
    {
        value = newValue;
    }

    public IDisposable Track(IObservable<T> source, Action<Exception> onError)
    {
        return source.Subscribe(v => value = v, onError);
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the acquisition health tiles: camera rate, dropped frames, channel sequence errors and frames received. Source1 = Frame subject; Source2 = HsfpHealth.")]
public class HsfpHealthVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    [XmlIgnore]
    [Description("The rig, used for the expected camera rate. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    [Description("Relative tolerance of the camera rate before it is flagged.")]
    public double RateTolerance { get; set; }

    public HsfpHealthVisualizer()
    {
        RateTolerance = 0.02;
    }

    public IObservable<TTickSource> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpHealth> health)
    {
        return Observable.Create<TTickSource>(observer =>
        {
            var latest = new Latest<HsfpHealth>();
            var healthSub = latest.Track(health, observer.OnError);
            var frameSub = frames.Subscribe(tick =>
            {
                HsfpStyle.Apply();
                if (Visible) Draw(latest.Value);
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new System.Reactive.Disposables.CompositeDisposable(healthSub, frameSub);
        });
    }

    void Draw(HsfpHealth health)
    {
        HsfpUi.SectionHeader("Health");
        var expected = ExpectedFrameRate();
        var rate = health != null ? health.FrameRate : 0;
        var rateOk = health != null && expected > 0 && Math.Abs(rate - expected) <= expected * RateTolerance;
        var spacing = HsfpDpi.ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - spacing) / 2f;
        var size = new Vector2(width, ImGui.GetTextLineHeightWithSpacing() * 2f + HsfpDpi.Px(18f));

        HsfpUi.Tile("##rate", "Camera rate" + (expected > 0 ? string.Format(" (expected {0:F1})", expected) : ""),
            health != null ? string.Format("{0:F1} Hz", rate) : "-", health == null ? HsfpUi.Muted : rateOk ? HsfpUi.Good : HsfpUi.Warning, size);
        ImGui.SameLine();
        HsfpUi.Tile("##frames", "Frames received", health != null ? health.Frames.ToString("N0") : "-", new Vector4(1, 1, 1, 1), size);
        HsfpUi.Tile("##dropped", "Dropped frames", health != null ? health.DroppedFrames.ToString("N0") : "-", CountColor(health, health != null ? health.DroppedFrames : 0), size);
        ImGui.SameLine();
        HsfpUi.Tile("##sequence", "Channel sequence errors", health != null ? health.SequenceErrors.ToString("N0") : "-", CountColor(health, health != null ? health.SequenceErrors : 0), size);
        ImGui.SetItemTooltip("Frames whose laser does not follow the previous frame's laser. A non-zero count means frames may be assigned to the wrong channel.");
    }

    static Vector4 CountColor(HsfpHealth health, long count)
    {
        return health == null ? HsfpUi.Muted : count == 0 ? HsfpUi.Good : HsfpUi.Bad;
    }

    double ExpectedFrameRate()
    {
        var rig = Rig;
        if (rig == null || rig.Camera == null || rig.Camera.Trigger == null) return 0;
        var period = rig.Camera.Trigger.OnDurationUs + rig.Camera.Trigger.OffDurationUs;
        return period > 0 ? 1e6 / period : 0;
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the laser sequence of the rig with the frames received per channel. Source1 = Frame subject; Source2 = HsfpHealth.")]
public class HsfpLaserTableVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    [XmlIgnore]
    [Description("The rig, whose lasers are listed. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    public IObservable<TTickSource> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpHealth> health)
    {
        return Observable.Create<TTickSource>(observer =>
        {
            var latest = new Latest<HsfpHealth>();
            var healthSub = latest.Track(health, observer.OnError);
            var frameSub = frames.Subscribe(tick =>
            {
                HsfpStyle.Apply();
                if (Visible) Draw(latest.Value);
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new System.Reactive.Disposables.CompositeDisposable(healthSub, frameSub);
        });
    }

    void Draw(HsfpHealth health)
    {
        HsfpUi.SectionHeader("Laser sequence");
        var rig = Rig;
        if (rig == null || rig.Lasers == null)
        {
            ImGui.TextDisabled("Waiting for the rig configuration.");
            return;
        }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##HsfpLasers", 7, flags, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Channel");
        ImGui.TableSetupColumn("Laser");
        ImGui.TableSetupColumn("Pin");
        ImGui.TableSetupColumn("Offset (us)");
        ImGui.TableSetupColumn("On (us)");
        ImGui.TableSetupColumn("Rate");
        ImGui.TableSetupColumn("Frames");
        ImGui.TableHeadersRow();
        for (int i = 0; i < rig.Lasers.Count; i++)
        {
            var laser = rig.Lasers[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(HsfpUi.WavelengthColor(laser.Wavelength), string.Format("Ch {0} - {1} nm", i, laser.Wavelength));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(laser.LaserName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted((i + 1).ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(laser.Pwm != null ? laser.Pwm.OffsetUs.ToString() : "-");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(laser.Pwm != null ? laser.Pwm.OnDurationUs.ToString() : "-");
            ImGui.TableSetColumnIndex(5);
            var rate = health != null && health.ChannelRates != null && i < health.ChannelRates.Length ? health.ChannelRates[i] : double.NaN;
            ImGui.TextUnformatted(double.IsNaN(rate) ? "-" : string.Format("{0:F1} Hz", rate));
            ImGui.TableSetColumnIndex(6);
            var count = health != null && health.FramesPerChannel != null && i < health.FramesPerChannel.Length ? health.FramesPerChannel[i] : 0;
            ImGui.TextUnformatted(health != null ? count.ToString("N0") : "-");
        }
        ImGui.EndTable();
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders one pill per laser of the rig, colored by wavelength, and emits the index of the selected channel. Source1 = Frame subject.")]
public class HsfpChannelSelectorVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    [XmlIgnore]
    [Description("The rig, whose lasers define the channels. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    [Description("Channel selected at startup.")]
    public int InitialChannel { get; set; }

    public IObservable<int> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<int>(observer =>
        {
            var selected = -1;
            return frames.Subscribe(_ =>
            {
                HsfpStyle.Apply();
                var rig = Rig;
                if (!Visible || rig == null || rig.Lasers == null || rig.Lasers.Count == 0) return;
                if (selected < 0 || selected >= rig.Lasers.Count)
                {
                    selected = Math.Max(0, Math.Min(InitialChannel, rig.Lasers.Count - 1));
                    observer.OnNext(selected);
                }
                for (int i = 0; i < rig.Lasers.Count; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    var laser = rig.Lasers[i];
                    var label = string.Format("Ch {0} - {1} nm##HsfpChannel{0}", i, laser.Wavelength);
                    if (HsfpUi.Pill(label, HsfpUi.WavelengthColor(laser.Wavelength), i == selected) && i != selected)
                    {
                        selected = i;
                        observer.OnNext(selected);
                    }
                }
            }, observer.OnError, observer.OnCompleted);
        });
    }
}
