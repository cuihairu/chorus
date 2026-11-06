using System.Text.Json;

namespace Chorus.Engine.Tests;

/// <summary>
/// The frame-channel budgets FIX-LINK-11 holds a run against: the gateway fanout's
/// dispatch capacity and latency (the <c>unit</c> shape) and the end-to-end commit-to-client
/// delivery of a real cluster (the <c>host</c> shape). The unit shape is the in-process
/// ceiling the §11 2000 f/s aggregate must fit through; the host shape is what a client
/// actually waits between a frame being committed and it arriving. Both live in an explicit
/// JSON file so the gate is reviewable without recompiling — a missing or malformed file
/// fails closed rather than quietly passing.
/// </summary>
public sealed record FrameChannelUnitConfig(
    int Rooms,
    int SeatsPerRoom,
    int TargetAggregateFrameRatePerSecond,
    double RunSeconds,
    int FramePayloadBytes,
    double AggregateFrameRateFloor,
    double DispatchLatencyP95BudgetMs,
    double DispatchLatencyP99BudgetMs,
    long UnintendedDropsBudget);

/// <summary>The end-to-end shape of a host run: real rooms at the §11 tick, real WS clients.</summary>
public sealed record FrameChannelHostConfig(
    int Rooms,
    int TickMs,
    int TicksPerRoom,
    int WarmupTicks,
    double AggregateFrameRateFloor,
    double CommitToClientP95BudgetMs,
    double CommitToClientP99BudgetMs,
    long UnintendedRebaselinesBudget);

public sealed record FrameChannelAcceptanceConfig(
    FrameChannelUnitConfig Unit,
    FrameChannelHostConfig Host);

/// <summary>The honest three-way verdict: a disabled run is never a pass.</summary>
public enum FrameChannelVerdict
{
    Passed,
    Failed,
    NotRun
}

/// <summary>What the in-suite unit gate measured over the fanout+outbox segment.</summary>
public sealed record FrameChannelMeasurement(
    int Rooms,
    int SeatsPerRoom,
    long FramesDispatched,
    long FramesDelivered,
    double AggregateFrameRate,
    double DispatchLatencyP50Ms,
    double DispatchLatencyP95Ms,
    double DispatchLatencyP99Ms,
    long UnintendedDrops,
    string? FirstError);

/// <summary>What one host run measured end to end: commit-to-client latency per received delta.</summary>
public sealed record FrameChannelHostMeasurement(
    int Rooms,
    int TickMs,
    int TicksPerRoom,
    long FramesDelivered,
    long FramesExpected,
    double AggregateFrameRate,
    double CommitToClientP50Ms,
    double CommitToClientP95Ms,
    double CommitToClientP99Ms,
    long UnintendedRebaselines,
    string? FirstError);

