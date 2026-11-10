namespace Chorus.NodeAgent;

/// <summary>One supervised process's telemetry (FIX-OBS-07): the CPU usage is a delta over
/// the previous sample (the first sample only establishes its baseline, so it reports 0 —
/// the "no delta yet" starting value, not a measured zero), resident memory and uptime are
/// levels answered in one read.</summary>
public sealed record MachineProcessSample(
    string ProcessInstanceId,
    string Role,
    double CpuPercent,
    long MemoryUsedBytes,
    long UptimeSeconds);

/// <summary>A supervised process the node agent tracks (FIX-OBS-07): the identity the
/// collector needs to read the process's /proc entries and its supervisor-recorded start.</summary>
public sealed record ProcessSource(
    string InstanceId,
    string Role,
    int Pid,
    DateTime StartedAtUtc);

/// <summary>
/// Samples the supervised processes into per-process telemetry (FIX-OBS-07). CPU is a delta
/// over the previous sample per pid (the first sample only establishes its baseline), resident
/// memory is VmRSS (a level), uptime is now minus the supervisor-recorded start. A process whose
/// /proc entry is unreadable (exited, no permission, non-Linux) is skipped — never a fake zero —
/// and an empty or fully-unreadable set reports absent. The sources are injectable so tests feed
/// fixture /proc text; production reads /proc and the bootstrap agent's slot table.
/// </summary>
public sealed class MachineProcessCollector
{
    /// <summary>Linux USER_HZ — the clock ticks per second /proc jiffies are counted in.
    /// Injectable because a test wants a deterministic divisor.</summary>
    private readonly Func<int> _clkTck;
    private readonly Func<long> _nowMs;
    private readonly Func<IReadOnlyList<ProcessSource>> _processes;
    private readonly Func<int, string?> _readStat;
    private readonly Func<int, string?> _readStatus;
    private readonly Dictionary<int, (long Jiffies, long WallMs)> _baselines = [];

    public MachineProcessCollector(
        Func<IReadOnlyList<ProcessSource>>? processes = null,
        Func<int, string?>? readStat = null,
        Func<int, string?>? readStatus = null,
        Func<int>? clkTck = null,
        Func<long>? nowMs = null)
    {
        _processes = processes ?? (() => []);
        _readStat = readStat ?? DefaultRead;
        _readStatus = readStatus ?? DefaultReadStatus;
        _clkTck = clkTck ?? (() => 100);
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static string? DefaultRead(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/stat"); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? DefaultReadStatus(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/status"); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Parses the utime+stime jiffies from a /proc/&lt;pid&gt;/stat line; null when
    /// malformed. The comm field sits in parentheses and may contain spaces, so the state field
    /// is found after the last ')'; fields are 1-based per proc(5): utime=14, stime=15.</summary>
    public static long? ParseJiffies(string? statLine)
    {
        if (statLine is null) return null;
        var close = statLine.LastIndexOf(')');
        if (close < 0 || close + 2 > statLine.Length) return null;
        var rest = statLine[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // rest[0] is state (field 3); utime is field 14 → rest[11]; stime field 15 → rest[12].
        if (rest.Length <= 12) return null;
        if (!long.TryParse(rest[11], out var utime) || !long.TryParse(rest[12], out var stime))
            return null;
        return utime + stime;
    }

    /// <summary>Parses VmRSS (kB) from a /proc/&lt;pid&gt;/status line into bytes; null when the
    /// line is absent or malformed.</summary>
    public static long? ParseRssBytes(string? statusLine)
    {
        if (statusLine is null) return null;
        foreach (var line in statusLine.Split('\n'))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) return null;
            return kb * 1024;
        }
        return null;
    }

    /// <summary>Samples each supervised process. Returns false (absent) when no process produced
    /// a reading — an empty set or every /proc entry unreadable.</summary>
    public bool TrySample(out IReadOnlyList<MachineProcessSample> samples)
    {
        var nowMs = _nowMs();
        var result = new List<MachineProcessSample>();
        foreach (var source in _processes())
        {
            // Unreadable or malformed — skip, never a fake zero.
            if (ParseJiffies(_readStat(source.Pid)) is not { } jiffies) continue;
            if (ParseRssBytes(_readStatus(source.Pid)) is not { } rssBytes) continue;

            var cpuPercent = 0.0;
            if (_baselines.TryGetValue(source.Pid, out var baseline))
            {
                var jiffiesDelta = jiffies - baseline.Jiffies;
                var wallMsDelta = nowMs - baseline.WallMs;
                if (jiffiesDelta >= 0 && wallMsDelta > 0)
                    cpuPercent = jiffiesDelta / (double)_clkTck() * 1000.0 / wallMsDelta * 100.0;
            }
            _baselines[source.Pid] = (jiffies, nowMs);

            var uptimeSeconds = (long)Math.Max(0, (nowMs / 1000.0) - (source.StartedAtUtc
                .ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds);
            result.Add(new MachineProcessSample(
                source.InstanceId, source.Role, cpuPercent, rssBytes, uptimeSeconds));
        }

        samples = result;
        return result.Count > 0;
    }
}
