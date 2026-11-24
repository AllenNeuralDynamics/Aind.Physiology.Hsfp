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
[Description("Plots the emission spectrum (mean of each column) inside each spectrum ROI, read from the latest image of the ROI's channel. Source1 = Frame subject; Source2 = HsfpFrame stream.")]
public class HsfpSpectrumVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    [XmlIgnore]
    [Description("The spectrum ROIs, edited in the Spectrum tab of the ROI manager. Set via PropertyMapping.")]
    public HsfpSpectrumRois SpectrumRois { get; set; }

    [XmlIgnore]
    [Description("The rig, used for channel names and colors. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    public IObservable<TTickSource> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpFrame> images)
    {
        return Observable.Create<TTickSource>(observer =>
        {
            var imageLock = new object();
            var latestImages = new Dictionary<int, IplImage>();
            var limits = new HsfpLimits();
            var imageSub = images.Subscribe(frame =>
            {
                lock (imageLock) latestImages[frame.Channel] = frame.Image;
            }, observer.OnError);

            var frameSub = frames.Subscribe(tick =>
            {
                HsfpStyle.Apply();
                if (Visible)
                {
                    // Reduced on the render thread, once per rendered frame, from the latest image of each channel.
                    var rois = SpectrumRois != null ? SpectrumRois.Rois : null;
                    var lines = new List<Tuple<HsfpSpectrumRoi, double[], double[]>>();
                    if (rois != null)
                    {
                        foreach (var roi in rois)
                        {
                            IplImage image;
                            lock (imageLock)
                            {
                                if (!latestImages.TryGetValue(roi.Channel, out image)) continue;
                            }
                            Rect rect;
                            if (!TryGetRect(roi, image.Size, out rect)) continue;
                            var ys = HsfpRoi.ColumnMeans(image, rect);
                            var xs = new double[ys.Length];
                            for (int i = 0; i < xs.Length; i++) xs[i] = rect.X + i;
                            lines.Add(Tuple.Create(roi, xs, ys));
                        }
                    }
                    Draw(lines, rois != null && rois.Count > 0, limits);
                }
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new CompositeDisposable(imageSub, frameSub);
        });
    }

    static bool TryGetRect(HsfpSpectrumRoi roi, Size size, out Rect rect)
    {
        var x0 = Math.Max(0, Math.Min(roi.X, size.Width));
        var y0 = Math.Max(0, Math.Min(roi.Y, size.Height));
        var x1 = Math.Max(x0, Math.Min(roi.X + roi.Width, size.Width));
        var y1 = Math.Max(y0, Math.Min(roi.Y + roi.Height, size.Height));
        rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        return rect.Width > 0 && rect.Height > 0;
    }

    void Draw(List<Tuple<HsfpSpectrumRoi, double[], double[]>> lines, bool hasRois, HsfpLimits limits)
    {
        HsfpUi.SectionHeader("Spectrum");
        ImGui.SameLine();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - limits.ButtonWidth("Y");
        if (right > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(right);
        limits.DrawButton("HsfpSpectrumY", "Y");
        if (!hasRois)
        {
            ImGui.TextDisabled("No spectrum ROIs: add them in the Spectrum tab of the image.");
            return;
        }
        double min = double.MaxValue, max = double.MinValue;
        foreach (var line in lines)
        {
            foreach (var y in line.Item3)
            {
                if (double.IsNaN(y)) continue;
                min = Math.Min(min, y);
                max = Math.Max(max, y);
            }
        }
        if (min <= max) limits.Observe(min, max);
        if (!ImPlot.BeginPlot("##HsfpSpectrum", ImGui.GetContentRegionAvail(), ImPlotFlags.NoTitle | ImPlotFlags.NoMenus)) return;
        ImPlot.SetupAxes("Sensor x (px)", "Mean (a.u.)", ImPlotAxisFlags.AutoFit, limits.Auto ? ImPlotAxisFlags.AutoFit : ImPlotAxisFlags.None);
        if (!limits.Auto) ImPlot.SetupAxisLimits(ImAxis.Y1, limits.Min, limits.Max, ImPlotCond.Always);
        foreach (var line in lines)
        {
            var roi = line.Item1;
            var label = roi.Name + " - " + HsfpChannels.Label(Rig, roi.Channel) + "##" + roi.Id;
            HsfpPlot.Line(label, line.Item2, line.Item3, line.Item2.Length, HsfpUi.SpectrumColor(roi.Id - 1));
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

    private double capacity = 120;
    [Description("Longest time window that can be shown (s): the history kept for the plots.")]
    public double Capacity { get { return capacity; } set { capacity = value; } }

    private double window = 30;
    [Description("Time window shown at startup (s). It can be changed in the plot header, up to Capacity.")]
    public double Window { get { return window; } set { window = value; } }

    static readonly double[] WindowChoices = { 5, 10, 30, 60, 120, 300, 600 };

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
            var shownWindow = Math.Max(1, Math.Min(Window, Capacity));
            // Y limits of each plot, keyed by its group ("f" + fiber or "c" + channel).
            var limits = new Dictionary<string, HsfpLimits>();

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
                    Draw(snapshot, now, ref byFiber, ref shownWindow, limits);
                }
                observer.OnNext(tick);
            }, observer.OnError, observer.OnCompleted);
            return new CompositeDisposable(activitySub, frameSub);
        });
    }

    static string FormatSeconds(double seconds)
    {
        return seconds >= 60 && seconds % 60 == 0 ? (seconds / 60).ToString("F0") + " min" : seconds.ToString("G4") + " s";
    }

    // Ticks every "nice" number of seconds before now, labelled as time before now ("-10 s" ... "0").
    static void SetupTimeTicks(double now, double span)
    {
        var steps = new[] { 1.0, 2, 5, 10, 15, 20, 30, 60, 120, 300 };
        var step = steps.FirstOrDefault(s => span / s <= 4);
        if (step <= 0) step = span / 4;
        var count = (int)Math.Floor(span / step + 1e-9) + 1;
        var positions = new double[count];
        var labels = new string[count];
        for (int i = 0; i < count; i++)
        {
            var ago = (count - 1 - i) * step;
            positions[i] = now - ago;
            // Unit on the oldest tick only, to keep the labels short.
            labels[i] = ago == 0 ? "0" : i == 0 ? "-" + ago.ToString("G4") + " s" : "-" + ago.ToString("G4");
        }
        ImPlot.SetupAxisTicks(ImAxis.X1, ref positions[0], count, labels);
    }

    void Draw(List<KeyValuePair<Tuple<int, int>, double[][]>> snapshot, double now, ref bool byFiber, ref double shownWindow, Dictionary<string, HsfpLimits> limits)
    {
        HsfpUi.SectionHeader("Activity - last");
        var choices = WindowChoices.Where(w => w <= Capacity).ToList();
        if (choices.Count == 0 || choices[choices.Count - 1] < Capacity) choices.Add(Capacity);
        foreach (var choice in choices)
        {
            ImGui.SameLine();
            if (HsfpUi.Pill(FormatSeconds(choice) + "##HsfpWindow", new Vector4(0.75f, 0.78f, 0.82f, 1f), Math.Abs(choice - shownWindow) < 1e-9)) shownWindow = choice;
        }
        var span = shownWindow;
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
        var titleHeight = ImGui.GetFrameHeightWithSpacing();
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
                var group = byFiber ? "f" + fibers[g] : "c" + channels[g];
                HsfpLimits plotLimits;
                if (!limits.TryGetValue(group, out plotLimits)) limits[group] = plotLimits = new HsfpLimits();
                double min = double.MaxValue, max = double.MinValue;
                foreach (var kv in snapshot)
                {
                    if (byFiber ? kv.Key.Item1 != fibers[g] : kv.Key.Item2 != channels[g]) continue;
                    for (int k = 0; k < kv.Value[1].Length; k++)
                    {
                        var v = kv.Value[1][k];
                        if (double.IsNaN(v) || kv.Value[0][k] < now - span) continue;
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
                if (min <= max) plotLimits.Observe(min, max);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(titleColor, title);
                ImGui.SameLine();
                var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - plotLimits.ButtonWidth("Y");
                if (right > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(right);
                plotLimits.DrawButton("HsfpActivityY" + group, "Y");
                if (ImPlot.BeginPlot("##HsfpActivity" + g, new Vector2(-1, height - ImGui.GetFrameHeightWithSpacing()), ImPlotFlags.NoTitle | ImPlotFlags.NoMenus | ImPlotFlags.NoLegend))
                {
                    // Shared time axis: every plot shows the same window, labelled on the bottom row only.
                    var bottomRow = g / columns == rows - 1 || g + columns >= groups;
                    var timeFlags = ImPlotAxisFlags.NoHighlight | ImPlotAxisFlags.NoLabel | (bottomRow ? ImPlotAxisFlags.None : ImPlotAxisFlags.NoTickLabels);
                    ImPlot.SetupAxis(ImAxis.X1, "##time", timeFlags);
                    var valueFlags = ImPlotAxisFlags.NoHighlight | ImPlotAxisFlags.NoLabel | (plotLimits.Auto ? ImPlotAxisFlags.AutoFit | ImPlotAxisFlags.RangeFit : ImPlotAxisFlags.None);
                    ImPlot.SetupAxis(ImAxis.Y1, "##value", valueFlags);
                    if (!plotLimits.Auto) ImPlot.SetupAxisLimits(ImAxis.Y1, plotLimits.Min, plotLimits.Max, ImPlotCond.Always);
                    ImPlot.SetupAxisLimits(ImAxis.X1, now - span, now, ImPlotCond.Always);
                    SetupTimeTicks(now, span);
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
