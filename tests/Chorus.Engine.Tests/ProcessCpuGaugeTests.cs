using System.Globalization;
using Chorus.EngineHost;
using Chorus.Engine.Session;
using Chorus.Gateway.Core.Metrics;

namespace Chorus.Engine.Tests;

/// <summary>
/// Peak-measurement instrument for the stage-3 trigger (todo §4): the worker
/// must expose its process CPU so "单 worker 高峰 CPU > 60%" becomes a countable
/// number instead of an unmeasurable claim. Math is a pure function of CPU-time
/// delta over wall delta scaled by core count; the gauge omits the line until a
/// previous sample exists.
/// </summary>
public sealed class ProcessCpuGaugeTests
{
    private static (TimeSpan Cpu, DateTimeOffset Wall) At(double cpuSeconds, int wallSeconds) =>
        (TimeSpan.FromSeconds(cpuSeconds), DateTimeOffset.UnixEpoch.AddSeconds(wallSeconds));

    [Fact]
    public void OneCoreFullyBusy_ForOneSecond_Is100Percent()
    {
        var pct = ProcessCpuGauge.Percent(At(10, 0), At(11, 1), coreCount: 1);
        Assert.Equal(100, pct!.Value, 5);
    }

    [Fact]
    public void SameCpuTime_SpreadOverCores_ScalesByCoreCount()
    {
        // 1s of CPU across 4 cores over 1s wall = 25% of total capacity.
        Assert.Equal(25, ProcessCpuGauge.Percent(At(10, 0), At(11, 1), coreCount: 4)!.Value, 5);
        Assert.Equal(50, ProcessCpuGauge.Percent(At(10, 0), At(10.5, 1), coreCount: 1)!.Value, 5);
    }

    [Fact]
    public void ClampsAtTotalCapacity()
    {
        var pct = ProcessCpuGauge.Percent(At(10, 0), At(14, 1), coreCount: 4);
        Assert.Equal(100, pct!.Value, 5);
    }

    [Fact]
    public void ZeroWallDelta_GuardsDivideByZero()
    {
        Assert.Null(ProcessCpuGauge.Percent(At(10, 5), At(11, 5), coreCount: 4));
    }

    [Fact]
    public void BackwardsCpuClock_GuardsNegativeDelta()
    {
        Assert.Null(ProcessCpuGauge.Percent(At(11, 0), At(10, 1), coreCount: 4));
    }

    [Fact]
    public void Gauge_FirstSample_HasNoPrevious_YieldsNull()
    {
        var gauge = new ProcessCpuGauge();
        Assert.Null(gauge.Sample());
    }

    [Fact]
    public void Gauge_SecondSample_ReturnsValueWithinRange()
    {
        var gauge = new ProcessCpuGauge();
        gauge.Sample();
        Thread.Sleep(20);
        var pct = gauge.Sample();
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 0, 100);
    }

    [Fact]
    public void RenderEngine_WithValue_EmitsGaugeLine_InInvariantFormat()
    {
        var text = ChorusMetricsText.RenderEngine(Array.Empty<SessionDiagnostics>(), ipcPublishFailures: 0, processCpuPercent: 37.5);
        Assert.Contains("# TYPE chorus_process_cpu_percent gauge", text, StringComparison.Ordinal);
        Assert.Contains(
            "chorus_process_cpu_percent " + 37.5.ToString("0.##", CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderEngine_WithoutSample_OmitsLine()
    {
        var text = ChorusMetricsText.RenderEngine(Array.Empty<SessionDiagnostics>(), ipcPublishFailures: 0, processCpuPercent: null);
        Assert.DoesNotContain("chorus_process_cpu_percent", text, StringComparison.Ordinal);
    }
}
