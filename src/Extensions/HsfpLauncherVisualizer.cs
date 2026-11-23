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

    /// <summary>The note, for <see cref="HsfpLauncherCommandKind.Annotation"/>; null otherwise.</summary>
    public string Text { get; private set; }

    public override string ToString()
    {
        return Text == null ? Kind.ToString() : Kind + ": " + Text;
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the acquisition panel: Start / Stop button, session details and a note box. Emits a HsfpLauncherCommand per press or sent note (split with HsfpLauncherCommandOfKind). Source1 = Frame subject.")]
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
                        started = true;
                        observer.OnNext(new HsfpLauncherCommand(HsfpLauncherCommandKind.StartExperiment, null));
                    }
                    HsfpUi.PopButtonColor();
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
