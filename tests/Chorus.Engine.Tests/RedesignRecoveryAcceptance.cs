using System.Text.Json;

namespace Chorus.Engine.Tests;

/// <summary>
/// The pre-declared recovery SLO the mass-recovery acceptance holds a run against (FIX-PERF-08):
/// the workload shape (100 rooms, each recovered after a number of committed ticks, at a tick
/// cadence), the recovery host's concurrency gate (how many recoveries run at once and how many
/// may wait), and the fail-able budgets — the detection, queue, read, replay, activate and total
/// phase percentiles, the peak host memory, and the recovered-room floor. They live in an
/// explicit JSON file (<c>recovery-acceptance.json</c>) so the gate is reviewable without
/// recompiling; a missing or malformed file fails closed rather than quietly passing.
/// </summary>
public sealed record RecoveryAcceptanceConfig(
    int Rooms,
    int RecoveryTicksPerRoom,
    int TickMs,
    int ConcurrentRecoveries,
    int QueuedRecoveries,
    double DetectP95BudgetMs,
    double QueueP95BudgetMs,
    double ReadP95BudgetMs,
    double ReplayP95BudgetMs,
    double ActivateP95BudgetMs,
    double TotalP95BudgetMs,
    double PeakMemoryBudgetMb,
    int RecoveredRoomsFloor);

/// <summary>The honest three-way verdict: a disabled run is never a pass.</summary>
public enum RecoveryVerdict
{
    Passed,
    Failed,
    NotRun
}

/// <summary>What one mass-recovery run actually measured, as handed to the acceptance.</summary>
public sealed record RecoveryMeasurement(
    int Rooms,
    int RecoveredRooms,
    int ExplicitRejections,
    double DetectP95Ms,
    double QueueP95Ms,
    double ReadP95Ms,
    double ReplayP95Ms,
    double ActivateP95Ms,
    double TotalP95Ms,
    double PeakMemoryMb);

/// <summary>
/// The acceptance outcome: the verdict, the named reasons for a failure, and the evidence the
/// verdict rests on — the environment it ran in, how long it ran, the budgets applied, and the
/// measurements taken. The printable form carries all of it so a report can be audited on its own.
/// </summary>
public sealed record RecoveryReport(
    RecoveryVerdict Verdict,
    IReadOnlyList<string> Reasons,
    string Environment,
    DateTime StartedUtc,
    DateTime EndedUtc,
    RecoveryAcceptanceConfig Config,
    RecoveryMeasurement? Measurement)
{
    public string Describe()
    {
        var verdictText = Verdict switch
        {
            RecoveryVerdict.NotRun => "NOT RUN",
            RecoveryVerdict.Passed => "PASSED",
            _ => "FAILED"
        };
        return
            FormattableString.Invariant($"""
        recovery acceptance: {verdictText} (target: {Config.Rooms} rooms, {Config.RecoveryTicksPerRoom} committed ticks each @ {Config.TickMs} ms)
          environment: {Environment}  started {StartedUtc:HH:mm:ss'Z'}  ran {(EndedUtc - StartedUtc).TotalSeconds:0.0}s
          gate: {Config.ConcurrentRecoveries} concurrent recoveries, {Config.QueuedRecoveries} queued
          budgets: detect p95 <= {Config.DetectP95BudgetMs:0} ms, queue p95 <= {Config.QueueP95BudgetMs:0} ms, read p95 <= {Config.ReadP95BudgetMs:0} ms, replay p95 <= {Config.ReplayP95BudgetMs:0} ms, activate p95 <= {Config.ActivateP95BudgetMs:0} ms, total p95 <= {Config.TotalP95BudgetMs:0} ms, peak memory <= {Config.PeakMemoryBudgetMb:0} MB, recovered >= {Config.RecoveredRoomsFloor}/{Config.Rooms}
        """) +
        (Measurement is null
            ? ""
            : "\n" + FormattableString.Invariant($"""
              measured: recovered {Measurement.RecoveredRooms}/{Measurement.Rooms} ({Measurement.ExplicitRejections} refused), detect p95 {Measurement.DetectP95Ms:0} ms, queue p95 {Measurement.QueueP95Ms:0} ms, read p95 {Measurement.ReadP95Ms:0} ms, replay p95 {Measurement.ReplayP95Ms:0} ms, activate p95 {Measurement.ActivateP95Ms:0} ms, total p95 {Measurement.TotalP95Ms:0} ms, peak memory {Measurement.PeakMemoryMb:0} MB
              """)) +
        (Reasons.Count == 0 ? "" : "\n  reasons:\n" + string.Join("", Reasons.Select(reason => $"   - {reason}\n")));
    }
}

