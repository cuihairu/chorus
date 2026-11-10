namespace Chorus.NodeAgent;

/// <summary>One machine memory sample (FIX-OBS-03): total, used and the used percent.</summary>
public readonly record struct MachineMemorySample(long TotalBytes, long UsedBytes, double UsedPercent);

/// <summary>
/// The machine memory collector (FIX-OBS-03): reads <c>/proc/meminfo</c> and reports the
/// total, used and used-percent memory. Memory is a level, not a delta — one read answers,
/// no baseline. Used is <c>total − MemAvailable</c>: available is the kernel's own
/// "usable right now" figure (free + reclaimable caches), so the used number is what a
/// scheduler or alert should read, not a free-minus-buffers arithmetic of our own. When
/// <c>MemAvailable</c> is absent (an older kernel) it falls back to
/// <c>MemFree+Buffers+Cached</c>. The read is injected so unit tests feed fixture
/// content; a missing file (a non-Linux node) yields no sample rather than a crash.
/// </summary>
public sealed class MachineMemoryCollector
{
    private readonly Func<string?> _read;

    public MachineMemoryCollector(Func<string?>? read = null)
    {
        _read = read ?? DefaultRead;
    }

    private static string? DefaultRead()
    {
        try
        {
            return File.ReadAllText("/proc/meminfo");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads and parses a sample. False means no value: no file, malformed
    /// content, or a total of zero — all read as absent, never a fake zero.</summary>
    public bool TrySample(out MachineMemorySample sample)
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

    /// <summary>Parses /proc/meminfo content into a sample. Null for a missing,
    /// malformed or empty-total content. Public so the parsing contract is directly
    /// testable.</summary>
    public static MachineMemorySample? Parse(string? meminfo)
    {
        if (meminfo is null)
            return null;
        long? total = null;
        long? available = null;
        long? free = null;
        long? buffers = null;
        long? cached = null;
        foreach (var rawLine in meminfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "MemTotal:       16384000 kB"
            var colon = rawLine.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = rawLine[..colon].Trim();
            var rest = rawLine[(colon + 1)..].Trim();
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !long.TryParse(parts[0], out var value))
                continue;
            var bytes = parts.Length > 1 ? Scale(parts[1], value) : value;
            switch (key)
            {
                case "MemTotal": total = bytes; break;
                case "MemAvailable": available = bytes; break;
                case "MemFree": free = bytes; break;
                case "Buffers": buffers = bytes; break;
                case "Cached": cached = bytes; break;
            }
        }
        if (total is null || total <= 0)
            return null;
        // No usable figure at all (no MemAvailable and no free/buffers/cached): the
        // used and percent cannot be answered honestly — absent, not a fake zero.
        if ((available ?? free + buffers + cached) is not { } usable)
            return null;
        var used = Math.Clamp(total.Value - usable, 0, total.Value);
        return new MachineMemorySample(total.Value, used, used * 100.0 / total.Value);
    }

    private static long Scale(string unit, long value) => unit switch
    {
        "kB" => value * 1024,
        "KB" => value * 1024,
        "MB" => value * 1024 * 1024,
        "GB" => value * 1024L * 1024 * 1024,
        _ => value, // "B" or bare (already bytes)
    };
}
