using Npgsql;

namespace Chorus.Control;

/// <summary>One reporting face's latest counters (FIX-OBS-10): the families it answered
/// with, keyed by the dictionary metric name. Only families the reporter sent appear.</summary>
public sealed record IngressGatewayCounters(
    string NodeId, string ProcessInstanceId, DateTime ReportedAtUtc,
    IReadOnlyDictionary<string, long> Counters);

/// <summary>One source bucket of the durable raw-input counter (FIX-OBS-10).</summary>
public sealed record IngressSourceCount(string Source, long Count);

/// <summary>The platform raw ingress stats (FIX-OBS-10, dictionary chorus_ingress_*_total
/// and chorus_input_events_total). Two horizons, honestly separate: the face counters sum
/// the fresh reporters' cumulative values — since each gateway process started, and only
/// while it still reports; the raw-input counter reads the data plane's input.event rows —
/// durable since the beginning, never reset by a restart.</summary>
public sealed record IngressStats(
    long Accepted, long Duplicates, long Unverified, long Unmapped,
    long RateLimited, long TooLarge, long TimedOut,
    IReadOnlyList<IngressGatewayCounters> Gateways,
    IReadOnlyList<IngressSourceCount> InputEventsBySource);

/// <summary>
/// Read-only ingress statistics (FIX-OBS-10). The face counters land in the ops table the
/// gateway's reporter writes; the read takes each reporter's latest row per family inside
/// the freshness window — the same latest-per-reporter rule the machine telemetry uses —
/// and sums the fresh reporters into the totals. A reporter that stopped reporting drops
/// out of the sums entirely: its cumulative values describe a process that no longer
/// exists. The raw-input counter reads the data plane's input.event by platform, so it
/// answers what the data plane durably received, independent of any gateway's lifetime.
/// The ops schema and input.event live only in PostgreSQL, so under the same-database
/// assumption the store reads through the control connection string; a non-PostgreSQL
/// control database answers zeros and empty rather than failing: no ingress traffic is a
/// stat, not an error.
/// </summary>
public sealed class IngressStatsStore(string connectionString, ControlDatabaseProvider provider)
{
    /// <summary>How long a reporter's latest rows stay fresh. The self-report cadence is
    /// seconds (the gateway posts readiness and counters on the same interval), so a
    /// reporter missing this many ticks is gone.</summary>
    private const int FreshSeconds = 15;

    /// <summary>The seven ingress families, pinned to the dictionary names (Chorus.Rpc
    /// OpsMetrics; a test asserts the two listings agree). The totals sum exactly these —
    /// a later family (FIX-OBS-11/12) rides the same table without entering this answer.</summary>
    public const string AcceptedFamily = "chorus_ingress_accepted_total";
    public const string DuplicatesFamily = "chorus_ingress_duplicates_total";
    public const string UnverifiedFamily = "chorus_ingress_unverified_total";
    public const string UnmappedFamily = "chorus_ingress_unmapped_total";
    public const string RateLimitedFamily = "chorus_ingress_rate_limited_total";
    public const string TooLargeFamily = "chorus_ingress_too_large_total";
    public const string TimedOutFamily = "chorus_ingress_timed_out_total";

    /// <summary>The ingress stats at this moment.</summary>
    public IngressStats Stats()
    {
        if (provider != ControlDatabaseProvider.PostgreSql)
            return new IngressStats(0, 0, 0, 0, 0, 0, 0, [], []);
        var gateways = ReadFreshReporters();
        long Sum(string family) => gateways.Sum(g =>
            g.Counters.TryGetValue(family, out var value) ? value : 0);
        return new IngressStats(Sum(AcceptedFamily), Sum(DuplicatesFamily), Sum(UnverifiedFamily),
            Sum(UnmappedFamily), Sum(RateLimitedFamily), Sum(TooLargeFamily), Sum(TimedOutFamily),
            gateways, ReadInputEventsBySource());
    }

    /// <summary>Each fresh reporter's latest row per family — the cumulative counters the
    /// process reported most recently while it still reported.</summary>
    private List<IngressGatewayCounters> ReadFreshReporters()
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
        var reporters = new Dictionary<(string Node, string Instance),
            (DateTime At, Dictionary<string, long> Counters)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var node = reader.GetString(0);
            var instance = reader.GetString(1);
            if (!reporters.TryGetValue((node, instance), out var reporter))
                reporters[(node, instance)] = reporter =
                    (reader.GetDateTime(4), new Dictionary<string, long>(StringComparer.Ordinal));
            reporter.Counters[reader.GetString(2)] = reader.GetInt64(3);
        }
        return reporters.OrderBy(r => r.Key.Node, StringComparer.Ordinal)
            .ThenBy(r => r.Key.Instance, StringComparer.Ordinal)
            .Select(r => new IngressGatewayCounters(r.Key.Node, r.Key.Instance, r.Value.At,
                r.Value.Counters))
            .ToList();
    }

    /// <summary>The data plane's own durable answer: every raw platform input it ever
    /// received, one row per distinct event, grouped by source.</summary>
    private List<IngressSourceCount> ReadInputEventsBySource()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT platform, count(*) FROM input.event GROUP BY platform ORDER BY platform
            """, connection);
        var result = new List<IngressSourceCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new IngressSourceCount(reader.GetString(0), reader.GetInt64(1)));
        return result;
    }
}
