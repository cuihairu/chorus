using Npgsql;

namespace Chorus.Control;

/// <summary>One endpoint's outbound platform API stats (FIX-OBS-12): the calls and
/// failures counters and the cumulative latency buckets — each bucket counts the
/// observations at or under its bound in milliseconds, and +Inf equals the calls
/// count.</summary>
public sealed record PlatformApiEndpointStats(
    string Endpoint, long Calls, long Failures, IReadOnlyList<PlatformApiLatencyBucket> Latency);

/// <summary>One cumulative latency bucket: Le is the bound ("10"…"5000" or "+Inf"), Count
/// the observations at or under it.</summary>
public sealed record PlatformApiLatencyBucket(string Le, long Count);

/// <summary>One reporting gateway's latest platform API counters, parsed out of the
/// composite families into their endpoints.</summary>
public sealed record PlatformApiGatewayStats(
    string NodeId, string ProcessInstanceId, DateTime ReportedAtUtc,
    IReadOnlyList<PlatformApiEndpointStats> Endpoints);

/// <summary>The outbound platform API answer (FIX-OBS-12): the fresh gateways' counters
/// summed per endpoint, and each fresh gateway's own parsed listing.</summary>
public sealed record PlatformApiStats(
    IReadOnlyList<PlatformApiEndpointStats> Endpoints,
    IReadOnlyList<PlatformApiGatewayStats> Gateways);

/// <summary>
/// Read-only outbound platform API statistics (FIX-OBS-12, dictionary
/// chorus_platform_api_calls_total / chorus_platform_api_failures_total /
/// chorus_platform_api_latency_ms). One horizon: the gateway counters land in the ops
/// table the reporter writes, as composite families — the scalar transport carries the
/// endpoint in the family name and the histogram as cumulative le buckets. The read
/// takes each fresh gateway's latest row per family, parses the endpoints apart and sums
/// the fresh gateways into the totals — a gateway that stopped reporting drops out
/// entirely, since its cumulative values describe a process that no longer exists. The
/// family names and bucket bounds are pinned to the dictionary (Chorus.Rpc OpsMetrics) by
/// a test; the ops schema lives only in PostgreSQL, so under the same-database assumption
/// the store reads through the control connection string, and a non-PostgreSQL control
/// database answers empty rather than failing: no platform traffic is a stat, not an
/// error.
/// </summary>
public sealed class PlatformApiStatsStore(string connectionString, ControlDatabaseProvider provider)
{
    /// <summary>How long a gateway's latest rows stay fresh (the self-report cadence).</summary>
    private const int FreshSeconds = 15;

    /// <summary>The families the composite names are built from, pinned to the dictionary
    /// names by a test (this project does not reference Chorus.Rpc).</summary>
    public const string CallsFamily = "chorus_platform_api_calls_total";
    public const string FailuresFamily = "chorus_platform_api_failures_total";
    public const string LatencyFamily = "chorus_platform_api_latency_ms";

    /// <summary>The histogram's cumulative bounds, ascending; a test pins them to the
    /// dictionary's. The +Inf bound is appended, never stored here.</summary>
    public static readonly IReadOnlyList<long> LatencyBoundsMs =
        [10, 25, 50, 100, 250, 500, 1000, 2500, 5000];

    /// <summary>The infinity bound, as the counter names it.</summary>
    public const string InfBound = "+Inf";

    /// <summary>The platform API stats at this moment.</summary>
    public PlatformApiStats Stats()
    {
        if (provider != ControlDatabaseProvider.PostgreSql)
            return new PlatformApiStats([], []);
        var gateways = ReadFreshGateways();
        return new PlatformApiStats(SumEndpoints(gateways), gateways);
    }

