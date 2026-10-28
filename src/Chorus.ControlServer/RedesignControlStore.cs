using Npgsql;

namespace Chorus.ControlServer;

public sealed record NodeStatus(string AgentInstanceId, long ReportSequence);
public sealed record ProcessStatus(bool Accepted, string Readiness, string EntityRecovery);
public sealed record OwnerStatus(string OwnerInstanceId, long Epoch, DateTime LeaseUntilUtc, string Status);
public sealed record RoomRoute(string RoomId, string TenantId, string GameId, string OwnerInstanceId,
    long Epoch, string? Endpoint, string Status);
public sealed record StreamerRoute(string StreamerId, string BindingId, string RoomId, string State);
public sealed record InstanceRoute(string ProcessInstanceId, string Role, string NodeId, string SlotId,
    string? Endpoint, string Readiness, string Admission);
public sealed record RoutingSnapshot(long Version, IReadOnlyList<RoomRoute> Rooms,
    IReadOnlyList<StreamerRoute> Streamers, IReadOnlyList<InstanceRoute> Instances);

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

        await using var command = new NpgsqlCommand("""
            INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,report_sequence,
                lifecycle,readiness,health,admission,entity_recovery,started_at,endpoint)
            VALUES (@instance,@slot,@node,@agent,@sequence,@lifecycle,@readiness,@health,@admission,@recovery,
                @started,@endpoint)
            ON CONFLICT(process_instance_id) DO UPDATE
            SET report_sequence=excluded.report_sequence,lifecycle=excluded.lifecycle,
                readiness=excluded.readiness,health=excluded.health,admission=excluded.admission,
                entity_recovery=excluded.entity_recovery,endpoint=excluded.endpoint,observed_at=clock_timestamp()
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

    /// <summary>
    /// Reads the versioned full routing snapshot (architecture 4.4): rooms with owner epochs and
    /// endpoints, live streamer bindings with their rooms, and every registered instance. The
    /// version is a 64-bit content hash — a deliberate deviation from a monotonic sequence: any
    /// mutation of the routed content produces a different version, so a consumer that missed
    /// Watch notices detects any gap by comparing versions and re-pulls instead of trusting a
    /// stale snapshot. Routing carries endpoints, instances and epochs only — never secrets.
    /// </summary>
    public async Task<RoutingSnapshot> ReadRoutingSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var rooms = new List<RoomRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT o.entity_id,o.tenant_id,o.game_id,o.owner_process_instance_id,o.epoch,o.status,p.endpoint
            FROM control.ownership o
            LEFT JOIN control.process p ON p.process_instance_id=o.owner_process_instance_id
            WHERE o.kind='room'
            ORDER BY o.tenant_id,o.game_id,o.entity_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rooms.Add(new RoomRoute(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(5)));
        }

        var streamers = new List<StreamerRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT s.streamer_id,b.binding_id,b.current_room_id,b.state
            FROM live.binding b
            JOIN live.session s ON s.live_session_id=b.live_session_id
            WHERE b.state IN ('Starting','Ready','Running','AwaitingPlatformClose')
            ORDER BY s.streamer_id,b.binding_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                streamers.Add(new StreamerRoute(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(3)));
        }

        var instances = new List<InstanceRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.process_instance_id,d.role,p.node_id,p.slot_id,p.endpoint,p.readiness,p.admission
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            ORDER BY p.process_instance_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                instances.Add(new InstanceRoute(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.GetString(6)));
        }

        var version = ContentHash(rooms.Select(r =>
            $"room|{r.RoomId}|{r.TenantId}|{r.GameId}|{r.OwnerInstanceId}|{r.Epoch}|{r.Endpoint ?? ""}|{r.Status}")
            .Concat(streamers.Select(s => $"streamer|{s.StreamerId}|{s.BindingId}|{s.RoomId}|{s.State}"))
            .Concat(instances.Select(i =>
                $"instance|{i.ProcessInstanceId}|{i.Role}|{i.NodeId}|{i.SlotId}|{i.Endpoint ?? ""}|{i.Readiness}|{i.Admission}")));
        return new RoutingSnapshot(version, rooms, streamers, instances);
    }

    /// <summary>FNV-1a over the canonical snapshot lines; collisions are acceptable — the version
    /// only signals "content changed", it is not an identity.</summary>
    private static long ContentHash(IEnumerable<string> lines)
    {
        unchecked
        {
            const long prime = 1099511628211;
            var hash = -3750763034362895579L; // 14695981039346656037 as int64
            foreach (var line in lines)
            {
                foreach (var c in line)
                {
                    hash ^= c;
                    hash *= prime;
                }
                hash ^= '\n';
                hash *= prime;
            }
            return hash;
        }
    }
}
