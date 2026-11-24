using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Hexa.NET.ImGui;

/// <summary>What a <see cref="HsfpLauncherCommand"/> asks for.</summary>
public enum HsfpLauncherCommandKind
{
    StartExperiment,
    EndExperiment,
    Annotation,
    /// <summary>A hardware check failed and the user chose to start anyway; the text describes the check.</summary>
    HardwareQA,
    /// <summary>Stop the cuTTLefish trigger sequence (camera and light sources), outside a session.</summary>
    PauseAcquisition,
    /// <summary>Restart the cuTTLefish trigger sequence, outside a session.</summary>
    ResumeAcquisition,
}

/// <summary>A button press or a sent note from <see cref="HsfpLauncherVisualizer"/>.</summary>
public class HsfpLauncherCommand
{
    public HsfpLauncherCommand(HsfpLauncherCommandKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public HsfpLauncherCommandKind Kind { get; private set; }

    /// <summary>The note, or the failed check for <see cref="HsfpLauncherCommandKind.HardwareQA"/>; null otherwise.</summary>
    public string Text { get; private set; }

    public override string ToString()
    {
        return Text == null ? Kind.ToString() : Kind + ": " + Text;
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the acquisition panel: Start / Stop button, session details and a note box. Asks for confirmation before starting if the cuTTLefish is not synchronized with the Harp clock. Emits a HsfpLauncherCommand per press or sent note (split with HsfpLauncherCommandOfKind). Source1 = Frame subject.")]
public class HsfpLauncherVisualizer
{
    const int MaxNoteLength = 4096;

    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private volatile bool recording;
    [Description("Whether the acquisition is running. Map it from the experiment state.")]
    public bool Recording { get { return recording; } set { recording = value; } }

    [XmlIgnore]
    [Description("The session. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.Session Session { get; set; }

    [XmlIgnore]
    [Description("The rig. Set via PropertyMapping.")]
    public AindPhysiologyHsfp.AindPhysioHsfpRig Rig { get; set; }

    [Description("The folder the acquisition is saved to. Set via PropertyMapping once logging starts.")]
    public string EpochPath { get; set; }

    readonly HsfpSyncStatus sync = new HsfpSyncStatus();

    [XmlIgnore]
    [Description("IS_SYNCHRONIZED bit of the cuTTLefish heartbeat (R_HEARTBEAT). Starting asks for confirmation unless it is set and still arriving. Set via PropertyMapping on each heartbeat.")]
    public bool IsSynchronized
    {
        get { return sync.LastValue; }
        set { sync.Update(value); }
    }

    const string SyncPopup = "Harp clock not synchronized##HsfpSyncCheck";

    class SentNote
    {
        public DateTime Time;
        public string Text;
    }

    public IObservable<HsfpLauncherCommand> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<HsfpLauncherCommand>(observer =>
        {
            var note = string.Empty;
            var history = new List<SentNote>();
            var refocus = false;
            var started = false;
            var stopped = false;

            return frames.Subscribe(_ =>
            {
                HsfpStyle.Apply();
                if (!Visible) return;

                var avail = ImGui.GetContentRegionAvail();
                var buttonSize = new Vector2(avail.X, Math.Max(ImGui.GetFrameHeight() * 1.8f, HsfpDpi.Px(44f)));
                if (!Recording && !started)
                {
                    HsfpUi.PushButtonColor(HsfpUi.StartColor);
                    if (ImGui.Button("Start experiment##HsfpStart", buttonSize))
                    {
                        var session = Session;
                        var skip = session != null && session.SkipHardwareValidation;
                        if (skip || sync.IsSynchronized)
                        {
                            started = true;
                            observer.OnNext(new HsfpLauncherCommand(HsfpLauncherCommandKind.StartExperiment, null));
                        }
                        else
                        {
                            ImGui.OpenPopup(SyncPopup);
                        }
                    }
                    HsfpUi.PopButtonColor();
                    string overridden;
                    if (DrawSyncConfirm(out overridden))
                    {
                        started = true;
                        if (overridden != null)
                        {
                            observer.OnNext(new HsfpLauncherCommand(HsfpLauncherCommandKind.HardwareQA, "Started without Harp clock synchronization: " + overridden));
                        }
                        observer.OnNext(new HsfpLauncherCommand(HsfpLauncherCommandKind.StartExperiment, null));
                    }
                }
                else
                {
                    ImGui.BeginDisabled(stopped);
                    HsfpUi.PushButtonColor(HsfpUi.StopColor);
                    if (ImGui.Button((stopped ? "Stopping..." : "Stop experiment") + "##HsfpStop", buttonSize))
                    {
                        stopped = true;
                        observer.OnNext(new HsfpLauncherCommand(HsfpLauncherCommandKind.EndExperiment, null));
                    }
                    HsfpUi.PopButtonColor();
                    ImGui.EndDisabled();
                }

                if (ImGui.BeginTable("##HsfpSession", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    var session = Session;
                    var rig = Rig;
                    Row("Subject", session != null ? session.Subject : "-");
                    Row("Session", session != null ? session.SessionName : "-");
                    Row("Experimenter", session != null && session.Experimenter != null ? string.Join(", ", session.Experimenter) : "-");
                    Row("Rig", rig != null ? rig.RigName : "-");
                    Row("Saving to", string.IsNullOrEmpty(EpochPath) ? "(created on Start)" : EpochPath);
                    ImGui.EndTable();
                }

                ImGui.Separator();
                HsfpUi.SectionHeader("Notes");
                if (refocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    refocus = false;
                }
                var sendWidth = HsfpDpi.FitWidth("Send", 60f);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - sendWidth - HsfpDpi.ItemSpacing.X);
                var send = ImGui.InputTextWithHint("##HsfpNote", "Enter a note...", ref note, new UIntPtr((uint)MaxNoteLength), ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                send |= ImGui.Button("Send##HsfpSend", new Vector2(sendWidth, 0));
                if (send && note.Trim().Length > 0)
                {
                    observer.OnNext(new HsfpLauncherCommand(HsfpLauncherCommandKind.Annotation, note));
                    history.Insert(0, new SentNote { Time = DateTime.Now, Text = note });
                    note = string.Empty;
                    ImGuiP.ClearActiveID();
                    refocus = true;
                }
                ImGui.BeginChild("##HsfpNotes", new Vector2(0, 0), ImGuiChildFlags.None);
                if (history.Count == 0) ImGui.TextDisabled("Notes are saved as Annotations software events.");
                foreach (var sent in history)
                {
                    ImGui.TextColored(HsfpUi.Muted, sent.Time.ToString("HH:mm:ss"));
                    ImGui.SameLine();
                    ImGui.TextWrapped(sent.Text);
                }
                ImGui.EndChild();
            },
            observer.OnError,
            observer.OnCompleted);
        });
    }

    /// <summary>
    /// Modal asking whether to start without clock synchronization. Returns true if the user starts; then
    /// <paramref name="overridden"/> describes the failed check, or is null if the clock recovered meanwhile.
    /// </summary>
    bool DrawSyncConfirm(out string overridden)
    {
        overridden = null;
        var started = false;
        // Centered on the panel. (ImGuiViewportPtr.Pos/Size are ref properties, which the Bonsai script compiler rejects.)
        var center = ImGui.GetWindowPos() + ImGui.GetWindowSize() * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal(SyncPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // The status may recover while the dialog is open.
            var synced = sync.IsSynchronized;
            var status = sync.Describe();
            ImGui.TextColored(synced ? HsfpUi.Good : HsfpUi.Bad, status);
            ImGui.Spacing();
            ImGui.TextUnformatted(synced
                ? "The clock is now synchronized. Start the experiment?"
                : "Timestamps may not align with other Harp devices.\nStart the experiment anyway? This is saved as a HardwareQA event.");
            ImGui.Spacing();
            var width = HsfpDpi.FitWidth("Start anyway", 120f);
            HsfpUi.PushButtonColor(synced ? HsfpUi.StartColor : HsfpUi.StopColor);
            if (ImGui.Button((synced ? "Start" : "Start anyway") + "##HsfpSyncStart", new Vector2(width, 0)))
            {
                started = true;
                overridden = synced ? null : status;
                ImGui.CloseCurrentPopup();
            }
            HsfpUi.PopButtonColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel##HsfpSyncCancel", new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        return started;
    }

    static void Row(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(HsfpUi.Muted, label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextWrapped(value ?? "-");
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Keeps only the HsfpLauncherCommand values of the given kind.")]
public class HsfpLauncherCommandOfKind
{
    [Description("The kind of command to keep.")]
    public HsfpLauncherCommandKind Kind { get; set; }

    public IObservable<HsfpLauncherCommand> Process(IObservable<HsfpLauncherCommand> source)
    {
        return source.Where(command => command.Kind == Kind);
    }
}
