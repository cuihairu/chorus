using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Chorus.Data;

/// <summary>
/// Storage-wait diagnostics (FIX-PERF-02): where a store call's time goes, one instrument
/// per phase so pool starvation and row-lock blocking light up different instruments instead
/// of one blurred "database slow". Connection opening carries the pool wait; the transaction
/// body carries every SQL statement of the business write, row-lock waits included — the
/// client cannot split a lock wait out of a statement without sampling the server, so the
/// body span is honestly labelled as both. No labels beyond the phase itself.
/// <para>Instantiable on purpose: production wires <see cref="Default"/>, tests wire a
/// private meter name so parallel suites cannot bleed their samples into another test's
/// assertions.</para>
/// </summary>
public sealed class DataMetrics : IDisposable
{
    public const string MeterName = "Chorus.Data";

    /// <summary>The process-wide meter every production store call shares.</summary>
    public static DataMetrics Default { get; } = new();

    private readonly Meter _meter;

    /// <summary>Opening a pooled connection; a saturated pool waits here.</summary>
    public Histogram<double> ConnOpen { get; }

    /// <summary>The transaction body: claim, business write and completion, lock waits included.</summary>
    public Histogram<double> TxWork { get; }

    /// <summary>Committing the receipt transaction.</summary>
    public Histogram<double> TxCommit { get; }

    /// <summary>A store read's query span, lock waits included.</summary>
    public Histogram<double> ReadExec { get; }

    public DataMetrics(string meterName = MeterName)
    {
        _meter = new Meter(meterName);
        ConnOpen = _meter.CreateHistogram<double>(
            "data.conn.open", "ms", "Opening a pooled connection, pool wait included.");
        TxWork = _meter.CreateHistogram<double>(
            "data.tx.work", "ms",
            "Every SQL statement of one receipt transaction before the commit, row-lock waits included.");
        TxCommit = _meter.CreateHistogram<double>(
            "data.tx.commit", "ms", "Committing one receipt transaction.");
        ReadExec = _meter.CreateHistogram<double>(
            "data.read.exec", "ms", "One store read's queries, lock waits included.");
    }

    /// <summary>Starts a phase clock on the Stopwatch timestamp; no allocation.</summary>
    public long Start() => Stopwatch.GetTimestamp();

    /// <summary>Records the elapsed phase time in milliseconds.</summary>
    public void Record(Histogram<double> instrument, long start) =>
        instrument.Record((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

    public void Dispose() => _meter.Dispose();
}
