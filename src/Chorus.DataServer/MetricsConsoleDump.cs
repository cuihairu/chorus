using System.Diagnostics.Metrics;

namespace Chorus.DataServer;

/// <summary>
/// Periodic percentile dump of the storage meters to stdout (FIX-PERF-05 measurement channel):
/// the benchmark hosts run as child processes whose stdout the test harness captures live, so
/// a plain line per instrument carries the phase distributions (connection open with its pool
/// wait, the transaction body with its lock waits, the commit) out of the process without any
/// new RPC. Opt-in: the cadence comes from <c>CHORUS_METRICS_DUMP_SECONDS</c>; unset means no
/// listener, no timer, no output.
/// </summary>
public static class MetricsConsoleDump
{
    /// <summary>The meter every storage phase instrument lives on (<see cref="Chorus.Data.DataMetrics"/>).</summary>
    private const string MeterPrefix = "Chorus.Data";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<double>> Histograms = [];
    private static readonly Dictionary<string, long> Counters = [];

    /// <summary>Wires the listener and the cadence loop when the env var is set.</summary>
    public static void StartFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("CHORUS_METRICS_DUMP_SECONDS");
        if (!int.TryParse(raw, out var seconds) || seconds <= 0) return;
        Start(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Separate for tests: subscribe and dump on an explicit cadence.</summary>
    public static void Start(TimeSpan cadence)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (!instrument.Meter.Name.StartsWith(MeterPrefix, StringComparison.Ordinal)) return;
            if (instrument is Histogram<double>) l.EnableMeasurementEvents(instrument);
            else if (instrument.GetType().IsGenericType
                     && instrument.GetType().GetGenericTypeDefinition() == typeof(Counter<>))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            lock (Gate)
            {
                if (!Histograms.TryGetValue(instrument.Name, out var samples))
                    Histograms[instrument.Name] = samples = [];
                samples.Add(value);
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (Gate) Counters[instrument.Name] = Counters.GetValueOrDefault(instrument.Name) + value;
        });
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(cadence);
                Console.WriteLine(RenderSnapshot());
                Console.Out.Flush();
            }
        });
    }

    /// <summary>One line per instrument, whole-process-life percentiles, stable format.</summary>
    public static string RenderSnapshot()
    {
        var lines = new List<string>();
        lock (Gate)
        {
            foreach (var (name, samples) in Histograms.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (samples.Count == 0) continue;
                var ordered = samples.OrderBy(value => value).ToList();
                double Percentile(double fraction) =>
                    ordered[Math.Min(ordered.Count - 1, (int)Math.Floor(ordered.Count * fraction))];
                lines.Add(FormattableString.Invariant(
                    $"metrics name={name} count={ordered.Count} p50={Percentile(0.50):0.0}ms p95={Percentile(0.95):0.0}ms p99={Percentile(0.99):0.0}ms max={ordered[^1]:0.0}ms"));
            }
            foreach (var (name, total) in Counters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                lines.Add($"metrics name={name} count={total}");
        }
        return string.Join("\n", lines) + (lines.Count == 0 ? "" : "\n");
    }
}
