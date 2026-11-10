namespace Chorus.NodeAgent;

/// <summary>One mounted-filesystem sample (FIX-OBS-05): capacity and usage per mount.</summary>
public readonly record struct MachineDiskSample(string Mount, long TotalBytes, long UsedBytes, double UsedPercent);

/// <summary>
/// The machine disk collector (FIX-OBS-05): reads <c>df -Pk</c> and reports, per mounted
/// filesystem, the total, used and used-percent bytes. Disk usage is a level — one read
/// answers, no baseline. The <c>-P</c> (POSIX) flag keeps one filesystem per line so long
/// mount paths never wrap; <c>-k</c> gives 1024-byte blocks. Pseudo and memory-backed
/// filesystems (tmpfs, proc, sysfs, …) are not disk and are skipped, as are zero-capacity
/// entries; <c>overlay</c> (the container root, backed by real storage) is kept. The read
/// is injected so unit tests feed fixture output; a missing or failed <c>df</c> (a
/// non-Linux node) yields no sample rather than a crash.
/// </summary>
public sealed class MachineDiskCollector
{
    /// <summary>Filesystem/device names that never represent disk storage: pseudo and
    /// memory-backed filesystems. Matched on the first <c>df</c> column.</summary>
    private static readonly HashSet<string> PseudoFilesystems = new(StringComparer.Ordinal)
    {
        "tmpfs", "devtmpfs", "proc", "sysfs", "devpts", "cgroup", "cgroup2",
        "mqueue", "shm", "securityfs", "pstore", "bpf", "tracefs", "debugfs",
        "configfs", "hugetlbfs", "ramfs", "efivarfs", "selinuxfs", "nsfs",
        "autofs", "binfmt_misc", "rpc_pipefs", "fusectl", "rootfs",
    };

    private readonly Func<string?> _read;

    public MachineDiskCollector(Func<string?>? read = null)
    {
        _read = read ?? DefaultRead;
    }

    private static string? DefaultRead()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("df", "-Pk")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads and parses the mounted filesystems. False means no value: no df,
    /// malformed output, or no usable mount — read as absent, never a fake zero.</summary>
    public bool TrySample(out IReadOnlyList<MachineDiskSample> samples)
    {
        var parsed = Parse(_read());
        if (parsed is null)
        {
            samples = Array.Empty<MachineDiskSample>();
            return false;
        }
        samples = parsed;
        return true;
    }

    /// <summary>Parses <c>df -Pk</c> output into one sample per usable mounted filesystem.
    /// Null for missing or unusable content (no usable mount). Public so the parsing
    /// contract is directly testable.</summary>
    public static IReadOnlyList<MachineDiskSample>? Parse(string? dfOutput)
    {
        if (dfOutput is null)
            return null;
        var samples = new List<MachineDiskSample>();
        var sawHeader = false;
        foreach (var rawLine in dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd();
            if (!sawHeader)
            {
                if (line.StartsWith("Filesystem", StringComparison.Ordinal))
                    sawHeader = true;
                continue;
            }
            // "dev blocks used available capacity% mount" — POSIX single-space fields;
            // the mount point is the last field and may contain escaped spaces (\040).
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
                continue;
            var device = fields[0];
            if (PseudoFilesystems.Contains(device))
                continue;
            if (!long.TryParse(fields[1], out var blocks) || blocks <= 0)
                continue;
            if (!long.TryParse(fields[2], out var usedBlocks) || usedBlocks < 0)
                continue;
            var mount = string.Join(' ', fields.Skip(5)).Replace("\\040", " ");
            var total = blocks * 1024L;
            var used = usedBlocks * 1024L;
            samples.Add(new MachineDiskSample(mount, total, used, used * 100.0 / total));
        }
        return samples.Count == 0 ? null : samples;
    }
}
