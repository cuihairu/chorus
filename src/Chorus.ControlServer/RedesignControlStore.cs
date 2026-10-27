using Npgsql;

namespace Chorus.ControlServer;

public sealed record NodeStatus(string AgentInstanceId, long ReportSequence);
public sealed record ProcessStatus(bool Accepted, string Readiness, string EntityRecovery);
public sealed record OwnerStatus(string OwnerInstanceId, long Epoch, DateTime LeaseUntilUtc, string Status);

/// <summary>M0 control writes. Database time and row locks decide ownership and report order.</summary>
public sealed class RedesignControlStore(string connectionString)
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
        string entityRecovery, DateTime startedAtUtc, CancellationToken cancellationToken = default)
    {
        if (reportSequence <= 0) throw new ArgumentOutOfRangeException(nameof(reportSequence));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var node = new NpgsqlCommand("""
            SELECT agent_instance_id FROM control.node WHERE node_id=@node FOR UPDATE
            """, connection, transaction))
        {
            node.Parameters.AddWithValue("node", nodeId);
            if (await node.ExecuteScalarAsync(cancellationToken) is not string current || current != agentInstanceId)
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

        await using var command = new NpgsqlCommand("""
            INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,report_sequence,
                lifecycle,readiness,health,admission,entity_recovery,started_at)
            VALUES (@instance,@slot,@node,@agent,@sequence,@lifecycle,@readiness,@health,@admission,@recovery,@started)
            ON CONFLICT(process_instance_id) DO UPDATE
            SET report_sequence=excluded.report_sequence,lifecycle=excluded.lifecycle,
                readiness=excluded.readiness,health=excluded.health,admission=excluded.admission,
                entity_recovery=excluded.entity_recovery,observed_at=clock_timestamp()
            WHERE control.process.agent_instance_id=excluded.agent_instance_id
              AND control.process.report_sequence<excluded.report_sequence
              AND control.process.started_at=excluded.started_at
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accepted = await reader.ReadAsync(cancellationToken);
        var result = accepted
            ? new ProcessStatus(true, reader.GetString(0), reader.GetString(1))
            : new ProcessStatus(false, "NotReady", "StaleReport");
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<OwnerStatus> ClaimOwnershipAsync(
        string tenantId, string gameId, string kind, string entityId,
        string ownerInstanceId, int leaseSeconds, CancellationToken cancellationToken = default)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.ownership(kind,entity_id,tenant_id,game_id,owner_process_instance_id,
                epoch,lease_until,status,version)
            VALUES (@kind,@entity,@tenant,@game,@owner,1,
                clock_timestamp()+@lease*interval '1 second','Active',1)
            ON CONFLICT(kind,entity_id) DO UPDATE
            SET owner_process_instance_id=excluded.owner_process_instance_id,
                epoch=CASE WHEN control.ownership.owner_process_instance_id=excluded.owner_process_instance_id
                    AND control.ownership.lease_until>clock_timestamp()
                    THEN control.ownership.epoch ELSE control.ownership.epoch+1 END,
                lease_until=excluded.lease_until,status='Active',version=control.ownership.version+1
            WHERE control.ownership.tenant_id=excluded.tenant_id
              AND control.ownership.game_id=excluded.game_id
            RETURNING owner_process_instance_id,epoch,lease_until,status
            """, connection);
        BindOwner(command, tenantId, gameId, kind, entityId);
        command.Parameters.AddWithValue("owner", ownerInstanceId);
        command.Parameters.AddWithValue("lease", leaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Ownership scope conflicts with an existing entity.");
        return new OwnerStatus(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2), reader.GetString(3));
    }

    public async Task<OwnerStatus?> GetOwnershipAsync(string tenantId, string gameId, string kind,
        string entityId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,lease_until,status
            FROM control.ownership WHERE tenant_id=@tenant AND game_id=@game AND kind=@kind AND entity_id=@entity
            """, connection);
        BindOwner(command, tenantId, gameId, kind, entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OwnerStatus(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2), reader.GetString(3)) : null;
    }

    private static void BindOwner(NpgsqlCommand command, string tenantId, string gameId, string kind, string entityId)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("entity", entityId);
    }
}
