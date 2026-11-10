using Chorus.Rpc;

namespace Chorus.PlatformGateway;

/// <summary>
/// The gateway's outbound platform API counters (FIX-OBS-12, dictionary
/// chorus_platform_api_calls_total, chorus_platform_api_failures_total and the
/// chorus_platform_api_latency_ms histogram, label endpoint). The vocabulary is bounded
/// by construction: one name per adapter method per platform — "{platform}.execute" for
/// a CallAsync, "{platform}.status" for a QueryAsync — never a per-request value. A
/// failed call is one whose transport exchange threw (HTTP fault or timeout); a platform
/// answer of "not executed" is still a successful API interaction. The histogram rides
/// the scalar counter transport as Prometheus-style cumulative buckets: each observation
/// lands in the bucket its latency falls in, the snapshot emits every bucket as the
/// count at or under its bound, and le=+Inf counts every observed call exactly like the
/// calls family.
/// </summary>
public sealed class PlatformApiCounters
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);

    /// <summary>Records one outbound platform API exchange with its wall latency.</summary>
    public void Observed(string endpoint, bool failed, double latencyMs)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(endpoint, out var counters))
                _endpoints[endpoint] = counters = new Endpoint();
            counters.Calls++;
            if (failed) counters.Failures++;
            var bounds = OpsMetrics.PlatformApiLatencyBoundsMs;
            for (var i = 0; i < bounds.Count; i++)
                if (latencyMs <= bounds[i])
                {
                    counters.Buckets[i]++;
                    break;
                }
        }
    }

    /// <summary>The cumulative snapshot the reporter posts: composite families naming the
    /// dictionary metric, the endpoint and — for latency — the cumulative bucket bound,
    /// since the ops table carries scalars only.</summary>
    public IReadOnlyList<(string Family, long Value)> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<(string Family, long Value)>();
            foreach (var (endpoint, counters) in _endpoints.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                snapshot.Add(($"{OpsMetrics.PlatformApiCallsTotal.Name}:{endpoint}", counters.Calls));
                snapshot.Add(($"{OpsMetrics.PlatformApiFailuresTotal.Name}:{endpoint}", counters.Failures));
                var bounds = OpsMetrics.PlatformApiLatencyBoundsMs;
                long cumulative = 0;
                for (var i = 0; i < bounds.Count; i++)
                {
                    cumulative += counters.Buckets[i];
                    snapshot.Add(($"{OpsMetrics.PlatformApiLatencyMs.Name}:{endpoint}:le={bounds[i]}", cumulative));
                }
                snapshot.Add(($"{OpsMetrics.PlatformApiLatencyMs.Name}:{endpoint}:le=+Inf", counters.Calls));
            }
            return snapshot;
        }
    }

    private sealed class Endpoint
    {
        public long Calls;
        public long Failures;
        public readonly long[] Buckets = new long[OpsMetrics.PlatformApiLatencyBoundsMs.Count];
    }
}
