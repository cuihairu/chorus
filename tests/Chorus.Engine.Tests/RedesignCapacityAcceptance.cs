using System.Text.Json;

namespace Chorus.Engine.Tests;

/// <summary>
/// The target-architecture numbers the capacity acceptance holds a run against (FIX-PERF-04).
/// The shape fields describe the workload the acceptance was declared for (100 rooms at a 20 Hz
/// tick); the budget fields are the fail-able thresholds. They live in an explicit JSON file
/// (<c>capacity-acceptance.json</c>) so the gate is reviewable without recompiling — a missing
/// or malformed file fails closed rather than quietly passing.
/// </summary>
public sealed record CapacityAcceptanceConfig(
    int Rooms,
    int TickHz,
    int TicksPerRoom,
    int HotRoomEventsPerSecond,
    double AggregateFrameRateFloor,
    double PerRoomCompletionP95BudgetMs,
    double CommonWindowFloorMs,
    double PlanFillFloor);

/// <summary>The honest three-way verdict: a disabled run is never a pass.</summary>
public enum CapacityVerdict
{
    Passed,
    Failed,
    NotRun
}

/// <summary>What one capacity run actually measured, as handed to the acceptance.</summary>
public sealed record CapacityMeasurement(
    int Rooms,
    int TicksPerRoom,
    long FramesCommitted,
    long FramesExpected,
    double AggregateFrameRate,
    double PerRoomCompletionP95Ms,
    double CommonWindowMs,
    long PlannedInputs,
    long EmittedInputs,
    long BacklogInputs,
    string? InputFirstError);

/// <summary>
/// The acceptance outcome: the verdict, the named reasons for a failure, and the evidence the
/// verdict rests on — the environment it ran in, how long it ran, the budgets applied, and the
/// measurements taken. The printable form carries all of it so a report can be audited on its own.
/// </summary>
public sealed record CapacityReport(
    CapacityVerdict Verdict,
    IReadOnlyList<string> Reasons,
    string Environment,
    DateTime StartedUtc,
    DateTime EndedUtc,
    CapacityAcceptanceConfig Config,
    CapacityMeasurement? Measurement)
{
    public string Describe()
    {
        var verdictText = Verdict switch
        {
            CapacityVerdict.NotRun => "NOT RUN",
            CapacityVerdict.Passed => "PASSED",
            _ => "FAILED"
        };
        return
            $"""
        capacity acceptance: {verdictText} (target: {Config.Rooms} rooms @ {Config.TickHz} Hz)
          environment: {Environment}  started {StartedUtc:HH:mm:ss'Z'}  ran {(EndedUtc - StartedUtc).TotalSeconds:0.0}s
          budgets: aggregate frame rate >= {Config.AggregateFrameRateFloor:0}/s, per-room completion p95 <= {Config.PerRoomCompletionP95BudgetMs:0} ms, common active window >= {Config.CommonWindowFloorMs:0} ms, plan fill >= {Config.PlanFillFloor:P0}
        """ +
        (Measurement is null
            ? ""
            : "\n" + $"""
              measured: {Measurement.AggregateFrameRate:0.0} frames/s, completion p95 {Measurement.PerRoomCompletionP95Ms:0} ms, common window {Measurement.CommonWindowMs:0} ms, plan fill {(Measurement.PlannedInputs <= 0 ? 0 : (double)Measurement.EmittedInputs / Measurement.PlannedInputs):P1} (planned {Measurement.PlannedInputs}, emitted {Measurement.EmittedInputs}, backlog {Measurement.BacklogInputs}), frames {Measurement.FramesCommitted}/{Measurement.FramesExpected}
              """) +
        (Reasons.Count == 0 ? "" : "\n  reasons:\n" + string.Join("", Reasons.Select(reason => $"   - {reason}\n")));
    }
}

