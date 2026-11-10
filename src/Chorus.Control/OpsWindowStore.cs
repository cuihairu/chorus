using Npgsql;

namespace Chorus.Control;

/// <summary>One family's windowed total (FIX-OBS-13): the summed counter increments the
/// family's reporters made inside the window, restart-aware — a reporter whose cumulative
/// value goes down between two samples restarted, and its post-restart value counts from
/// zero. Composite families (the platform API's endpoint and latency-bucket forms) roll
/// up under their base dictionary name.</summary>
public sealed record OpsWindowFamilyStats(string Family, long Delta);

/// <summary>One minute of a family's windowed series: MinuteUnix is the bucket start
/// (epoch seconds, minute-aligned), Delta the increments that landed in it. A minute
/// with no samples is absent, not zero — absence is no data, not a zero reading.</summary>
public sealed record OpsWindowSeriesPoint(long MinuteUnix, long Delta);

/// <summary>One family's per-minute series across the window, points ascending.</summary>
public sealed record OpsWindowFamilySeries(string Family, IReadOnlyList<OpsWindowSeriesPoint> Points);

/// <summary>The windowed aggregate answer (FIX-OBS-13): each family's total delta over
/// the window and the same deltas bucketed per minute, plus the window's bounds as
/// answered.</summary>
public sealed record OpsWindowStats(int WindowMinutes, DateTime WindowStartUtc,
    IReadOnlyList<OpsWindowFamilyStats> Families, IReadOnlyList<OpsWindowFamilySeries> Series);

/// <summary>
/// Windowed aggregation over the ops sample history and its retention (FIX-OBS-13). The
/// app-counter series the faces report (OBS-10/11/12) is cumulative per process; the
/// windowed delta sums each reporter's per-step increments inside the window — a
/// per-sample LAG over the reporter's series, seeded with the reporter's latest
/// pre-window row as the baseline so a step that straddles the window start counts once,
/// and a drop between steps (a process restart) counts the post-restart value from zero.
/// Composite families roll up under their base name (everything before the first colon),
/// so the platform API's per-endpoint counters and latency buckets answer as their
/// dictionary families. Unlike the live totals' freshness rule, a reporter that stopped
/// reporting mid-window still contributes the steps it made inside the window — the
/// window describes what happened, not what is alive. A minute with no samples stays
/// absent from the series. The same store prunes the ops sample tables past the
/// retention horizon (the app counters by their report clock, the machine samples by
/// theirs — disks and process rows fall with their parent by cascade). The ops schema
/// lives only in PostgreSQL, so under the same-database assumption the store reads
/// through the control connection string; a non-PostgreSQL control database answers
/// empty rather than failing.
/// </summary>
public sealed class OpsWindowStore(string connectionString, ControlDatabaseProvider provider)
{
    /// <summary>The windowed read, clamped like the telemetry windows (a mistyped minute
    /// count answers a sane window, not an unbounded scan).</summary>
    public OpsWindowStats Stats(int windowMinutes)
    {
        var window = Math.Clamp(windowMinutes, 1, 24 * 60);
        var start = DateTime.UtcNow.AddMinutes(-window);
        if (provider != ControlDatabaseProvider.PostgreSql)
            return new OpsWindowStats(window, start, [], []);
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"""
            WITH bounded AS (
                SELECT node_id, process_instance_id, family, reported_at, value, id, TRUE AS in_window
                FROM ops.app_counter_sample
                WHERE reported_at >= @start
                UNION ALL
                SELECT DISTINCT ON (node_id, process_instance_id, family)
                    node_id, process_instance_id, family, reported_at, value, id, FALSE
                FROM ops.app_counter_sample
                WHERE reported_at < @start
                ORDER BY node_id, process_instance_id, family, reported_at DESC, id DESC
            ),
            steps AS (
                SELECT split_part(family, ':', 1) AS base_family,
                    (floor(extract(epoch FROM reported_at) / 60) * 60)::bigint AS minute_unix,
                    in_window,
                    CASE WHEN prev IS NULL THEN value
                         WHEN value < prev THEN value
                         ELSE value - prev END AS delta
                FROM (
                    SELECT node_id, process_instance_id, family, reported_at, value, in_window,
                        LAG(value) OVER (PARTITION BY node_id, process_instance_id, family
                            ORDER BY reported_at, id) AS prev
                    FROM bounded
                ) lagged
            )
            SELECT base_family, minute_unix, grouping(minute_unix) AS is_total, sum(delta) AS delta
            FROM steps
            WHERE in_window
            GROUP BY GROUPING SETS ((base_family), (base_family, minute_unix))
            ORDER BY base_family, is_total DESC, minute_unix
            """, connection);
        command.Parameters.AddWithValue("start", start);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        var series = new Dictionary<string, List<(long Minute, long Delta)>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var family = reader.GetString(0);
            var delta = reader.GetInt64(3);
            if (reader.GetInt64(2) == 1)
                totals[family] = totals.GetValueOrDefault(family) + delta;
            else if (!series.TryGetValue(family, out var points))
                series[family] = [((reader.GetInt64(1), delta))];
            else
                points.Add((reader.GetInt64(1), delta));
        }
        return new OpsWindowStats(window, start,
            totals.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new OpsWindowFamilyStats(p.Key, p.Value))
                .ToList(),
            series.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new OpsWindowFamilySeries(p.Key,
                    p.Value.OrderBy(point => point.Minute)
                        .Select(point => new OpsWindowSeriesPoint(point.Minute, point.Delta))
                        .ToList()))
                .ToList());
    }

    /// <summary>Drops the ops sample rows older than the cutoff — the app counters by
    /// their report clock and the machine samples by their sample clock, whose disk and
    /// process children fall with them by cascade. Answers the rows removed (zero when
    /// retention is a no-op, and always zero without PostgreSQL).</summary>
    public int PruneSamples(DateTimeOffset cutoff)
    {
        if (provider != ControlDatabaseProvider.PostgreSql) return 0;
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        var removed = 0;
        foreach (var (table, column) in new[] { ("ops.app_counter_sample", "reported_at"),
            ("ops.machine_sample", "sampled_at") })
        {
            using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE {column} < @cutoff", connection);
            command.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);
            removed += command.ExecuteNonQuery();
        }
        return removed;
    }
}
