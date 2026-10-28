using Npgsql;

namespace Chorus.ControlServer;

public sealed record QueueGateState(string PartitionKey, bool Suspended, DateTime UpdatedAtUtc, string UpdatedBy);
public sealed record AdminBindingSnapshot(string BindingId, string GameId, long Generation, string State,
    string? CurrentRoomId, DateTime CreatedAtUtc);
public sealed record AdminTicketSnapshot(string TicketId, string State, string PartitionKey,
    string? ReservationId, long Version);
public sealed record AdminTaskSnapshot(string TaskId, string OperationId, string Status, int AttemptCount,
    long TaskEpoch);
public sealed record AdminStreamerSnapshot(bool Found, string StreamerId, string BanState,
    string? CurrentSessionId, long SessionVersion, IReadOnlyList<AdminBindingSnapshot> Bindings,
    AdminTicketSnapshot? Ticket, OwnerStatus? Ownership, IReadOnlyList<AdminTaskSnapshot> Tasks);
public sealed record AdminParticipantSnapshot(string ParticipantId, string? BindingId, string Role, string State);
public sealed record AdminRoomSnapshot(bool Found, string TenantId, string GameId, string Status, long Epoch,
    long LastTick, long StateVersion, OwnerStatus? Ownership, IReadOnlyList<AdminParticipantSnapshot> Participants,
    long ResultVersion, long FinalFrame, int GrantsTotal, int GrantsGranted);
public sealed record AdminTraceEvent(long Sequence, string EventId, string Disposition, string? BindingId,
    DateTime ReceivedAtUtc, string PayloadJson);
public sealed record AdminTraceFrame(long Tick, string CommitId, string InputRefsJson, string EffectsJson);
public sealed record AdminTraceEffect(string EffectId, string Kind, string Status, string PayloadJson);
public sealed record AdminTraceGrant(string PlayerId, string GrantKey, string Status, string OperationId);
public sealed record AdminTraceLedger(string OperationId, string LineId, string PlayerId, string AssetId,
    long Delta, string Reason, long ResultingBalance, DateTime CommittedAtUtc);
public sealed record AdminRoundTrace(bool Found, string Status, long Epoch, long LastTick,
    IReadOnlyList<AdminTraceEvent> Events, IReadOnlyList<AdminTraceFrame> Frames,
    IReadOnlyList<AdminTraceEffect> Effects, long ResultVersion, string ResultJson,
    IReadOnlyList<AdminTraceGrant> Grants, IReadOnlyList<AdminTraceLedger> Ledger);

