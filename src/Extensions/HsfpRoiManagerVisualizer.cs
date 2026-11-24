using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Hexa.NET.ImGui;
using Newtonsoft.Json;
using OpenCV.Net;

/// <summary>A region of the camera image whose spectrum is plotted. Spectrum ROIs are never recorded.</summary>
public class HsfpSpectrumRoi
{
    /// <summary>Number shown as "S{Id}"; also picks the color.</summary>
    public int Id { get; set; }

    /// <summary>cuTTLefish channel whose image the spectrum is read from.</summary>
    public int Channel { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public string Name { get { return "S" + Id; } }
}

/// <summary>An immutable snapshot of the spectrum ROIs.</summary>
public class HsfpSpectrumRois
{
    public HsfpSpectrumRois(IList<HsfpSpectrumRoi> rois)
    {
        Rois = new ReadOnlyCollection<HsfpSpectrumRoi>(rois);
    }

    public ReadOnlyCollection<HsfpSpectrumRoi> Rois { get; private set; }
}

/// <summary>Output of <see cref="HsfpRoiManagerVisualizer"/>. A member keeps its reference until it changes.</summary>
public class HsfpRoiState
{
    public HsfpRoiState(AindPhysiologyHsfp.RoiSettings fibers, HsfpSpectrumRois spectrum)
    {
        Fibers = fibers;
        Spectrum = spectrum;
    }

    /// <summary>Fiber ROIs, used to compute and record the activity.</summary>
    public AindPhysiologyHsfp.RoiSettings Fibers { get; private set; }

    /// <summary>Spectrum ROIs, only plotted.</summary>
    public HsfpSpectrumRois Spectrum { get; private set; }
}

/// <summary>
/// Represents an operator that renders the live camera image, with the fiber ROIs and the spectrum ROIs, and
/// persists both to disk.
/// </summary>
/// <remarks>
/// <para>Regions tab: one ROI per fiber of the task logic and per light source of the rig. Every ROI is
/// independent: moving one never moves another. The edge has the fiber's color and the fill the light source's
/// color. Selecting a channel shows its image and ROIs; the "All" channel (0) shows the maximum intensity
/// projection of the last frame of every channel, with every ROI.</para>
/// <para>Spectrum tab: any number of spectrum ROIs, each reading the image of one channel, plotted by
/// <see cref="HsfpSpectrumVisualizer"/>. They are saved to
/// <see cref="LocalSpectrumRoiPath"/> on every change, and are cleared if that file cannot be read.</para>
/// <para>The ROIs are not saved with the data, so they can be edited during a session. The Lock toggle locks every
/// ROI against accidental edits; it is switched on when <see cref="Recording"/> becomes true, and can be
/// switched off at any time.</para>
/// <para>The image is drawn through a colormap, between automatic or fixed limits, set per channel.</para>
/// Source1 = Frame subject; Source2 = HsfpFrame stream (live preview images).
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the live image of the selected channel (or the max projection of all channels) with the fiber ROIs and the spectrum ROIs, and emits both when they change. Source1 = Frame subject; Source2 = HsfpFrame stream.")]
public class HsfpRoiManagerVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private volatile bool recording;
    [Description("Whether a session is running. Map it from the experiment state: the ROIs are locked when it starts, and can be unlocked.")]
    public bool Recording { get { return recording; } set { recording = value; } }

    private volatile int channel;
    [Description("The cuTTLefish channel whose image and ROIs are shown, or 0 for all channels. Map it from the channel selector.")]
    public int Channel { get { return channel; } set { channel = value; } }

    private string localRoiDefaultPath = "../local/hsfp_roi.json";
    [Description("File used by Save/Load to persist the fiber ROIs. If it exists, the ROIs are loaded from it at startup.")]
    [FileNameFilter("JSON|*.json|All Files|*.*")]
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    public string LocalRoiDefaultPath { get { return localRoiDefaultPath; } set { localRoiDefaultPath = value; } }

    private string localSpectrumRoiPath = "../local/hsfp_spectrum_roi.json";
    [Description("File where the spectrum ROIs are saved on every change, and loaded from at startup.")]
    [FileNameFilter("JSON|*.json|All Files|*.*")]
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    public string LocalSpectrumRoiPath { get { return localSpectrumRoiPath; } set { localSpectrumRoiPath = value; } }

