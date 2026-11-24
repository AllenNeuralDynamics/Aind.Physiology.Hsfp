using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Bonsai;
using BFC.ObisLaser;
using BFC.ObisLaser.Design;
using Hexa.NET.ImGui;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using ObisLaser.Net;

/// <summary>A light source of the rig with the OBIS laser found for it.</summary>
public class HsfpLaser
{
    public HsfpLaser(int channel, string serialNumber, string model, string portName, int laserChannel, double wavelength)
    {
        Channel = channel;
        SerialNumber = serialNumber;
        Model = model;
        PortName = portName;
        LaserChannel = laserChannel;
        Wavelength = wavelength;
    }

    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; private set; }

    [Description("The serial number of the laser.")]
    public string SerialNumber { get; private set; }

    [Description("The model the laser reports.")]
    public string Model { get; private set; }

    [Description("The serial port of the laser or of the controller it is connected to.")]
    public string PortName { get; private set; }

    [Description("The channel of the laser on its port: 0 if connected directly, 1 or more behind a controller.")]
    public int LaserChannel { get; private set; }

    [Description("The nominal wavelength the laser reports (nm).")]
    public double Wavelength { get; private set; }

    public override string ToString()
    {
        return string.Format("Ch {0}: {1} S/N {2} on {3} channel {4} ({5:F0} nm)", Channel, Model, SerialNumber, PortName, LaserChannel, Wavelength);
    }
}

/// <summary>A command addressed to the laser of one light source.</summary>
public class HsfpLaserCommand
{
    public HsfpLaserCommand(int channel, ObisCommand command)
        : this(channel, command, false)
    {
    }

    public HsfpLaserCommand(int channel, ObisCommand command, bool readout)
    {
        Channel = channel;
        Command = command;
        Readout = readout;
    }

    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; private set; }

    public ObisCommand Command { get; private set; }

    [Description("Whether the command is part of a readout of the lasers, logged in the Lasers software event. A readout command may also address the laser box of the laser (channel 0), and its reply is not shown in the laser tabs.")]
    public bool Readout { get; private set; }

    public override string ToString()
    {
        return string.Format("Ch {0}: {1}{2}", Channel, Command, Readout ? " (readout)" : string.Empty);
    }
}

/// <summary>A reply of the laser of one light source.</summary>
public class HsfpLaserReply
{
    public HsfpLaserReply(int channel, ObisReply reply)
        : this(channel, reply, false)
    {
    }

