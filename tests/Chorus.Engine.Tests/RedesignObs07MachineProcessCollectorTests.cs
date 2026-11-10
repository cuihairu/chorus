using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The supervised-process collector (FIX-OBS-07): jiffies come from /proc/&lt;pid&gt;/stat after the
/// comm parentheses (comm may contain spaces), CPU percent is a delta over the previous sample
/// per pid (the first sample only establishes its baseline — 0 is the "no delta yet" starting
/// value, not a measured zero), VmRSS is a level read once, uptime is now minus the
/// supervisor-recorded start, and a process whose /proc entry is unreadable is skipped — never
/// a fake zero. Tests feed fixture /proc text; production reads the files and the agent's slot
/// table.
/// </summary>
public sealed class RedesignObs07MachineProcessCollectorTests
{
    private const string Stat = "123 (agent  worker) R 1 2 3 0 -1 4194304 0 0 0 0 100 50 " +
        "0 0 0 0 0 0 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
    private const string Status = "Name:\tchorus-agent\nVmRSS:\t  2048 kB\nThreads:\t4\n";

    [Fact]
    public void ParsesJiffiesAfterTheCommParentheses()
    {
        // comm "agent  worker" contains spaces; utime (field 14) = 100, stime (field 15) = 50.
        Assert.Equal(150, MachineProcessCollector.ParseJiffies(Stat));
        Assert.Null(MachineProcessCollector.ParseJiffies("123 no parens here"));
        Assert.Null(MachineProcessCollector.ParseJiffies("123 (agent) R 1 2"));
        Assert.Null(MachineProcessCollector.ParseJiffies(null));
    }

    [Fact]
    public void ParsesVmRssIntoBytes()
    {
        Assert.Equal(2048L * 1024, MachineProcessCollector.ParseRssBytes(Status));
        Assert.Null(MachineProcessCollector.ParseRssBytes("Name:\tchorus-agent\n"));
        Assert.Null(MachineProcessCollector.ParseRssBytes(null));
    }

    [Fact]
    public void CpuPercentIsADeltaAndTheFirstSampleOnlyEstablishesTheBaseline()
    {
        // 50 jiffies over 2000 ms at CLK_TCK 100 = 500 ms of CPU in 2000 ms of wall = 25%.
        var jiffies = new Dictionary<int, long> { [11] = 1000, [12] = 1050 };
        var now = 1000L;
        var collector = new MachineProcessCollector(
            processes: () => [new ProcessSource("p-1", "gateway", 11, DateTimeOffset.FromUnixTimeSeconds(100).UtcDateTime)],
            readStat: pid => $"1 (proc) R 0 0 0 0 0 0 0 0 0 0 {jiffies[pid]} 0",
            readStatus: _ => Status,
            clkTck: () => 100,
            nowMs: () => now);
        collector.TrySample(out var samples);
        Assert.Equal(0, samples[0].CpuPercent, 6); // baseline, not a measured zero

        jiffies[11] = 1050;
        now = 3000;
        collector.TrySample(out samples);
        Assert.Equal(25.0, samples[0].CpuPercent, 6);
    }

    [Fact]
    public void UptimeIsNowMinusTheSupervisorRecordedStart()
    {
        var collector = new MachineProcessCollector(
            processes: () => [new ProcessSource("p-1", "gateway", 11, DateTimeOffset.FromUnixTimeSeconds(100).UtcDateTime)],
            readStat: _ => Stat,
            readStatus: _ => Status,
            nowMs: () => 250_000);
        collector.TrySample(out var samples);
        Assert.Equal(150, samples[0].UptimeSeconds);
    }

    [Fact]
    public void SkipsAProcessWhoseProcEntryIsUnreadable()
    {
        var collector = new MachineProcessCollector(
            processes: () =>
            [
                new ProcessSource("p-exited", "gateway", 11, DateTime.UtcNow),
                new ProcessSource("p-live", "gateway", 12, DateTime.UtcNow),
            ],
            readStat: pid => pid == 11 ? null : Stat, // the exited process has no /proc entry
            readStatus: pid => pid == 11 ? null : Status);
        Assert.True(collector.TrySample(out var samples));
        var entry = Assert.Single(samples);
        Assert.Equal("p-live", entry.ProcessInstanceId);
    }

