namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-11: the frame-channel gate itself must be fail-able and honest. An injected
/// over-budget dispatch latency fails the verdict; a met target passes; a frame loss or an
/// unintended outbox drop fails even when every latency budget passes; a disabled host run
/// reports NOT RUN and can never be read as a pass; the report names the environment, the
/// duration and every budget it applied; the shipped config must stand for 3x the §11
/// aggregate, not a fraction of it; a missing or malformed config fails closed.
/// </summary>
public sealed class RedesignFrameChannelAcceptanceUnitTests
{
    private static readonly FrameChannelUnitConfig UnitConfig = new(
        Rooms: 20, SeatsPerRoom: 2, TargetAggregateFrameRatePerSecond: 6000,
        RunSeconds: 3.0, FramePayloadBytes: 4096, AggregateFrameRateFloor: 5700,
        DispatchLatencyP95BudgetMs: 5.0, DispatchLatencyP99BudgetMs: 12.0,
        UnintendedDropsBudget: 0);

    private static readonly DateTime Started = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ended = new(2026, 11, 6, 10, 0, 3, DateTimeKind.Utc);

    private static FrameChannelMeasurement MetTarget() => new(
        Rooms: 20, SeatsPerRoom: 2, FramesDispatched: 36_000, FramesDelivered: 36_000,
        AggregateFrameRate: 5980, DispatchLatencyP50Ms: 0.08, DispatchLatencyP95Ms: 0.21,
        DispatchLatencyP99Ms: 0.42, UnintendedDrops: 0, FirstError: null);

    [Fact]
    public void AnInjectedOverBudgetDispatchLatencyFailsTheAcceptance()
    {
        var overBudget = MetTarget() with { DispatchLatencyP95Ms = 6.4, DispatchLatencyP99Ms = 15.8 };
        var report = FrameChannelAcceptance.Evaluate(overBudget, UnitConfig, Started, Ended);
        Assert.Equal(FrameChannelVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons,
            reason => reason.Contains("dispatch p95") && reason.Contains("6.40") && reason.Contains("5"));
        Assert.Contains(report.Reasons,
            reason => reason.Contains("dispatch p99") && reason.Contains("15.80") && reason.Contains("12"));
    }

    [Fact]
    public void AMetTargetPassesTheAcceptance()
    {
        var report = FrameChannelAcceptance.Evaluate(MetTarget(), UnitConfig, Started, Ended);
        Assert.Equal(FrameChannelVerdict.Passed, report.Verdict);
        Assert.Empty(report.Reasons);
    }

    [Fact]
    public void AFrameLossFailsEvenWhenEveryLatencyBudgetPasses()
    {
        var lossy = MetTarget() with { FramesDelivered = 33_000, AggregateFrameRate = 5500 };
        var report = FrameChannelAcceptance.Evaluate(lossy, UnitConfig, Started, Ended);
        Assert.Equal(FrameChannelVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons, reason => reason.Contains("a frame was lost before the transport"));
        Assert.Contains(report.Reasons, reason => reason.Contains("below the floor"));
    }

    [Fact]
    public void AnUnintendedDropFailsEvenWhenTheRateHolds()
    {
        var dropping = MetTarget() with { UnintendedDrops = 3 };
        var report = FrameChannelAcceptance.Evaluate(dropping, UnitConfig, Started, Ended);
        Assert.Equal(FrameChannelVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons,
            reason => reason.Contains("dropped") && reason.Contains("must not overflow the send queue"));
    }

    [Fact]
    public void ADishonestWorkloadShapeFailsTheAcceptance()
    {
        var shrunk = MetTarget() with { Rooms = 2, FramesDispatched = 3_600, FramesDelivered = 3_600 };
        var report = FrameChannelAcceptance.Evaluate(shrunk, UnitConfig, Started, Ended);
        Assert.Equal(FrameChannelVerdict.Failed, report.Verdict);
        Assert.Contains(report.Reasons, reason => reason.Contains("does not match the acceptance target"));
    }

