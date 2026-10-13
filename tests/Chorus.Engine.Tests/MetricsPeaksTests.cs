using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// LoadTool run-time metrics sampling (todo §4 peak-measurement item): parse
/// Prometheus exposition values and keep run-length peaks, so a sustained-load
/// run reports the highest sessions / tick duration / process CPU it observed
/// instead of only the final scrape.
/// </summary>
public sealed class MetricsPeaksTests
{
    private const string Exposition1 = """
        # TYPE chorus_sessions gauge
        chorus_sessions 2
        # TYPE chorus_tick_duration_ms gauge
        chorus_tick_duration_ms 10
        # TYPE chorus_process_cpu_percent gauge
        chorus_process_cpu_percent 30.5
        """;

    private const string Exposition2 = """
        # TYPE chorus_sessions gauge
        chorus_sessions 5
        # TYPE chorus_tick_duration_ms gauge
        chorus_tick_duration_ms 7
        # TYPE chorus_process_cpu_percent gauge
        chorus_process_cpu_percent 55
        """;

    [Fact]
    public void SampleValue_ReadsGaugeLine()
    {
        Assert.Equal(2, MetricsExposition.SampleValue(Exposition1, "chorus_sessions"));
        Assert.Equal(10, MetricsExposition.SampleValue(Exposition1, "chorus_tick_duration_ms"));
        Assert.Equal(30.5, MetricsExposition.SampleValue(Exposition1, "chorus_process_cpu_percent"));
    }

    [Fact]
    public void SampleValue_MissingOrMalformed_YieldsNull()
    {
        Assert.Null(MetricsExposition.SampleValue(Exposition1, "chorus_absent_metric"));
        Assert.Null(MetricsExposition.SampleValue("", "chorus_sessions"));
        // A name that is a prefix of another must not match.
        Assert.Null(MetricsExposition.SampleValue(Exposition1, "chorus_session"));
    }

    [Fact]
    public void Peaks_KeepRunMaxPerMetric()
    {
        var peaks = new MetricsPeaks();
        peaks.Observe(Exposition1);
        peaks.Observe(Exposition2);

        Assert.Equal(5, peaks.MaxSessions);
        Assert.Equal(10, peaks.MaxTickDurationMs);
        Assert.Equal(55, peaks.MaxCpuPercent);
    }

    [Fact]
    public void Peaks_MissingMetric_LeavesPreviousPeakUntouched()
    {
        var withoutCpu = """
            chorus_sessions 3
            chorus_tick_duration_ms 20
            """;

        var peaks = new MetricsPeaks();
        peaks.Observe(Exposition1);
        peaks.Observe(withoutCpu);

        Assert.Equal(3, peaks.MaxSessions);
        Assert.Equal(20, peaks.MaxTickDurationMs);
        Assert.Equal(30.5, peaks.MaxCpuPercent);
    }

    [Fact]
    public void Peaks_FreshTracker_AllNull()
    {
        var peaks = new MetricsPeaks();
        Assert.Null(peaks.MaxSessions);
        Assert.Null(peaks.MaxTickDurationMs);
        Assert.Null(peaks.MaxCpuPercent);
    }
}
