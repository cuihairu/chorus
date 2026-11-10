using Npgsql;

namespace Chorus.Control;

/// <summary>One node's latest machine telemetry within the query window (FIX-OBS-07): the
/// resource families as reported (an absent family is null, per the collectors' contract),
/// that sample's per-mount disk rows, and how many samples the node reported in the window.</summary>
public sealed record MachineStatsNode(
    string NodeId, DateTime SampledAtUtc, long ReportSequence,
    double? CpuPercent, MachineMemoryStats? Memory, MachineLoadStats? Load,
    IReadOnlyList<MachineDiskStats> Disk, long Samples);

public sealed record MachineMemoryStats(long TotalBytes, long UsedBytes, double UsedPercent);

public sealed record MachineLoadStats(double L1, double L5, double L15);

public sealed record MachineDiskStats(string Mount, long TotalBytes, long UsedBytes, double UsedPercent);

/// <summary>One supervised process's latest reading within the window (FIX-OBS-07).</summary>
public sealed record ProcessStatsEntry(
    string NodeId, string ProcessInstanceId, string Role, DateTime SampledAtUtc,
    double CpuPercent, long MemoryUsedBytes, long UptimeSeconds);

/// <summary>One node's process stats (FIX-OBS-07): the latest reading per supervised process
/// and the process count of the node's latest sample — the derived `processes_running`, not
/// an independent counter.</summary>
public sealed record ProcessStatsNode(
    string NodeId, int ProcessesRunning, IReadOnlyList<ProcessStatsEntry> Processes);

/// <summary>
/// Read-only statistics over the ops telemetry tables (FIX-OBS-07) — the browser management
/// face WEB-06 consumes. The ops schema lives only in PostgreSQL, the one business write
/// store, so under the same-database assumption the store reads through the control
/// connection string. A non-PostgreSQL control database (local Sqlite development) has no
/// ops tables and answers empty rather than failing: no telemetry is a stat, not an error.
/// </summary>
public sealed class TelemetryStore(string connectionString, ControlDatabaseProvider provider)
{
    private static readonly string WindowClause = "sampled_at >= clock_timestamp() - @window * interval '1 minute'";

