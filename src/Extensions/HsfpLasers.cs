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
using ObisLaser.Net;

/// <summary>A light source of the rig with the OBIS laser found for it.</summary>
public class HsfpLaser
{
    public HsfpLaser(int channel, string name, string serialNumber, string model, string portName, int laserChannel, double wavelength)
    {
        Channel = channel;
        Name = name;
        SerialNumber = serialNumber;
        Model = model;
        PortName = portName;
        LaserChannel = laserChannel;
        Wavelength = wavelength;
    }

    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; private set; }

    [Description("The name of the light source in the rig.")]
    public string Name { get; private set; }

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
        return string.Format("Ch {0} {1}: S/N {2} on {3} channel {4} ({5:F0} nm)", Channel, Name, SerialNumber, PortName, LaserChannel, Wavelength);
    }
}

/// <summary>A command from the panel of one light source, addressed to its laser.</summary>
public class HsfpLaserCommand
{
    public HsfpLaserCommand(int channel, ObisCommand command)
    {
        Channel = channel;
        Command = command;
    }

    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; private set; }

    public ObisCommand Command { get; private set; }

    public override string ToString()
    {
        return string.Format("Ch {0}: {1}", Channel, Command);
    }
}

/// <summary>A reply of the laser of one light source.</summary>
public class HsfpLaserReply
{
    public HsfpLaserReply(int channel, ObisReply reply)
    {
        Channel = channel;
        Reply = reply;
    }

    [Description("The cuTTLefish channel of the light source.")]
    public int Channel { get; private set; }

    public ObisReply Reply { get; private set; }

    public override string ToString()
    {
        return string.Format("Ch {0}: {1}", Channel, Reply);
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Finds the OBIS laser of each light source of the rig with a serial number, and checks that it reports the light source's wavelength. Raises an error naming every laser that is missing or does not match.")]
public class FindHsfpLasers
{
    public FindHsfpLasers()
    {
        WavelengthTolerance = 2;
    }

    [Description("The largest difference (nm) between the wavelength a laser reports and the wavelength of its light source.")]
    public double WavelengthTolerance { get; set; }

    public IObservable<HsfpLaser[]> Process(IObservable<AindPhysiologyHsfp.AindPhysioHsfpRig> source)
    {
        return source.SelectMany(rig => Observable.Start(() => Find(rig, WavelengthTolerance)));
    }

    /// <summary>
    /// Probes only the ports of OBIS devices connected over USB (never the cuTTLefish port), and only if a light
    /// source has a serial number, so that no other serial device is written to.
    /// </summary>
    internal static HsfpLaser[] Find(AindPhysiologyHsfp.AindPhysioHsfpRig rig, double tolerance)
    {
        var wanted = HsfpChannels.Get(rig).Where(c => !string.IsNullOrWhiteSpace(c.LightSource.SerialNumber)).ToList();
        if (wanted.Count == 0) return new HsfpLaser[0];

        var excluded = rig.Cuttlefish != null && !string.IsNullOrEmpty(rig.Cuttlefish.PortName) ? new[] { rig.Cuttlefish.PortName } : new string[0];
        var ports = ObisDiscovery.GetCandidatePortNames().Where(port => !excluded.Contains(port, StringComparer.OrdinalIgnoreCase)).ToList();
        var found = ports.Count > 0
            ? ObisDiscovery.FindAll(ports, excluded).Select(d => new FoundDevice(d)).Where(d => d.DeviceType == DeviceType.Ddl || d.DeviceType == DeviceType.Opsl).ToList()
            : new List<FoundDevice>();

        var errors = new List<string>();
        var matches = new List<Tuple<HsfpChannel, FoundDevice>>();
        foreach (var channel in wanted)
        {
            var serial = channel.LightSource.SerialNumber.Trim();
            var candidates = found.Where(d => string.Equals((d.SerialNumber ?? string.Empty).Trim(), serial, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) errors.Add(string.Format("{0}: no laser with serial number {1} was found.", Describe(channel), serial));
            else if (candidates.Count > 1) errors.Add(string.Format("{0}: several lasers report serial number {1}.", Describe(channel), serial));
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
                        var channel = match.Item1;
                        var device = match.Item2;
                        var wavelength = connection.Execute(ObisProtocol.GetWavelength(), device.Channel);
                        var expected = channel.LightSource.Wavelength;
                        if (!expected.HasValue || Math.Abs(wavelength - expected.Value) > tolerance)
                        {
                            errors.Add(string.Format("{0}: laser {1} reports {2:F0} nm, but the light source is {3} nm.",
                                Describe(channel), device.SerialNumber, wavelength, expected.HasValue ? expected.Value.ToString() : "not set"));
                            continue;
                        }
                        lasers.Add(new HsfpLaser(channel.Channel, channel.LightSource.Name, device.SerialNumber, device.Model, device.PortName, device.Channel, wavelength));
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

        return lasers.OrderBy(l => l.Channel).ToArray();
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
        return string.Format("Channel {0} ({1})", channel.Channel, channel.LightSource.Name);
    }
}

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Sends the command of each light source to its laser, with one Device per laser port, and emits the replies of each light source's laser. Source1 = HsfpLaserCommand; Source2 = the lasers found. Emission is turned off when the workflow stops.")]
public class HsfpLaserRouter
{
    public IObservable<HsfpLaserReply> Process(IObservable<HsfpLaserCommand> commands, IObservable<HsfpLaser[]> lasers)
    {
        return lasers.Select(set => Observable.Create<HsfpLaserReply>(observer =>
        {
            var byChannel = set.ToDictionary(laser => laser.Channel);
            var ports = new Dictionary<string, Subject<ObisCommand>>(StringComparer.OrdinalIgnoreCase);
            var subscriptions = new CompositeDisposable();
            foreach (var port in set.GroupBy(laser => laser.PortName, StringComparer.OrdinalIgnoreCase))
            {
                var portCommands = new Subject<ObisCommand>();
                ports.Add(port.Key, portCommands);
                // Replies are traced back to their light source by the laser channel on this port.
                var lightSources = port.ToDictionary(laser => laser.LaserChannel, laser => laser.Channel);
                subscriptions.Add(new Device { PortName = port.Key }.Process(portCommands).Subscribe(reply =>
                {
                    int channel;
                    if (lightSources.TryGetValue(reply.Command.Channel, out channel)) observer.OnNext(new HsfpLaserReply(channel, reply));
                }, observer.OnError));
            }

            subscriptions.Add(commands.Subscribe(command =>
            {
                HsfpLaser laser;
                if (command == null || !byChannel.TryGetValue(command.Channel, out laser)) return;
                if (command.Command.Channel != laser.LaserChannel) return;
                ports[laser.PortName].OnNext(command.Command);
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
            ImGui.TextDisabled("No light source has a laser serial number in the rig.");
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
            ImGui.SetItemTooltip(string.Format("{0}\nS/N {1} ({2})\n{3}, channel {4}", laser.Name, laser.SerialNumber, laser.Model, laser.PortName, laser.LaserChannel));
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
    public IObservable<HsfpLaser[]> Process(IObservable<AindPhysiologyHsfp.AindPhysioHsfpRig> source)
    {
        return source.Select(rig => HsfpChannels.Get(rig).Select(channel => new HsfpLaser(
            channel.Channel,
            channel.LightSource.Name,
            "SIM-" + channel.Channel,
            "Simulated",
            "SIM",
            channel.Index + 1,
            channel.LightSource.Wavelength.HasValue ? channel.LightSource.Wavelength.Value : 0)).ToArray());
    }
}