/// <summary>
/// The verdict: the outcome, the named reasons for a failure, and the evidence the verdict
/// rests on — the environment it ran in, how long it ran, the budgets applied, and the
/// measurements taken. The printable form carries all of it so a report can be audited on
/// its own.
/// </summary>
public sealed record FrameChannelReport(
    FrameChannelVerdict Verdict,
    IReadOnlyList<string> Reasons,
    string Environment,
    DateTime StartedUtc,
    DateTime EndedUtc,
    FrameChannelAcceptanceConfig Config,
    FrameChannelMeasurement? UnitMeasurement,
    FrameChannelHostMeasurement? HostMeasurement)
{
    public string Describe()
    {
        var verdictText = Verdict switch
        {
            FrameChannelVerdict.NotRun => "NOT RUN",
            FrameChannelVerdict.Passed => "PASSED",
            _ => "FAILED"
        };
        // A shape this run did not exercise prints no budgets: a zero line can never
        // pose as a budget that was applied.
        var ranUnit = Config.Unit.Rooms > 0;
        var ranHost = Config.Host.Rooms > 0;
        var target = ranUnit && ranHost
            ? FormattableString.Invariant(
                $"target: {Config.Unit.Rooms} rooms x {Config.Unit.SeatsPerRoom} seats @ {Config.Unit.TargetAggregateFrameRatePerSecond}/s, host {Config.Host.Rooms} rooms @ {Config.Host.TickMs} ms")
            : ranUnit
                ? FormattableString.Invariant(
                    $"target: {Config.Unit.Rooms} rooms x {Config.Unit.SeatsPerRoom} seats @ {Config.Unit.TargetAggregateFrameRatePerSecond}/s")
                : FormattableString.Invariant(
                    $"target: host {Config.Host.Rooms} rooms @ {Config.Host.TickMs} ms");
        return
            FormattableString.Invariant($"""
        frame channel acceptance: {verdictText} ({target})
          environment: {Environment}  started {StartedUtc:HH:mm:ss'Z'}  ran {(EndedUtc - StartedUtc).TotalSeconds:0.0}s
        """) +
        (ranUnit
            ? "\n" + FormattableString.Invariant($"""
              unit budgets: aggregate frame rate >= {Config.Unit.AggregateFrameRateFloor:0}/s, dispatch p95 <= {Config.Unit.DispatchLatencyP95BudgetMs:0} ms, p99 <= {Config.Unit.DispatchLatencyP99BudgetMs:0} ms, unintended drops <= {Config.Unit.UnintendedDropsBudget}
              """)
            : "") +
        (ranHost
            ? "\n" + FormattableString.Invariant($"""
              host budgets: aggregate frame rate >= {Config.Host.AggregateFrameRateFloor:0}/s, commit-to-client p95 <= {Config.Host.CommitToClientP95BudgetMs:0} ms, p99 <= {Config.Host.CommitToClientP99BudgetMs:0} ms, re-baselines <= {Config.Host.UnintendedRebaselinesBudget}
              """)
            : "") +
        (UnitMeasurement is null
            ? ""
            : "\n" + FormattableString.Invariant($"""
              unit measured: {UnitMeasurement.AggregateFrameRate:0.0} frames/s, dispatch p50={UnitMeasurement.DispatchLatencyP50Ms:0.00} p95={UnitMeasurement.DispatchLatencyP95Ms:0.00} p99={UnitMeasurement.DispatchLatencyP99Ms:0.00} ms, drops {UnitMeasurement.UnintendedDrops} (dispatched {UnitMeasurement.FramesDispatched}, delivered {UnitMeasurement.FramesDelivered})
              """)) +
        (HostMeasurement is null
            ? ""
            : "\n" + FormattableString.Invariant($"""
              host measured: {HostMeasurement.AggregateFrameRate:0.0} frames/s, commit-to-client p50={HostMeasurement.CommitToClientP50Ms:0.0} p95={HostMeasurement.CommitToClientP95Ms:0.0} p99={HostMeasurement.CommitToClientP99Ms:0.0} ms, re-baselines {HostMeasurement.UnintendedRebaselines} (delivered {HostMeasurement.FramesDelivered}/{HostMeasurement.FramesExpected})
              """)) +
        (Reasons.Count == 0 ? "" : "\n  reasons:\n" + string.Join("", Reasons.Select(reason => $"   - {reason}\n")));
    }
}

