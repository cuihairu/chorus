using System.Diagnostics.Metrics;
using DataServerDump = Chorus.DataServer.MetricsConsoleDump;
using RoomDump = Chorus.GameServer.MetricsConsoleDump;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-PERF-05 measurement channel: the hosts' opt-in meter dump must turn recorded samples
/// into stable percentile lines — the benchmark parses these straight out of the captured
/// host stdout, so the format (name, count, p50/p95/p99/max) is the contract.
/// </summary>
public sealed class RedesignMetricsDumpUnitTests
{
    [Fact]
    public void TheRoomDumpCarriesWholeRunPercentiles()
    {
        RoomDump.Start(TimeSpan.FromHours(1));
        using var meter = new Meter("Chorus.Room");
        var histogram = meter.CreateHistogram<double>("unit.dump.room");
        for (var value = 1; value <= 100; value++)
            histogram.Record(value);
        var snapshot = RoomDump.RenderSnapshot();
        Assert.Contains("name=unit.dump.room count=100", snapshot);
        Assert.Contains("p50=51.0ms", snapshot);
        Assert.Contains("p95=96.0ms", snapshot);
        Assert.Contains("max=100.0ms", snapshot);
    }

    [Fact]
    public void TheStorageDumpCarriesWholeRunPercentiles()
    {
        DataServerDump.Start(TimeSpan.FromHours(1));
        using var meter = new Meter("Chorus.Data");
        var histogram = meter.CreateHistogram<double>("unit.dump.storage");
        for (var value = 1; value <= 10; value++)
            histogram.Record(value * 10);
        var snapshot = DataServerDump.RenderSnapshot();
        Assert.Contains("name=unit.dump.storage count=10", snapshot);
        Assert.Contains("p50=60.0ms", snapshot);
        Assert.Contains("max=100.0ms", snapshot);
    }
}