    [XmlIgnore]
    [Description("The rig: its light sources define the channels and the camera region the image size. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    [XmlIgnore]
    [Description("The task logic: one fiber ROI is created per measurement and channel. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpTaskLogic TaskLogic { get; set; }

    const float HandleSize = 8f;
    const int MinSize = 4;
    const int AllChannels = 0;

    class FiberState
    {
        public int FiberNumber;
        public bool Enabled = true;
        public List<int[]> Regions = new List<int[]>(); // x, y, width, height per channel, in firing order
    }

    class SpectrumState
    {
        public int Id;
        public int Channel;
        public int[] Rect;
    }

    enum Tab { Regions, Spectrum }

    enum DragMode { None, Move, Resize }

    /// <summary>A ROI drawn on the image this frame.</summary>
    class Item
    {
        public bool IsSpectrum;
        public int Index;        // fiber index or spectrum index
        public int ChannelIndex; // firing-order index of the region (fibers)
        public int[] Rect;
        public Vector4 Edge;
        public Vector4 Fill;
        public string Label;
        public bool Editable;
        public bool Reference;   // drawn as a faint outline only
    }

    class Drag
    {
        public DragMode Mode;
        public bool IsSpectrum;
        public int Index;
        public int ChannelIndex;
        public Vector2 Start;
        public int[] Origin;
        public bool Created;
    }

    public IObservable<HsfpRoiState> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpFrame> images)
    {
        return Observable.Create<HsfpRoiState>(observer =>
        {
            var fibers = new List<FiberState>();
            var spectrum = new List<SpectrumState>();
            var nextSpectrumId = 1;
            var seeded = false;
            var tab = Tab.Regions;
            var image = new HsfpColormapImage();
            var imageKey = int.MinValue;
            var imageLock = new object();
            var latestImages = new Dictionary<int, IplImage>();
            var dirty = new HashSet<int>();
            IplImage projection = null;
            var imageSize = new Vector2(0, 0);
            var limits = new Dictionary<int, HsfpLimits>();
            var colormapIndex = 0;
            var showRois = true;
            var locked = false;
            var wasRecording = false;
            string message = null;
            string pendingConfirm = null;
            Drag drag = null;
            var selectedFiber = -1;
            var selectedFiberChannel = -1;
            var selectedSpectrum = -1;
            AindPhysiologyHsfp.RoiSettings fiberSettings = null;
            HsfpSpectrumRois spectrumSettings = null;
            var saveSpectrum = false;

            var imageSub = images.Subscribe(frame =>
            {
                lock (imageLock)
                {
                    latestImages[frame.Index] = frame.Image;
                    dirty.Add(frame.Index);
                }
            }, observer.OnError);

            var frameSub = frames.Subscribe(_ =>
            {
                HsfpStyle.Apply();
                var rig = Rig;
                if (!Visible) return;
                if (rig == null || rig.Channels == null || rig.Camera == null)
                {
                    ImGui.TextDisabled("Waiting for the rig configuration.");
                    return;
                }
                var channels = HsfpChannels.Get(rig);
                var channelCount = Math.Max(1, channels.Count);
                var width = rig.Camera.Region != null && rig.Camera.Region.Width > 0 ? rig.Camera.Region.Width : 1;
                var height = rig.Camera.Region != null && rig.Camera.Region.Height > 0 ? rig.Camera.Region.Height : 1;
                var fibersChanged = false;
                var spectrumChanged = false;

                if (!seeded)
                {
                    Seed(fibers, TaskLogic, channelCount, width, height);
                    string error;
                    if (File.Exists(LocalRoiDefaultPath) && !TryLoad(fibers, channelCount, out error))
                    {
                        message = "Could not load the saved ROIs:\n" + error + "\nUsing the default ROIs.";
                    }
                    if (!TryLoadSpectrum(spectrum, channels, width, height))
                    {
                        spectrum.Clear();
                        saveSpectrum = true;
                    }
                    nextSpectrumId = spectrum.Count > 0 ? spectrum.Max(s => s.Id) + 1 : 1;
                    seeded = true;
                    fibersChanged = true;
                    spectrumChanged = true;
                }

                // The view: a channel (firing-order index), or all channels.
                var all = Channel == AllChannels;
                var current = all ? -1 : Math.Max(0, HsfpChannels.IndexOf(rig, Channel));
                var viewChannel = all ? AllChannels : channels.Count > 0 ? channels[current].Channel : Channel;
                var key = all ? AllChannels : viewChannel;

                // Live image of the view: the selected channel, or the max projection of every channel.
                IplImage pending = null;
                lock (imageLock)
                {
                    if (all)
                    {
                        if ((dirty.Count > 0 || imageKey != key) && latestImages.Count > 0)
                        {
                            projection = MaxProjection(latestImages.Values, projection);
                            pending = projection;
                            dirty.Clear();
                        }
                    }
                    else
                    {
                        IplImage latest;
                        if ((dirty.Contains(current) || imageKey != key) && latestImages.TryGetValue(current, out latest))
                        {
                            pending = latest;
                            dirty.Remove(current);
                        }
                    }
                }
                HsfpLimits viewLimits;
                if (!limits.TryGetValue(key, out viewLimits)) limits[key] = viewLimits = new HsfpLimits();
                if (pending != null)
                {
                    image.Update(pending);
                    imageKey = key;
                    imageSize = new Vector2(pending.Width, pending.Height);
                    viewLimits.Observe(image.DataMin, image.DataMax);
                }
                if (imageSize.X <= 0) imageSize = new Vector2(width, height);

                var recordingNow = Recording;
                if (recordingNow && !wasRecording) locked = true;
                wasRecording = recordingNow;
                var editable = !locked;

                // Tabs.
                if (ImGui.BeginTabBar("##HsfpRoiTabs"))
                {
                    if (ImGui.BeginTabItem("Regions##HsfpRoiTab"))
                    {
                        tab = Tab.Regions;
                        ImGui.EndTabItem();
                    }
                    ImGui.SetItemTooltip("Fiber ROIs: the regions whose activity is computed and recorded.");
                    if (ImGui.BeginTabItem("Spectrum##HsfpRoiTab"))
                    {
                        tab = Tab.Spectrum;
                        ImGui.EndTabItem();
                    }
                    ImGui.SetItemTooltip("Spectrum ROIs: regions whose spectrum is plotted below. Never recorded.");
                    ImGui.EndTabBar();
                }

                // Toolbar: tab actions on the left, colormap and limits on the right.
                if (tab == Tab.Regions)
                {
                    if (ImGui.Button("Save##HsfpRoi")) pendingConfirm = "Save";
                    ImGui.SetItemTooltip("Save the fiber ROIs to " + LocalRoiDefaultPath);
                    ImGui.SameLine();
                    ImGui.BeginDisabled(!editable);
                    ImGui.BeginDisabled(!File.Exists(LocalRoiDefaultPath));
                    if (ImGui.Button("Load##HsfpRoi")) pendingConfirm = "Load";
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.Button("Reset##HsfpRoi")) pendingConfirm = "Reset";
                    ImGui.EndDisabled();
                }
                else
                {
                    ImGui.BeginDisabled(!editable);
                    if (ImGui.Button("+ Add##HsfpSpectrum"))
                    {
                        var roiChannel = all ? (channels.Count > 0 ? channels[0].Channel : 1) : viewChannel;
                        var w = Math.Max(MinSize, (int)(imageSize.X * 0.3));
                        var h = Math.Max(MinSize, (int)(imageSize.Y * 0.15));
                        var x = Clamp((int)(imageSize.X * 0.35) + 10 * (spectrum.Count % 5), 0, Math.Max(0, (int)imageSize.X - w));
                        var y = Clamp((int)(imageSize.Y * 0.4) + 10 * (spectrum.Count % 5), 0, Math.Max(0, (int)imageSize.Y - h));
                        spectrum.Add(new SpectrumState { Id = nextSpectrumId++, Channel = roiChannel, Rect = new[] { x, y, w, h } });
                        selectedSpectrum = spectrum.Count - 1;
                        spectrumChanged = true;
                    }
                    ImGui.SetItemTooltip("Add a spectrum ROI on this channel. You can also drag on an empty part of the image.");
                    ImGui.SameLine();
                    ImGui.BeginDisabled(spectrum.Count == 0);
                    if (ImGui.Button("Reset##HsfpSpectrum")) pendingConfirm = "Clear";
                    ImGui.EndDisabled();
                    ImGui.SetItemTooltip("Remove every spectrum ROI.");
                    ImGui.EndDisabled();
                }
                var comboWidth = HsfpDpi.FitWidth("Twilight", 90f) + ImGui.GetFrameHeight();
                var toggleLabel = showRois ? "Hide ROIs" : "Show ROIs";
                var lockLabel = locked ? "Locked" : "Lock";
                var rightWidth = HsfpDpi.FitWidth(lockLabel, 0) + HsfpDpi.FitWidth(toggleLabel, 0) + comboWidth + viewLimits.ButtonWidth("Range") + 3f * HsfpDpi.ItemSpacing.X;
                ImGui.SameLine();
                var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - rightWidth;
                if (right > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(right);
                if (HsfpUi.Pill(lockLabel + "##HsfpRoiLock", locked ? HsfpUi.Warning : HsfpUi.Muted, locked)) locked = !locked;
                ImGui.SetItemTooltip(locked
                    ? "The ROIs are locked. Click to unlock them (they are locked automatically when a session starts)."
                    : "Lock every ROI against accidental edits. They are locked automatically when a session starts.");
                ImGui.SameLine();
                if (HsfpUi.Pill(toggleLabel + "##HsfpRoiToggle", HsfpUi.Muted, !showRois)) showRois = !showRois;
                ImGui.SetItemTooltip(showRois ? "Hide the ROIs to see the whole image." : "Show the ROIs again.");
                ImGui.SameLine();
                HsfpColormaps.Combo("##HsfpColormap", ref colormapIndex, comboWidth);
                ImGui.SetItemTooltip("Colormap of the image.");
                ImGui.SameLine();
                viewLimits.DrawButton("HsfpImage" + key, "Range");

                // Image, colorbar on its right, ROI table below.
                var visibleItems = showRois ? BuildItems(tab, all, current, channels, fibers, spectrum, viewChannel, editable) : new List<Item>();
                var tableRows = tab == Tab.Regions ? (all ? fibers.Count * channelCount : fibers.Count) : Math.Max(1, spectrum.Count);
                var avail = ImGui.GetContentRegionAvail();
                var colorbarWidth = HsfpDpi.Px(56f);
                var tableHeight = Math.Min(avail.Y * 0.4f, ImGui.GetFrameHeightWithSpacing() * (tableRows + 1.5f));
                var maxImageHeight = Math.Max(HsfpDpi.Px(60f), avail.Y - tableHeight - HsfpDpi.ItemSpacing.Y);
                var maxImageWidth = Math.Max(HsfpDpi.Px(60f), avail.X - colorbarWidth - HsfpDpi.ItemSpacing.X);
                var scale = Math.Min(maxImageWidth / imageSize.X, maxImageHeight / imageSize.Y);
                var displaySize = new Vector2(imageSize.X * scale, imageSize.Y * scale);
                var origin = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();
                var hasImage = image.IsValid && imageKey == key;
                if (hasImage)
                {
                    image.Draw(displaySize, viewLimits.Lo, viewLimits.Hi, colormapIndex);
                }
                else
                {
                    ImGui.Dummy(displaySize);
                }
                var imageHovered = ImGui.IsItemHovered();
                if (!hasImage)
                {
                    drawList.AddRectFilled(origin, origin + displaySize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.02f, 0.02f, 0.03f, 1f)));
                    var text = all ? "No frames yet" : "No frames for this channel yet";
                    drawList.AddText(origin + (displaySize - ImGui.CalcTextSize(text)) * 0.5f, ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
                }
                ImGui.SameLine();
                HsfpColormaps.Colorbar(colormapIndex, viewLimits.Lo, viewLimits.Hi, new Vector2(colorbarWidth, displaySize.Y));

                // Hit-test: the topmost interactive item under the mouse.
                var mouse = ImGui.GetMousePos();
                var handle = HsfpDpi.Px(HandleSize);
                Item hover = null;
                var hoverMode = DragMode.None;
                for (int i = visibleItems.Count - 1; i >= 0 && hover == null; i--)
                {
                    var item = visibleItems[i];
                    if (item.Reference || !imageHovered) continue;
                    var min = origin + new Vector2(item.Rect[0], item.Rect[1]) * scale;
                    var max = origin + new Vector2(item.Rect[0] + item.Rect[2], item.Rect[1] + item.Rect[3]) * scale;
                    if (item.Editable && mouse.X >= max.X - handle && mouse.X <= max.X + handle * 0.5f && mouse.Y >= max.Y - handle && mouse.Y <= max.Y + handle * 0.5f)
                    {
                        hover = item; hoverMode = DragMode.Resize;
                    }
                    else if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
                    {
                        hover = item; hoverMode = DragMode.Move;
                    }
                }

                if (drag == null && imageHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (hover != null)
                    {
                        if (hover.IsSpectrum) { selectedSpectrum = hover.Index; }
                        else { selectedFiber = hover.Index; selectedFiberChannel = hover.ChannelIndex; }
                        if (hover.Editable)
                        {
                            drag = new Drag { Mode = hoverMode, IsSpectrum = hover.IsSpectrum, Index = hover.Index, ChannelIndex = hover.ChannelIndex, Start = mouse, Origin = (int[])hover.Rect.Clone() };
                        }
                    }
                    else if (tab == Tab.Spectrum && showRois && editable)
                    {
                        // Drag on an empty part of the image to draw a new spectrum ROI.
                        var p = (mouse - origin) / scale;
                        var roiChannel = all ? (channels.Count > 0 ? channels[0].Channel : 1) : viewChannel;
                        var rect = new[] { Clamp((int)p.X, 0, Math.Max(0, (int)imageSize.X - MinSize)), Clamp((int)p.Y, 0, Math.Max(0, (int)imageSize.Y - MinSize)), MinSize, MinSize };
                        spectrum.Add(new SpectrumState { Id = nextSpectrumId++, Channel = roiChannel, Rect = rect });
                        selectedSpectrum = spectrum.Count - 1;
                        drag = new Drag { Mode = DragMode.Resize, IsSpectrum = true, Index = selectedSpectrum, Start = mouse, Origin = new[] { rect[0], rect[1], 0, 0 }, Created = true };
                        spectrumChanged = true;
                    }
                    else
                    {
                        selectedFiber = -1;
                    }
                }
                if (drag != null)
                {
                    var target = drag.IsSpectrum
                        ? (drag.Index < spectrum.Count ? spectrum[drag.Index].Rect : null)
                        : (drag.Index < fibers.Count ? fibers[drag.Index].Regions[drag.ChannelIndex] : null);
                    if (!editable || target == null || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        if (drag.Created && target != null && target[2] <= MinSize && target[3] <= MinSize)
                        {
                            // A click without a drag: no new ROI.
                            spectrum.RemoveAt(drag.Index);
                            selectedSpectrum = -1;
                            nextSpectrumId--;
                            spectrumChanged = true;
                        }
                        if (drag.IsSpectrum) saveSpectrum = true;
                        drag = null;
                    }
                    else
                    {
                        var delta = (mouse - drag.Start) / scale;
                        var dx = (int)Math.Round(delta.X);
                        var dy = (int)Math.Round(delta.Y);
                        var o = drag.Origin;
                        if (drag.Mode == DragMode.Move)
                        {
                            target[0] = Clamp(o[0] + dx, 0, (int)imageSize.X - o[2]);
                            target[1] = Clamp(o[1] + dy, 0, (int)imageSize.Y - o[3]);
                        }
                        else
                        {
                            target[2] = Clamp(o[2] + dx, MinSize, (int)imageSize.X - o[0]);
                            target[3] = Clamp(o[3] + dy, MinSize, (int)imageSize.Y - o[1]);
                        }
                        if (drag.IsSpectrum) spectrumChanged = true; else fibersChanged = true;
                    }
                }
                if (hover != null && hover.Editable && (hoverMode == DragMode.Resize || (drag != null && drag.Mode == DragMode.Resize))) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);
                else if (drag != null && drag.Mode == DragMode.Resize) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);
                else if ((hover != null && hover.Editable) || drag != null) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                else if (tab == Tab.Spectrum && showRois && editable && imageHovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                if (tab == Tab.Spectrum && editable && selectedSpectrum >= 0 && selectedSpectrum < spectrum.Count && drag == null
                    && ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.Delete))
                {
                    spectrum.RemoveAt(selectedSpectrum);
                    selectedSpectrum = -1;
                    spectrumChanged = true;
                    saveSpectrum = true;
                }

                // Draw the ROIs.
                drawList.PushClipRect(origin, origin + displaySize, true);
                foreach (var item in visibleItems)
                {
                    var min = origin + new Vector2(item.Rect[0], item.Rect[1]) * scale;
                    var max = origin + new Vector2(item.Rect[0] + item.Rect[2], item.Rect[1] + item.Rect[3]) * scale;
                    if (item.Reference)
                    {
                        drawList.AddRect(min, max, HsfpUi.WithAlpha(item.Edge, 0.45f), 0f, ImDrawFlags.None, HsfpDpi.Px(1f));
                        continue;
                    }
                    var selected = item.IsSpectrum ? item.Index == selectedSpectrum : item.Index == selectedFiber && item.ChannelIndex == selectedFiberChannel;
                    var active = selected || item == hover || (drag != null && drag.IsSpectrum == item.IsSpectrum && drag.Index == item.Index && drag.ChannelIndex == item.ChannelIndex);
                    drawList.AddRectFilled(min, max, HsfpUi.WithAlpha(item.Fill, active ? 0.32f : 0.16f));
                    drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(item.Edge), 0f, ImDrawFlags.None, HsfpDpi.Px(active ? 2.5f : 1.5f));
                    if (item.Editable)
                    {
                        drawList.AddRectFilled(max - new Vector2(handle, handle), max, ImGui.ColorConvertFloat4ToU32(item.Edge));
                    }
                    var labelPos = min + new Vector2(HsfpDpi.Px(3f), HsfpDpi.Px(1f));
                    drawList.AddRectFilled(labelPos - new Vector2(HsfpDpi.Px(2f), 0), labelPos + ImGui.CalcTextSize(item.Label) + new Vector2(HsfpDpi.Px(2f), 0), HsfpUi.WithAlpha(new Vector4(0f, 0f, 0f, 1f), 0.55f));
                    drawList.AddText(labelPos, ImGui.ColorConvertFloat4ToU32(item.Edge), item.Label);
                }
                drawList.PopClipRect();

                // View label in the top-left corner of the image.
                var viewLabel = all ? "All - max projection" : HsfpChannels.Label(rig, viewChannel);
                var viewColor = all ? HsfpUi.Muted : HsfpPlot.ChannelColor(rig, viewChannel);
                var viewPos = origin + new Vector2(HsfpDpi.Px(6f), displaySize.Y - ImGui.GetTextLineHeight() - HsfpDpi.Px(5f));
                drawList.AddRectFilled(viewPos - new Vector2(HsfpDpi.Px(4f), HsfpDpi.Px(2f)), viewPos + ImGui.CalcTextSize(viewLabel) + new Vector2(HsfpDpi.Px(4f), HsfpDpi.Px(2f)), HsfpUi.WithAlpha(new Vector4(0f, 0f, 0f, 1f), 0.6f), HsfpDpi.Px(3f));
                drawList.AddText(viewPos, ImGui.ColorConvertFloat4ToU32(viewColor), viewLabel);

                if (tab == Tab.Regions)
                {
                    fibersChanged |= DrawFiberTable(rig, fibers, channels, all, current, editable, (int)imageSize.X, (int)imageSize.Y, ref selectedFiber, ref selectedFiberChannel);
                }
                else
                {
                    var tableChanged = DrawSpectrumTable(rig, spectrum, channels, editable, (int)imageSize.X, (int)imageSize.Y, ref selectedSpectrum);
                    spectrumChanged |= tableChanged;
                    saveSpectrum |= tableChanged;
                }
                var confirmed = DrawConfirm(fibers, spectrum, ref pendingConfirm, ref message, channelCount, width, height);
                if (confirmed == "Clear")
                {
                    selectedSpectrum = -1;
                    nextSpectrumId = 1;
                    spectrumChanged = true;
                    saveSpectrum = true;
                }
                else if (confirmed != null)
                {
                    fibersChanged = true;
                }

                if (message != null)
                {
                    ImGui.OpenPopup("HSFP ROIs##HsfpRoiMessage");
                }
                if (ImGui.BeginPopupModal("HSFP ROIs##HsfpRoiMessage", ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextUnformatted(message ?? string.Empty);
                    if (ImGui.Button("OK", new Vector2(HsfpDpi.Px(120f), 0)))
                    {
                        message = null;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }

                if (saveSpectrum && drag == null)
                {
                    SaveSpectrum(spectrum);
                    saveSpectrum = false;
                }
                if (fibersChanged) fiberSettings = ToSettings(fibers);
                if (spectrumChanged) spectrumSettings = ToSpectrum(spectrum);
                if (fibersChanged || spectrumChanged) observer.OnNext(new HsfpRoiState(fiberSettings, spectrumSettings));
            }, observer.OnError, observer.OnCompleted);

            return new CompositeDisposable(imageSub, frameSub, Disposable.Create(() =>
            {
                lock (imageLock)
                {
                    if (projection != null) projection.Dispose();
                    projection = null;
                }
            }));
        });
    }

    static List<Item> BuildItems(Tab tab, bool all, int current, IList<HsfpChannel> channels, List<FiberState> fibers, List<SpectrumState> spectrum, int viewChannel, bool editable)
    {
        var items = new List<Item>();
        var channelCount = Math.Max(1, channels.Count);
        for (int c = 0; c < channelCount && tab == Tab.Regions; c++)
        {
            if (!all && c != current) continue;
            var source = c < channels.Count ? channels[c] : null;
            var fill = source != null ? HsfpUi.ChannelColor(source.LightSource, source.Channel) : HsfpUi.Muted;
            for (int i = 0; i < fibers.Count; i++)
            {
                var fiber = fibers[i];
                var label = "Fiber " + fiber.FiberNumber;
                if (all && source != null) label += " - Ch " + source.Channel;
                items.Add(new Item
                {
                    Index = i,
                    ChannelIndex = c,
                    Rect = fiber.Regions[c],
                    Edge = fiber.Enabled ? HsfpUi.FiberColor(i) : HsfpUi.Muted,
                    Fill = fill,
                    Label = label,
                    Editable = editable,
                });
            }
        }
        if (tab == Tab.Spectrum)
        {
            for (int i = 0; i < spectrum.Count; i++)
            {
                var roi = spectrum[i];
                if (!all && roi.Channel != viewChannel) continue;
                var source = channels.FirstOrDefault(c => c.Channel == roi.Channel);
                items.Add(new Item
                {
                    IsSpectrum = true,
                    Index = i,
                    Rect = roi.Rect,
                    Edge = HsfpUi.SpectrumColor(roi.Id - 1),
                    Fill = source != null ? HsfpUi.ChannelColor(source.LightSource, source.Channel) : HsfpUi.Muted,
                    Label = all ? "S" + roi.Id + " - Ch " + roi.Channel : "S" + roi.Id,
                    Editable = editable,
                });
            }
        }
        return items;
    }

    /// <summary>Pixel-wise maximum of the images of the same size and depth as the first one.</summary>
    static IplImage MaxProjection(IEnumerable<IplImage> images, IplImage buffer)
    {
        IplImage first = null;
        foreach (var image in images)
        {
            if (first == null)
            {
                first = image;
                if (buffer == null || buffer.Width != image.Width || buffer.Height != image.Height || buffer.Depth != image.Depth || buffer.Channels != image.Channels)
                {
                    if (buffer != null) buffer.Dispose();
                    buffer = new IplImage(image.Size, image.Depth, image.Channels);
                }
                CV.Copy(image, buffer);
            }
            else if (image.Width == first.Width && image.Height == first.Height && image.Depth == first.Depth && image.Channels == first.Channels)
            {
                CV.Max(buffer, image, buffer);
            }
        }
        return buffer;
    }

    static bool InputRect(int[] rect, int imageWidth, int imageHeight, int column)
    {
        var changed = false;
        for (int k = 0; k < 4; k++)
        {
            ImGui.TableSetColumnIndex(column + k);
            ImGui.SetNextItemWidth(-1);
            var value = rect[k];
            if (ImGui.InputInt("##v" + k, ref value, 0, 0))
            {
                var limit = k == 0 || k == 2 ? imageWidth : imageHeight;
                rect[k] = k < 2 ? Clamp(value, 0, Math.Max(0, limit - MinSize)) : Clamp(value, MinSize, Math.Max(MinSize, limit - rect[k - 2]));
                changed = true;
            }
        }
        return changed;
    }

    static bool DrawFiberTable(AindPhysiologyHsfp.AindPhysioHsfpRig rig, List<FiberState> fibers, IList<HsfpChannel> channels, bool all, int current, bool editable, int imageWidth, int imageHeight, ref int selectedFiber, ref int selectedChannel)
    {
        var changed = false;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY;
        var columns = all ? 7 : 6;
        if (!ImGui.BeginTable("##HsfpRoiTable", columns, flags, ImGui.GetContentRegionAvail())) return false;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Fiber", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("Fiber 00", 70f));
        if (all) ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("Ch 0 - 000 nm", 90f));
        ImGui.TableSetupColumn("Recorded", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("Recorded", 70f));
        ImGui.TableSetupColumn("X (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Y (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Width (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Height (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();
        var channelCount = Math.Max(1, channels.Count);
        for (int i = 0; i < fibers.Count; i++)
        {
            var fiber = fibers[i];
            for (int c = 0; c < channelCount; c++)
            {
                if (!all && c != current) continue;
                ImGui.PushID(i * 16 + c);
                ImGui.TableNextRow();
                var selected = i == selectedFiber && c == selectedChannel;
                ImGui.TableSetColumnIndex(0);
                ImGui.PushStyleColor(ImGuiCol.Text, fiber.Enabled ? HsfpUi.FiberColor(i) : HsfpUi.Muted);
                if (ImGui.Selectable("Fiber " + fiber.FiberNumber + "##sel", selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                {
                    selectedFiber = i;
                    selectedChannel = c;
                }
                ImGui.PopStyleColor();
                var column = 1;
                if (all)
                {
                    ImGui.TableSetColumnIndex(column++);
                    var source = c < channels.Count ? channels[c] : null;
                    if (source != null) ImGui.TextColored(HsfpUi.ChannelColor(source.LightSource, source.Channel), HsfpChannels.Label(rig, source.Channel));
                }
                ImGui.TableSetColumnIndex(column++);
                ImGui.BeginDisabled(!editable);
                changed |= ImGui.Checkbox("##enabled", ref fiber.Enabled);
                changed |= InputRect(fiber.Regions[c], imageWidth, imageHeight, column);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
        ImGui.EndTable();
        return changed;
    }

    static bool DrawSpectrumTable(AindPhysiologyHsfp.AindPhysioHsfpRig rig, List<SpectrumState> spectrum, IList<HsfpChannel> channels, bool editable, int imageWidth, int imageHeight, ref int selected)
    {
        if (spectrum.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No spectrum ROIs. Drag on the image, or click + Add, to plot the spectrum of a region.");
            return false;
        }
        var changed = false;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##HsfpSpectrumTable", 7, flags, ImGui.GetContentRegionAvail())) return false;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("S00", 50f));
        ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("Ch 0 - 000 nm", 110f) + ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("X (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Y (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Width (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Height (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableHeadersRow();
        var remove = -1;
        for (int i = 0; i < spectrum.Count; i++)
        {
            var roi = spectrum[i];
            ImGui.PushID(roi.Id);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.PushStyleColor(ImGuiCol.Text, HsfpUi.SpectrumColor(roi.Id - 1));
            if (ImGui.Selectable("S" + roi.Id + "##sel", i == selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap)) selected = i;
            ImGui.PopStyleColor();
            ImGui.BeginDisabled(!editable);
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            var source = channels.FirstOrDefault(c => c.Channel == roi.Channel);
            ImGui.PushStyleColor(ImGuiCol.Text, source != null ? HsfpUi.ChannelColor(source.LightSource, source.Channel) : HsfpUi.Muted);
            if (ImGui.BeginCombo("##channel", HsfpChannels.Label(rig, roi.Channel)))
            {
                foreach (var item in channels)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, HsfpUi.ChannelColor(item.LightSource, item.Channel));
                    if (ImGui.Selectable(HsfpChannels.Label(rig, item.Channel), item.Channel == roi.Channel) && item.Channel != roi.Channel)
                    {
                        roi.Channel = item.Channel;
                        changed = true;
                    }
                    ImGui.PopStyleColor();
                }
                ImGui.EndCombo();
            }
            ImGui.PopStyleColor();
            ImGui.SetItemTooltip("Channel whose image this ROI's spectrum is read from.");
            changed |= InputRect(roi.Rect, imageWidth, imageHeight, 2);
            ImGui.TableSetColumnIndex(6);
            if (ImGui.Button("x##remove", new Vector2(ImGui.GetFrameHeight(), 0))) remove = i;
            ImGui.SetItemTooltip("Remove this spectrum ROI (or select it and press Delete).");
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (remove >= 0)
        {
            spectrum.RemoveAt(remove);
            if (selected == remove) selected = -1;
            else if (selected > remove) selected--;
            changed = true;
        }
        return changed;
    }

    /// <summary>Draws the confirmation popup. Returns the action confirmed this frame, or null.</summary>
    string DrawConfirm(List<FiberState> fibers, List<SpectrumState> spectrum, ref string pendingConfirm, ref string message, int channelCount, int width, int height)
    {
        string confirmed = null;
        if (pendingConfirm != null) ImGui.OpenPopup("Confirm##HsfpRoiConfirm");
        if (ImGui.BeginPopupModal("Confirm##HsfpRoiConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var action = pendingConfirm;
            var text = action == "Save" ? "Save the fiber ROIs to " + LocalRoiDefaultPath + "?"
                : action == "Load" ? "Replace the fiber ROIs with the ones saved in " + LocalRoiDefaultPath + "?"
                : action == "Clear" ? "Remove every spectrum ROI?"
                : "Reset the fiber ROIs to the defaults?";
            ImGui.TextUnformatted(text);
            var buttonWidth = HsfpDpi.Px(120f);
            var label = action == "Clear" ? "Remove" : action;
            if (ImGui.Button(label + "##HsfpRoiConfirmYes", new Vector2(buttonWidth, 0)))
            {
                string error;
                if (action == "Save")
                {
                    try
                    {
                        WriteJson(LocalRoiDefaultPath, ToSettings(fibers));
                    }
                    catch (Exception ex)
                    {
                        message = "Could not save the ROIs:\n" + ex.Message;
                    }
                }
                else if (action == "Load")
                {
                    if (!TryLoad(fibers, channelCount, out error)) message = "Could not load the ROIs:\n" + error;
                    confirmed = action;
                }
                else if (action == "Clear")
                {
                    spectrum.Clear();
                    confirmed = action;
                }
                else
                {
                    Seed(fibers, TaskLogic, channelCount, width, height);
                    confirmed = action;
                }
                pendingConfirm = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##HsfpRoiConfirmNo", new Vector2(buttonWidth, 0)))
            {
                pendingConfirm = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        return confirmed;
    }

    static void WriteJson(string path, object value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented));
    }

    static void Seed(List<FiberState> fibers, AindPhysiologyHsfp.AindPhysioHsfpTaskLogic taskLogic, int channelCount, int width, int height)
    {
        fibers.Clear();
        var measurements = taskLogic != null && taskLogic.TaskParameters != null && taskLogic.TaskParameters.Measurements != null
            ? taskLogic.TaskParameters.Measurements.OrderBy(m => m.PatchCord).ToList()
            : new List<AindPhysiologyHsfp.Measurement>();
        var count = Math.Max(1, measurements.Count);
        for (int i = 0; i < count; i++)
        {
            var band = height / (double)count;
            var h = Math.Max(MinSize, (int)(band * 0.6));
            var y = Clamp((int)(band * (i + 0.5) - h / 2.0), 0, Math.Max(0, height - h));
            var fiber = new FiberState
            {
                FiberNumber = i < measurements.Count ? measurements[i].FiberNumber : i,
                Enabled = i >= measurements.Count || measurements[i].Enabled,
            };
            for (int c = 0; c < channelCount; c++) fiber.Regions.Add(new[] { (int)(width * 0.05), y, Math.Max(MinSize, (int)(width * 0.9)), h });
            fibers.Add(fiber);
        }
    }

    bool TryLoad(List<FiberState> fibers, int channelCount, out string error)
    {
        error = null;
        AindPhysiologyHsfp.RoiSettings settings;
        try
        {
            settings = JsonConvert.DeserializeObject<AindPhysiologyHsfp.RoiSettings>(File.ReadAllText(LocalRoiDefaultPath));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        if (settings == null || settings.Rois == null)
        {
            error = "The file has no ROIs.";
            return false;
        }
        var missing = new List<int>();
        foreach (var fiber in fibers)
        {
            var saved = settings.Rois.FirstOrDefault(s => s.FiberNumber == fiber.FiberNumber);
            if (saved == null || saved.Regions == null || saved.Regions.Count == 0)
            {
                missing.Add(fiber.FiberNumber);
                continue;
            }
            fiber.Enabled = saved.Enabled;
            fiber.Regions.Clear();
            for (int c = 0; c < channelCount; c++)
            {
                var region = saved.Regions[Math.Min(c, saved.Regions.Count - 1)];
                fiber.Regions.Add(new[] { region.X, region.Y, region.Width, region.Height });
            }
        }
        if (missing.Count > 0)
        {
            error = "No saved ROI for fibers " + string.Join(", ", missing) + "; they keep the default ROI.";
            return false;
        }
        return true;
    }

    /// <summary>Loads the spectrum ROIs. Returns false if the file exists but cannot be used.</summary>
    bool TryLoadSpectrum(List<SpectrumState> spectrum, IList<HsfpChannel> channels, int width, int height)
    {
        spectrum.Clear();
        if (!File.Exists(LocalSpectrumRoiPath)) return true;
        try
        {
            var saved = JsonConvert.DeserializeObject<List<HsfpSpectrumRoi>>(File.ReadAllText(LocalSpectrumRoiPath));
            if (saved == null) return false;
            foreach (var roi in saved)
            {
                if (roi == null || roi.Id <= 0 || !channels.Any(c => c.Channel == roi.Channel)) return false;
                if (roi.Width < MinSize || roi.Height < MinSize || roi.X < 0 || roi.Y < 0 || roi.X + roi.Width > width || roi.Y + roi.Height > height) return false;
                if (spectrum.Any(s => s.Id == roi.Id)) return false;
                spectrum.Add(new SpectrumState { Id = roi.Id, Channel = roi.Channel, Rect = new[] { roi.X, roi.Y, roi.Width, roi.Height } });
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    void SaveSpectrum(List<SpectrumState> spectrum)
    {
        try
        {
            WriteJson(LocalSpectrumRoiPath, ToSpectrum(spectrum).Rois);
        }
        catch (Exception)
        {
            // Spectrum ROIs are a convenience; failing to save them must not interrupt the acquisition.
        }
    }

    static HsfpSpectrumRois ToSpectrum(List<SpectrumState> spectrum)
    {
        return new HsfpSpectrumRois(spectrum.Select(s => new HsfpSpectrumRoi
        {
            Id = s.Id,
            Channel = s.Channel,
            X = s.Rect[0],
            Y = s.Rect[1],
            Width = s.Rect[2],
            Height = s.Rect[3],
        }).ToList());
    }

    static AindPhysiologyHsfp.RoiSettings ToSettings(List<FiberState> fibers)
    {
        var settings = new AindPhysiologyHsfp.RoiSettings();
        settings.Rois = fibers.Select(fiber =>
        {
            var roi = new AindPhysiologyHsfp.FiberRoi();
            roi.FiberNumber = fiber.FiberNumber;
            roi.Enabled = fiber.Enabled;
            roi.Regions = fiber.Regions.Select(r =>
            {
                var region = new AindPhysiologyHsfp.CameraRegion();
                region.X = r[0];
                region.Y = r[1];
                region.Width = r[2];
                region.Height = r[3];
                return region;
            }).ToList();
            return roi;
        }).ToList();
        return settings;
    }

    static int Clamp(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }
}
