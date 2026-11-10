namespace Chorus.NodeAgent;

/// <summary>
/// The machine CPU collector (FIX-OBS-02): reads the aggregate <c>/proc/stat</c> line and
/// reports the CPU usage percent between two samples. The first sample only establishes
/// the baseline — the percent answers from the second on, because a utilization is a
/// delta over an interval, never an instantaneous level. <c>idle+iowait</c> counts as idle
/// (iowait is time spent waiting for IO, not driving a core) and <c>guest/guest_nice</c>
/// are excluded (already inside user/nice on Linux, so including them double-counts).
/// The read is injected so unit tests feed fixture content; a missing file (a non-Linux
/// node) yields no sample rather than a crash — the value is simply absent.
/// </summary>
public sealed class MachineCpuCollector
{
    private readonly Func<string?> _read;
    private (double Idle, double Total)? _previous;

    public MachineCpuCollector(Func<string?>? read = null)
    {
        _read = read ?? DefaultRead;
    }

    /// <summary>Reads the aggregate CPU line of /proc/stat, or null when unavailable.</summary>
    private static string? DefaultRead()
    {
        try
        {
            using var reader = new StreamReader("/proc/stat");
            return reader.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Advances the collector one sample. False means no percent this time: no baseline
    /// yet, no file, a malformed line, or zero elapsed ticks — all read as "no value".
    /// </summary>
    public bool TrySample(out double cpuPercent)
    {
        var current = Parse(_read());
        if (current is null)
        {
            _previous = null;
            cpuPercent = 0;
            return false;
        }
        var previous = _previous;
        _previous = current;
        if (previous is null)
        {
            cpuPercent = 0;
            return false;
        }
        var totalDelta = current.Value.Total - previous.Value.Total;
        var idleDelta = current.Value.Idle - previous.Value.Idle;
        if (totalDelta <= 0)
        {
            cpuPercent = 0;
            return false;
        }
        cpuPercent = (1.0 - idleDelta / totalDelta) * 100.0;
        return true;
    }

    /// <summary>Parses the aggregate cpu line (the first line of /proc/stat) into
    /// (idle, total) tick counts. Null for a missing or malformed line. Public so the
    /// parsing contract is directly testable.</summary>
    public static (double Idle, double Total)? Parse(string? statLine)
    {
        if (statLine is null)
            return null;
        var fields = statLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "cpu  user nice system idle iowait irq softirq steal guest guest_nice"
        if (fields.Length < 8 || fields[0] != "cpu")
            return null;
        var ticks = new double[8];
        for (var i = 0; i < 8; i++)
        {
            if (!double.TryParse(fields[i + 1], out ticks[i]))
                return null;
        }
        var total = ticks[0] + ticks[1] + ticks[2] + ticks[3] + ticks[4] + ticks[5] + ticks[6] + ticks[7];
        var idle = ticks[3] + ticks[4];
        return (idle, total);
    }
}
