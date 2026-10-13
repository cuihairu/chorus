using System.Diagnostics;

namespace Chorus.EngineHost;

/// <summary>
/// Worker process CPU as a share of total machine capacity, computed between
/// /metrics scrapes (todo §4 peak-measurement item; the stage-3 trigger reads
/// this as "单 worker 高峰 CPU"). The first scrape has no previous sample and
/// yields null — the line is omitted rather than reported as a fake zero.
/// </summary>
public sealed class ProcessCpuGauge
{
    private readonly Process _process = Process.GetCurrentProcess();
    private (TimeSpan Cpu, DateTimeOffset Wall)? _previous;

    public double? Sample()
    {
        var current = (_process.TotalProcessorTime, DateTimeOffset.UtcNow);
        var previous = _previous;
        _previous = current;
        return previous is { } prev ? Percent(prev, current, Environment.ProcessorCount) : null;
    }

    /// <summary>
    /// CPU-time delta over wall delta scaled by core count, as a percentage of
    /// total machine capacity (all cores busy = 100). Guards: no elapsed wall
    /// time or a backwards CPU clock return null instead of a nonsense number.
    /// </summary>
    public static double? Percent(
        (TimeSpan Cpu, DateTimeOffset Wall) previous,
        (TimeSpan Cpu, DateTimeOffset Wall) current,
        int coreCount)
    {
        var wallMs = (current.Wall - previous.Wall).TotalMilliseconds;
        var cpuMs = (current.Cpu - previous.Cpu).TotalMilliseconds;
        if (wallMs <= 0 || cpuMs < 0)
        {
            return null;
        }

        var cores = Math.Max(1, coreCount);
        return Math.Clamp(cpuMs / wallMs / cores * 100.0, 0, 100.0);
    }
}