    [Fact]
    public void AbsentWhenNoProcessProducesAReading()
    {
        var empty = new MachineProcessCollector(processes: () => []);
        Assert.False(empty.TrySample(out var none));
        Assert.Empty(none);

        var unreadable = new MachineProcessCollector(
            processes: () => [new ProcessSource("p-1", "gateway", 11, DateTime.UtcNow)],
            readStat: _ => null, readStatus: _ => null);
        Assert.False(unreadable.TrySample(out var absent));
        Assert.Empty(absent);
    }

    [Fact]
    public void AnswersEverySupervisedProcess()
    {
        var collector = new MachineProcessCollector(
            processes: () =>
            [
                new ProcessSource("p-1", "gateway", 11, DateTime.UtcNow),
                new ProcessSource("p-2", "game-server", 12, DateTime.UtcNow),
            ],
            readStat: _ => Stat,
            readStatus: _ => Status);
        Assert.True(collector.TrySample(out var samples));
        Assert.Equal(2, samples.Count);
        Assert.Equal("gateway", samples[0].Role);
        Assert.Equal("p-2", samples[1].ProcessInstanceId);
        Assert.Equal(2048L * 1024, samples[1].MemoryUsedBytes);
    }
}

/// <summary>The sampler folds the process family into the machine snapshot without masking
/// the other families: an absent process family stays null while the machine families answer
/// (FIX-OBS-07).</summary>
public sealed class RedesignObs07MachineTelemetrySamplerTests
{
    [Fact]
    public void FoldsTheProcessFamilyIntoTheSnapshot()
    {
        var sampler = new MachineTelemetrySampler(
            process: new MachineProcessCollector(
                processes: () => [new ProcessSource("p-1", "gateway", 11, DateTime.UtcNow)],
                readStat: _ => MachineProcessCollectorFixture.Stat,
                readStatus: _ => MachineProcessCollectorFixture.Status));
        var sample = sampler.Sample(1_000);
        Assert.NotNull(sample.Process);
        Assert.Equal("p-1", sample.Process[0].ProcessInstanceId);
    }

    [Fact]
    public void AnAbsentProcessFamilyDoesNotMaskTheMachineFamilies()
    {
        var sampler = new MachineTelemetrySampler(
            load: new MachineLoadCollector(() => "1.00 0.50 0.25 1/100 1234\n"),
            process: new MachineProcessCollector(processes: () => []));
        var sample = sampler.Sample(1_000);
        Assert.Null(sample.Process);
        Assert.True(sample.Load.HasValue); // machine families still answer
        Assert.Equal(1.0, sample.Load!.Value.Load1m, 6);
    }
}

/// <summary>The process entries ride the telemetry request next to the disk entries, each
/// carrying its instance id, role, CPU percent, resident memory and uptime (FIX-OBS-07).</summary>
public sealed class RedesignObs07MachineTelemetryMapperTests
{
    [Fact]
    public void MapsProcessEntriesOntoTheRequest()
    {
        var sample = new MachineTelemetrySample(
            SampledAtUnixMs: 1_000,
            CpuPercent: null, Memory: null, Load: null, Disk: null,
            Process:
            [
                new MachineProcessSample("p-1", "gateway", 12.5, 2048L * 1024, 150),
                new MachineProcessSample("p-2", "game-server", 0, 4096, 60),
            ]);

        var request = MachineTelemetryMapper.ToRequest(sample, "node-a", "agent-1", 3);

        Assert.Equal(2, request.Process.Count);
        Assert.Equal("p-1", request.Process[0].ProcessInstanceId);
        Assert.Equal("gateway", request.Process[0].Role);
        Assert.Equal(12.5, request.Process[0].CpuPercent, 6);
        Assert.Equal(2048L * 1024, request.Process[0].MemoryUsedBytes);
        Assert.Equal(150, request.Process[0].UptimeSeconds);
        Assert.Equal("game-server", request.Process[1].Role);
    }

    [Fact]
    public void AnAbsentProcessFamilyLeavesTheRequestFieldEmpty()
    {
        var request = MachineTelemetryMapper.ToRequest(
            new MachineTelemetrySample(100, null, null, null, null, null), "node-a", "agent-1", 1);
        Assert.Empty(request.Process);
    }
}

/// <summary>Shared fixture /proc text for the collector tests.</summary>
internal static class MachineProcessCollectorFixture
{
    public const string Stat = "123 (proc) R 1 2 3 0 -1 4194304 0 0 0 0 100 50 " +
        "0 0 0 0 0 0 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
    public const string Status = "Name:\tproc\nVmRSS:\t  2048 kB\n";
}
