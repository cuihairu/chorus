using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// The mass-recovery baseline (FIX-PERF-08, opt-in): one hundred rooms run on one game host,
/// the host dies, and the whole fleet recovers simultaneously on the replacement through the
/// real takeover path. The orchestration, the measurements and the verdict live in
/// <see cref="RecoveryBenchmarkHarness"/> so the long-session acceptance (REC-02) runs the same
/// path at different coordinates; this class holds the batch's declared workload shape and the
/// honest gate — gated by CHORUS_RUN_RECOVERY_CAPACITY=1; a run that happened is held against
/// the pre-declared SLO (<c>recovery-acceptance.json</c>); a disabled run reports NOT RUN and
/// never counts as a pass. Honest dev-machine data, not a production capacity claim.
/// </summary>
public sealed class RedesignRecoveryCapacityBenchmarkTests(ITestOutputHelper output)
{
    // The batch's declared workload shape (must match recovery-acceptance.json exactly): one
    // hundred rooms, each killed once it has committed past the recovery depth (RecoveryTicks),
    // at a tick cadence. The head at the kill is well below the round end (SessionTicks), so a
    // recovered room must still have room to tick past the head it died at: if the round ended
    // at RecoveryTicks itself, rooms already Finished at the kill could never grow again and the
    // "tick past the head" assertion would fail on finished rounds, not on recovery.
    private const int RoomCount = 100;
    private const int SessionTicks = 400;
    private const int RecoveryTicks = 120;
    private const int TickMs = 50;

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task HundredRoomsRecoverSimultaneouslyAndReportPhaseDistributions()
    {
        var config = RecoveryAcceptance.LoadConfig();
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
        var report = await RecoveryBenchmarkHarness.RunAsync(output, config, "rec-cap");
        output.WriteLine(report.Describe());
        Assert.Equal(RecoveryVerdict.Passed, report.Verdict);
    }

    [Fact]
    public async Task InvalidRecoveryGateConfigurationFailsClosedAtStartup()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // FIX-PERF-08: a mistyped gate bound is a configuration error — the host refuses to
        // start, it does not silently disable the recovery bound and admit an unbounded burst.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
                new Dictionary<string, string>
                {
                    ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["CHORUS_GAME_MAX_CONCURRENT_RECOVERIES"] = "not-a-number"
                }, clientTenant: "tenant-a"));
        Assert.Contains("must be a positive integer", error.Message);
    }
}