/// <summary>
/// Admin reads and controlled failure commands (operations 6). Everything here is join-only:
/// the snapshots and the AT31 trace assemble the room's or the streamer's whole footprint from
/// the authoritative tables, and every command lands exactly once in control.audit — the
/// unique (request_id, command) index turns a retried command into a visible duplicate.
/// </summary>
public sealed class RedesignAdminStore(string connectionString)
{
    /// <summary>Writes one audit row; false means the (request id, command) pair already acted.</summary>
    public async Task<bool> WriteAuditAsync(string requestId, string operation, string reason, string scope,
        string command, string detailJson, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO control.audit(request_id,operator,reason,scope,command,detail)
                VALUES (@request,@operator,@reason,@scope,@command,@detail::jsonb)
                """, connection);
            insert.Parameters.AddWithValue("request", requestId);
            insert.Parameters.AddWithValue("operator", operation);
            insert.Parameters.AddWithValue("reason", reason);
            insert.Parameters.AddWithValue("scope", scope);
            insert.Parameters.AddWithValue("command", command);
            insert.Parameters.AddWithValue("detail", detailJson);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException error) when (error.SqlState == "23505")
        {
            return false;
        }
    }

    public async Task<QueueGateState> SetQueueGateAsync(string partitionKey, bool suspended, string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.queue_gate(partition_key,suspended,updated_at,updated_by)
            VALUES (@partition,@suspended,clock_timestamp(),@by)
            ON CONFLICT(partition_key) DO UPDATE
            SET suspended=excluded.suspended, updated_at=clock_timestamp(), updated_by=excluded.updated_by
            RETURNING partition_key,suspended,updated_at,updated_by
            """, connection);
        command.Parameters.AddWithValue("partition", partitionKey);
        command.Parameters.AddWithValue("suspended", suspended);
        command.Parameters.AddWithValue("by", updatedBy);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The queue gate upsert returned no row.");
        return new QueueGateState(reader.GetString(0), reader.GetBoolean(1),
            reader.GetDateTime(2), reader.GetString(3));
    }

    /// <summary>The partitions whose pairing is parked — the coordinator consults this each cycle.</summary>
    public async Task<IReadOnlySet<string>> ListSuspendedPartitionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT partition_key FROM control.queue_gate WHERE suspended", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var partitions = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            partitions.Add(reader.GetString(0));
        return partitions;
    }

    /// <summary>The gate as it stands — a duplicated command replays the current state, not its own.</summary>
    public async Task<QueueGateState?> GetQueueGateAsync(string partitionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT partition_key,suspended,updated_at,updated_by FROM control.queue_gate WHERE partition_key=@partition",
            connection);
        command.Parameters.AddWithValue("partition", partitionKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new QueueGateState(reader.GetString(0), reader.GetBoolean(1),
            reader.GetDateTime(2), reader.GetString(3));
    }

    public async Task<string?> ReadProcessLifecycleAsync(string processInstanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT lifecycle FROM control.process WHERE process_instance_id=@instance", connection);
        command.Parameters.AddWithValue("instance", processInstanceId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<AdminStreamerSnapshot> GetStreamerSnapshotAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string banState = "", currentSession = "";
        long sessionVersion = 0;
        var found = false;
        await using (var state = new NpgsqlCommand("""
            SELECT ban_state,COALESCE(current_session_id,''),current_session_version
            FROM auth.streamer_state WHERE tenant_id=@tenant AND streamer_id=@streamer
            """, connection))
        {
            state.Parameters.AddWithValue("tenant", tenantId);
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                found = true;
                banState = reader.GetString(0);
                currentSession = reader.GetString(1);
                sessionVersion = reader.GetInt64(2);
            }
        }
        if (!found) return new AdminStreamerSnapshot(false, streamerId, "", null, 0, [], null, null, []);

        var bindings = new List<AdminBindingSnapshot>();
        await using (var query = new NpgsqlCommand("""
            SELECT b.binding_id,b.game_id,b.generation,b.state,b.current_room_id,b.created_at
            FROM live.binding b JOIN live.session s ON s.live_session_id=b.live_session_id
            WHERE s.tenant_id=@tenant AND s.streamer_id=@streamer
            ORDER BY b.created_at DESC LIMIT 20
            """, connection))
        {
            query.Parameters.AddWithValue("tenant", tenantId);
            query.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                bindings.Add(new AdminBindingSnapshot(reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDateTime(5)));
        }

        AdminTicketSnapshot? ticket = null;
        await using (var query = new NpgsqlCommand("""
            SELECT t.ticket_id,t.state,t.partition_key,t.reservation_id,t.version
            FROM match.ticket t
            JOIN live.binding b ON b.binding_id=t.binding_id
            JOIN live.session s ON s.live_session_id=b.live_session_id
            WHERE s.tenant_id=@tenant AND s.streamer_id=@streamer
            ORDER BY t.created_at DESC LIMIT 1
            """, connection))
        {
            query.Parameters.AddWithValue("tenant", tenantId);
            query.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                ticket = new AdminTicketSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4));
        }

        OwnerStatus? ownership = null;
        await using (var query = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,lease_until,status FROM control.ownership
            WHERE tenant_id=@tenant AND kind='streamer' AND entity_id=@streamer
            ORDER BY version DESC LIMIT 1
            """, connection))
        {
            query.Parameters.AddWithValue("tenant", tenantId);
            query.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                ownership = new OwnerStatus(reader.GetString(0), reader.GetInt64(1),
                    reader.GetDateTime(2), reader.GetString(3));
        }

        var tasks = new List<AdminTaskSnapshot>();
        if (bindings.Count > 0)
        {
            var ids = bindings.Select(binding => binding.BindingId).ToArray();
            await using var query = new NpgsqlCommand("""
                SELECT task_id,operation_id,status,attempt_count,task_epoch FROM live.task
                WHERE binding_id = ANY(@bindings) ORDER BY task_id LIMIT 20
                """, connection);
            query.Parameters.AddWithValue("bindings", ids);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tasks.Add(new AdminTaskSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt64(4)));
        }
        return new AdminStreamerSnapshot(true, streamerId, banState,
            currentSession.Length == 0 ? null : currentSession, sessionVersion, bindings, ticket, ownership, tasks);
    }

    public async Task<AdminRoomSnapshot> GetRoomSnapshotAsync(string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string tenantId = "", gameId = "", status = "";
        long epoch = 0, lastTick = 0, stateVersion = 0;
        var found = false;
        await using (var room = new NpgsqlCommand(
            "SELECT tenant_id,game_id,status,epoch,last_tick,state_version FROM game.room WHERE room_id=@room",
            connection))
        {
            room.Parameters.AddWithValue("room", roomId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                found = true;
                tenantId = reader.GetString(0);
                gameId = reader.GetString(1);
                status = reader.GetString(2);
                epoch = reader.GetInt64(3);
                lastTick = reader.GetInt64(4);
                stateVersion = reader.GetInt64(5);
            }
        }
        if (!found) return new AdminRoomSnapshot(false, "", "", "", 0, 0, 0, null, [], 0, 0, 0, 0);

        OwnerStatus? ownership = null;
        await using (var query = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,lease_until,status FROM control.ownership
            WHERE kind='room' AND entity_id=@room ORDER BY version DESC LIMIT 1
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                ownership = new OwnerStatus(reader.GetString(0), reader.GetInt64(1),
                    reader.GetDateTime(2), reader.GetString(3));
        }

        var participants = new List<AdminParticipantSnapshot>();
        await using (var query = new NpgsqlCommand("""
            SELECT participant_id,binding_id,role,state FROM game.participation
            WHERE room_id=@room ORDER BY participant_id
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                participants.Add(new AdminParticipantSnapshot(reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        long resultVersion = 0, finalFrame = 0;
        var grantsTotal = 0;
        var grantsGranted = 0;
        await using (var query = new NpgsqlCommand("""
            SELECT r.result_version,r.final_frame,
                count(g.grant_key),
                count(*) FILTER (WHERE g.status='Granted')
            FROM game.result r LEFT JOIN settlement.grant g
                ON g.room_id=r.room_id AND g.result_version=r.result_version
            WHERE r.room_id=@room GROUP BY r.result_version,r.final_frame
            ORDER BY r.result_version DESC LIMIT 1
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                resultVersion = reader.GetInt64(0);
                finalFrame = reader.GetInt64(1);
                grantsTotal = Convert.ToInt32(reader.GetInt64(2));
                grantsGranted = Convert.ToInt32(reader.GetInt64(3));
            }
        }
        return new AdminRoomSnapshot(true, tenantId, gameId, status, epoch, lastTick, stateVersion, ownership,
            participants, resultVersion, finalFrame, grantsTotal, grantsGranted);
    }

    /// <summary>
    /// The AT31 chain for one room: routed events in routing order, the frames that consumed
    /// them with the effects each durably announced, the committed result, its grant manifest
    /// and the ledger lines the grants produced.
    /// </summary>
    public async Task<AdminRoundTrace> TraceRoundAsync(string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string status = "";
        long epoch = 0, lastTick = 0;
        var found = false;
        await using (var room = new NpgsqlCommand(
            "SELECT status,epoch,last_tick FROM game.room WHERE room_id=@room", connection))
        {
            room.Parameters.AddWithValue("room", roomId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                found = true;
                status = reader.GetString(0);
                epoch = reader.GetInt64(1);
                lastTick = reader.GetInt64(2);
            }
        }
        if (!found) return new AdminRoundTrace(false, "", 0, 0, [], [], [], 0, "", [], []);

        var events = new List<AdminTraceEvent>();
        await using (var query = new NpgsqlCommand("""
            SELECT d.sequence,e.event_id,e.disposition,e.binding_id,e.received_at,e.payload::text
            FROM input.delivery d JOIN input.event e ON e.event_id=d.event_id
            WHERE d.room_id=@room ORDER BY d.consumer_scope,d.sequence
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                events.Add(new AdminTraceEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4), reader.GetString(5)));
        }

        var frames = new List<AdminTraceFrame>();
        await using (var query = new NpgsqlCommand("""
            SELECT tick_id,commit_id,input_refs::text,effects::text FROM game.frame
            WHERE room_id=@room ORDER BY tick_id
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                frames.Add(new AdminTraceFrame(reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3)));
        }

        var effects = new List<AdminTraceEffect>();
        await using (var query = new NpgsqlCommand("""
            SELECT effect_id,kind,status,payload::text FROM infra.outbox
            WHERE starts_with(effect_id, @room || '/') ORDER BY effect_id
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                effects.Add(new AdminTraceEffect(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3)));
        }

        long resultVersion = 0;
        var resultJson = "";
        await using (var query = new NpgsqlCommand(
            "SELECT result_version,result::text FROM game.result WHERE room_id=@room ORDER BY result_version DESC LIMIT 1",
            connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                resultVersion = reader.GetInt64(0);
                resultJson = reader.GetString(1);
            }
        }

        var grants = new List<AdminTraceGrant>();
        await using (var query = new NpgsqlCommand("""
            SELECT player_id,grant_key,status,operation_id FROM settlement.grant
            WHERE room_id=@room ORDER BY player_id,grant_key
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                grants.Add(new AdminTraceGrant(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3)));
        }

        var ledger = new List<AdminTraceLedger>();
        await using (var query = new NpgsqlCommand("""
            SELECT l.operation_id,l.line_id,l.player_id,l.asset_id,l.delta,l.reason,l.resulting_balance,
                l.committed_at
            FROM player.ledger l
            WHERE l.operation_id IN (SELECT operation_id FROM settlement.grant WHERE room_id=@room)
            ORDER BY l.committed_at,l.line_id
            """, connection))
        {
            query.Parameters.AddWithValue("room", roomId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ledger.Add(new AdminTraceLedger(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6),
                    reader.GetDateTime(7)));
        }
        return new AdminRoundTrace(true, status, epoch, lastTick, events, frames, effects, resultVersion,
            resultJson, grants, ledger);
    }

    /// <summary>Parks a process as Draining: visible in the process table and snapshots; the
    /// scheduler treats it as a graceful-stop candidate. Returns null when the process is unknown.</summary>
    public async Task<string?> DrainProcessAsync(string processInstanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.process SET lifecycle='Draining', observed_at=clock_timestamp()
            WHERE process_instance_id=@instance RETURNING lifecycle
            """, connection);
        command.Parameters.AddWithValue("instance", processInstanceId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
