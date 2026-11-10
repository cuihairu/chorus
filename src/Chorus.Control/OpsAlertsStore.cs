using System.Text;

namespace Chorus.Control;

/// <summary>The threshold words (FIX-OBS-15): resource limits for the machine telemetry
/// (CPU, memory, a node that stopped sampling) and link limits for the event chain
/// (ingress rejections, failing effect dispatch, failing platform API calls). Values are
/// plain configuration numbers — the console renders the words, nothing pages anyone.</summary>
public sealed record OpsAlertThresholds(
    double MachineCpuPercent,
    double MachineMemoryPercent,
    int MachineQuietMinutes,
    long IngressRejections,
    long EffectsFailed,
    long EffectsPending,
    long PlatformApiFailures);

/// <summary>One active breach: what kind of limit, on what subject, observed against
/// threshold, with the detail sentence the console can show verbatim.</summary>
public sealed record OpsAlert(
    string Kind, string Subject, double Observed, double Threshold, string Detail);

/// <summary>
/// Read-only threshold evaluation over the statistics faces (FIX-OBS-15) — the answer is
/// computed on read from the same stores the stats endpoints answer from, so there is no
/// alert state to persist and nothing to acknowledge: a standing breach says so on every
/// read and stops the moment its underlying face stops breaching. The machine words read
/// the telemetry window (a node quiet past the threshold has no live reading, so its
/// stale CPU and memory are not judged); the link words read the windowed counter deltas
/// for the ingress refusal reasons, the durable outbox stages for the effect backlog and
/// failures, and the fresh gateway rollup for platform API failures. The ops and infra
/// schemas live only in PostgreSQL under the same-database assumption; a non-PostgreSQL
/// control database answers empty rather than failing — no breaches is an answer, not an
/// error. Like the event-heat alert this is an informational console word, not an
/// external page.
/// </summary>
public sealed class OpsAlertsStore(
    TelemetryStore telemetry,
    OpsWindowStore window,
    DeliveryStatsStore delivery,
    PlatformApiStatsStore platformApi,
    OpsAlertThresholds thresholds)
{
    /// <summary>The refusal families with their console words, pinned to the ingress
    /// store's const names (a test asserts the listing agrees).</summary>
    private static readonly (string Family, string Word)[] RejectionReasons =
    [
        (IngressStatsStore.DuplicatesFamily, "duplicates"),
        (IngressStatsStore.UnverifiedFamily, "unverified"),
        (IngressStatsStore.UnmappedFamily, "unmapped"),
        (IngressStatsStore.RateLimitedFamily, "rate limited"),
        (IngressStatsStore.TooLargeFamily, "too large"),
        (IngressStatsStore.TimedOutFamily, "timed out"),
    ];

    /// <summary>The breaches active right now, ordered by kind then subject.</summary>
    public IReadOnlyList<OpsAlert> Active()
    {
        var alerts = new List<OpsAlert>();
        var now = DateTimeOffset.UtcNow;

        foreach (var node in telemetry.MachineStats(null, 24 * 60))
        {
            var quietMinutes = (now - node.SampledAtUtc).TotalMinutes;
            if (quietMinutes >= thresholds.MachineQuietMinutes)
            {
                alerts.Add(new OpsAlert("machine-quiet", node.NodeId, Math.Round(quietMinutes, 1),
                    thresholds.MachineQuietMinutes,
                    $"no machine sample for {Math.Round(quietMinutes)} minutes (last at {node.SampledAtUtc:u}); the node is judged quiet, its stale readings are not."));
                continue;
            }
            if (node.CpuPercent is not null && node.CpuPercent >= thresholds.MachineCpuPercent)
                alerts.Add(new OpsAlert("machine-cpu", node.NodeId, node.CpuPercent.Value,
                    thresholds.MachineCpuPercent,
                    $"latest sample reads {node.CpuPercent.Value:F1}% cpu."));
            if (node.Memory is not null && node.Memory.UsedPercent >= thresholds.MachineMemoryPercent)
                alerts.Add(new OpsAlert("machine-memory", node.NodeId, node.Memory.UsedPercent,
                    thresholds.MachineMemoryPercent,
                    $"latest sample reads {node.Memory.UsedPercent:F1}% memory used."));
        }

        var deltas = window.Stats(60).Families.ToDictionary(f => f.Family, f => f.Delta);
        var rejectionFloor = Math.Max(thresholds.IngressRejections, 1);
        foreach (var (family, word) in RejectionReasons)
        {
            var delta = deltas.TryGetValue(family, out var value) ? value : 0;
            if (delta >= rejectionFloor)
                alerts.Add(new OpsAlert("ingress-rejections", word, delta,
                    thresholds.IngressRejections,
                    $"{delta} {word} refusals in the last hour (window delta)."));
        }

        var chain = delivery.Stats();
        var failedFloor = Math.Max(thresholds.EffectsFailed, 1);
        if (chain.EffectsFailed >= failedFloor)
            alerts.Add(new OpsAlert("effects-failed", "outbox", chain.EffectsFailed,
                thresholds.EffectsFailed,
                $"{chain.EffectsFailed} effect attempts have not settled done — dispatch retries are burning."));
        if (chain.EffectsPending >= thresholds.EffectsPending && chain.EffectsPending > 0)
            alerts.Add(new OpsAlert("effects-pending", "outbox", chain.EffectsPending,
                thresholds.EffectsPending,
                $"{chain.EffectsPending} effects are waiting to dispatch."));

        var apiFloor = Math.Max(thresholds.PlatformApiFailures, 1);
        foreach (var endpoint in platformApi.Stats().Endpoints)
        {
            if (endpoint.Failures >= apiFloor)
                alerts.Add(new OpsAlert("platform-api-failures", endpoint.Endpoint, endpoint.Failures,
                    thresholds.PlatformApiFailures,
                    $"{endpoint.Failures} of {endpoint.Calls} calls to {endpoint.Endpoint} failed (process-lifetime)."));
        }

        return alerts
            .OrderBy(alert => alert.Kind, StringComparer.Ordinal)
            .ThenBy(alert => alert.Subject, StringComparer.Ordinal)
            .ToList();
    }
}