/// <summary>
/// The fail-able frame-channel gate (FIX-LINK-11): one place that turns a measurement into a
/// verdict against the explicit config. Every budget can fail the run; a disabled run reports
/// NOT RUN and never counts as a pass; the report always names the environment and the
/// duration, so a number read out of context cannot pose as an acceptance.
/// </summary>
public static class FrameChannelAcceptance
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Loads the explicit acceptance config; a missing or malformed file fails closed.</summary>
    public static FrameChannelAcceptanceConfig LoadConfig(string? path = null)
    {
        var file = path ?? Path.Combine(AppContext.BaseDirectory, "frame-channel-acceptance.json");
        if (!File.Exists(file))
            throw new InvalidOperationException(
                $"the frame channel acceptance config is missing at {file} — the gate refuses to run without its budgets");
        var config = JsonSerializer.Deserialize<FrameChannelAcceptanceConfig>(File.ReadAllText(file), JsonOptions);
        if (config is null || config.Unit.Rooms <= 0 || config.Unit.SeatsPerRoom <= 0
            || config.Unit.TargetAggregateFrameRatePerSecond <= 0 || config.Unit.RunSeconds <= 0
            || config.Unit.FramePayloadBytes <= 0 || config.Unit.AggregateFrameRateFloor <= 0
            || config.Unit.DispatchLatencyP95BudgetMs <= 0 || config.Unit.DispatchLatencyP99BudgetMs <= 0
            || config.Unit.UnintendedDropsBudget < 0
            || config.Host.Rooms <= 0 || config.Host.TickMs <= 0 || config.Host.TicksPerRoom <= 0
            || config.Host.WarmupTicks < 0 || config.Host.AggregateFrameRateFloor <= 0
            || config.Host.CommitToClientP95BudgetMs <= 0 || config.Host.CommitToClientP99BudgetMs <= 0
            || config.Host.UnintendedRebaselinesBudget < 0)
            throw new InvalidOperationException(
                $"the frame channel acceptance config at {file} is malformed — every budget must be set and positive");
        return config;
    }

    /// <summary>Where the run happened and on what — part of every verdict, not an afterthought.</summary>
    public static string EnvironmentDescription() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
        $"({Environment.ProcessorCount} cpu, {Environment.MachineName})";

    /// <summary>The disabled gate: an explicit NOT RUN that can never be read as a pass.</summary>
    public static FrameChannelReport NotRun(FrameChannelHostConfig config, string reason,
        DateTime startedUtc, DateTime endedUtc) =>
        new(FrameChannelVerdict.NotRun, [reason], EnvironmentDescription(), startedUtc, endedUtc,
            new FrameChannelAcceptanceConfig(new FrameChannelUnitConfig(0, 0, 0, 0, 0, 0, 0, 0, 0), config),
            null, null);

    /// <summary>
    /// Compares the in-suite unit gate's measurement against every budget. Any breach — a
    /// workload shape that does not match the declared target, a delivered rate below the
    /// floor, a dispatch latency over budget, an unintended outbox drop, or a first error —
    /// adds its named reason and fails the verdict.
    /// </summary>
    public static FrameChannelReport Evaluate(FrameChannelMeasurement measurement,
        FrameChannelUnitConfig config, DateTime startedUtc, DateTime endedUtc)
    {
        var reasons = new List<string>();
        if (measurement.Rooms != config.Rooms || measurement.SeatsPerRoom != config.SeatsPerRoom)
            reasons.Add(FormattableString.Invariant(
                $"the workload shape ({measurement.Rooms} rooms x {measurement.SeatsPerRoom} seats) does not match the acceptance target ({config.Rooms} x {config.SeatsPerRoom})"));
        if (measurement.FramesDelivered < measurement.FramesDispatched * 0.999)
            reasons.Add(FormattableString.Invariant(
                $"the fanout delivered {measurement.FramesDelivered} of {measurement.FramesDispatched} dispatched frames — a frame was lost before the transport"));
        if (measurement.AggregateFrameRate < config.AggregateFrameRateFloor)
            reasons.Add(FormattableString.Invariant(
                $"the aggregate frame rate {measurement.AggregateFrameRate:0.0}/s is below the floor {config.AggregateFrameRateFloor:0}/s (target {config.TargetAggregateFrameRatePerSecond}/s)"));
        if (measurement.DispatchLatencyP95Ms > config.DispatchLatencyP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the dispatch p95 {measurement.DispatchLatencyP95Ms:0.00} ms is over the budget {config.DispatchLatencyP95BudgetMs:0} ms"));
        if (measurement.DispatchLatencyP99Ms > config.DispatchLatencyP99BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the dispatch p99 {measurement.DispatchLatencyP99Ms:0.00} ms is over the budget {config.DispatchLatencyP99BudgetMs:0} ms"));
        if (measurement.UnintendedDrops > config.UnintendedDropsBudget)
            reasons.Add(FormattableString.Invariant(
                $"the fanout dropped {measurement.UnintendedDrops} frames (refused/superseded outbox entries) against a budget of {config.UnintendedDropsBudget} — the target rate must not overflow the send queue"));
        if (measurement.FirstError is { } firstError)
            reasons.Add($"the frame channel harness recorded an error: {firstError}");
        return new FrameChannelReport(
            reasons.Count == 0 ? FrameChannelVerdict.Passed : FrameChannelVerdict.Failed,
            reasons, EnvironmentDescription(), startedUtc, endedUtc,
            new FrameChannelAcceptanceConfig(config, new FrameChannelHostConfig(0, 0, 0, 0, 0, 0, 0, 0)),
            measurement, null);
    }

    /// <summary>
    /// Compares a host run's end-to-end measurement against every budget. Any breach — a
    /// delivered rate below the floor, a commit-to-client latency over budget, an unintended
    /// re-baseline, or a first error — adds its named reason and fails the verdict.
    /// </summary>
    public static FrameChannelReport EvaluateHost(FrameChannelHostMeasurement measurement,
        FrameChannelHostConfig config, DateTime startedUtc, DateTime endedUtc)
    {
        var reasons = new List<string>();
        if (measurement.Rooms != config.Rooms || measurement.TickMs != config.TickMs
            || measurement.TicksPerRoom != config.TicksPerRoom)
            reasons.Add(FormattableString.Invariant(
                $"the workload shape ({measurement.Rooms} rooms x {measurement.TicksPerRoom} ticks @ {measurement.TickMs} ms) does not match the acceptance target ({config.Rooms} x {config.TicksPerRoom} @ {config.TickMs} ms)"));
        if (measurement.FramesDelivered < measurement.FramesExpected * 0.999)
            reasons.Add(FormattableString.Invariant(
                $"the clients received {measurement.FramesDelivered} of {measurement.FramesExpected} expected frames"));
        if (measurement.AggregateFrameRate < config.AggregateFrameRateFloor)
            reasons.Add(FormattableString.Invariant(
                $"the aggregate frame rate {measurement.AggregateFrameRate:0.0}/s is below the floor {config.AggregateFrameRateFloor:0}/s"));
        if (measurement.CommitToClientP95Ms > config.CommitToClientP95BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the commit-to-client p95 {measurement.CommitToClientP95Ms:0.0} ms is over the budget {config.CommitToClientP95BudgetMs:0} ms"));
        if (measurement.CommitToClientP99Ms > config.CommitToClientP99BudgetMs)
            reasons.Add(FormattableString.Invariant(
                $"the commit-to-client p99 {measurement.CommitToClientP99Ms:0.0} ms is over the budget {config.CommitToClientP99BudgetMs:0} ms"));
        if (measurement.UnintendedRebaselines > config.UnintendedRebaselinesBudget)
            reasons.Add(FormattableString.Invariant(
                $"the clients saw {measurement.UnintendedRebaselines} re-baseline snapshots against a budget of {config.UnintendedRebaselinesBudget} — a slow client must not re-baseline at the target rate"));
        if (measurement.FirstError is { } firstError)
            reasons.Add($"the frame channel harness recorded an error: {firstError}");
        return new FrameChannelReport(
            reasons.Count == 0 ? FrameChannelVerdict.Passed : FrameChannelVerdict.Failed,
            reasons, EnvironmentDescription(), startedUtc, endedUtc,
            new FrameChannelAcceptanceConfig(new FrameChannelUnitConfig(0, 0, 0, 0, 0, 0, 0, 0, 0), config),
            null, measurement);
    }
}
