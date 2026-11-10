using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>The telemetry wire mapping (FIX-OBS-06): presence flags separate an absent family
/// from a real zero, and per-mount disk usage carries its bounded mount label.</summary>
public sealed class RedesignObs06MachineTelemetryMapperTests
{
    [Fact]
    public void MapsAllFamiliesWithTheirPresenceAndDiskLabels()
    {
        var sample = new MachineTelemetrySample(
            SampledAtUnixMs: 1_234_567,
            CpuPercent: 42.5,
            Memory: new MachineMemorySample(1024, 512, 50.0),
            Load: new MachineLoadSample(1.5, 1.25, 1.0),
            Disk:
            [
                new MachineDiskSample("/", 1000, 400, 40.0),
                new MachineDiskSample("/data", 2000, 500, 25.0),
            ],
            Process: null);

        var request = MachineTelemetryMapper.ToRequest(sample, "node-a", "agent-1", 7);

        Assert.Equal("node-a", request.NodeId);
        Assert.Equal("agent-1", request.AgentInstanceId);
        Assert.Equal(1_234_567, request.SampledAtUnixMs);
        Assert.Equal(7, request.ReportSequence);
        Assert.True(request.CpuPresent);
        Assert.Equal(42.5, request.CpuPercent, 6);
        Assert.True(request.MemoryPresent);
        Assert.Equal(1024, request.MemoryTotalBytes);
        Assert.Equal(512, request.MemoryUsedBytes);
        Assert.Equal(50.0, request.MemoryUsedPercent, 6);
        Assert.True(request.LoadPresent);
        Assert.Equal(1.5, request.Load1M, 6);
        Assert.Equal(1.0, request.Load15M, 6);
        Assert.Equal(2, request.Disk.Count);
        Assert.Equal("/", request.Disk[0].Mount);
        Assert.Equal(40.0, request.Disk[0].UsedPercent, 6);
        Assert.Equal("/data", request.Disk[1].Mount);
    }

    [Fact]
    public void MapsAbsentFamiliesAsNotPresent()
    {
        var request = MachineTelemetryMapper.ToRequest(
            new MachineTelemetrySample(100, null, null, null, null, null), "node-a", "agent-1", 1);

        Assert.False(request.CpuPresent);
        Assert.False(request.MemoryPresent);
        Assert.False(request.LoadPresent);
        Assert.Empty(request.Disk);
        // The value fields stay zero, but the presence flag is the contract — the receiver
        // reads the value only when present, so zero is never mistaken for a real reading.
        Assert.Equal(0, request.CpuPercent, 6);
    }
}