    /// <summary>Each fresh gateway's latest row per family, parsed into its endpoints.</summary>
    private List<PlatformApiGatewayStats> ReadFreshGateways()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"""
            SELECT DISTINCT ON (node_id, process_instance_id, family)
                node_id, process_instance_id, family, value, reported_at
            FROM ops.app_counter_sample
            WHERE reported_at >= clock_timestamp() - interval '{FreshSeconds} seconds'
            ORDER BY node_id, process_instance_id, family, reported_at DESC, id DESC
            """, connection);
        var gateways = new Dictionary<(string Node, string Instance),
            (DateTime At, Dictionary<string, EndpointAccumulator> Endpoints)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!TryParseFamily(reader.GetString(2), out var endpoint, out var kind, out var bound))
                continue;
            var node = reader.GetString(0);
            var instance = reader.GetString(1);
            if (!gateways.TryGetValue((node, instance), out var gateway))
                gateways[(node, instance)] = gateway = (reader.GetDateTime(4),
                    new Dictionary<string, EndpointAccumulator>(StringComparer.Ordinal));
            if (!gateway.Endpoints.TryGetValue(endpoint, out var accumulator))
                gateway.Endpoints[endpoint] = accumulator = new EndpointAccumulator();
            accumulator.Add(kind, bound, reader.GetInt64(3));
        }
        return gateways.OrderBy(g => g.Key.Node, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Instance, StringComparer.Ordinal)
            .Select(g => new PlatformApiGatewayStats(g.Key.Node, g.Key.Instance, g.Value.At,
                g.Value.Endpoints.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new PlatformApiEndpointStats(p.Key, p.Value.Calls, p.Value.Failures,
                        OrderedBuckets(p.Value.Buckets)))
                    .ToList()))
            .ToList();
    }

    /// <summary>Sums the fresh gateways into one row per endpoint; a bound a gateway never
    /// reported counts as zero there.</summary>
    private static IReadOnlyList<PlatformApiEndpointStats> SumEndpoints(
        IReadOnlyList<PlatformApiGatewayStats> gateways)
    {
        var merged = new Dictionary<string, EndpointAccumulator>(StringComparer.Ordinal);
        foreach (var gateway in gateways)
            foreach (var endpoint in gateway.Endpoints)
            {
                if (!merged.TryGetValue(endpoint.Endpoint, out var accumulator))
                    merged[endpoint.Endpoint] = accumulator = new EndpointAccumulator();
                accumulator.Calls += endpoint.Calls;
                accumulator.Failures += endpoint.Failures;
                foreach (var bucket in endpoint.Latency)
                    accumulator.Buckets[bucket.Le] = accumulator.Buckets.GetValueOrDefault(bucket.Le) + bucket.Count;
            }
        return merged.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new PlatformApiEndpointStats(p.Key, p.Value.Calls, p.Value.Failures,
                OrderedBuckets(p.Value.Buckets)))
            .ToList();
    }

    /// <summary>The composite families the reporter emits, parsed apart — a family this
    /// store does not own (the ingress and event families ride the same table) is skipped
    /// here, not an error.</summary>
    private static bool TryParseFamily(string family, out string endpoint,
        out EndpointAccumulator.Kind kind, out string bound)
    {
        endpoint = "";
        bound = "";
        kind = EndpointAccumulator.Kind.Calls;
        if (family.StartsWith(CallsFamily + ":", StringComparison.Ordinal))
        {
            endpoint = family[(CallsFamily.Length + 1)..];
            kind = EndpointAccumulator.Kind.Calls;
            return endpoint.Length > 0;
        }
        if (family.StartsWith(FailuresFamily + ":", StringComparison.Ordinal))
        {
            endpoint = family[(FailuresFamily.Length + 1)..];
            kind = EndpointAccumulator.Kind.Failures;
            return endpoint.Length > 0;
        }
        if (family.StartsWith(LatencyFamily + ":", StringComparison.Ordinal))
        {
            var marker = family.LastIndexOf(":le=", StringComparison.Ordinal);
            if (marker < LatencyFamily.Length + 1) return false;
            endpoint = family[(LatencyFamily.Length + 1)..marker];
            bound = family[(marker + 4)..];
            kind = EndpointAccumulator.Kind.Latency;
            return endpoint.Length > 0 && bound.Length > 0;
        }
        return false;
    }

    /// <summary>The bucket list in bound order, zeros included, +Inf last.</summary>
    private static IReadOnlyList<PlatformApiLatencyBucket> OrderedBuckets(
        IReadOnlyDictionary<string, long> buckets) =>
        LatencyBoundsMs.Select(b => new PlatformApiLatencyBucket(b.ToString(),
                buckets.GetValueOrDefault(b.ToString())))
            .Append(new PlatformApiLatencyBucket(InfBound, buckets.GetValueOrDefault(InfBound)))
            .ToList();

    /// <summary>One endpoint's counters under construction, per gateway or merged.</summary>
    private sealed class EndpointAccumulator
    {
        public enum Kind { Calls, Failures, Latency }

        public long Calls;
        public long Failures;
        public readonly Dictionary<string, long> Buckets = new(StringComparer.Ordinal);

        public void Add(Kind kind, string bound, long value)
        {
            if (kind == Kind.Calls) Calls += value;
            else if (kind == Kind.Failures) Failures += value;
            else Buckets[bound] = Buckets.GetValueOrDefault(bound) + value;
        }
    }
}