/// <summary>
/// The fail-able performance gate (FIX-PERF-08): one place that turns a mass-recovery
/// measurement into a verdict against the pre-declared SLO. Every budget can fail the run; a
/// disabled run reports NOT RUN and never counts as a pass; the report always names the
/// environment and the duration, so a number read out of context cannot pose as an acceptance.
/// </summary>
public static class RecoveryAcceptance
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Loads the explicit acceptance config; a missing or malformed file fails closed.</summary>
    public static RecoveryAcceptanceConfig LoadConfig(string? path = null)
    {
        var file = path ?? Path.Combine(AppContext.BaseDirectory, "recovery-acceptance.json");
        if (!File.Exists(file))
            throw new InvalidOperationException(
                $"the recovery acceptance config is missing at {file} — the gate refuses to run without its budgets");
        var config = JsonSerializer.Deserialize<RecoveryAcceptanceConfig>(File.ReadAllText(file), JsonOptions);
        if (config is null || config.Rooms <= 0 || config.RecoveryTicksPerRoom <= 0 || config.TickMs <= 0
            || config.ConcurrentRecoveries <= 0 || config.QueuedRecoveries < 0
            || config.DetectP95BudgetMs <= 0 || config.QueueP95BudgetMs <= 0 || config.ReadP95BudgetMs <= 0
            || config.ReplayP95BudgetMs <= 0 || config.ActivateP95BudgetMs <= 0
            || config.TotalP95BudgetMs <= 0 || config.PeakMemoryBudgetMb <= 0
            || config.RecoveredRoomsFloor <= 0)
            throw new InvalidOperationException(
                $"the recovery acceptance config at {file} is malformed — every budget must be set and positive");
        return config;
    }

    /// <summary>Where the run happened and on what — part of every verdict, not an afterthought.</summary>
    public static string EnvironmentDescription() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
        $"({Environment.ProcessorCount} cpu, {Environment.MachineName})";

    /// <summary>The disabled gate: an explicit NOT RUN that can never be read as a pass.</summary>
    public static RecoveryReport NotRun(RecoveryAcceptanceConfig config, string reason,
        DateTime startedUtc, DateTime endedUtc) =>
        new(RecoveryVerdict.NotRun, [reason], EnvironmentDescription(), startedUtc, endedUtc, config, null);

    /// <summary>
    /// Compares one measurement against every budget. Any breach — rooms that did not recover,
    /// an explicit rejection, a phase percentile over its budget, or a peak memory over its cap
    /// — adds its named reason and fails the verdict.
    /// </summary>
    public static RecoveryReport Evaluate(RecoveryMeasurement measurement, RecoveryAcceptanceConfig config,
        DateTime startedUtc, DateTime endedUtc)
    {
        var reasons = new List<string>();
        if (measurement.Rooms != config.Rooms)
            reasons.Add(FormattableString.Invariant(
                $"the workload shape ({measurement.Rooms} rooms) does not match the acceptance target ({config.Rooms})"));
        if (measurement.RecoveredRooms < config.RecoveredRoomsFloor)
            reasons.Add(FormattableString.Invariant(
                $"{measurement.RecoveredRooms}/{measurement.Rooms} rooms recovered, below the floor {config.RecoveredRoomsFloor}"));
        if (measurement.ExplicitRejections > 0)
            reasons.Add($"{measurement.ExplicitRejections} recoveries were refused explicitly by the concurrency gate");
        if (measurement.DetectP95Ms > config.DetectP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the detection p95 {measurement.DetectP95Ms:0} ms is over the budget {config.DetectP95BudgetMs:0} ms"));
        if (measurement.QueueP95Ms > config.QueueP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the queue p95 {measurement.QueueP95Ms:0} ms is over the budget {config.QueueP95BudgetMs:0} ms"));
        if (measurement.ReadP95Ms > config.ReadP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the read p95 {measurement.ReadP95Ms:0} ms is over the budget {config.ReadP95BudgetMs:0} ms"));
        if (measurement.ReplayP95Ms > config.ReplayP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the replay p95 {measurement.ReplayP95Ms:0} ms is over the budget {config.ReplayP95BudgetMs:0} ms"));
        if (measurement.ActivateP95Ms > config.ActivateP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the activate p95 {measurement.ActivateP95Ms:0} ms is over the budget {config.ActivateP95BudgetMs:0} ms"));
        if (measurement.TotalP95Ms > config.TotalP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the total recovery p95 {measurement.TotalP95Ms:0} ms is over the budget {config.TotalP95BudgetMs:0} ms"));
        if (measurement.PeakMemoryMb > config.PeakMemoryBudgetMb)
            reasons.Add(FormattableString.Invariant(
                $"the peak host memory {measurement.PeakMemoryMb:0} MB is over the budget {config.PeakMemoryBudgetMb:0} MB"));
        return new RecoveryReport(
            reasons.Count == 0 ? RecoveryVerdict.Passed : RecoveryVerdict.Failed,
            reasons, EnvironmentDescription(), startedUtc, endedUtc, config, measurement);
    }
}
