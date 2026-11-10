using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine memory collector (FIX-OBS-03): /proc/meminfo parses into a total/used/
/// percent sample where used is total minus MemAvailable (the kernel's "usable now"
/// figure), MemFree+Buffers+Cached is the older-kernel fallback, kB scales to bytes,
/// and a missing or unusable source yields no value instead of a crash or a fake zero.
/// </summary>
public sealed class RedesignObs03MachineMemoryCollectorTests
{
    private const string Meminfo =
        """
        MemTotal:       16384000 kB
        MemFree:         4096000 kB
        MemAvailable:    8192000 kB
        Buffers:          819200 kB
        Cached:          2048000 kB
        SwapCached:            0 kB
        """;

    [Fact]
    public void UsesMemAvailableForTheUsedFigure()
    {
        var sample = MachineMemoryCollector.Parse(Meminfo);
        Assert.NotNull(sample);
        // MemTotal 16384000 kB * 1024 = 16777216000 bytes; MemAvailable 8192000 kB.
        Assert.Equal(16777216000L, sample.Value.TotalBytes);
        // used = total - available = 8192000 kB = 8388608000 bytes.
        Assert.Equal(8388608000L, sample.Value.UsedBytes);
        // used/total = 0.5.
        Assert.Equal(50.0, sample.Value.UsedPercent, 6);
    }

    [Fact]
    public void FallsBackToFreePlusBuffersPlusCachedWithoutMemAvailable()
    {
        var meminfo =
            """
            MemTotal:       1000000 kB
            MemFree:          200000 kB
            Buffers:          100000 kB
            Cached:           200000 kB
            """;
        var sample = MachineMemoryCollector.Parse(meminfo);
        Assert.NotNull(sample);
        // available falls back to 200000+100000+200000 = 500000 kB.
        Assert.Equal(500000L * 1024, sample.Value.UsedBytes);
        Assert.Equal(50.0, sample.Value.UsedPercent, 6);
    }

    [Fact]
    public void ClampsUsedIntoTheTotalRange()
    {
        // A kernel reporting more available than total must not produce a negative used.
        var meminfo = "MemTotal: 1000 kB\nMemAvailable: 1500 kB\n";
        var sample = MachineMemoryCollector.Parse(meminfo);
        Assert.NotNull(sample);
        Assert.Equal(0, sample.Value.UsedBytes);
        Assert.Equal(0.0, sample.Value.UsedPercent, 6);
    }

    [Fact]
    public void ReturnsNullForMissingMalformedOrUnusableContent()
    {
        Assert.Null(MachineMemoryCollector.Parse(null));
        Assert.Null(MachineMemoryCollector.Parse(""));
        Assert.Null(MachineMemoryCollector.Parse("MemFree: 100 kB\n"));
        // No usable figure at all: total but no available/free/buffers/cached.
        Assert.Null(MachineMemoryCollector.Parse("MemTotal: 1000 kB\nSwapTotal: 0 kB\n"));
        Assert.Null(MachineMemoryCollector.Parse("not a meminfo file\n"));
    }

    [Fact]
    public void TheCollectorAnswersFromTheInjectedSource()
    {
        string? content = Meminfo;
        var collector = new MachineMemoryCollector(() => content);
        Assert.True(collector.TrySample(out var sample));
        Assert.Equal(50.0, sample.UsedPercent, 6);

        // A non-Linux node has no /proc/meminfo: absent, never a fake zero.
        content = null;
        Assert.False(collector.TrySample(out _));
    }
}
