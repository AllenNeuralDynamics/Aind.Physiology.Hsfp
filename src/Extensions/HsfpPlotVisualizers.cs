using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using OpenCV.Net;

static class HsfpPlot
{
    public static unsafe void Line(string label, double[] xs, double[] ys, int count, Vector4 color)
    {
        if (count <= 0) return;
        ImPlot.SetNextLineStyle(color, HsfpDpi.Px(1.5f));
        fixed (double* x = xs)
        fixed (double* y = ys)
        {
            ImPlot.PlotLine(label, x, y, count);
        }
    }

    public static string ChannelLabel(AindPhysiologyHsfp.AindPhysioHsfpRig rig, int channel)
    {
        return HsfpChannels.Label(rig, channel);
    }

    public static Vector4 ChannelColor(AindPhysiologyHsfp.AindPhysioHsfpRig rig, int channel)
    {
        return HsfpUi.ChannelColor(HsfpChannels.Find(rig, channel), channel);
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Plots the emission spectrum (mean of each column) inside each fiber's ROI, for the selected channel. Source1 = Frame subject; Source2 = HsfpFrame stream.")]
public class HsfpSpectrumVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private volatile int channel;
    [Description("The cuTTLefish channel whose spectrum is shown. Map it from the channel selector.")]
    public int Channel { get { return channel; } set { channel = value; } }

    [XmlIgnore]
    [Description("The regions of interest. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.RoiSettings RoiSettings { get; set; }

    [XmlIgnore]
    [Description("The rig, used for channel names. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    class Spectra
    {
        public int Channel;
        public List<int> Fibers = new List<int>();
        public List<double[]> Xs = new List<double[]>();
        public List<double[]> Ys = new List<double[]>();
    }

    public IObservable<TTickSource> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpFrame> images)
    {
        return Observable.Create<TTickSource>(observer =>
        {
            var latest = new Latest<Spectra>();
            // Only the selected channel is reduced, at most once per rendered frame.
            var computeGate = 0;
            var imageSub = images.Where(frame => frame.Channel == Channel).Subscribe(frame =>
            {
                if (System.Threading.Interlocked.Exchange(ref computeGate, 1) == 1) return;
                var settings = RoiSettings;
                var spectra = new Spectra { Channel = frame.Channel };
                if (settings != null && settings.Rois != null)
                {
                    foreach (var roi in settings.Rois.Where(r => r.Enabled))
                    {
                        Rect rect;
                        if (!HsfpRoi.TryGetRect(roi, frame.Index, frame.Image.Size, out rect)) continue;
                        var ys = HsfpRoi.ColumnMeans(frame.Image, rect);
                        var xs = new double[ys.Length];
                        for (int i = 0; i < xs.Length; i++) xs[i] = rect.X + i;
                        spectra.Fibers.Add(roi.FiberNumber);
                        spectra.Xs.Add(xs);
                        spectra.Ys.Add(ys);
                    }
                }
                latest.Set(spectra);
            }, observer.OnError);

            var frameSub = frames.Subscribe(tick =>
            {
                HsfpStyle.Apply();
                System.Threading.Interlocked.Exchange(ref computeGate, 0);
                if (Visible) Draw(latest.Value);
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new CompositeDisposable(imageSub, frameSub);
        });
    }

    void Draw(Spectra spectra)
    {
        var current = Channel;
        HsfpUi.SectionHeader("Spectrum");
        ImGui.SameLine();
        ImGui.TextColored(HsfpPlot.ChannelColor(Rig, current), HsfpPlot.ChannelLabel(Rig, current));
        var settings = RoiSettings;
        if (!ImPlot.BeginPlot("##HsfpSpectrum", ImGui.GetContentRegionAvail(), ImPlotFlags.NoTitle | ImPlotFlags.NoMenus)) return;
        ImPlot.SetupAxes("Sensor x (px)", "Mean (a.u.)", ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.AutoFit);
        if (spectra != null && spectra.Channel == current)
        {
            for (int i = 0; i < spectra.Fibers.Count; i++)
            {
                var index = settings != null && settings.Rois != null ? settings.Rois.FindIndex(r => r.FiberNumber == spectra.Fibers[i]) : i;
                HsfpPlot.Line("Fiber " + spectra.Fibers[i], spectra.Xs[i], spectra.Ys[i], spectra.Xs[i].Length, HsfpUi.FiberColor(Math.Max(0, index)));
            }
        }
        ImPlot.EndPlot();
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Plots the recent fluorescence of each ROI, by channel (one plot per light source, one line per fiber) or by fiber (one plot per fiber, one line per light source). Source1 = Frame subject; Source2 = HsfpActivity stream.")]
public class HsfpActivityVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private double capacity = 30;
    [Description("Time window shown in the plots (s).")]
    public double Capacity { get { return capacity; } set { capacity = value; } }

    [Description("Minimum height of each plot, in pixels at 96 DPI.")]
    public float MinPlotHeight { get; set; }

    [XmlIgnore]
    [Description("The rig, used for channel names and colors. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    public HsfpActivityVisualizer()
    {
        MinPlotHeight = 80f;
    }

    class Series
    {
        public readonly Queue<double> Times = new Queue<double>();
        public readonly Queue<double> Values = new Queue<double>();
    }

    public IObservable<TTickSource> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpActivity> activity)
    {
        return Observable.Create<TTickSource>(observer =>
        {
            var gate = new object();
            // Keyed by (fiber number, channel).
            var series = new Dictionary<Tuple<int, int>, Series>();
            var latestTime = 0.0;
            var byFiber = false;

            var activitySub = activity.Subscribe(value =>
            {
                lock (gate)
                {
                    latestTime = Math.Max(latestTime, value.Seconds);
                    for (int i = 0; i < value.FiberNumbers.Length; i++)
                    {
                        var key = Tuple.Create(value.FiberNumbers[i], value.Channel);
                        Series s;
                        if (!series.TryGetValue(key, out s)) series[key] = s = new Series();
                        s.Times.Enqueue(value.Seconds);
                        s.Values.Enqueue(value.Values[i]);
                    }
                    foreach (var s in series.Values)
                    {
                        while (s.Times.Count > 0 && s.Times.Peek() < latestTime - Capacity)
                        {
                            s.Times.Dequeue();
                            s.Values.Dequeue();
                        }
                    }
                }
            }, observer.OnError);

            var frameSub = frames.Subscribe(tick =>
            {
                HsfpStyle.Apply();
                if (Visible)
                {
                    List<KeyValuePair<Tuple<int, int>, double[][]>> snapshot;
                    double now;
                    lock (gate)
                    {
                        now = latestTime;
                        snapshot = series.Select(kv => new KeyValuePair<Tuple<int, int>, double[][]>(kv.Key, new[] { kv.Value.Times.ToArray(), kv.Value.Values.ToArray() })).ToList();
                    }
                    Draw(snapshot, now, ref byFiber);
                }
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new CompositeDisposable(activitySub, frameSub);
        });
    }

    void Draw(List<KeyValuePair<Tuple<int, int>, double[][]>> snapshot, double now, ref bool byFiber)
    {
        HsfpUi.SectionHeader(string.Format("Activity - last {0:F0} s", Capacity));
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - HsfpDpi.FitWidth("By channel", 0) - HsfpDpi.FitWidth("By fiber", 0) - HsfpDpi.ItemSpacing.X + ImGui.GetCursorPosX());
        if (HsfpUi.Pill("By channel##HsfpActivity", new Vector4(0.75f, 0.78f, 0.82f, 1f), !byFiber)) byFiber = false;
        ImGui.SameLine();
        if (HsfpUi.Pill("By fiber##HsfpActivity", new Vector4(0.75f, 0.78f, 0.82f, 1f), byFiber)) byFiber = true;

        var fibers = snapshot.Select(kv => kv.Key.Item1).Distinct().OrderBy(f => f).ToList();
        var channels = HsfpChannels.Get(Rig).Select(c => c.Channel).ToList();
        if (channels.Count == 0) channels = snapshot.Select(kv => kv.Key.Item2).Distinct().OrderBy(c => c).ToList();
        if (fibers.Count == 0)
        {
            ImGui.TextDisabled("No activity yet: waiting for frames and enabled ROIs.");
            return;
        }

        // Grid whose cells are closest to a wide plot (3:1), among those that keep the minimum height.
        var groups = byFiber ? fibers.Count : channels.Count;
        var avail = ImGui.GetContentRegionAvail();
        var titleHeight = ImGui.GetTextLineHeightWithSpacing();
        var cellOverhead = titleHeight + 2f * HsfpDpi.ItemSpacing.Y + 2f * HsfpDpi.Px(3f);
        var columns = 1;
        var bestScore = double.MaxValue;
        for (int c = 1; c <= Math.Max(1, groups); c++)
        {
            var r = (groups + c - 1) / c;
            var plotHeight = avail.Y / r - cellOverhead;
            var plotWidth = avail.X / c;
            var score = Math.Abs(Math.Log(Math.Max(1, plotWidth) / Math.Max(1, plotHeight) / 3.0));
            if (plotHeight < HsfpDpi.Px(MinPlotHeight)) score += 100;
            if (score < bestScore) { bestScore = score; columns = c; }
        }
        var rows = (groups + columns - 1) / columns;
        var height = Math.Max(HsfpDpi.Px(MinPlotHeight), avail.Y / rows - cellOverhead) + titleHeight;
        ImGui.BeginChild("##HsfpActivityPlots", new Vector2(0, 0));
        if (ImGui.BeginTable("##HsfpActivityGrid", columns, ImGuiTableFlags.SizingStretchSame))
        {
            for (int g = 0; g < groups; g++)
            {
                ImGui.TableNextColumn();
                var title = byFiber ? "Fiber " + fibers[g] : HsfpPlot.ChannelLabel(Rig, channels[g]);
                var titleColor = byFiber ? HsfpUi.FiberColor(g) : HsfpPlot.ChannelColor(Rig, channels[g]);
                ImGui.TextColored(titleColor, title);
                if (ImPlot.BeginPlot("##HsfpActivity" + g, new Vector2(-1, height - ImGui.GetTextLineHeightWithSpacing()), ImPlotFlags.NoTitle | ImPlotFlags.NoMenus | ImPlotFlags.NoLegend))
                {
                    var axes = ImPlotAxisFlags.NoHighlight | ImPlotAxisFlags.AutoFit;
                    ImPlot.SetupAxis(ImAxis.X1, "##time", axes | ImPlotAxisFlags.NoLabel);
                    ImPlot.SetupAxis(ImAxis.Y1, "##value", axes | ImPlotAxisFlags.NoLabel);
                    ImPlot.SetupAxisLimits(ImAxis.X1, now - Capacity, now, ImPlotCond.Always);
                    ImPlot.SetupAxisTicks(ImAxis.X1, now - Capacity, now, 2, new[] { "-" + Capacity.ToString("F0") + " s", "0" });
                    foreach (var kv in snapshot)
                    {
                        if (byFiber ? kv.Key.Item1 != fibers[g] : kv.Key.Item2 != channels[g]) continue;
                        var color = byFiber ? HsfpPlot.ChannelColor(Rig, kv.Key.Item2) : HsfpUi.FiberColor(fibers.IndexOf(kv.Key.Item1));
                        var label = byFiber ? HsfpPlot.ChannelLabel(Rig, kv.Key.Item2) : "Fiber " + kv.Key.Item1;
                        HsfpPlot.Line(label, kv.Value[0], kv.Value[1], kv.Value[0].Length, color);
                    }
                    ImPlot.EndPlot();
                }
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }
}
