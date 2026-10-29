using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Chorus.GameServer;

/// <summary>
/// Tick-phase latency recording (FIX-PERF-01): where a tick's time goes, one instrument per
/// phase so counts never mix, and no room id in any label — an instrument's cardinality is
/// the phase set itself, never the room fleet. The per-tick inbox pull is labelled as what
/// it is, the migration baseline (architecture 6.1.6): the target path — the gateway pushing
/// events straight into the room (architecture §4.6, todo §22.9) — gets its own instrument
/// when it lands, and this one retires rather than being redefined as the target.
/// <para>Instantiable on purpose: production wires <see cref="Default"/>, tests wire a
/// private meter name so parallel suites cannot bleed their samples into another test's
/// assertions.</para>
/// </summary>
public sealed class RoomMetrics : IDisposable
{
    public const string MeterName = "Chorus.Room";

    /// <summary>The process-wide meter every production actor shares.</summary>
    public static RoomMetrics Default { get; } = new();

    private readonly Meter _meter;

    /// <summary>The round-inbox pull at the store (migration baseline, not the target path).</summary>
    public Histogram<double> InboxPull { get; }

    public Histogram<double> Apply { get; }

    public Histogram<double> Serialize { get; }

    public Histogram<double> Commit { get; }

    public Counter<long> CommitRetries { get; }

    public Histogram<double> Whole { get; }

    public Histogram<double> RecoveryRead { get; }

    public Histogram<double> RecoveryReplay { get; }

    public RoomMetrics(string meterName = MeterName)
    {
        _meter = new Meter(meterName);
        InboxPull = _meter.CreateHistogram<double>(
            "room.tick.inbox_pull", "ms",
            "Reading the round inbox at the store, once per tick (migration baseline pull).");
        Apply = _meter.CreateHistogram<double>(
            "room.tick.apply", "ms", "Applying one module tick.");
        Serialize = _meter.CreateHistogram<double>(
            "room.tick.serialize", "ms",
            "Serializing state for a checkpoint or a subscriber snapshot inside a tick.");
        Commit = _meter.CreateHistogram<double>(
            "room.tick.commit", "ms",
            "The CommitFrame phase to its verdict, retries included.");
        CommitRetries = _meter.CreateCounter<long>(
            "room.tick.commit_retry", "{attempt}",
            "CommitFrame attempts whose verdict was lost and were resent (FIX-FRAME-03).");
        Whole = _meter.CreateHistogram<double>(
            "room.tick.whole", "ms",
            "One whole tick, wake to verdict and publish; early returns pay their time too.");
        RecoveryRead = _meter.CreateHistogram<double>(
            "room.recovery.read", "ms",
            "Loading one recovery window's plan, page by page.");
        RecoveryReplay = _meter.CreateHistogram<double>(
            "room.recovery.replay", "ms",
            "Replaying one recovery window's frame span to the head.");
    }

    /// <summary>Starts a phase clock on the Stopwatch timestamp; no allocation.</summary>
    public long Start() => Stopwatch.GetTimestamp();

    /// <summary>Records the elapsed phase time in milliseconds.</summary>
    public void Record(Histogram<double> instrument, long start) =>
        instrument.Record((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

    public void Dispose() => _meter.Dispose();
}
