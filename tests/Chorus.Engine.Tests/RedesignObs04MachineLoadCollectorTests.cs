using Chorus.NodeAgent;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine load collector (FIX-OBS-04): /proc/loadavg parses into the 1/5/15-minute
/// averages, a missing or malformed line yields no value, and a negative average (which
/// the kernel never emits) is refused as malformed rather than reported.
/// </summary>
public sealed class RedesignObs04MachineLoadCollectorTests
{
    [Fact]
    public void ParsesTheThreeAverages()
    {
        var sample = MachineLoadCollector.Parse("0.15 0.08 0.05 1/234 5678");
        Assert.NotNull(sample);
        Assert.Equal(0.15, sample.Value.Load1m, 6);
        Assert.Equal(0.08, sample.Value.Load5m, 6);
        Assert.Equal(0.05, sample.Value.Load15m, 6);
    }

    [Fact]
    public void ParsesZeroLoad()
    {
        var sample = MachineLoadCollector.Parse("0.00 0.00 0.00 0/1 100");
        Assert.NotNull(sample);
        Assert.Equal(0.0, sample.Value.Load1m, 6);
    }

    [Fact]
    public void ReturnsNullForMissingOrMalformedLines()
    {
        Assert.Null(MachineLoadCollector.Parse(null));
        Assert.Null(MachineLoadCollector.Parse(""));
        Assert.Null(MachineLoadCollector.Parse("0.15 0.08"));
        Assert.Null(MachineLoadCollector.Parse("zero 0.08 0.05 1/2 3"));
        Assert.Null(MachineLoadCollector.Parse("-1.00 0.08 0.05 1/2 3"));
    }

    [Fact]
    public void TheCollectorAnswersFromTheInjectedSource()
    {
        string? line = "1.50 1.25 1.00 3/400 9999";
        var collector = new MachineLoadCollector(() => line);
        Assert.True(collector.TrySample(out var sample));
        Assert.Equal(1.50, sample.Load1m, 6);
        Assert.Equal(1.00, sample.Load15m, 6);

        line = null;
        Assert.False(collector.TrySample(out _));
    }
}
