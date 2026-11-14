using System.Diagnostics;

namespace Chorus.DevTools;

/// <summary>One forwarded log entry on the devtools channel: the level as
/// LogLevel's own member name ("Information", "Warning", ...), the logger
/// category, the wall-clock timestamp, and the formatted body (the message
/// template rendered; a thrown exception appended beneath it).</summary>
public sealed record DevLogEntry(string Level, string Category, long TimestampMs, string Body);

/// <summary>One process metrics snapshot on the devtools channel.
/// CpuPercent carries the EngineHost ProcessCpuGauge's caliber — own-process
/// CPU as a share of total machine capacity (all cores busy = 100), null until
/// the gauge's first sample lands — so the IDE panel and the /metrics face stay
/// one caliber. The default snapshot is process-only; the host supplies the
/// sampler so the numbers come from its own gauge instance.</summary>
public sealed record DevMetricsSnapshot(
    double? CpuPercent, long ManagedMemoryBytes, long WorkingSetBytes, int ThreadCount,
    int ProcessId, long TimestampMs)
{
    public static DevMetricsSnapshot SelfProcess(double? cpuPercent = null) => new(
        cpuPercent,
        GC.GetTotalMemory(false),
        Environment.WorkingSet,
        Process.GetCurrentProcess().Threads.Count,
        Environment.ProcessId,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
