namespace Chorus.NodeAgent;

/// <summary>One machine load sample (FIX-OBS-04): the 1/5/15-minute load averages.</summary>
public readonly record struct MachineLoadSample(double Load1m, double Load5m, double Load15m);

/// <summary>
/// The machine load collector (FIX-OBS-04): reads <c>/proc/loadavg</c> and reports the
/// 1/5/15-minute load averages. Load is a level — one read answers, no baseline. The
/// read is injected so unit tests feed fixture content; a missing file (a non-Linux
/// node) yields no sample rather than a crash.
/// </summary>
public sealed class MachineLoadCollector
{
    private readonly Func<string?> _read;

    public MachineLoadCollector(Func<string?>? read = null)
    {
        _read = read ?? DefaultRead;
    }

    private static string? DefaultRead()
    {
        try
        {
            using var reader = new StreamReader("/proc/loadavg");
            return reader.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads and parses a sample. False means no value: no file or malformed
    /// content — read as absent, never a fake zero.</summary>
    public bool TrySample(out MachineLoadSample sample)
    {
        var parsed = Parse(_read());
        if (parsed is null)
        {
            sample = default;
            return false;
        }
        sample = parsed.Value;
        return true;
    }

    /// <summary>Parses a /proc/loadavg line into the three averages. Null for a missing
    /// or malformed line. Public so the parsing contract is directly testable.</summary>
    public static MachineLoadSample? Parse(string? loadavg)
    {
        if (loadavg is null)
            return null;
        var fields = loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "0.00 0.01 0.05 1/234 5678" — the fourth field is running/total tasks.
        if (fields.Length < 3
            || !double.TryParse(fields[0], out var load1)
            || !double.TryParse(fields[1], out var load5)
            || !double.TryParse(fields[2], out var load15))
            return null;
        if (load1 < 0 || load5 < 0 || load15 < 0)
            return null;
        return new MachineLoadSample(load1, load5, load15);
    }
}