    /// <summary>Latest machine sample per node in the window, with its disk rows and the
    /// node's sample count in the window. `nodeId` narrows to one node.</summary>
    public IReadOnlyList<MachineStatsNode> MachineStats(string? nodeId, int windowMinutes)
    {
        if (provider != ControlDatabaseProvider.PostgreSql) return [];
        var window = Math.Clamp(windowMinutes, 1, 24 * 60);
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        var nodeFilter = string.IsNullOrWhiteSpace(nodeId) ? "TRUE" : "node_id = @node";
        using var samples = new NpgsqlCommand($"""
            WITH latest AS (
                SELECT DISTINCT ON (node_id) id, node_id, sampled_at, report_sequence, cpu_percent,
                    memory_total_bytes, memory_used_bytes, memory_used_percent,
                    load_1m, load_5m, load_15m
                FROM ops.machine_sample
                WHERE {WindowClause} AND {nodeFilter}
                ORDER BY node_id, sampled_at DESC, id DESC
            )
            SELECT l.id, l.node_id, l.sampled_at, l.report_sequence, l.cpu_percent,
                l.memory_total_bytes, l.memory_used_bytes, l.memory_used_percent,
                l.load_1m, l.load_5m, l.load_15m,
                (SELECT COUNT(*) FROM ops.machine_sample m
                 WHERE m.node_id = l.node_id AND {WindowClause})
            FROM latest l ORDER BY l.node_id
            """, connection);
        samples.Parameters.AddWithValue("window", window);
        if (nodeFilter != "TRUE") samples.Parameters.AddWithValue("node", nodeId!.Trim());
        var ids = new List<long>();
        var byNode = new Dictionary<string, (long SampleId, DateTime At, long Sequence, double? Cpu,
            MachineMemoryStats? Memory, MachineLoadStats? Load, long Samples)>();
        using (var reader = samples.ExecuteReader())
        {
            while (reader.Read())
            {
                var nodeIdValue = reader.GetString(1);
                ids.Add(reader.GetInt64(0));
                var memory = reader.IsDBNull(5) ? null : new MachineMemoryStats(
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetDouble(7));
                var load = reader.IsDBNull(8) ? null : new MachineLoadStats(
                    reader.GetDouble(8), reader.GetDouble(9), reader.GetDouble(10));
                byNode[nodeIdValue] = (reader.GetInt64(0), reader.GetDateTime(2), reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4), memory, load, reader.GetInt64(11));
            }
        }
        var disks = ids.Count == 0 ? [] : ReadDisks(connection, ids);
        var result = new List<MachineStatsNode>(byNode.Count);
        foreach (var (id, row) in byNode)
            result.Add(new MachineStatsNode(id, row.At, row.Sequence, row.Cpu, row.Memory, row.Load,
                disks.TryGetValue(row.SampleId, out var mounts) ? mounts : [], row.Samples));
        return result;
    }

    /// <summary>Latest process reading per supervised process of each node's latest sample in
    /// the window; `processes_running` is that sample's process-row count (a node reporting
    /// machine telemetry with no readable processes answers 0, not absence).</summary>
    public IReadOnlyList<ProcessStatsNode> ProcessStats(string? nodeId, int windowMinutes)
    {
        if (provider != ControlDatabaseProvider.PostgreSql) return [];
        var window = Math.Clamp(windowMinutes, 1, 24 * 60);
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        var nodeFilter = string.IsNullOrWhiteSpace(nodeId) ? "TRUE" : "node_id = @node";
        using var command = new NpgsqlCommand($"""
            WITH latest AS (
                SELECT DISTINCT ON (node_id) id, node_id, sampled_at
                FROM ops.machine_sample
                WHERE {WindowClause} AND {nodeFilter}
                ORDER BY node_id, sampled_at DESC, id DESC
            )
            SELECT l.node_id, l.sampled_at, p.process_instance_id, p.role,
                p.cpu_percent, p.memory_used_bytes, p.uptime_seconds
            FROM latest l LEFT JOIN ops.process_sample p ON p.machine_sample_id = l.id
            ORDER BY l.node_id, p.process_instance_id
            """, connection);
        command.Parameters.AddWithValue("window", window);
        if (nodeFilter != "TRUE") command.Parameters.AddWithValue("node", nodeId!.Trim());
        var order = new List<string>();
        var byNode = new Dictionary<string, List<ProcessStatsEntry>>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var nodeIdValue = reader.GetString(0);
                if (!byNode.TryGetValue(nodeIdValue, out var entries))
                {
                    entries = [];
                    byNode[nodeIdValue] = entries;
                    order.Add(nodeIdValue);
                }
                // The LEFT JOIN answers null process columns for a sample with no process rows.
                if (reader.IsDBNull(2)) continue;
                entries.Add(new ProcessStatsEntry(nodeIdValue, reader.GetString(2), reader.GetString(3),
                    reader.GetDateTime(1), reader.GetDouble(4), reader.GetInt64(5), reader.GetInt64(6)));
            }
        }
        var result = new List<ProcessStatsNode>(order.Count);
        foreach (var id in order)
            result.Add(new ProcessStatsNode(id, byNode[id].Count, byNode[id]));
        return result;
    }

    private static Dictionary<long, List<MachineDiskStats>> ReadDisks(
        NpgsqlConnection connection, IReadOnlyList<long> sampleIds)
    {
        using var command = new NpgsqlCommand("""
            SELECT machine_sample_id, mount, total_bytes, used_bytes, used_percent
            FROM ops.machine_disk_sample WHERE machine_sample_id = ANY(@ids)
            ORDER BY machine_sample_id, mount
            """, connection);
        command.Parameters.AddWithValue("ids", sampleIds.ToArray());
        var result = new Dictionary<long, List<MachineDiskStats>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sampleId = reader.GetInt64(0);
            if (!result.TryGetValue(sampleId, out var mounts))
                result[sampleId] = mounts = [];
            mounts.Add(new MachineDiskStats(
                reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4)));
        }
        return result;
    }
}
