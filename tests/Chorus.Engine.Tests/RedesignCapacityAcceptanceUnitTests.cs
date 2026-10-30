namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-PERF-04: the performance gate itself must be fail-able and honest. An injected
/// over-budget latency fails the verdict; a met target passes; a disabled run reports
/// NOT RUN and can never be read as a pass; the report names the environment, the duration
/// and every budget it applied; a missing or malformed config fails closed.
/// </summary>
public sealed class RedesignCapacityAcceptanceUnitTests
{
    private static readonly CapacityAcceptanceConfig Config = new(
        Rooms: 100, TickHz: 20, TicksPerRoom: 100, HotRoomEventsPerSecond: 500,
        AggregateFrameRateFloor: 2000, PerRoomCompletionP95BudgetMs: 5000,
        CommonWindowFloorMs: 250, PlanFillFloor: 0.95);

    private static readonly DateTime Started = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ended = new(2026, 10, 30, 10, 0, 12, DateTimeKind.Utc);

    private static CapacityMeasurement MetTarget() => new(
        Rooms: 100, TicksPerRoom: 100, FramesCommitted: 10_000, FramesExpected: 10_000,
        AggregateFrameRate: 2050, PerRoomCompletionP95Ms: 4600, CommonWindowMs: 400,
        PlannedInputs: 50_000, EmittedInputs: 49_500, BacklogInputs: 500, InputFirstError: null);

    [Fact]
    public void AnInjectedOverBudgetLatencyFailsTheAcceptance()
    {
        // The dev-baseline shape: the machine ran, but its latencies sit far over the target.
        var overBudget = MetTarget() with
        {
            AggregateFrameRate = 90, PerRoomCompletionP95Ms = 104_924, CommonWindowMs = 69538
        };
        var report = CapacityAcceptance.Evaluate(overBudget, Config, Started, Ended);
        Assert.Equal(CapacityVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons,
            reason => reason.Contains("aggregate frame rate") && reason.Contains("2000"));
        Assert.Contains(report.Reasons,
            reason => reason.Contains("completion p95") && reason.Contains("104924"));
    }

    [Fact]
    public void AMetTargetPassesTheAcceptance()
    {
        var report = CapacityAcceptance.Evaluate(MetTarget(), Config, Started, Ended);
        Assert.Equal(CapacityVerdict.Passed, report.Verdict);
        Assert.Empty(report.Reasons);
    }

    [Fact]
    public void AShortfallPlanFailsEvenWhenEveryLatencyBudgetPasses()
    {
        var shortfall = MetTarget() with { EmittedInputs = 1_626, BacklogInputs = 53_710 };
        var report = CapacityAcceptance.Evaluate(shortfall, Config, Started, Ended);
        Assert.Equal(CapacityVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons, reason => reason.Contains("never replaces the target"));
    }

    [Fact]
    public void ADishonestWorkloadShapeFailsTheAcceptance()
    {
        var shrunk = MetTarget() with { Rooms = 10, FramesCommitted = 1_000, FramesExpected = 1_000 };
        var report = CapacityAcceptance.Evaluate(shrunk, Config, Started, Ended);
        Assert.Equal(CapacityVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons, reason => reason.Contains("does not match the acceptance target"));
    }

    [Fact]
    public void ADisabledRunIsNotRunAndNeverCountsAsPassed()
    {
        var report = CapacityAcceptance.NotRun(Config,
            "set CHORUS_RUN_M2_CAPACITY=1 to opt in — this run does not count as a capacity pass",
            Started, Ended);
        Assert.Equal(CapacityVerdict.NotRun, report.Verdict);
        Assert.NotEqual(CapacityVerdict.Passed, report.Verdict);
        Assert.Null(report.Measurement);
        var text = report.Describe();
        Assert.Contains("NOT RUN", text);
        Assert.Contains("does not count as a capacity pass", text);
    }

    [Fact]
    public void TheReportCarriesEnvironmentDurationAndEveryBudget()
    {
        var report = CapacityAcceptance.Evaluate(MetTarget(), Config, Started, Ended);
        var text = report.Describe();
        Assert.Contains("cpu", text);          // the environment it ran in
        Assert.Contains("ran 12.0s", text);    // how long it ran
        Assert.Contains("2000", text);         // aggregate frame rate floor
        Assert.Contains("5000 ms", text);      // per-room completion p95 budget
        Assert.Contains("250 ms", text);       // common window floor
        Assert.Contains("95%", text);          // plan fill floor
        Assert.Contains("2050.0 frames/s", text);
        Assert.Contains("\nmeasured: ", text); // the measured evidence is its own line
    }

    [Fact]
    public void TheShippedConfigCarriesTheTargetArchitecture()
    {
        var config = CapacityAcceptanceConfigLoaderForTest();
        Assert.Equal(100, config.Rooms);
        Assert.Equal(20, config.TickHz);
        Assert.Equal(500, config.HotRoomEventsPerSecond);
        Assert.True(config.AggregateFrameRateFloor >= config.Rooms * config.TickHz,
            "the frame-rate floor must stand for the full target architecture, not a fraction of it");
    }

    [Fact]
    public void AMissingConfigFailsClosed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "chorus-capacity-missing-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<InvalidOperationException>(() => CapacityAcceptance.LoadConfig(missing));
        Assert.Contains("refuses to run without its budgets", error.Message);
    }

    [Fact]
    public void AMalformedConfigFailsClosed()
    {
        var malformed = Path.Combine(Path.GetTempPath(), "chorus-capacity-malformed-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(malformed, """{"rooms":0,"tickHz":20,"ticksPerRoom":100,"hotRoomEventsPerSecond":500,"aggregateFrameRateFloor":2000,"perRoomCompletionP95BudgetMs":5000,"commonWindowFloorMs":250,"planFillFloor":0.95}""");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => CapacityAcceptance.LoadConfig(malformed));
            Assert.Contains("malformed", error.Message);
        }
        finally
        {
            File.Delete(malformed);
        }
    }

    /// <summary>Loads the shipped config from the test output directory (copied by the csproj).</summary>
    private static CapacityAcceptanceConfig CapacityAcceptanceConfigLoaderForTest() =>
        CapacityAcceptance.LoadConfig();
}
