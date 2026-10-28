using Npgsql;

namespace Chorus.ControlServer;

public sealed record NodeStatus(string AgentInstanceId, long ReportSequence);
public sealed record ProcessStatus(bool Accepted, string Readiness, string EntityRecovery);
public sealed record OwnerStatus(string OwnerInstanceId, long Epoch, DateTime LeaseUntilUtc, string Status);
public sealed record RoomRoute(string RoomId, string TenantId, string GameId, string OwnerInstanceId,
    long Epoch, string? Endpoint, string Status);
public sealed record StreamerRoute(string StreamerId, string BindingId, string RoomId, string State,
    string? OwnerInstanceId, long Epoch);
public sealed record InstanceRoute(string ProcessInstanceId, string Role, string NodeId, string SlotId,
    string? Endpoint, string Readiness, string Admission);
public sealed record RoutingSnapshot(long Version, IReadOnlyList<RoomRoute> Rooms,
    IReadOnlyList<StreamerRoute> Streamers, IReadOnlyList<InstanceRoute> Instances);
public sealed record DeploymentPlan(string SlotId, string NodeId, string Role, string DesiredState,
    string DesiredVersion, string PackageHash, long PlanGeneration, string? CommandId,
    DateTime? CommandExpiresAtUtc);

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

    /// <summary>
    /// Extends the lease of every Active ownership whose owner still reports live (architecture
    /// 4.2: the registrar renews on the owner's behalf while the owner is observable). An owner
    /// whose process row went stale is not renewed — its lease runs out and the entity becomes
    /// takeable. A legacy owner without any process row is never renewed: it keeps its own
    /// renewal discipline.
    /// </summary>
    public async Task<int> RenewLiveOwnershipsAsync(int leaseSeconds, int gateSeconds,
        CancellationToken cancellationToken = default)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        if (gateSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(gateSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.ownership AS ownership
            SET lease_until=clock_timestamp()+@lease*interval '1 second', version=ownership.version+1
            FROM control.process AS process
            WHERE ownership.status='Active'
              AND process.process_instance_id=ownership.owner_process_instance_id
              AND process.lifecycle='Running'
              AND process.observed_at>=clock_timestamp()-@gate*interval '1 second'
            """, connection);
        command.Parameters.AddWithValue("lease", leaseSeconds);
        command.Parameters.AddWithValue("gate", gateSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Control-plane handover of a single-domain ownership (architecture 10.3, the streamer
    /// form): bumps the epoch, moves the holder and parks the row Recovering in one transaction.
    /// Until the new holder's own claim confirms the row (same holder with a live lease keeps
    /// the epoch and returns it Active), the renewal registrar leaves it alone — a handover
    /// nobody completes expires and becomes takeable again. The old holder's data-plane writes
    /// for a streamer entity are fenced by the session domain, not by this row: rotation already
    /// replaced the session it might speak for. The <paramref name="fromInstance"/> guard makes
    /// a stale command visible instead of moving an entity that changed hands meanwhile.
    /// </summary>
    public async Task<OwnerStatus> TransferOwnershipAsync(string tenantId, string gameId, string kind,
        string entityId, string fromInstance, string toInstance, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT owner_process_instance_id,status FROM control.ownership
            WHERE tenant_id=@tenant AND game_id=@game AND kind=@kind AND entity_id=@entity
            FOR UPDATE
            """, connection, transaction))
        {
            BindOwner(lockCommand, tenantId, gameId, kind, entityId);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The ownership vanished mid-transfer.");
            var holder = reader.GetString(0);
            var status = reader.GetString(1);
            if (!StringComparer.Ordinal.Equals(holder, fromInstance))
                throw new InvalidOperationException($"The entity is held by {holder}, not {fromInstance}.");
            if (!StringComparer.Ordinal.Equals(status, "Active"))
                throw new InvalidOperationException("The ownership is not Active; it cannot transfer.");
            await reader.DisposeAsync();
        }
        await using (var command = new NpgsqlCommand("""
            UPDATE control.ownership
            SET owner_process_instance_id=@to,epoch=epoch+1,
                lease_until=clock_timestamp()+@lease*interval '1 second',
                status='Recovering',version=version+1
            WHERE tenant_id=@tenant AND game_id=@game AND kind=@kind AND entity_id=@entity
            RETURNING owner_process_instance_id,epoch,lease_until,status
            """, connection, transaction))
        {
            BindOwner(command, tenantId, gameId, kind, entityId);
            command.Parameters.AddWithValue("to", toInstance);
            command.Parameters.AddWithValue("lease", leaseSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The ownership vanished mid-transfer.");
            var takeover = new OwnerStatus(reader.GetString(0), reader.GetInt64(1),
                reader.GetDateTime(2), reader.GetString(3));
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return takeover;
        }
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
            SELECT s.streamer_id,b.binding_id,b.current_room_id,b.state,
                o.owner_process_instance_id,COALESCE(o.epoch,0)
            FROM live.binding b
            JOIN live.session s ON s.live_session_id=b.live_session_id
            LEFT JOIN control.ownership o ON o.kind='streamer' AND o.tenant_id=b.tenant_id
                AND o.game_id=b.game_id AND o.entity_id=s.streamer_id
            WHERE b.state IN ('Starting','Ready','Running','AwaitingPlatformClose')
            ORDER BY s.streamer_id,b.binding_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                streamers.Add(new StreamerRoute(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
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
            .Concat(streamers.Select(s =>
                $"streamer|{s.StreamerId}|{s.BindingId}|{s.RoomId}|{s.State}|{s.OwnerInstanceId ?? ""}|{s.Epoch}"))
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

    /// <summary>
    /// Applies a deployment plan (architecture 10.2). The plan generation is monotonic: an
    /// older plan never overwrites a newer one, so a re-delivered or reordered command is a
    /// visible no-op for the store, and the agent never sees the plan history rewind.
    /// </summary>
    public async Task<DeploymentPlan> ApplyPlanAsync(string nodeId, string slotId, string role,
        string desiredState, string desiredVersion, string packageHash, long planGeneration,
        string? commandId, DateTime? commandExpiresAtUtc, CancellationToken cancellationToken = default)
    {
        if (planGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(planGeneration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,
                desired_state,package_hash,command_id,command_expires_at)
            VALUES (@slot,@node,@role,@version,@generation,@state,@package,@command,@expires)
            ON CONFLICT(slot_id) DO UPDATE
            SET node_id=excluded.node_id,role=excluded.role,desired_version=excluded.desired_version,
                plan_generation=excluded.plan_generation,desired_state=excluded.desired_state,
                package_hash=excluded.package_hash,command_id=excluded.command_id,
                command_expires_at=excluded.command_expires_at,updated_at=clock_timestamp()
            WHERE excluded.plan_generation>=control.deployment.plan_generation
            RETURNING slot_id,node_id,role,desired_state,desired_version,package_hash,
                plan_generation,command_id,command_expires_at
            """, connection);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("version", desiredVersion);
        command.Parameters.AddWithValue("generation", planGeneration);
        command.Parameters.AddWithValue("state", desiredState);
        command.Parameters.AddWithValue("package", packageHash);
        if (commandId is null) command.Parameters.AddWithValue("command", DBNull.Value);
        else command.Parameters.AddWithValue("command", commandId);
        if (commandExpiresAtUtc is null) command.Parameters.AddWithValue("expires", DBNull.Value);
        else command.Parameters.AddWithValue("expires", commandExpiresAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // The generation guard refused the write: return the plan that won.
            await reader.DisposeAsync();
            await using var current = new NpgsqlCommand("""
                SELECT slot_id,node_id,role,desired_state,desired_version,package_hash,
                    plan_generation,command_id,command_expires_at
                FROM control.deployment WHERE slot_id=@slot
                """, connection);
            current.Parameters.AddWithValue("slot", slotId);
            await using var currentReader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await currentReader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The deployment plan disappeared mid-apply.");
            return ReadPlan(currentReader);
        }
        return ReadPlan(reader);
    }

    /// <summary>The plans of one node — the WatchPlans face. Unapplied bootstrap rows surface
    /// with their seed values ('bootstrap' version, generation 1).</summary>
    public async Task<IReadOnlyList<DeploymentPlan>> ListPlansAsync(string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var plans = new List<DeploymentPlan>();
        await using (var command = new NpgsqlCommand("""
            SELECT slot_id,node_id,role,desired_state,desired_version,package_hash,
                plan_generation,command_id,command_expires_at
            FROM control.deployment WHERE node_id=@node ORDER BY slot_id
            """, connection))
        {
            command.Parameters.AddWithValue("node", nodeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                plans.Add(ReadPlan(reader));
        }
        return plans;
    }

    private static DeploymentPlan ReadPlan(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDateTime(8));
}
