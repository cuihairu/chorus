namespace Chorus.NodeAgent;

/// <summary>One machine telemetry snapshot (FIX-OBS-06): the sampled machine resource
/// families at one moment. A family is null when the collector reports no value — absent,
/// never a fake zero (non-Linux, no CPU baseline yet, malformed source, no usable mount).</summary>
public sealed record MachineTelemetrySample(
    long SampledAtUnixMs,
    double? CpuPercent,
    MachineMemorySample? Memory,
    MachineLoadSample? Load,
    IReadOnlyList<MachineDiskSample>? Disk);

/// <summary>
/// Samples the four machine collectors into one telemetry snapshot (FIX-OBS-06). The
/// collectors are injectable so tests feed fixture read sources; production uses the
/// default file-backed collectors. Each collector's own contract stands: CPU is a delta
/// (the first sample only establishes its baseline, so the first snapshot reports no CPU),
/// memory/load/disk are levels (one read answers).
/// </summary>
public sealed class MachineTelemetrySampler(
    MachineCpuCollector? cpu = null,
    MachineMemoryCollector? memory = null,
    MachineLoadCollector? load = null,
    MachineDiskCollector? disk = null)
{
    private readonly MachineCpuCollector _cpu = cpu ?? new MachineCpuCollector();
    private readonly MachineMemoryCollector _memory = memory ?? new MachineMemoryCollector();
    private readonly MachineLoadCollector _load = load ?? new MachineLoadCollector();
    private readonly MachineDiskCollector _disk = disk ?? new MachineDiskCollector();

    /// <summary>Reads one snapshot at the given node-clock millisecond.</summary>
    public MachineTelemetrySample Sample(long sampledAtUnixMs)
    {
        var cpuPercent = _cpu.TrySample(out var cpuValue) ? cpuValue : (double?)null;
        var memorySample = _memory.TrySample(out var memoryValue) ? memoryValue : (MachineMemorySample?)null;
        var loadSample = _load.TrySample(out var loadValue) ? loadValue : (MachineLoadSample?)null;
        var diskSamples = _disk.TrySample(out var diskValue) ? diskValue : null;
        return new MachineTelemetrySample(sampledAtUnixMs, cpuPercent, memorySample, loadSample, diskSamples);
    }
}
