using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

public sealed record NodeStatus(string AgentInstanceId, long ReportSequence);
public sealed record ProcessStatus(bool Accepted, string Readiness, string EntityRecovery);

/// <summary>M0 control writes. Database time and row locks decide ownership and report order.</summary>
public sealed partial class RedesignControlStore(string connectionString)
{
    public async Task<NodeStatus> RegisterNodeAsync(string nodeId, string agentInstanceId, string capabilitiesJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.node(node_id,agent_instance_id,capabilities,report_sequence)
            VALUES (@node,@agent,@capabilities::jsonb,0)
            ON CONFLICT(node_id) DO UPDATE
            SET agent_instance_id=excluded.agent_instance_id,
                capabilities=excluded.capabilities,
                report_sequence=CASE WHEN control.node.agent_instance_id=excluded.agent_instance_id
                    THEN control.node.report_sequence ELSE 0 END,
                observed_at=clock_timestamp()
            RETURNING agent_instance_id,report_sequence
            """, connection);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("agent", agentInstanceId);
        command.Parameters.AddWithValue("capabilities", capabilitiesJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Node registration returned no row.");
        return new NodeStatus(reader.GetString(0), reader.GetInt64(1));
    }

    public async Task<ProcessStatus> ReportProcessAsync(
        string nodeId, string agentInstanceId, string slotId, string role, string processInstanceId,
        long reportSequence, string lifecycle, string readiness, string health, string admission,
        string entityRecovery, DateTime startedAtUtc, string? endpoint = null, bool selfReport = false,
        CancellationToken cancellationToken = default)
    {
        if (reportSequence <= 0) throw new ArgumentOutOfRangeException(nameof(reportSequence));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A supervising agent speaks for its node; a self-reporting process speaks for itself —
        // the node row lock still serializes both against a concurrent agent takeover.
        await using (var node = new NpgsqlCommand("""
            SELECT agent_instance_id FROM control.node WHERE node_id=@node FOR UPDATE
            """, connection, transaction))
        {
            node.Parameters.AddWithValue("node", nodeId);
            if (await node.ExecuteScalarAsync(cancellationToken) is not string current
                || (!selfReport && current != agentInstanceId))
                return new ProcessStatus(false, "NotReady", "StaleAgent");
        }

        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,desired_state,package_hash)
            VALUES (@slot,@node,@role,'bootstrap',1,'Running','bootstrap')
            ON CONFLICT(slot_id) DO NOTHING
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue("slot", slotId);
            seed.Parameters.AddWithValue("node", nodeId);
            seed.Parameters.AddWithValue("role", role);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var slot = new NpgsqlCommand(
            "SELECT node_id,role FROM control.deployment WHERE slot_id=@slot FOR UPDATE", connection, transaction))
        {
            slot.Parameters.AddWithValue("slot", slotId);
            await using var slotReader = await slot.ExecuteReaderAsync(cancellationToken);
            if (!await slotReader.ReadAsync(cancellationToken) || slotReader.GetString(0) != nodeId ||
                slotReader.GetString(1) != role)
                return new ProcessStatus(false, "NotReady", "SlotMismatch");
        }

        // The upsert orders incarnations, not just sequences (FIX-SCHED-01): a restarted
        // process under the same instance id starts its sequence over with a later
        // started_at, and the newer incarnation takes the row over — its endpoint replaces
        // the dead one's. A replay from an older incarnation, or a stale sequence inside the
        // same one, is rejected; both leave the row exactly as the live process left it.
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,report_sequence,
                lifecycle,readiness,health,admission,entity_recovery,started_at,endpoint)
            VALUES (@instance,@slot,@node,@agent,@sequence,@lifecycle,@readiness,@health,@admission,@recovery,
                @started,@endpoint)
            ON CONFLICT(process_instance_id) DO UPDATE
            SET report_sequence=excluded.report_sequence,started_at=excluded.started_at,
                lifecycle=excluded.lifecycle,
                readiness=excluded.readiness,health=excluded.health,
                -- Draining is the admin's word and it is one way (FIX-SCHED-02): a drained
                -- instance that keeps reporting cannot un-drain itself — returning to
                -- service is an admin act, never the host's optimism.
                admission=CASE WHEN control.process.admission='Draining'
                    THEN 'Draining' ELSE excluded.admission END,
                entity_recovery=excluded.entity_recovery,endpoint=excluded.endpoint,observed_at=clock_timestamp()
            WHERE control.process.agent_instance_id=excluded.agent_instance_id
              AND (control.process.started_at<excluded.started_at
                   OR (control.process.started_at=excluded.started_at
                       AND control.process.report_sequence<excluded.report_sequence))
            RETURNING readiness,entity_recovery
            """, connection, transaction);
        command.Parameters.AddWithValue("instance", processInstanceId);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("agent", agentInstanceId);
        command.Parameters.AddWithValue("sequence", reportSequence);
        command.Parameters.AddWithValue("lifecycle", lifecycle);
        command.Parameters.AddWithValue("readiness", readiness);
        command.Parameters.AddWithValue("health", health);
        command.Parameters.AddWithValue("admission", admission);
        command.Parameters.AddWithValue("recovery", entityRecovery);
        command.Parameters.AddWithValue("started", startedAtUtc);
        if (endpoint is null) command.Parameters.AddWithValue("endpoint", DBNull.Value);
        else command.Parameters.AddWithValue("endpoint", endpoint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accepted = await reader.ReadAsync(cancellationToken);
        var result = accepted
            ? new ProcessStatus(true, reader.GetString(0), reader.GetString(1))
            : new ProcessStatus(false, "NotReady", "StaleReport");
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>One mounted filesystem in a machine telemetry snapshot (FIX-OBS-06).</summary>
    public sealed record MachineDiskMount(string Mount, long TotalBytes, long UsedBytes, double UsedPercent);

    /// <summary>A supervised process's telemetry in one machine sample (FIX-OBS-07).</summary>
    public sealed record MachineProcessSample(
        string ProcessInstanceId, string Role, double CpuPercent,
        long MemoryUsedBytes, long UptimeSeconds);

    /// <summary>One counter family of a face's app-counter report (FIX-OBS-10): the family
    /// names the dictionary metric, the value is the reporter's cumulative count.</summary>
    public sealed record AppCounterEntry(string Family, long Value);

    /// <summary>
    /// Stores one accepted machine telemetry snapshot (FIX-OBS-06/07): the node agent's sample
    /// lands as one append-only row (per-mount disk and per-process samples ride child tables).
    /// The node row lock verifies the reporting agent is still the node's registered agent — a
    /// stale agent's late sample is refused rather than written under a displaced identity.
    /// Telemetry is at-least-once: a retry whose first response was lost can duplicate a row,
    /// harmless for periodic levels.
    /// </summary>
    public async Task<bool> ReportMachineTelemetryAsync(
        string nodeId, string agentInstanceId, DateTime sampledAtUtc, long reportSequence,
        double? cpuPercent, (long Total, long Used, double Percent)? memory,
        (double L1, double L5, double L15)? load,
        IReadOnlyList<MachineDiskMount> disk,
        IReadOnlyList<MachineProcessSample> process, CancellationToken cancellationToken = default)
    {
        if (reportSequence <= 0) throw new ArgumentOutOfRangeException(nameof(reportSequence));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var node = new NpgsqlCommand(
            "SELECT agent_instance_id FROM control.node WHERE node_id=@node FOR UPDATE", connection, transaction))
        {
            node.Parameters.AddWithValue("node", nodeId);
            if (await node.ExecuteScalarAsync(cancellationToken) is not string current || current != agentInstanceId)
                return false;
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO ops.machine_sample(node_id, agent_instance_id, sampled_at, report_sequence,
                cpu_percent, memory_total_bytes, memory_used_bytes, memory_used_percent,
                load_1m, load_5m, load_15m)
            VALUES (@node, @agent, @sampled, @sequence, @cpu, @mem_total, @mem_used, @mem_percent,
                @load_1, @load_5, @load_15)
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("node", nodeId);
        insert.Parameters.AddWithValue("agent", agentInstanceId);
        insert.Parameters.AddWithValue("sampled", sampledAtUtc);
        insert.Parameters.AddWithValue("sequence", reportSequence);
        insert.Parameters.AddWithValue("cpu", (object?)cpuPercent ?? DBNull.Value);
        insert.Parameters.AddWithValue("mem_total", (object?)memory?.Total ?? DBNull.Value);
        insert.Parameters.AddWithValue("mem_used", (object?)memory?.Used ?? DBNull.Value);
        insert.Parameters.AddWithValue("mem_percent", (object?)memory?.Percent ?? DBNull.Value);
        insert.Parameters.AddWithValue("load_1", (object?)load?.L1 ?? DBNull.Value);
        insert.Parameters.AddWithValue("load_5", (object?)load?.L5 ?? DBNull.Value);
        insert.Parameters.AddWithValue("load_15", (object?)load?.L15 ?? DBNull.Value);
        var sampleId = (long)(await insert.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Machine telemetry insert returned no row."));
        foreach (var mount in disk)
        {
            await using var diskInsert = new NpgsqlCommand("""
                INSERT INTO ops.machine_disk_sample(machine_sample_id, mount, total_bytes, used_bytes, used_percent)
                VALUES (@sample, @mount, @total, @used, @percent)
                """, connection, transaction);
            diskInsert.Parameters.AddWithValue("sample", sampleId);
            diskInsert.Parameters.AddWithValue("mount", mount.Mount);
            diskInsert.Parameters.AddWithValue("total", mount.TotalBytes);
            diskInsert.Parameters.AddWithValue("used", mount.UsedBytes);
            diskInsert.Parameters.AddWithValue("percent", mount.UsedPercent);
            await diskInsert.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var proc in process)
        {
            await using var processInsert = new NpgsqlCommand("""
                INSERT INTO ops.process_sample(machine_sample_id, process_instance_id, role,
                    cpu_percent, memory_used_bytes, uptime_seconds)
                VALUES (@sample, @instance, @role, @cpu, @memory, @uptime)
                """, connection, transaction);
            processInsert.Parameters.AddWithValue("sample", sampleId);
            processInsert.Parameters.AddWithValue("instance", proc.ProcessInstanceId);
            processInsert.Parameters.AddWithValue("role", proc.Role);
            processInsert.Parameters.AddWithValue("cpu", proc.CpuPercent);
            processInsert.Parameters.AddWithValue("memory", proc.MemoryUsedBytes);
            processInsert.Parameters.AddWithValue("uptime", proc.UptimeSeconds);
            await processInsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The face's own counters (FIX-OBS-10): each accepted report appends one row
    /// per family. The reporter's identity was checked against its certificate by the gRPC
    /// service (the self-report trust, no agent row involved), so the store lands the rows
    /// as claimed — the same division ReportMachineTelemetry uses, minus the agent check a
    /// face has no agent for.</summary>
    public async Task<bool> ReportAppCountersAsync(
        string nodeId, string processInstanceId, DateTime reportedAtUtc,
        IReadOnlyList<AppCounterEntry> counters, CancellationToken cancellationToken = default)
    {
        if (counters.Count == 0) throw new ArgumentException("A counter report names no family.", nameof(counters));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var counter in counters)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO ops.app_counter_sample(node_id, process_instance_id, family,
                    value, reported_at)
                VALUES (@node, @instance, @family, @value, @reported)
                """, connection, transaction);
            insert.Parameters.AddWithValue("node", nodeId);
            insert.Parameters.AddWithValue("instance", processInstanceId);
            insert.Parameters.AddWithValue("family", counter.Family);
            insert.Parameters.AddWithValue("value", counter.Value);
            insert.Parameters.AddWithValue("reported", reportedAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The live game-host directory feed (FIX-SCHED-01): game-server processes with
    /// a reported endpoint, alive (Running, or Draining — a drained host keeps serving its
    /// existing rooms, so the control plane keeps its address; only Exited is gone) and
    /// observed within the freshness window. This is the valid process report the
    /// ClusterDirectory is maintained from — staleness, not a config file, decides who
    /// stays reachable. Admission and readiness ride along (FIX-SCHED-02): the directory
    /// keeps every reachable host and its scheduler filters on these, so a drained or full
    /// host stops taking NEW rooms without becoming unreachable for the ones it holds.</summary>
    public async Task<IReadOnlyList<GameHostReport>> ListGameHostReportsAsync(
        int freshnessSeconds, CancellationToken cancellationToken = default)
    {
        if (freshnessSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(freshnessSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT p.process_instance_id,p.endpoint,p.observed_at,p.readiness,p.admission
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            WHERE d.role='game-server'
              AND p.endpoint IS NOT NULL
              AND p.lifecycle IN ('Running','Draining')
              AND p.observed_at>=clock_timestamp()-@fresh*interval '1 second'
            ORDER BY p.process_instance_id
            """, connection);
        command.Parameters.AddWithValue("fresh", freshnessSeconds);
        var reports = new List<GameHostReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reports.Add(new GameHostReport(reader.GetString(0), reader.GetString(1),
                reader.GetDateTime(2), reader.GetString(3), reader.GetString(4)));
        return reports;
    }
}
