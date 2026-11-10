using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine telemetry sampler (FIX-OBS-06): one snapshot aggregates the four collectors
/// with each collector's own contract — CPU is a delta so the first sample only establishes
/// its baseline and reports no percent, memory/load/disk are levels answered in one read,
/// and a family whose source is absent (a non-Linux node) is reported as absent, never a
/// fake zero.
/// </summary>
public sealed class RedesignObs06MachineTelemetrySamplerTests
{
    private const string FirstCpu = "cpu 100 0 100 800 100 0 0 0";
    private const string SecondCpu = "cpu 200 0 200 1600 200 0 0 0";
    private const string Meminfo = "MemTotal: 16384000 kB\nMemAvailable: 8192000 kB\n";
    private const string Loadavg = "0.15 0.08 0.05 1/234 5678";
    private const string DfOutput =
        """
        Filesystem 1024-blocks Used Available Capacity Mounted on
        /dev/sda1 10240000 2048000 8192000 20% /
        """;

    [Fact]
    public void AggregatesAllFourFamiliesOnceCpuHasItsBaseline()
    {
        string cpu = FirstCpu;
        var sampler = new MachineTelemetrySampler(
            new MachineCpuCollector(() => cpu),
            new MachineMemoryCollector(() => Meminfo),
            new MachineLoadCollector(() => Loadavg),
            new MachineDiskCollector(() => DfOutput));

        var first = sampler.Sample(1_000);
        Assert.Null(first.CpuPercent); // baseline only
        Assert.NotNull(first.Memory);
        Assert.NotNull(first.Load);
        Assert.NotNull(first.Disk);

        cpu = SecondCpu;
        var second = sampler.Sample(2_000);
        // idleΔ=900, totalΔ=1100 → (1-900/1100)*100 = 18.1818%.
        Assert.Equal(18.1818, second.CpuPercent!.Value, 4);
        Assert.Equal(50.0, second.Memory!.Value.UsedPercent, 6);
        Assert.Equal(0.15, second.Load!.Value.Load1m, 6);
        Assert.Single(second.Disk!);
        Assert.Equal("/", second.Disk[0].Mount);
        Assert.Equal(20.0, second.Disk[0].UsedPercent, 6);
    }

    [Fact]
    public void ReportsAbsentFamiliesAsNullNeverFakeZero()
    {
        // A non-Linux node has no /proc/stat, /proc/meminfo, /proc/loadavg or df: every
        // family is absent, and no family pretends to a zero value.
        var sampler = new MachineTelemetrySampler(
            new MachineCpuCollector(() => null),
            new MachineMemoryCollector(() => null),
            new MachineLoadCollector(() => null),
            new MachineDiskCollector(() => null));
        var sample = sampler.Sample(1_000);
        Assert.Null(sample.CpuPercent);
        Assert.Null(sample.Memory);
        Assert.Null(sample.Load);
        Assert.Null(sample.Disk);
    }

    [Fact]
    public void AbsentFamiliesDoNotMaskThePresentOnes()
    {
        // Memory/load/disk absent, CPU present (after its baseline): the snapshot keeps the
        // present family and reports the others absent — one bad source never blanks the rest.
        string cpu = FirstCpu;
        var sampler = new MachineTelemetrySampler(
            new MachineCpuCollector(() => cpu),
            new MachineMemoryCollector(() => null),
            new MachineLoadCollector(() => null),
            new MachineDiskCollector(() => null));
        _ = sampler.Sample(1_000);
        cpu = SecondCpu;
        var sample = sampler.Sample(2_000);
        Assert.NotNull(sample.CpuPercent);
        Assert.Null(sample.Memory);
        Assert.Null(sample.Load);
        Assert.Null(sample.Disk);
    }
}
