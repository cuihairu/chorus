using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// The long-session recovery acceptance (REC-02, opt-in): a small fleet of rooms that have each
/// committed a deep frame history (well past the batch's short session) run on one game host,
/// the host dies, and they recover on the replacement through the same real takeover path as the
/// batch — the orchestration, the measurements and the verdict live in
/// <see cref="RecoveryBenchmarkHarness"/>. The declared point of a long session is that the read
/// that replays a room's deep history and the recovery host's memory stay bounded: the SLO's
/// read and memory budgets are the same order as the batch's even though each room has committed
/// far more ticks, so a read or replay that grew with the history would breach them. Gated by
/// CHORUS_RUN_RECOVERY_CAPACITY=1 and held against the pre-declared SLO
/// (<c>recovery-acceptance-long.json</c>); a disabled run reports NOT RUN and never counts as a
/// pass. Honest dev-machine data, not a production capacity claim.
/// </summary>
public sealed class RedesignRecoveryLongSessionBenchmarkTests(ITestOutputHelper output)
{
    // The long session's declared workload shape (must match recovery-acceptance-long.json
    // exactly): a small fleet with a deep session — each room commits past the recovery depth
    // (RecoveryTicks, halfway through) so the recovery reads a history far deeper than the
    // batch's, and a recovered room still has half the round to tick past the head it died at.
    private const int RoomCount = 3;
    private const int SessionTicks = 4000;
    private const int RecoveryTicks = 2000;
    private const int TickMs = 50;

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task LongSessionRoomsRecoverWithBoundedReadAndMemory()
    {
        var config = RecoveryAcceptance.LoadConfig(
            Path.Combine(AppContext.BaseDirectory, "recovery-acceptance-long.json"));
        if (Environment.GetEnvironmentVariable("CHORUS_RUN_RECOVERY_CAPACITY") != "1")
        {
            var notRun = RecoveryAcceptance.NotRun(config,
                "set CHORUS_RUN_RECOVERY_CAPACITY=1 to opt in — this run does not count as a recovery pass",
                DateTime.UtcNow, DateTime.UtcNow);
            output.WriteLine(notRun.Describe());
            return;
        }
        // The workload shape must match the declared SLO exactly, or the budgets judge a
        // different run than the one declared.
        Assert.Equal(RoomCount, config.Rooms);
        Assert.Equal(SessionTicks, config.SessionTicks);
        Assert.Equal(RecoveryTicks, config.RecoveryTicksPerRoom);
        Assert.Equal(TickMs, config.TickMs);
        var report = await RecoveryBenchmarkHarness.RunAsync(output, config, "rec-long");
        output.WriteLine(report.Describe());
        Assert.Equal(RecoveryVerdict.Passed, report.Verdict);
    }

    [Fact]
    public void LongSessionAcceptanceConfigLoadsAndFailsClosed()
    {
        // The long session's SLO is a real file with the declared long-session shape: a small
        // fleet on a deep session (the whole point is the deep frame history, so the recovery
        // depth must sit well inside the session) — and a missing or malformed file fails closed
        // rather than quietly passing.
        var config = RecoveryAcceptance.LoadConfig(
            Path.Combine(AppContext.BaseDirectory, "recovery-acceptance-long.json"));
        Assert.True(config.Rooms < 100, "a long session is a small fleet, not the batch shape");
        Assert.True(config.SessionTicks > config.RecoveryTicksPerRoom,
            "the recovery depth must sit inside the session, or a recovered room could not tick past its head");

        var broken = Path.Combine(AppContext.BaseDirectory, "recovery-acceptance-long.json.broken");
        File.WriteAllText(broken, "{ \"rooms\": 3 }");
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => RecoveryAcceptance.LoadConfig(broken));
        }
        finally
        {
            File.Delete(broken);
        }
    }
}
