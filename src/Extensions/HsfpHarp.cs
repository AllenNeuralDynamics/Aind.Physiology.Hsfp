using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;

/// <summary>
/// Latest Harp clock synchronization status of the cuTTLefish: the IS_SYNCHRONIZED bit (bit 0) of its
/// <c>R_HEARTBEAT</c> register (address 18), which the device sends once per second when <c>HEARTBEAT_EN</c> is set
/// in <c>R_OPERATION_CTRL</c>. The register is parsed in the workflow; visualizers receive the bit through a
/// PropertyMapping and keep it here, with the time it arrived.
/// </summary>
public class HsfpSyncStatus
{
    /// <summary>Heartbeats older than this are considered lost (the device sends one per second).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    readonly object gate = new object();
    bool synchronized;
    DateTime? receivedAt;

    public void Update(bool isSynchronized)
    {
        lock (gate)
        {
            synchronized = isSynchronized;
            receivedAt = DateTime.UtcNow;
        }
    }

    /// <summary>The last value received, or false if none was.</summary>
    public bool LastValue
    {
        get { lock (gate) return synchronized; }
    }

    /// <summary>Whether a heartbeat was ever received.</summary>
    public bool HasValue
    {
        get { lock (gate) return receivedAt.HasValue; }
    }

    /// <summary>Whether no heartbeat was received for longer than <see cref="Timeout"/>.</summary>
    public bool IsStale
    {
        get { lock (gate) return !receivedAt.HasValue || DateTime.UtcNow - receivedAt.Value > Timeout; }
    }

    /// <summary>Whether the last heartbeat reported IS_SYNCHRONIZED and heartbeats are still arriving.</summary>
    public bool IsSynchronized
    {
        get { lock (gate) return synchronized && receivedAt.HasValue && DateTime.UtcNow - receivedAt.Value <= Timeout; }
    }

    /// <summary>Short status, for a tile.</summary>
    public string Summary
    {
        get { return !HasValue ? "No heartbeat" : IsStale ? "Heartbeat lost" : LastValue ? "Synchronized" : "Not synchronized"; }
    }

    /// <summary>One-line description, for tooltips, the confirmation dialog and the HardwareQA event.</summary>
    public string Describe()
    {
        lock (gate)
        {
            if (!receivedAt.HasValue) return "No heartbeat received from the cuTTLefish.";
            var age = DateTime.UtcNow - receivedAt.Value;
            if (age > Timeout) return string.Format("No heartbeat from the cuTTLefish for {0:F0} s.", age.TotalSeconds);
            return synchronized
                ? "The cuTTLefish is synchronized with the Harp clock."
                : "The cuTTLefish is not synchronized with the Harp clock (IS_SYNCHRONIZED is low).";
        }
    }
}

[Description("Generates the IS_SYNCHRONIZED bit once per second, standing in for the cuTTLefish heartbeat when testing the UI without hardware.")]
public class SimulatedSyncStatus : Source<bool>
{
    public SimulatedSyncStatus()
    {
        IsSynchronized = true;
    }

    [Description("The simulated IS_SYNCHRONIZED bit. Can be changed while running.")]
    public bool IsSynchronized { get; set; }

    public override IObservable<bool> Generate()
    {
        return Observable.Interval(TimeSpan.FromSeconds(1)).Select(_ => IsSynchronized);
    }
}