    [Fact]
    public void ADisabledHostRunIsNotRunAndNeverCountsAsPassed()
    {
        var report = FrameChannelAcceptance.NotRun(new FrameChannelHostConfig(
                Rooms: 10, TickMs: 50, TicksPerRoom: 100, WarmupTicks: 10,
                AggregateFrameRateFloor: 180, CommitToClientP95BudgetMs: 25.0,
                CommitToClientP99BudgetMs: 50.0, UnintendedRebaselinesBudget: 0),
            "set CHORUS_RUN_FRAME_CHANNEL=1 to opt in — this run does not count as a frame-channel pass",
            Started, Ended);
        Assert.Equal(FrameChannelVerdict.NotRun, report.Verdict);
        Assert.NotEqual(FrameChannelVerdict.Passed, report.Verdict);
        Assert.Null(report.UnitMeasurement);
        Assert.Null(report.HostMeasurement);
        var text = report.Describe();
        Assert.Contains("NOT RUN", text);
        Assert.Contains("does not count as a frame-channel pass", text);
    }

    [Fact]
    public void TheReportCarriesEnvironmentDurationAndEveryBudget()
    {
        var report = FrameChannelAcceptance.Evaluate(MetTarget(), UnitConfig, Started, Ended);
        var text = report.Describe();
        Assert.Contains("cpu", text);          // the environment it ran in
        Assert.Contains("ran 3.0s", text);     // how long it ran
        Assert.Contains("5700", text);         // aggregate frame rate floor
        Assert.Contains("5 ms", text);         // dispatch p95 budget
        Assert.Contains("12 ms", text);        // dispatch p99 budget
        Assert.Contains("5980.0 frames/s", text);
        Assert.Contains("\nunit measured: ", text); // the measured evidence is its own line
    }

    [Fact]
    public void TheShippedConfigCarriesTheTargetArchitecture()
    {
        var config = FrameChannelAcceptance.LoadConfig();
        // The unit shape must exercise the full fanout at 3x the §11 aggregate — a fraction
        // of it cannot catch a multi-x regression with the in-process ceiling this wide.
        Assert.True(config.Unit.TargetAggregateFrameRatePerSecond >= 3 * 2000,
            "the unit target must stand for at least 3x the architecture's 2000 frames/s aggregate");
        Assert.True(config.Unit.AggregateFrameRateFloor
                >= config.Unit.TargetAggregateFrameRatePerSecond * 0.95,
            "the floor must sit within 5% of the declared target");
        // The host shape must stand for its own declared room shape: every room at its tick.
        Assert.True(config.Host.AggregateFrameRateFloor
                >= config.Host.Rooms * (1000.0 / config.Host.TickMs) * 0.8,
            "the host floor must stand for at least 80% of the configured rooms at their tick");
    }

    [Fact]
    public void AMissingConfigFailsClosed()
    {
        var missing = Path.Combine(Path.GetTempPath(),
            "chorus-frame-channel-missing-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<InvalidOperationException>(() => FrameChannelAcceptance.LoadConfig(missing));
        Assert.Contains("refuses to run without its budgets", error.Message);
    }

    [Fact]
    public void AMalformedConfigFailsClosed()
    {
        var malformed = Path.Combine(Path.GetTempPath(),
            "chorus-frame-channel-malformed-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(malformed,
            """{"unit":{"rooms":0,"seatsPerRoom":2,"targetAggregateFrameRatePerSecond":6000,"runSeconds":3.0,"framePayloadBytes":4096,"aggregateFrameRateFloor":5700,"dispatchLatencyP95BudgetMs":5.0,"dispatchLatencyP99BudgetMs":12.0,"unintendedDropsBudget":0},"host":{"rooms":10,"tickMs":50,"ticksPerRoom":100,"warmupTicks":10,"aggregateFrameRateFloor":180,"commitToClientP95BudgetMs":25.0,"commitToClientP99BudgetMs":50.0,"unintendedRebaselinesBudget":0}}""");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => FrameChannelAcceptance.LoadConfig(malformed));
            Assert.Contains("malformed", error.Message);
        }
        finally
        {
            File.Delete(malformed);
        }
    }
}
