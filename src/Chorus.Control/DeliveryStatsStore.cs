using Npgsql;

namespace Chorus.Control;

/// <summary>One reporting room host's latest counters (FIX-OBS-11): the families it
/// answered with, keyed by the dictionary metric name. Only families the reporter sent
/// appear.</summary>
public sealed record DeliveryGatewayCounters(
    string NodeId, string ProcessInstanceId, DateTime ReportedAtUtc,
    IReadOnlyDictionary<string, long> Counters);

/// <summary>The event delivery and effect chain stats (FIX-OBS-11, dictionary
/// chorus_events_*_total and chorus_effects_*). Two horizons, honestly separate: the
/// session-face counters sum the fresh reporters' cumulative values — since each room
/// host process started, and only while it still reports; the delivery/effect counts and
/// the outbox dispatch stages read the data and control planes' own durable tables — exact
/// since the beginning, never reset by a restart. The delivered/effect pair brackets the
/// room-side chain (a delivery row is written when the routing hands the event to its
/// target room's inbox; the frame commit that consumes it marks the effect applied), and
/// the outbox stages are the control plane's effect dispatch over infra.outbox — the
/// failed counter derives from the attempt ledger (every claim that was not the claim that
/// settled the row Done was a failed attempt; rows are never pruned).</summary>
public sealed record DeliveryStats(
    long Ingested, long Applied, long Merged, long Dropped,
    long Delivered, long EffectsApplied,
    long EffectsPending, long EffectsDispatching, long EffectsDone, long EffectsFailed,
    IReadOnlyList<DeliveryGatewayCounters> Reporters);

/// <summary>
/// Read-only event delivery statistics (FIX-OBS-11). The session-face counters land in
/// the ops table the room host's reporter writes; the read takes each reporter's latest
/// row per family inside the freshness window and sums the fresh reporters into the
/// totals — a reporter that stopped reporting drops out of the sums entirely, since its
/// cumulative values describe a process that no longer exists. The delivery, effect and
/// outbox numbers read the durable tables directly, so they answer what the planes
/// durably hold, independent of any process's lifetime. The input, infra and ops schemas
/// live only in PostgreSQL, so under the same-database assumption the store reads through
/// the control connection string; a non-PostgreSQL control database answers zeros and
/// empty rather than failing: no delivery traffic is a stat, not an error.
/// </summary>
public sealed class DeliveryStatsStore(string connectionString, ControlDatabaseProvider provider)
{
    /// <summary>How long a reporter's latest rows stay fresh. The self-report cadence is
    /// seconds (the room host posts readiness and counters on the same interval), so a
    /// reporter missing this many ticks is gone.</summary>
    private const int FreshSeconds = 15;

    /// <summary>The session-face families, pinned to the dictionary names (Chorus.Rpc
    /// OpsMetrics; a test asserts the two listings agree). The totals sum exactly these —
    /// a later family (FIX-OBS-12) rides the same table without entering this answer.</summary>
    public const string IngestedFamily = "chorus_events_ingested_total";
    public const string AppliedFamily = "chorus_events_applied_total";
    public const string MergedFamily = "chorus_events_merged_total";
    public const string DroppedFamily = "chorus_events_dropped_total";

    /// <summary>The delivery stats at this moment.</summary>
    public DeliveryStats Stats()
    {
        if (provider != ControlDatabaseProvider.PostgreSql)
            return new DeliveryStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        var reporters = ReadFreshReporters();
        long Sum(string family) => reporters.Sum(r =>
            r.Counters.TryGetValue(family, out var value) ? value : 0);
        var (delivered, effects) = ReadDeliveryCounts();
        var stages = ReadOutboxStages();
        return new DeliveryStats(Sum(IngestedFamily), Sum(AppliedFamily), Sum(MergedFamily),
            Sum(DroppedFamily), delivered, effects,
            stages.Pending, stages.Dispatching, stages.Done, stages.Failed, reporters);
    }

    /// <summary>Each fresh reporter's latest row per family — the cumulative counters the
    /// process reported most recently while it still reported.</summary>
    private List<DeliveryGatewayCounters> ReadFreshReporters()
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
            .Select(r => new DeliveryGatewayCounters(r.Key.Node, r.Key.Instance, r.Value.At,
                r.Value.Counters))
            .ToList();
    }

    /// <summary>The data plane's own durable answer: every delivery row is an event the
    /// routing handed to its target room's inbox, and the Consumed mark is the frame
    /// commit that applied the event's effect at that room.</summary>
    private (long Delivered, long EffectsApplied) ReadDeliveryCounts()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT count(*), count(*) FILTER (WHERE state='Consumed') FROM input.delivery
            """, connection);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>The control plane's outbox stages: the dispatch loop's own table, counted
    /// by status. The failed counter derives from the attempt ledger — a row settled Done
    /// spent its last attempt succeeding, so every other claim it ever took failed; a row
    /// still pending took only failed claims so far. Rows are never pruned, so the
    /// derivation never moves backward.</summary>
    private (long Pending, long Dispatching, long Done, long Failed) ReadOutboxStages()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "SELECT status, count(*), sum(attempt_count) FROM infra.outbox GROUP BY status",
            connection);
        long pending = 0, dispatching = 0, done = 0, attempts = 0, rows = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var status = reader.GetString(0);
            var count = reader.GetInt64(1);
            attempts += reader.GetInt64(2);
            rows += count;
            if (status == "Pending") pending = count;
            else if (status == "Dispatching") dispatching = count;
            else if (status == "Done") done = count;
        }
        return (pending, dispatching, done, attempts - done);
    }
}