    public HsfpLaserReply(int channel, ObisReply reply, bool readout)
    {
        Channel = channel;
        Reply = reply;
        Readout = readout;
    }

    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; private set; }

    public ObisReply Reply { get; private set; }

    [Description("Whether the reply is to a readout command.")]
    public bool Readout { get; private set; }

    public override string ToString()
    {
        return string.Format("Ch {0}: {1}", Channel, Reply);
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Finds the OBIS laser of each light source of the rig by its serial number, and reads its model and wavelength. Raises an error naming every laser that is missing.")]
public class FindHsfpLasers
{
    public IObservable<HsfpLaser[]> Process(IObservable<AindPhysiologyHsfp.AindPhysioHsfpRig> source)
    {
        return source.SelectMany(rig => Observable.Start(() => Find(rig)));
    }

    /// <summary>
    /// Probes only the ports of OBIS devices connected over USB (never the cuTTLefish port), so that no other serial
    /// device is written to.
    /// </summary>
    internal static HsfpLaser[] Find(AindPhysiologyHsfp.AindPhysioHsfpRig rig)
    {
        var wanted = HsfpChannels.Get(rig).ToList();
        if (wanted.Count == 0)
        {
            HsfpChannels.SetLasers(new HsfpLaser[0]);
            return new HsfpLaser[0];
        }

        var excluded = rig.Cuttlefish != null && !string.IsNullOrEmpty(rig.Cuttlefish.PortName) ? new[] { rig.Cuttlefish.PortName } : new string[0];
        var ports = ObisDiscovery.GetCandidatePortNames().Where(port => !excluded.Contains(port, StringComparer.OrdinalIgnoreCase)).ToList();
        var found = ports.Count > 0
            ? ObisDiscovery.FindAll(ports, excluded).Select(d => new FoundDevice(d)).Where(d => d.DeviceType == DeviceType.Ddl || d.DeviceType == DeviceType.Opsl).ToList()
            : new List<FoundDevice>();

        var errors = new List<string>();
        var matches = new List<Tuple<HsfpChannel, FoundDevice>>();
        foreach (var channel in wanted)
        {
            var serial = (channel.LightSource.SerialNumber ?? string.Empty).Trim();
            if (serial.Length == 0)
            {
                errors.Add(string.Format("Channel {0}: the light source has no serial number.", channel.Channel));
                continue;
            }
            var candidates = found.Where(d => string.Equals((d.SerialNumber ?? string.Empty).Trim(), serial, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) errors.Add(string.Format("{0}: no laser with this serial number was found.", Describe(channel)));
            else if (candidates.Count > 1) errors.Add(string.Format("{0}: several lasers report this serial number.", Describe(channel)));
            else matches.Add(Tuple.Create(channel, candidates[0]));
        }

        var lasers = new List<HsfpLaser>();
        foreach (var port in matches.GroupBy(m => m.Item2.PortName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using (var connection = ObisConnection.Open(port.Key, ObisConnection.DefaultTimeout))
                {
                    foreach (var match in port)
                    {
                        var device = match.Item2;
                        var wavelength = connection.Execute(ObisProtocol.GetWavelength(), device.Channel);
                        lasers.Add(new HsfpLaser(match.Item1.Channel, device.SerialNumber, device.Model, device.PortName, device.Channel, wavelength));
                    }
                }
            }
            catch (Exception error)
            {
                if (!(error is ObisException || error is System.IO.IOException || error is UnauthorizedAccessException || error is TimeoutException)) throw;
                errors.Add(string.Format("Could not read the wavelength of the lasers on {0}: {1}", port.Key, error.Message));
            }
        }

        if (errors.Count > 0)
        {
            var available = found.Count == 0
                ? (ports.Count == 0 ? "No OBIS device is connected over USB." : "No OBIS laser answered on " + string.Join(", ", ports) + ".")
                : "Lasers found: " + string.Join("; ", found.Select(d => string.Format("S/N {0} ({1}) on {2} channel {3}", d.SerialNumber, d.Model, d.PortName, d.Channel))) + ".";
            throw new InvalidOperationException("OBIS lasers do not match the rig. " + string.Join(" ", errors) + " " + available);
        }

        var result = lasers.OrderBy(l => l.Channel).ToArray();
        HsfpChannels.SetLasers(result);
        return result;
    }

    /// <summary>
    /// The properties of a <see cref="DiscoveredDevice"/>. It is a record with init-only setters, whose properties
    /// Bonsai's script compiler cannot read directly, so they are read by reflection.
    /// </summary>
    internal class FoundDevice
    {
        public FoundDevice(DiscoveredDevice device)
        {
            var type = device.GetType();
            PortName = (string)type.GetProperty("PortName").GetValue(device, null);
            Channel = (int)type.GetProperty("Channel").GetValue(device, null);
            DeviceType = (DeviceType)type.GetProperty("DeviceType").GetValue(device, null);
            Model = (string)type.GetProperty("Model").GetValue(device, null);
            SerialNumber = (string)type.GetProperty("SerialNumber").GetValue(device, null);
        }

        public string PortName { get; private set; }

        public int Channel { get; private set; }

        public DeviceType DeviceType { get; private set; }

        public string Model { get; private set; }

        public string SerialNumber { get; private set; }
    }

    static string Describe(HsfpChannel channel)
    {
        return string.Format("Channel {0} (S/N {1})", channel.Channel, channel.LightSource.SerialNumber);
    }
}

/// <summary>The read-only queries of a laser readout.</summary>
static class HsfpLaserQueries
{
    /// <summary>
    /// The queries asked of each laser, as typed commands, created anew for each readout. GetErrors is left out
    /// because reading an error removes it from the laser's queue, and SelfTest because it runs a self-test.
    /// </summary>
    public static IEnumerable<CommandPayload> Laser()
    {
        yield return new Identify();
        yield return new GetInfo();
        yield return new GetState();
        for (int index = 0; index < 4; index++) yield return new GetUserInfo { Index = index };
        yield return new GetFieldCalibrationDate();
        yield return new GetDiodeTemperature();
        yield return new GetDiodeTemperatureSetpoint();
        yield return new GetInternalTemperature();
        yield return new GetBaseplateTemperatureLimits();
        yield return new GetDiodeTemperatureLimits();
        yield return new GetInternalTemperatureLimits();
        yield return new GetThresholdCurrent();
        yield return new GetMaxCurrent();
        yield return new GetInterlock();
        yield return new GetNoise();
        yield return new GetPowerCycles();
        yield return new GetHours();
        yield return new GetDiodeHours();
        yield return new GetAutoStart();
        yield return new GetCdrhDelay();
        yield return new GetTecEnabled();
        yield return new GetDiodeWarmup();
        yield return new GetStatusIndicator();
        yield return new GetAnalogBlanking();
        yield return new GetAnalogImpedance();
    }

    /// <summary>
    /// The queries asked of a laser box (channel 0 of its port), as raw queries: a box rejects some of the queries a
    /// typed command such as GetInfo combines, which would lose all of its values.
    /// </summary>
    public static readonly string[] Controller =
    {
        "*IDN?",
        "SYST:INF:TYP?", "SYST:INF:MOD?", "SYST:INF:SNUM?", "SYST:INF:PNUM?",
        "SYST:INF:FVER?", "SYST:INF:PVER?", "SYST:INF:CDAT?", "SYST:INF:MDAT?", "SYST:INF:USER?",
        "SYST:STAT?", "SYST:FAUL?", "SYST:HOUR?", "SYST:CYCL?",
        "SYST:CDRH?", "SYST:AUT?", "SYST:LOCK?", "SYST:COMM:HAND?", "SYST:COMM:PROM?",
        "SOUR:TEMP:BAS?", "SOUR:TEMP:INT?",
    };
}

/// <summary>What a laser readout asks.</summary>
public enum HsfpLaserReadoutKind
{
    /// <summary>Every read-only command of each laser and every query of each laser box.</summary>
    Full,

    /// <summary>The output power each laser measures (GetPower).</summary>
    Power,
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Creates the read-only commands of a readout of the OBIS lasers (and, for a full readout, of their laser boxes), to be sent to the LaserCommands subject. Their replies are turned into rows by CreateHsfpLaserMessages.")]
public class CreateHsfpLaserReadout
{
    [Description("What the readout asks: everything (Full), or the measured output power only (Power).")]
    public HsfpLaserReadoutKind Kind { get; set; }

    public IObservable<HsfpLaserCommand[]> Process(IObservable<HsfpLaser[]> source)
    {
        return source.Select(lasers => Create(lasers, Kind));
    }

    internal static HsfpLaserCommand[] Create(HsfpLaser[] lasers, HsfpLaserReadoutKind kind)
    {
        lasers = lasers ?? new HsfpLaser[0];
        var commands = new List<HsfpLaserCommand>();
        foreach (var laser in lasers)
        {
            var payloads = kind == HsfpLaserReadoutKind.Full ? HsfpLaserQueries.Laser() : new CommandPayload[] { new GetPower() };
            foreach (var payload in payloads) commands.Add(new HsfpLaserCommand(laser.Channel, payload.CreateCommand(laser.LaserChannel), true));
        }
        if (kind == HsfpLaserReadoutKind.Full)
        {
            // The laser box of each port is read once, through the first of its lasers.
            foreach (var port in lasers.Where(l => l.LaserChannel > 0).GroupBy(l => l.PortName, StringComparer.OrdinalIgnoreCase))
            {
                var laser = port.First();
                foreach (var query in HsfpLaserQueries.Controller)
                {
                    commands.Add(new HsfpLaserCommand(laser.Channel, new BFC.ObisLaser.Query { Text = query }.CreateCommand(0), true));
                }
            }
        }
        return commands.ToArray();
    }
}

/// <summary>One reply of a laser readout, logged as one row of the Lasers software event.</summary>
public class HsfpLaserMessage
{
    [JsonProperty("channel")]
    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; set; }

    [JsonProperty("serial_number")]
    [Description("The serial number of the laser.")]
    public string SerialNumber { get; set; }

    [JsonProperty("model")]
    [Description("The model the laser reports.")]
    public string Model { get; set; }

    [JsonProperty("wavelength")]
    [Description("The nominal wavelength the laser reports (nm).")]
    public double Wavelength { get; set; }

    [JsonProperty("port_name")]
    [Description("The serial port of the laser or of the laser box it is connected to.")]
    public string PortName { get; set; }

    [JsonProperty("laser_channel")]
    [Description("The channel of the laser on its port: 0 if connected directly, 1 or more behind a laser box.")]
    public int LaserChannel { get; set; }

    [JsonProperty("device")]
    [Description("Who replied: the laser, or the laser box it is connected to (controller).")]
    public string Device { get; set; }

    [JsonProperty("command")]
    [Description("The command replied to: a typed command such as GetInfo, GetState, GetPower or GetUserInfo(2), or the raw query of a laser box such as SYST:INF:SNUM?.")]
    public string Command { get; set; }

    [JsonProperty("value")]
    [Description("The reply, typed, with enums as names; null if the command was rejected.")]
    public JToken Value { get; set; }

    [JsonProperty("error")]
    [Description("Why the command was rejected, as \"ERR <code>: <message>\"; null if it succeeded.")]
    public string Error { get; set; }

    public override string ToString()
    {
        return string.Format("Ch {0} {1}: {2} = {3}", Channel, Device, Command, Error ?? (Value != null ? Value.ToString(Formatting.None) : "null"));
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Turns each reply to a readout command into one HsfpLaserMessage, as it arrives, to be logged as one row of the Lasers software event (with the latest Harp timestamp). Source1 = HsfpLaserReply; Source2 = the lasers found.")]
public class CreateHsfpLaserMessages
{
    static readonly JsonSerializer ValueSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        Converters = { new StringEnumConverter() },
    });

    public IObservable<HsfpLaserMessage> Process(IObservable<HsfpLaserReply> replies, IObservable<HsfpLaser[]> lasers)
    {
        return Observable.Create<HsfpLaserMessage>(observer =>
        {
            var byChannel = new Dictionary<int, HsfpLaser>();
            var gate = new object();
            var lasersSubscription = lasers.Subscribe(found =>
            {
                lock (gate) byChannel = (found ?? new HsfpLaser[0]).ToDictionary(laser => laser.Channel);
            }, observer.OnError);
            var repliesSubscription = replies.Subscribe(reply =>
            {
                if (reply == null || !reply.Readout || reply.Reply == null) return;
                HsfpLaser laser;
                lock (gate)
                {
                    if (!byChannel.TryGetValue(reply.Channel, out laser)) return;
                }
                observer.OnNext(Create(laser, reply.Reply));
            }, observer.OnError, observer.OnCompleted);
            return new CompositeDisposable(lasersSubscription, repliesSubscription);
        });
    }

    static HsfpLaserMessage Create(HsfpLaser laser, ObisReply reply)
    {
        var command = reply.Command;
        var controller = command.Channel == 0 && laser.LaserChannel > 0;
        var query = command.Name == "Query" && command.Text.StartsWith("Query(") && command.Text.EndsWith(")")
            ? command.Text.Substring(6, command.Text.Length - 7)
            : command.Text;
        var message = new HsfpLaserMessage
        {
            Channel = laser.Channel,
            SerialNumber = laser.SerialNumber,
            Model = laser.Model,
            Wavelength = laser.Wavelength,
            PortName = laser.PortName,
            LaserChannel = laser.LaserChannel,
            Device = controller ? "controller" : "laser",
            Command = query,
        };
        if (reply.IsSuccess)
        {
            var lines = reply.Value as IEnumerable<string>;
            if (reply.Value == null) message.Value = JValue.CreateNull();
            else if (lines != null && !(reply.Value is string)) message.Value = new JValue(string.Join(" | ", lines.ToArray()));
            else message.Value = JToken.FromObject(reply.Value, ValueSerializer);
        }
        else
        {
            var code = reply.ErrorCode;
            var text = reply.Error != null ? reply.Error.Message : "failed";
            message.Error = code.HasValue ? string.Format("ERR {0}: {1}", code.Value, text) : "ERR: " + text;
        }
        return message;
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Sends the command of each light source to its laser, with one Device per laser port, and emits the replies of each light source's laser. Readout commands may also address the laser box of the laser; they are sent one at a time per port, each after the reply to the previous one, and their replies are flagged. Source1 = HsfpLaserCommand; Source2 = the lasers found. Emission is turned off when the workflow stops.")]
public class HsfpLaserRouter
{
    /// <summary>
    /// The commands of one port. A readout sends many commands at once, more than a Device queues, so they wait
    /// here and only one is with the Device at a time. Commands of the laser tabs go straight to the Device.
    /// </summary>
    class Port
    {
        public readonly object Gate = new object();
        public readonly Subject<ObisCommand> Commands = new Subject<ObisCommand>();
        public readonly List<HsfpLaserCommand> Readout = new List<HsfpLaserCommand>();
        public HsfpLaserCommand InFlight;

        // Called with Gate held. A readout command already waiting with the same text for the same light source and
        // channel (a poll the port has not caught up with) is not queued again, so a slow port never falls behind.
        public void Enqueue(HsfpLaserCommand command)
        {
            foreach (var waiting in Readout)
            {
                if (waiting.Channel == command.Channel && waiting.Command.Channel == command.Command.Channel && waiting.Command.Text == command.Command.Text) return;
            }
            Readout.Add(command);
            SendNextReadout();
        }

        // Called with Gate held.
        public void SendNextReadout()
        {
            if (InFlight != null || Readout.Count == 0) return;
            InFlight = Readout[0];
            Readout.RemoveAt(0);
            Commands.OnNext(InFlight.Command);
        }
    }

    public IObservable<HsfpLaserReply> Process(IObservable<HsfpLaserCommand> commands, IObservable<HsfpLaser[]> lasers)
    {
        return lasers.Select(set => Observable.Create<HsfpLaserReply>(observer =>
        {
            var byChannel = set.ToDictionary(laser => laser.Channel);
            var ports = new Dictionary<string, Port>(StringComparer.OrdinalIgnoreCase);
            var subscriptions = new CompositeDisposable();
            foreach (var group in set.GroupBy(laser => laser.PortName, StringComparer.OrdinalIgnoreCase))
            {
                var port = new Port();
                ports.Add(group.Key, port);
                // Replies are traced back to their light source by the laser channel on this port.
                var lightSources = group.ToDictionary(laser => laser.LaserChannel, laser => laser.Channel);
                subscriptions.Add(new Device { PortName = group.Key }.Process(port.Commands).Subscribe(reply =>
                {
                    HsfpLaserCommand readout = null;
                    lock (port.Gate)
                    {
                        if (port.InFlight != null && ReferenceEquals(port.InFlight.Command, reply.Command))
                        {
                            readout = port.InFlight;
                            port.InFlight = null;
                            port.SendNextReadout();
                        }
                    }
                    int channel;
                    if (readout != null) observer.OnNext(new HsfpLaserReply(readout.Channel, reply, true));
                    else if (lightSources.TryGetValue(reply.Command.Channel, out channel)) observer.OnNext(new HsfpLaserReply(channel, reply));
                }, observer.OnError));
            }

            subscriptions.Add(commands.Subscribe(command =>
            {
                HsfpLaser laser;
                if (command == null || !byChannel.TryGetValue(command.Channel, out laser)) return;
                var port = ports[laser.PortName];
                if (command.Readout)
                {
                    if (command.Command.Channel != laser.LaserChannel && command.Command.Channel != 0) return;
                    lock (port.Gate) port.Enqueue(command);
                    return;
                }
                if (command.Command.Channel != laser.LaserChannel) return;
                lock (port.Gate) port.Commands.OnNext(command.Command);
            }, observer.OnError));
            return subscriptions;
        })).Switch();
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders one tab per OBIS laser found, with its dashboard, and emits the commands requested from the dashboards. Source1 = Frame subject; Source2 = the lasers found; Source3 = HsfpLaserReply.")]
public class HsfpLasersVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    class Tab
    {
        public HsfpLaser Laser;
        public ObisLaserController Controller;
        public IDisposable Commands;
    }

    public IObservable<HsfpLaserCommand> Process<TTickSource>(IObservable<TTickSource> frames, IObservable<HsfpLaser[]> lasers, IObservable<HsfpLaserReply> replies)
    {
        return Observable.Create<HsfpLaserCommand>(observer =>
        {
            var gate = new object();
            Tab[] tabs = null;
            Action clear = () =>
            {
                if (tabs == null) return;
                foreach (var tab in tabs)
                {
                    tab.Commands.Dispose();
                    tab.Controller.Dispose();
                }
            };

            var lasersSubscription = lasers.Subscribe(found =>
            {
                lock (gate)
                {
                    clear();
                    tabs = found.Select(laser =>
                    {
                        var controller = new ObisLaserController(laser.LaserChannel);
                        var channel = laser.Channel;
                        var commands = controller.Commands.Subscribe(command => observer.OnNext(new HsfpLaserCommand(channel, command)));
                        return new Tab { Laser = laser, Controller = controller, Commands = commands };
                    }).ToArray();
                }
            }, observer.OnError);

            var repliesSubscription = replies.Subscribe(reply =>
            {
                Tab[] current;
                lock (gate) current = tabs;
                if (current == null) return;
                if (reply == null || reply.Readout) return;
                foreach (var tab in current)
                {
                    if (tab.Laser.Channel == reply.Channel) tab.Controller.Receive(reply.Reply);
                }
            }, observer.OnError);

            var frameSubscription = frames.Subscribe(_ =>
            {
                HsfpStyle.Apply();
                if (!Visible) return;
                Tab[] current;
                lock (gate) current = tabs;
                Draw(current);
            }, observer.OnError, observer.OnCompleted);

            return Disposable.Create(() =>
            {
                frameSubscription.Dispose();
                repliesSubscription.Dispose();
                lasersSubscription.Dispose();
                lock (gate)
                {
                    clear();
                    tabs = null;
                }
            });
        });
    }

    static void Draw(Tab[] tabs)
    {
        if (tabs == null)
        {
            ImGui.TextDisabled("Looking for the OBIS lasers of the rig...");
            return;
        }

        if (tabs.Length == 0)
        {
            ImGui.TextDisabled("The rig has no light source.");
            return;
        }

        if (!ImGui.BeginTabBar("##HsfpLasers")) return;
        foreach (var tab in tabs)
        {
            var laser = tab.Laser;
            var emitting = tab.Controller.IsEmitting;
            var label = string.Format("Ch {0} - {1:F0} nm{2}##HsfpLaser{0}", laser.Channel, laser.Wavelength, emitting == true ? "  ON" : string.Empty);
            ImGui.PushStyleColor(ImGuiCol.Text, HsfpUi.WavelengthColor(laser.Wavelength));
            var open = ImGui.BeginTabItem(label);
            ImGui.PopStyleColor();
            ImGui.SetItemTooltip(string.Format("{0}\nS/N {1}\n{2}, channel {3}", laser.Model, laser.SerialNumber, laser.PortName, laser.LaserChannel));
            if (!open) continue;
            tab.Controller.Draw();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Emits a simulated OBIS laser for each light source of the rig, for testing the acquisition UI without lasers. Their dashboards wait for replies that never arrive.")]
public class SimulatedHsfpLasers
{
    static readonly double[] Wavelengths = { 405, 445, 488, 514, 561, 594, 640 };

    public IObservable<HsfpLaser[]> Process(IObservable<AindPhysiologyHsfp.AindPhysioHsfpRig> source)
    {
        return source.Select(rig =>
        {
            var lasers = HsfpChannels.Get(rig).Select(channel => new HsfpLaser(
                channel.Channel,
                channel.LightSource.SerialNumber,
                "Simulated",
                "SIM",
                channel.Index + 1,
                Wavelengths[channel.Index % Wavelengths.Length])).ToArray();
            HsfpChannels.SetLasers(lasers);
            return lasers;
        });
    }
}
