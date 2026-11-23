using System;
using System.Collections.Generic;
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

/// <summary>
/// Represents an operator that lets the user place the region of interest of each fiber on the
/// live camera image of the selected channel, and persists the ROIs to disk.
/// </summary>
/// <remarks>
/// One ROI per fiber of the task logic, with one rectangle per channel (lasers of the rig), since
/// the emission of each laser lands on a different part of the sensor. With "Link channels" on,
/// moving or resizing a ROI applies the same change to every channel. ROIs can be edited only while
/// <see cref="Enabled"/> is true (map it from the negated experiment state). At startup the ROIs
/// are loaded from <see cref="LocalRoiDefaultPath"/> when it exists. Emits the current
/// <see cref="AindPhysiologyHsfp.RoiSettings"/> at startup and whenever it changes.
/// Source1 = Frame subject; Source2 = HsfpFrame stream (live preview images).
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the live image of the selected channel with one draggable/resizable ROI per fiber, an ROI table and Save/Load/Reset, and emits the current RoiSettings when it changes. Source1 = Frame subject; Source2 = HsfpFrame stream.")]
public class HsfpRoiManagerVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private volatile bool enabled = true;
    [Description("Specifies whether the ROIs can be edited. Map it from the negated experiment state.")]
    public bool Enabled { get { return enabled; } set { enabled = value; } }

    private volatile int channel;
    [Description("The channel whose image and regions are shown. Map it from the channel selector.")]
    public int Channel { get { return channel; } set { channel = value; } }

    private string localRoiDefaultPath = "../local/hsfp_roi.json";
    [Description("File used by Save/Load to persist the ROIs. If it exists, the ROIs are loaded from it at startup.")]
    [FileNameFilter("JSON|*.json|All Files|*.*")]
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    public string LocalRoiDefaultPath { get { return localRoiDefaultPath; } set { localRoiDefaultPath = value; } }

    [XmlIgnore]
    [Description("The rig: the lasers define the channels and the camera region the image size. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    [XmlIgnore]
    [Description("The task logic: one ROI is created per measurement. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpTaskLogic TaskLogic { get; set; }

    const float HandleSize = 8f;
    const float MinSize = 4f;

    class RoiState
    {
        public int FiberNumber;
        public bool Enabled = true;
        public List<int[]> Regions = new List<int[]>(); // x, y, width, height per channel
    }

    enum DragMode { None, Move, Resize }

    public IObservable<AindPhysiologyHsfp.RoiSettings> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpFrame> images)
    {
        return Observable.Create<AindPhysiologyHsfp.RoiSettings>(observer =>
        {
            var rois = new List<RoiState>();
            var seeded = false;
            var linkChannels = true;
            var texture = new HsfpTexture();
            var textureChannel = -1;
            var imageLock = new object();
            var latestImages = new Dictionary<int, IplImage>();
            var dirty = new HashSet<int>();
            var imageSize = new Vector2(0, 0);
            string message = null;
            string pendingConfirm = null;
            var dragIndex = -1;
            var dragMode = DragMode.None;
            var dragStart = new Vector2();
            var dragOrigin = new List<int[]>();

            var imageSub = images.Subscribe(frame =>
            {
                lock (imageLock)
                {
                    latestImages[frame.Channel] = frame.Image;
                    dirty.Add(frame.Channel);
                }
            }, observer.OnError);

            var frameSub = frames.Subscribe(_ =>
            {
                HsfpStyle.Apply();
                var rig = Rig;
                if (!Visible) return;
                if (rig == null || rig.Lasers == null || rig.Camera == null)
                {
                    ImGui.TextDisabled("Waiting for the rig configuration.");
                    return;
                }
                var channelCount = Math.Max(1, rig.Lasers.Count);
                var current = Math.Max(0, Math.Min(Channel, channelCount - 1));
                var width = rig.Camera.Region != null && rig.Camera.Region.Width > 0 ? rig.Camera.Region.Width : 1;
                var height = rig.Camera.Region != null && rig.Camera.Region.Height > 0 ? rig.Camera.Region.Height : 1;
                var changed = false;

                if (!seeded)
                {
                    Seed(rois, TaskLogic, channelCount, width, height);
                    string error;
                    if (File.Exists(LocalRoiDefaultPath) && !TryLoad(rois, channelCount, out error))
                    {
                        message = "Could not load the saved ROIs:\n" + error + "\nUsing the default ROIs.";
                    }
                    seeded = true;
                    changed = true;
                }

                // Live image of the selected channel.
                IplImage pending = null;
                lock (imageLock)
                {
                    IplImage image;
                    if ((dirty.Contains(current) || textureChannel != current) && latestImages.TryGetValue(current, out image))
                    {
                        pending = image;
                        dirty.Remove(current);
                    }
                }
                if (pending != null)
                {
                    texture.Update(pending);
                    textureChannel = current;
                    imageSize = new Vector2(pending.Width, pending.Height);
                }
                if (imageSize.X <= 0) imageSize = new Vector2(width, height);

                var editable = Enabled;
                DrawToolbar(rois, editable, ref linkChannels, ref pendingConfirm, channelCount, width, height);

                // Image with ROI overlays, scaled to the available width.
                var avail = ImGui.GetContentRegionAvail();
                var tableHeight = Math.Min(avail.Y * 0.4f, ImGui.GetFrameHeightWithSpacing() * (rois.Count + 2.5f));
                var maxImageHeight = Math.Max(HsfpDpi.Px(60f), avail.Y - tableHeight - HsfpDpi.ItemSpacing.Y);
                var scale = Math.Min(avail.X / imageSize.X, maxImageHeight / imageSize.Y);
                var displaySize = new Vector2(imageSize.X * scale, imageSize.Y * scale);
                var origin = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();
                if (texture.IsValid && textureChannel == current)
                {
                    ImGui.Image(texture.TexRef, displaySize);
                }
                else
                {
                    ImGui.Dummy(displaySize);
                    drawList.AddRectFilled(origin, origin + displaySize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.02f, 0.02f, 0.03f, 1f)));
                    var text = "No frames for this channel yet";
                    drawList.AddText(origin + (displaySize - ImGui.CalcTextSize(text)) * 0.5f, ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
                }
                var imageHovered = ImGui.IsItemHovered();

                // Hit-test and draw the ROIs.
                var mouse = ImGui.GetMousePos();
                var hoverIndex = -1;
                var hoverMode = DragMode.None;
                for (int i = 0; i < rois.Count; i++)
                {
                    var r = rois[i].Regions[current];
                    var min = origin + new Vector2(r[0], r[1]) * scale;
                    var max = origin + new Vector2(r[0] + r[2], r[1] + r[3]) * scale;
                    var handle = HsfpDpi.Px(HandleSize);
                    if (imageHovered && mouse.X >= max.X - handle && mouse.X <= max.X + handle * 0.5f && mouse.Y >= max.Y - handle && mouse.Y <= max.Y + handle * 0.5f)
                    {
                        hoverIndex = i; hoverMode = DragMode.Resize;
                    }
                    else if (imageHovered && hoverMode != DragMode.Resize && mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
                    {
                        hoverIndex = i; hoverMode = DragMode.Move;
                    }
                }

                if (editable && dragMode == DragMode.None && hoverIndex >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    dragIndex = hoverIndex;
                    dragMode = hoverMode;
                    dragStart = mouse;
                    dragOrigin = rois[dragIndex].Regions.Select(r => (int[])r.Clone()).ToList();
                }
                if (dragMode != DragMode.None)
                {
                    if (!editable || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        dragMode = DragMode.None;
                        dragIndex = -1;
                    }
                    else
                    {
                        var delta = (mouse - dragStart) / scale;
                        var dx = (int)Math.Round(delta.X);
                        var dy = (int)Math.Round(delta.Y);
                        for (int c = 0; c < channelCount; c++)
                        {
                            if (!linkChannels && c != current) continue;
                            var o = dragOrigin[c];
                            var r = rois[dragIndex].Regions[c];
                            if (dragMode == DragMode.Move)
                            {
                                r[0] = Clamp(o[0] + dx, 0, (int)imageSize.X - o[2]);
                                r[1] = Clamp(o[1] + dy, 0, (int)imageSize.Y - o[3]);
                            }
                            else
                            {
                                r[2] = Clamp(o[2] + dx, (int)MinSize, (int)imageSize.X - o[0]);
                                r[3] = Clamp(o[3] + dy, (int)MinSize, (int)imageSize.Y - o[1]);
                            }
                        }
                        changed = true;
                    }
                }
                if (editable && (hoverMode == DragMode.Resize || dragMode == DragMode.Resize)) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);
                else if (editable && (hoverMode == DragMode.Move || dragMode == DragMode.Move)) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

                drawList.PushClipRect(origin, origin + displaySize, true);
                for (int i = 0; i < rois.Count; i++)
                {
                    var roi = rois[i];
                    var r = roi.Regions[current];
                    var min = origin + new Vector2(r[0], r[1]) * scale;
                    var max = origin + new Vector2(r[0] + r[2], r[1] + r[3]) * scale;
                    var color = roi.Enabled ? HsfpUi.FiberColor(i) : HsfpUi.Muted;
                    var active = i == dragIndex || i == hoverIndex;
                    drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, active ? 0.22f : 0.10f)));
                    drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(color), 0f, ImDrawFlags.None, HsfpDpi.Px(active ? 2.5f : 1.5f));
                    if (editable)
                    {
                        var handle = HsfpDpi.Px(HandleSize);
                        drawList.AddRectFilled(max - new Vector2(handle, handle), max, ImGui.ColorConvertFloat4ToU32(color));
                    }
                    drawList.AddText(min + new Vector2(HsfpDpi.Px(3f), HsfpDpi.Px(1f)), ImGui.ColorConvertFloat4ToU32(color), "Fiber " + roi.FiberNumber);
                }
                drawList.PopClipRect();

                changed |= DrawTable(rois, current, channelCount, editable, linkChannels, (int)imageSize.X, (int)imageSize.Y);
                changed |= DrawConfirm(rois, ref pendingConfirm, ref message, channelCount, width, height);

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

                if (changed) observer.OnNext(ToSettings(rois));
            }, observer.OnError, observer.OnCompleted);

            return new CompositeDisposable(imageSub, frameSub);
        });
    }

    void DrawToolbar(List<RoiState> rois, bool editable, ref bool linkChannels, ref string pendingConfirm, int channelCount, int width, int height)
    {
        HsfpUi.SectionHeader(editable ? "Regions of interest" : "Regions of interest (locked while recording)");
        ImGui.BeginDisabled(!editable);
        if (ImGui.Button("Save##HsfpRoi")) pendingConfirm = "Save";
        ImGui.SameLine();
        ImGui.BeginDisabled(!File.Exists(LocalRoiDefaultPath));
        if (ImGui.Button("Load##HsfpRoi")) pendingConfirm = "Load";
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Reset##HsfpRoi")) pendingConfirm = "Reset";
        ImGui.SameLine();
        ImGui.Checkbox("Link channels##HsfpRoi", ref linkChannels);
        ImGui.SetItemTooltip("Moving or resizing a ROI applies the same change to every channel.");
        ImGui.EndDisabled();
    }

    bool DrawConfirm(List<RoiState> rois, ref string pendingConfirm, ref string message, int channelCount, int width, int height)
    {
        var changed = false;
        if (pendingConfirm != null) ImGui.OpenPopup("Confirm##HsfpRoiConfirm");
        if (ImGui.BeginPopupModal("Confirm##HsfpRoiConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var action = pendingConfirm;
            var text = action == "Save" ? "Save the ROIs to " + LocalRoiDefaultPath + "?"
                : action == "Load" ? "Replace the ROIs with the ones saved in " + LocalRoiDefaultPath + "?"
                : "Reset the ROIs to the defaults?";
            ImGui.TextUnformatted(text);
            var buttonWidth = HsfpDpi.Px(120f);
            if (ImGui.Button(action + "##HsfpRoiConfirmYes", new Vector2(buttonWidth, 0)))
            {
                string error;
                if (action == "Save")
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(Path.GetFullPath(LocalRoiDefaultPath));
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        File.WriteAllText(LocalRoiDefaultPath, JsonConvert.SerializeObject(ToSettings(rois), Formatting.Indented));
                    }
                    catch (Exception ex)
                    {
                        message = "Could not save the ROIs:\n" + ex.Message;
                    }
                }
                else if (action == "Load")
                {
                    if (!TryLoad(rois, channelCount, out error)) message = "Could not load the ROIs:\n" + error;
                    changed = true;
                }
                else
                {
                    rois.Clear();
                    Seed(rois, TaskLogic, channelCount, width, height);
                    changed = true;
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
        return changed;
    }

    static bool DrawTable(List<RoiState> rois, int current, int channelCount, bool editable, bool linkChannels, int imageWidth, int imageHeight)
    {
        var changed = false;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##HsfpRoiTable", 6, flags, ImGui.GetContentRegionAvail())) return false;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Fiber", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("Fiber 00", 70f));
        ImGui.TableSetupColumn("Recorded", ImGuiTableColumnFlags.WidthFixed, HsfpDpi.FitWidth("Recorded", 70f));
        ImGui.TableSetupColumn("X (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Y (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Width (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Height (px)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();
        ImGui.BeginDisabled(!editable);
        for (int i = 0; i < rois.Count; i++)
        {
            var roi = rois[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(roi.Enabled ? HsfpUi.FiberColor(i) : HsfpUi.Muted, "Fiber " + roi.FiberNumber);
            ImGui.TableSetColumnIndex(1);
            changed |= ImGui.Checkbox("##enabled", ref roi.Enabled);
            var region = roi.Regions[current];
            for (int k = 0; k < 4; k++)
            {
                ImGui.TableSetColumnIndex(2 + k);
                ImGui.SetNextItemWidth(-1);
                var value = region[k];
                if (ImGui.InputInt("##v" + k, ref value, 0, 0))
                {
                    var limit = k == 0 || k == 2 ? imageWidth : imageHeight;
                    value = k < 2 ? Clamp(value, 0, limit - 1) : Clamp(value, (int)MinSize, limit);
                    var delta = value - region[k];
                    for (int c = 0; c < channelCount; c++)
                    {
                        if (!linkChannels && c != current) continue;
                        roi.Regions[c][k] = c == current ? value : Clamp(roi.Regions[c][k] + delta, k < 2 ? 0 : (int)MinSize, limit);
                    }
                    changed = true;
                }
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        ImGui.EndTable();
        return changed;
    }

    static void Seed(List<RoiState> rois, AindPhysiologyHsfp.AindPhysioHsfpTaskLogic taskLogic, int channelCount, int width, int height)
    {
        rois.Clear();
        var measurements = taskLogic != null && taskLogic.TaskParameters != null && taskLogic.TaskParameters.Measurements != null
            ? taskLogic.TaskParameters.Measurements.OrderBy(m => m.PatchCord).ToList()
            : new List<AindPhysiologyHsfp.Measurement>();
        var count = Math.Max(1, measurements.Count);
        for (int i = 0; i < count; i++)
        {
            var band = height / (double)count;
            var h = Math.Max((int)MinSize, (int)(band * 0.6));
            var y = Clamp((int)(band * (i + 0.5) - h / 2.0), 0, Math.Max(0, height - h));
            var roi = new RoiState
            {
                FiberNumber = i < measurements.Count ? measurements[i].FiberNumber : i,
                Enabled = i >= measurements.Count || measurements[i].Enabled,
            };
            for (int c = 0; c < channelCount; c++) roi.Regions.Add(new[] { (int)(width * 0.05), y, Math.Max((int)MinSize, (int)(width * 0.9)), h });
            rois.Add(roi);
        }
    }

    bool TryLoad(List<RoiState> rois, int channelCount, out string error)
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
        foreach (var roi in rois)
        {
            var saved = settings.Rois.FirstOrDefault(s => s.FiberNumber == roi.FiberNumber);
            if (saved == null || saved.Regions == null || saved.Regions.Count == 0)
            {
                missing.Add(roi.FiberNumber);
                continue;
            }
            roi.Enabled = saved.Enabled;
            roi.Regions.Clear();
            for (int c = 0; c < channelCount; c++)
            {
                var region = saved.Regions[Math.Min(c, saved.Regions.Count - 1)];
                roi.Regions.Add(new[] { region.X, region.Y, region.Width, region.Height });
            }
        }
        if (missing.Count > 0)
        {
            error = "No saved ROI for fibers " + string.Join(", ", missing) + "; they keep the default ROI.";
            return false;
        }
        return true;
    }

    static AindPhysiologyHsfp.RoiSettings ToSettings(List<RoiState> rois)
    {
        var settings = new AindPhysiologyHsfp.RoiSettings();
        settings.Rois = rois.Select(roi =>
        {
            var fiber = new AindPhysiologyHsfp.FiberRoi();
            fiber.FiberNumber = roi.FiberNumber;
            fiber.Enabled = roi.Enabled;
            fiber.Regions = roi.Regions.Select(r =>
            {
                var region = new AindPhysiologyHsfp.CameraRegion();
                region.X = r[0];
                region.Y = r[1];
                region.Width = r[2];
                region.Height = r[3];
                return region;
            }).ToList();
            return fiber;
        }).ToList();
        return settings;
    }

    static int Clamp(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }
}
