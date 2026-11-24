using System;
using System.ComponentModel;
using System.Linq;
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

    readonly HsfpSyncStatus sync = new HsfpSyncStatus();

    [XmlIgnore]
    [Description("IS_SYNCHRONIZED bit of the cuTTLefish heartbeat (R_HEARTBEAT). Set via PropertyMapping on each heartbeat.")]
    public bool IsSynchronized
    {
        get { return sync.LastValue; }
        set { sync.Update(value); }
    }

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
        var avail = ImGui.GetContentRegionAvail();
        var width = (avail.X - spacing) / 2f;
        // Three rows of tiles, shrunk to fit the panel when it is short.
        var height = Math.Min(ImGui.GetTextLineHeightWithSpacing() * 2f + HsfpDpi.Px(18f), (avail.Y - 2f * HsfpDpi.ItemSpacing.Y) / 3f);
        var size = new Vector2(width, Math.Max(ImGui.GetTextLineHeightWithSpacing() * 2f, height));

        HsfpUi.Tile("##rate", "Camera rate" + (expected > 0 ? string.Format(" (expected {0:F2})", expected) : ""),
            health != null ? string.Format("{0:F1} Hz", rate) : "-", health == null ? HsfpUi.Muted : rateOk ? HsfpUi.Good : HsfpUi.Warning, size);
        ImGui.SameLine();
        HsfpUi.Tile("##frames", "Frames received", health != null ? health.Frames.ToString("N0") : "-", new Vector4(1, 1, 1, 1), size);
        HsfpUi.Tile("##dropped", "Dropped frames", health != null ? health.DroppedFrames.ToString("N0") : "-", CountColor(health, health != null ? health.DroppedFrames : 0), size);
        ImGui.SameLine();
        HsfpUi.Tile("##sequence", "Channel sequence errors", health != null ? health.SequenceErrors.ToString("N0") : "-", CountColor(health, health != null ? health.SequenceErrors : 0), size);
        ImGui.SetItemTooltip("Frames whose light source does not follow the previous frame's. A non-zero count means frames may be assigned to the wrong channel.");

        HsfpUi.Tile("##sync", "Harp clock", sync.Summary, sync.IsSynchronized ? HsfpUi.Good : HsfpUi.Bad, new Vector2(width * 2f + spacing, size.Y));
        ImGui.SetItemTooltip(sync.Describe() + " Read from the IS_SYNCHRONIZED bit of the cuTTLefish R_HEARTBEAT register, sent once per second.");
    }

    static Vector4 CountColor(HsfpHealth health, long count)
    {
        return health == null ? HsfpUi.Muted : count == 0 ? HsfpUi.Good : HsfpUi.Bad;
    }

    double ExpectedFrameRate()
    {
        return HsfpTriggerSchedule.FrameRate(Rig);
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
    [Description("The rig, whose cuTTLefish channels are listed. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    private double refreshInterval = 1.0;
    [Description("Interval (s) between updates of the rates and frame counts, so they stay readable.")]
    public double RefreshInterval { get { return refreshInterval; } set { refreshInterval = value; } }

    public IObservable<TTickSource> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpHealth> health)
    {
        return Observable.Create<TTickSource>(observer =>
        {
            var latest = new Latest<HsfpHealth>();
            var healthSub = latest.Track(health, observer.OnError);
            HsfpHealth shown = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frameSub = frames.Subscribe(tick =>
            {
                HsfpStyle.Apply();
                if (shown == null || clock.Elapsed.TotalSeconds >= RefreshInterval)
                {
                    shown = latest.Value;
                    if (shown != null) clock.Restart();
                }
                if (Visible) Draw(shown);
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new System.Reactive.Disposables.CompositeDisposable(healthSub, frameSub);
        });
    }

    void Draw(HsfpHealth health)
    {
        HsfpUi.SectionHeader("cuTTLefish channels");
        var rig = Rig;
        if (rig == null || rig.Channels == null)
        {
            ImGui.TextDisabled("Waiting for the rig configuration.");
            return;
        }
        var channels = HsfpChannels.Get(rig);
        var timing = rig.Timing;
        if (timing != null)
        {
            ImGui.TextDisabled(string.Format(
                "Frame every {0} us ({1:F2} Hz); each light source every {2} us.",
                timing.FramePeriodUs, HsfpTriggerSchedule.FrameRate(rig), (long)timing.FramePeriodUs * channels.Count));
            ImGui.TextDisabled(string.Format(
                "Light sources: {0} us on, {1} us after their frame's camera trigger.",
                timing.LightSourceOnDurationUs, timing.LightSourceDelayUs));
        }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##HsfpChannels", 6, flags, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Channel");
        ImGui.TableSetupColumn("Device");
        ImGui.TableSetupColumn("Offset (us)");
        ImGui.TableSetupColumn("On (us)");
        ImGui.TableSetupColumn("Rate");
        ImGui.TableSetupColumn("Frames");
        ImGui.TableHeadersRow();

        AllenNeuralDynamics.Cuttlefish.PwmSettingsPayload camera;
        var valid = TrySchedule(() => HsfpTriggerSchedule.Camera(rig), out camera);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(HsfpChannels.CameraChannel.ToString());
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(rig.Camera != null ? "Camera trigger" : "-");
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(valid ? camera.OffsetUs.ToString() : "-");
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(valid ? camera.OnDurationUs.ToString() : "-");
        ImGui.TableSetColumnIndex(4);
        ImGui.TextUnformatted(health != null ? string.Format("{0:F1} Hz", health.FrameRate) : "-");
        ImGui.TableSetColumnIndex(5);
        ImGui.TextUnformatted(health != null ? health.Frames.ToString("N0") : "-");

        for (int c = 1; c <= HsfpChannels.LightSourceChannelCount; c++)
        {
            var item = channels.FirstOrDefault(x => x.Channel == c);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (item == null)
            {
                ImGui.TextDisabled(c.ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextDisabled("unused");
                continue;
            }
            ImGui.TextColored(HsfpPlot.ChannelColor(rig, c), HsfpChannels.Label(rig, c));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.LightSource.Name);
            AllenNeuralDynamics.Cuttlefish.PwmSettingsPayload schedule;
            valid = TrySchedule(() => HsfpTriggerSchedule.LightSource(rig, item), out schedule);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(valid ? schedule.OffsetUs.ToString() : "-");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(valid ? schedule.OnDurationUs.ToString() : "-");
            var i = item.Index;
            ImGui.TableSetColumnIndex(4);
            var rate = health != null && health.ChannelRates != null && i < health.ChannelRates.Length ? health.ChannelRates[i] : double.NaN;
            ImGui.TextUnformatted(double.IsNaN(rate) ? "-" : string.Format("{0:F1} Hz", rate));
            ImGui.TableSetColumnIndex(5);
            var count = health != null && health.FramesPerChannel != null && i < health.FramesPerChannel.Length ? health.FramesPerChannel[i] : 0;
            ImGui.TextUnformatted(health != null ? count.ToString("N0") : "-");
        }
        ImGui.EndTable();
    }

    static bool TrySchedule(Func<AllenNeuralDynamics.Cuttlefish.PwmSettingsPayload> create, out AllenNeuralDynamics.Cuttlefish.PwmSettingsPayload schedule)
    {
        try
        {
            schedule = create();
            return true;
        }
        catch (ArgumentException)
        {
            schedule = default(AllenNeuralDynamics.Cuttlefish.PwmSettingsPayload);
            return false;
        }
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders an All pill and one pill per light source of the rig, colored by wavelength, and emits the cuTTLefish channel of the selected one, or 0 for All. Source1 = Frame subject.")]
public class HsfpChannelSelectorVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    [XmlIgnore]
    [Description("The rig, whose cuTTLefish channels define the light sources. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    /// <summary>Value emitted for the "All" view: the max projection of every channel.</summary>
    public const int AllChannels = 0;

    [Description("cuTTLefish channel selected at startup, or 0 for all channels. If it has no light source, the first one is selected.")]
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
                var channels = HsfpChannels.Get(rig);
                if (!Visible || channels.Count == 0) return;
                if (selected != AllChannels && !channels.Any(c => c.Channel == selected))
                {
                    selected = InitialChannel == AllChannels || channels.Any(c => c.Channel == InitialChannel) ? InitialChannel : channels[0].Channel;
                    observer.OnNext(selected);
                }
                if (HsfpUi.Pill("All##HsfpChannelAll", new Vector4(0.91f, 0.92f, 0.93f, 1f), selected == AllChannels) && selected != AllChannels)
                {
                    selected = AllChannels;
                    observer.OnNext(selected);
                }
                ImGui.SetItemTooltip("Maximum intensity projection of the last frame of every channel, with every ROI.");
                for (int i = 0; i < channels.Count; i++)
                {
                    ImGui.SameLine();
                    var channel = channels[i].Channel;
                    var label = HsfpChannels.Label(rig, channel) + "##HsfpChannel" + channel;
                    if (HsfpUi.Pill(label, HsfpPlot.ChannelColor(rig, channel), channel == selected) && channel != selected)
                    {
                        selected = channel;
                        observer.OnNext(selected);
                    }
                }
            }, observer.OnError, observer.OnCompleted);
        });
    }
}