/// <summary>
/// The fail-able performance gate (FIX-PERF-04): one place that turns a capacity measurement
/// into a verdict against the explicit config. Every budget can fail the run; a disabled run
/// reports NOT RUN and never counts as a pass; the report always names the environment and the
/// duration, so a number read out of context cannot pose as an acceptance.
/// </summary>
public static class CapacityAcceptance
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Loads the explicit acceptance config; a missing or malformed file fails closed.</summary>
    public static CapacityAcceptanceConfig LoadConfig(string? path = null)
    {
        var file = path ?? Path.Combine(AppContext.BaseDirectory, "capacity-acceptance.json");
        if (!File.Exists(file))
            throw new InvalidOperationException(
                $"the capacity acceptance config is missing at {file} — the gate refuses to run without its budgets");
        var config = JsonSerializer.Deserialize<CapacityAcceptanceConfig>(File.ReadAllText(file), JsonOptions);
        if (config is null || config.Rooms <= 0 || config.TickHz <= 0 || config.TicksPerRoom <= 0
            || config.HotRoomEventsPerSecond <= 0 || config.AggregateFrameRateFloor <= 0
            || config.PerRoomCompletionP95BudgetMs <= 0 || config.CommonWindowFloorMs <= 0
            || config.PlanFillFloor is <= 0 or > 1)
            throw new InvalidOperationException(
                $"the capacity acceptance config at {file} is malformed — every budget must be set and positive");
        return config;
    }

    /// <summary>Where the run happened and on what — part of every verdict, not an afterthought.</summary>
    public static string EnvironmentDescription() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
        $"({Environment.ProcessorCount} cpu, {Environment.MachineName})";

    /// <summary>The disabled gate: an explicit NOT RUN that can never be read as a pass.</summary>
    public static CapacityReport NotRun(CapacityAcceptanceConfig config, string reason,
        DateTime startedUtc, DateTime endedUtc) =>
        new(CapacityVerdict.NotRun, [reason], EnvironmentDescription(), startedUtc, endedUtc, config, null);

    /// <summary>
    /// Compares one measurement against every budget. Any breach — a workload shape that does
    /// not match the declared target, missing frames, a thin common window, an aggregate frame
    /// rate below the target architecture, a per-room completion over its budget, or an input
    /// plan the generator could not keep — adds its named reason and fails the verdict.
    /// </summary>
    public static CapacityReport Evaluate(CapacityMeasurement measurement, CapacityAcceptanceConfig config,
        DateTime startedUtc, DateTime endedUtc)
    {
        var reasons = new List<string>();
        if (measurement.Rooms != config.Rooms || measurement.TicksPerRoom != config.TicksPerRoom)
            reasons.Add($"the workload shape ({measurement.Rooms} rooms x {measurement.TicksPerRoom} ticks) " +
                        $"does not match the acceptance target ({config.Rooms} x {config.TicksPerRoom})");
        if (measurement.FramesCommitted != measurement.FramesExpected)
            reasons.Add($"the rooms committed {measurement.FramesCommitted} frames, expected {measurement.FramesExpected}");
        if (measurement.CommonWindowMs < config.CommonWindowFloorMs)
            reasons.Add($"the common active window {measurement.CommonWindowMs:0} ms is below the floor {config.CommonWindowFloorMs:0} ms");
        if (measurement.AggregateFrameRate < config.AggregateFrameRateFloor)
            reasons.Add($"the aggregate frame rate {measurement.AggregateFrameRate:0.0}/s is below the target " +
                        $"{config.AggregateFrameRateFloor:0}/s ({config.Rooms} rooms x {config.TickHz} Hz)");
        if (measurement.PerRoomCompletionP95Ms > config.PerRoomCompletionP95BudgetMs)
            reasons.Add($"the per-room completion p95 {measurement.PerRoomCompletionP95Ms:0} ms is over the budget " +
                        $"{config.PerRoomCompletionP95BudgetMs:0} ms (at {config.TickHz} Hz a full round is {config.TicksPerRoom * 1000.0 / config.TickHz:0} ms)");
        var fill = measurement.PlannedInputs <= 0 ? 1.0 : (double)measurement.EmittedInputs / measurement.PlannedInputs;
        if (fill < config.PlanFillFloor)
            reasons.Add($"the input generator kept only {fill:P1} of its plan (planned {measurement.PlannedInputs}, " +
                        $"emitted {measurement.EmittedInputs}, backlog {measurement.BacklogInputs}) — the achieved rate never replaces the target");
        if (measurement.InputFirstError is { } firstError)
            reasons.Add($"the input generator recorded an error: {firstError}");
        return new CapacityReport(
            reasons.Count == 0 ? CapacityVerdict.Passed : CapacityVerdict.Failed,
            reasons, EnvironmentDescription(), startedUtc, endedUtc, config, measurement);
    }
}
