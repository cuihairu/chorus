using System.Globalization;

namespace Chorus.LoadTool;

/// <summary>
/// Reads one gauge value out of a Prometheus text exposition (todo §4
/// peak-measurement item). Line-format tolerant, exact-name matching: a metric
/// whose name merely extends the requested one must not match.
/// </summary>
public static class MetricsExposition
{
    public static double? SampleValue(string exposition, string metricName)
    {
        foreach (var line in exposition.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (!trimmed.StartsWith(metricName, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = trimmed.AsSpan(metricName.Length);
            if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
            {
                continue;
            }

            var valueToken = rest.ToString().Trim();
            var spaceIndex = valueToken.IndexOf(' ');
            if (spaceIndex >= 0)
            {
                valueToken = valueToken[..spaceIndex];
            }

            if (double.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// Run-length peaks over periodically scraped engine metrics: a sustained-load
/// run reports the highest sessions / tick duration / process CPU it observed,
/// not just the final scrape. A scrape missing one metric leaves that peak
/// untouched.
/// </summary>
public sealed class MetricsPeaks
{
    public double? MaxSessions { get; private set; }
    public double? MaxTickDurationMs { get; private set; }
    public double? MaxCpuPercent { get; private set; }

    public void Observe(string exposition)
    {
        MaxSessions = Keep(MaxSessions, MetricsExposition.SampleValue(exposition, "chorus_sessions"));
        MaxTickDurationMs = Keep(MaxTickDurationMs, MetricsExposition.SampleValue(exposition, "chorus_tick_duration_ms"));
        MaxCpuPercent = Keep(MaxCpuPercent, MetricsExposition.SampleValue(exposition, "chorus_process_cpu_percent"));
    }

    private static double? Keep(double? current, double? next) =>
        next is null ? current : current is { } c ? Math.Max(c, next.Value) : next;
}
