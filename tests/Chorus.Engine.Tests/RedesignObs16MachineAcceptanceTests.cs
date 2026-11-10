using Chorus.Control;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-OBS-16's acceptance of the machine statistics and the dropout word, driven by the
/// real production chain end to end: a BootstrapTool-provisioned NodeAgent process runs its
/// own MachineTelemetrySampler on a one-second period, reports over gRPC to the control
/// seed it supervises, and the samples land in ops.machine_sample — the same tables the
/// TelemetryStore face and the OpsAlertsStore words read. While the agent lives, the face
/// answers the node's real readings and keeps moving (the report sequence advances); after
/// the agent dies — the real dropout incident — the node is judged quiet once the last word
/// ages past the quiet floor, and its stale readings are not judged: with one-percent
/// resource floors a judged stale reading would always add a second word, so the quiet
/// answer being the only word is the proof.
/// </summary>
public sealed class RedesignObs16MachineAcceptanceTests
{
    private static readonly OpsAlertThresholds Defaults = new(90, 90, 5, 1, 0, 1000, 1);

    [Fact]
    public async Task TheRealAgentKeepsReportingAndTheMachineFaceAnswersItsReadings()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var agent = await environment.StartBootstrapAgentAsync(
            agentEnvironment: new Dictionary<string, string>
            {
                ["CHORUS_TELEMETRY_PERIOD_SECONDS"] = "1"
            });
        var telemetry = new TelemetryStore(environment.DatabaseConnectionString, ControlDatabaseProvider.PostgreSql);

        // The first sample only establishes the CPU baseline (the sampler's contract), and
        // samples sent before the node's registration are refused and eventually dropped by
        // the bounded pipeline — so the honest wait is for a settled window: several landed
        // samples AND a cpu reading. The bootstrap itself (tool, agent, supervised seeds,
        // registration) has been observed past a minute on loaded runners — the bound
        // covers that, not the telemetry period.
        MachineStatsNode node = WaitUntil(
            () => telemetry.MachineStats(null, 15).FirstOrDefault(n => n.Samples >= 3 && n.CpuPercent is not null),
            TimeSpan.FromSeconds(120), "the agent's machine telemetry did not reach the face.\n"
            + environment.AgentOutput(agent));
        Assert.Equal("node-bootstrap", node.NodeId);

        // The face answers the real machine's readings: present families carry plausible
        // values, the disk face carries the real mounts, and the window counted the samples.
        Assert.InRange(node.CpuPercent!.Value, 0, 100);
        Assert.NotNull(node.Memory);
        Assert.InRange(node.Memory.UsedPercent, 0, 100);
        Assert.True(node.Memory.TotalBytes > 0);
        Assert.NotNull(node.Load);
        Assert.True(node.Load.L1 >= 0);
        Assert.NotEmpty(node.Disk);
        Assert.All(node.Disk, disk => Assert.InRange(disk.UsedPercent, 0, 100));

        // The agent keeps reporting: a later read carries a strictly greater sequence, so
        // the face moves with the node rather than freezing on one sample.
        var sequence = node.ReportSequence;
        WaitUntil(
            () => telemetry.MachineStats(null, 15).FirstOrDefault(n => n.ReportSequence > sequence),
            TimeSpan.FromSeconds(30), "the agent did not report a further sample.\n"
            + environment.AgentOutput(agent));

        // The node's last word is seconds old: no quiet word at the default five-minute
        // floor. (The real box's cpu or memory could legitimately breach its default — a
        // loaded runner is real telemetry too — so only the quiet word is asserted here.)
        Assert.DoesNotContain(AlertsStore(environment.DatabaseConnectionString, Defaults).Active(),
            alert => alert.Kind == "machine-quiet");
    }

    [Fact]
    public async Task AfterTheAgentGoesDarkTheNodeIsJudgedQuietAndItsLastReadingIsNot()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var agent = await environment.StartBootstrapAgentAsync(
            agentEnvironment: new Dictionary<string, string>
            {
                ["CHORUS_TELEMETRY_PERIOD_SECONDS"] = "1"
            });
        // One-percent resource floors: a live machine always sits above them (page cache
        // keeps real used memory over one percent), so the live node breaches — and any
        // judged stale reading would keep breaching after death. One-minute quiet floor.
        var alerts = AlertsStore(environment.DatabaseConnectionString, new OpsAlertThresholds(1, 1, 1, 1, 0, 1000, 1));

        // While the agent lives, its fresh readings are judged: the floors breach.
        WaitUntil(() => alerts.Active().Any(alert =>
            alert.Subject == "node-bootstrap" && alert.Kind.StartsWith("machine-")),
            TimeSpan.FromSeconds(45), "the live node's readings did not breach the one-percent floors.\n"
            + environment.AgentOutput(agent));

        // The dropout incident itself: the agent process dies (its supervised seed hosts
        // with it), and the database keeps the last samples it received.
        environment.Kill("node-agent");

        // The quiet word arrives when the last word ages past the floor — one wall-clock
        // minute after the kill — and it is the ONLY word: the stale readings are not judged.
        var active = WaitUntil(
            () => alerts.Active() is [var quiet] && quiet.Kind == "machine-quiet"
                && quiet.Subject == "node-bootstrap" ? alerts.Active() : null,
            TimeSpan.FromSeconds(120), "the quiet node did not answer exactly the quiet word.\n"
            + environment.AgentOutput(agent));
        var quiet = Assert.Single(active);
        Assert.Equal("machine-quiet", quiet.Kind);
        Assert.Equal("node-bootstrap", quiet.Subject);
        Assert.Equal(1, quiet.Threshold);
        Assert.InRange(quiet.Observed, 0.9, 5);
        Assert.Contains("the node is judged quiet, its stale readings are not", quiet.Detail);
    }

    private static OpsAlertsStore AlertsStore(string connectionString, OpsAlertThresholds thresholds) => new(
        new TelemetryStore(connectionString, ControlDatabaseProvider.PostgreSql),
        new OpsWindowStore(connectionString, ControlDatabaseProvider.PostgreSql),
        new DeliveryStatsStore(connectionString, ControlDatabaseProvider.PostgreSql),
        new PlatformApiStatsStore(connectionString, ControlDatabaseProvider.PostgreSql),
        thresholds);

    /// <summary>Polls a condition until it holds — the bootstrap's process churn and the
    /// wall-clock quiet floor make fixed sleeps wrong.</summary>
    private static void WaitUntil(Func<bool> probe, TimeSpan budget, string failure)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (probe()) return;
            Thread.Sleep(500);
        }
        throw new TimeoutException(failure);
    }

    /// <summary>Polls a computed answer until it satisfies the probe.</summary>
    private static T WaitUntil<T>(Func<T?> probe, TimeSpan budget, string failure) where T : class
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } answer) return answer;
            Thread.Sleep(500);
        }
        throw new TimeoutException(failure);
    }
}
