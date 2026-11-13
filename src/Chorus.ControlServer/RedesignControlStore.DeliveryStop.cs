using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

/// <summary>The persisted stop-delivery command (FIX-END-05, architecture 6.4 step 3): the
/// room, the round generation the begin derived from the mounts, and the CloseRouteVersion
/// computed from the operation's own recorded facts at issue. The version is frozen at issue —
/// a repeated delivery of the recorded command always carries the same version, so a replay
/// can never close a newer round.</summary>
public sealed record StopDeliveryCommand(
    string TenantId, string GameId, string EndOperationId, string RoomId, long RoundGeneration,
    long CloseRouteVersion, DateTime IssuedAt);

/// <summary>One gateway in the stop-delivery responder set (FIX-END-05): the process instance,
/// the endpoint the command is pushed at (null when the gateway confirmed routing but reported
/// no reachable endpoint), and the per-responder state the later steps drive — an offline but
/// authorized gateway stays Pending until it answers. The step-4 answer (FIX-END-07) freezes
/// the responder's own facts: the cutoff watermark its push leg observed and the informational
/// drain counts of its confirmation pass.</summary>
public sealed record StopDeliveryResponder(
    string ProcessInstanceId, string? Endpoint, string State, DateTime? AnsweredAt,
    long CutoffSequence = 0, long DrainedOutOfRound = 0, long DrainedRouted = 0,
    bool HoldsAuthorization = false);

/// <summary>The step-4 ack's answer (FIX-END-07): whether this call recorded the responder's
/// first confirmation, and the version it recorded against — a repeat at the recorded version
/// answers Duplicate with the first answer's facts standing unchanged.</summary>
public sealed record DeliveryStopAck(bool Recorded, bool Duplicate, long RecordedCloseRouteVersion);

public sealed partial class RedesignControlStore
{
    /// <summary>
    /// Persists the stop-delivery command and its responder set (FIX-END-05, architecture 6.4
    /// step 3). The CloseRouteVersion is derived here from the operation's own recorded facts —
    /// never taken from a caller — and frozen with the row: a repeated issue replays the
    /// recorded command, so a late or racing delivery cannot close a newer round. The responder
    /// set is every gateway that held a confirmed routing version at issue: an offline gateway
    /// keeps its process and confirm rows, so it stays a pending responder — the command waits
    /// for it, it is never skipped.
    /// </summary>
    public async Task<(StopDeliveryCommand Command, bool Duplicate, List<StopDeliveryResponder> Responders)>
        IssueStopDeliveryAsync(string tenantId, string gameId, string endOperationId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var operation = await ReadEndOperationAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken)
            ?? throw new EndOperationConflictException(
                $"The end operation {endOperationId} does not exist.");
        if (operation.Phase == EndOperationPhases.Done)
            throw new EndOperationConflictException(
                $"The end operation {endOperationId} is Done; no stop delivery is issued.");

        // The replay path: a command row already stands — the recorded version is the command,
        // whatever the world looks like now.
        var existing = await ReadStopDeliveryAsync(connection, transaction, tenantId, gameId,
            endOperationId, cancellationToken);
        if (existing is not null)
        {
            var responders = await ListStopDeliveryRespondersAsync(
                connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (existing, true, responders);
        }

        var version = DeriveCloseRouteVersion(operation);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.stop_delivery(tenant_id,game_id,end_operation_id,room_id,
                round_generation,close_route_version)
            VALUES (@tenant,@game,@operation,@room,@generation,@version)
            ON CONFLICT (tenant_id,game_id,end_operation_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("operation", endOperationId);
            insert.Parameters.AddWithValue("room", operation.RoomId);
            insert.Parameters.AddWithValue("generation", operation.RoundGeneration);
            insert.Parameters.AddWithValue("version", version);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        // The responder set at issue: every platform-gateway process with a confirmed routing
        // version. No liveness filter — an offline gateway's confirm row is still its route
        // authorization, and the command must reach it whenever it comes back.
        await using (var responders = new NpgsqlCommand("""
            INSERT INTO control.stop_delivery_responder(tenant_id,game_id,end_operation_id,
                process_instance_id,endpoint)
            SELECT @tenant,@game,@operation,p.process_instance_id,p.endpoint
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
            WHERE d.role='platform-gateway'
            ON CONFLICT (tenant_id,game_id,end_operation_id,process_instance_id) DO NOTHING
            """, connection, transaction))
        {
            responders.Parameters.AddWithValue("tenant", tenantId);
            responders.Parameters.AddWithValue("game", gameId);
            responders.Parameters.AddWithValue("operation", endOperationId);
            await responders.ExecuteNonQueryAsync(cancellationToken);
        }

        var command = await ReadStopDeliveryAsync(connection, transaction, tenantId, gameId,
            endOperationId, cancellationToken)
            ?? throw new InvalidOperationException("The stop-delivery row vanished under issue.");
        var set = await ListStopDeliveryRespondersAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (command, false, set);
    }

    /// <summary>The recorded stop-delivery command, or null when none was issued yet.</summary>
    public async Task<StopDeliveryCommand?> GetStopDeliveryAsync(string tenantId, string gameId,
        string endOperationId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadStopDeliveryAsync(connection, null, tenantId, gameId, endOperationId,
            cancellationToken);
    }

    /// <summary>The command's responder set — the pending and answered gateways alike.</summary>
    public async Task<List<StopDeliveryResponder>> ListStopDeliveryRespondersAsync(
        string tenantId, string gameId, string endOperationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ListStopDeliveryRespondersAsync(connection, null, tenantId, gameId,
            endOperationId, cancellationToken);
    }

    /// <summary>
    /// The step-4 barrier the coordinator drives every pass (FIX-END-08, architecture 6.4
    /// step 4): the confirmation owed is every gateway holding route authorization NOW, not
    /// only the set the issue froze. One transaction grows the responder set to cover holders
    /// that authorized after the issue (a missed instance must not slip past the barrier),
    /// refreshes the endpoint of every still-pending row from the live process row (a
    /// restarted holder under the same instance id gets its current address), and returns the
    /// set with each responder's authorization currency: a holder counts while its process
    /// reports a live lifecycle within the stale window. A responder whose authorization
    /// lapsed — the process died, the supervisor reported its exit, or its reports simply
    /// stopped — owes nothing: the receiver-side fence (the standing end operation this
    /// method's caller drives) refuses its pushes regardless, so the exit is safe only
    /// because the fence stands, which is why this method refuses an operation that is not
    /// open. Rows are never rewritten to an exited state: a lapsed responder's Pending row
    /// is the honest evidence of the confirmation that never came, and a holder that comes
    /// back fresh re-enters the barrier and owes its answer again.
    /// </summary>
    public async Task<List<StopDeliveryResponder>> ResolveDeliveryStopBarrierAsync(
        string tenantId, string gameId, string endOperationId, int staleSeconds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var open = new NpgsqlCommand(
            "SELECT phase FROM control.end_operation" +
            " WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation",
            connection, transaction))
        {
            open.Parameters.AddWithValue("tenant", tenantId);
            open.Parameters.AddWithValue("game", gameId);
            open.Parameters.AddWithValue("operation", endOperationId);
            if (await open.ExecuteScalarAsync(cancellationToken) is not string phase
                || phase == EndOperationPhases.Done)
                throw new EndOperationConflictException(
                    $"The end operation {endOperationId} does not stand open for a delivery-stop barrier.");
        }
        // Holders that authorized after the issue join the standing barrier: the same
        // derivation the issue froze, inserted as pending responders so the re-push leg can
        // carry them the command and their ack is admissible.
        await using (var grow = new NpgsqlCommand("""
            INSERT INTO control.stop_delivery_responder(tenant_id,game_id,end_operation_id,
                process_instance_id,endpoint)
            SELECT @tenant,@game,@operation,p.process_instance_id,p.endpoint
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
            WHERE d.role='platform-gateway'
            ON CONFLICT (tenant_id,game_id,end_operation_id,process_instance_id) DO NOTHING
            """, connection, transaction))
        {
            grow.Parameters.AddWithValue("tenant", tenantId);
            grow.Parameters.AddWithValue("game", gameId);
            grow.Parameters.AddWithValue("operation", endOperationId);
            await grow.ExecuteNonQueryAsync(cancellationToken);
        }
        // A still-pending responder's endpoint follows its live process row — the address the
        // re-push uses, never the one the issue froze.
        await using (var refresh = new NpgsqlCommand("""
            UPDATE control.stop_delivery_responder r
            SET endpoint = p.endpoint
            FROM control.process p
            WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.end_operation_id=@operation
              AND r.process_instance_id=p.process_instance_id AND r.state='Pending'
              AND p.endpoint IS NOT NULL AND r.endpoint IS DISTINCT FROM p.endpoint
            """, connection, transaction))
        {
            refresh.Parameters.AddWithValue("tenant", tenantId);
            refresh.Parameters.AddWithValue("game", gameId);
            refresh.Parameters.AddWithValue("operation", endOperationId);
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }
        var set = await ListStopDeliveryRespondersAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
        await using (var currency = new NpgsqlCommand("""
            SELECT r.process_instance_id,
                (p.lifecycle IN ('Running','Draining')
                    AND p.observed_at >= clock_timestamp() - @stale * interval '1 second')
            FROM control.stop_delivery_responder r
            LEFT JOIN control.process p ON p.process_instance_id = r.process_instance_id
            WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.end_operation_id=@operation
            """, connection, transaction))
        {
            currency.Parameters.AddWithValue("tenant", tenantId);
            currency.Parameters.AddWithValue("game", gameId);
            currency.Parameters.AddWithValue("operation", endOperationId);
            currency.Parameters.AddWithValue("stale", staleSeconds);
            await using var reader = await currency.ExecuteReaderAsync(cancellationToken);
            var flags = new Dictionary<string, bool>();
            while (await reader.ReadAsync(cancellationToken))
                flags[reader.GetString(0)] = reader.GetBoolean(1);
            set = set.Select(responder => responder with
            {
                HoldsAuthorization = flags.GetValueOrDefault(responder.ProcessInstanceId)
            }).ToList();
        }
        await transaction.CommitAsync(cancellationToken);
        return set;
    }

    /// <summary>
    /// Advances StopDeliveryIssued to DeliveryStopped only when the barrier is complete
    /// (FIX-END-08): no gateway holding route authorization — a live process with a confirmed
    /// routing version — stands without its own Stopped confirmation. The completeness
    /// predicate lives in the advancing statement itself, so a holder whose confirm lands
    /// between the coordinator's snapshot and this update cannot slip past: the update's own
    /// snapshot sees it or the update commits before the confirm, and a holder that
    /// postdates the barrier provably never pushed for this round (its monitor answers no
    /// resolve until its confirm lands, and the receiver-side fence refuses the closed round
    /// from then on). An empty holder set is vacuously complete. False means the barrier
    /// parked — or another driver moved the phase first — and the next pass reads the row
    /// exactly as it stands.
    /// </summary>
    public async Task<bool> AdvanceDeliveryStoppedBarrierAsync(string tenantId, string gameId,
        string endOperationId, int staleSeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET phase='DeliveryStopped', last_error=NULL, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
              AND phase='StopDeliveryIssued'
              AND NOT EXISTS (
                SELECT 1 FROM control.process p
                JOIN control.deployment d ON d.slot_id=p.slot_id
                JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
                WHERE d.role='platform-gateway'
                  AND p.lifecycle IN ('Running','Draining')
                  AND p.observed_at >= clock_timestamp() - @stale * interval '1 second'
                  AND NOT EXISTS (
                    SELECT 1 FROM control.stop_delivery_responder r
                    WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.end_operation_id=@operation
                      AND r.process_instance_id=p.process_instance_id AND r.state='Stopped'))
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("stale", staleSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Records one responder's DeliveryStopped confirmation (FIX-END-07, architecture 6.4
    /// step 4). The ack validates against the recorded command — never the phase — so a
    /// restarted gateway re-acking after the ladder moved past DeliveryStopped still
    /// replays idempotently. First answer wins: only a Pending row accepts the update, so
    /// a repeat at the recorded version changes nothing and the first answer's watermark
    /// and counts stand — a restarted process's honest zeros cannot lower them. A wrong
    /// room or version, an instance outside the frozen responder set, or a command that
    /// never issued refuses without writing.
    /// </summary>
    public async Task<DeliveryStopAck> AcknowledgeStopDeliveryAsync(string tenantId, string gameId,
        string endOperationId, string roomId, string processInstanceId, long closeRouteVersion,
        long cutoffSequence, long drainedOutOfRound, long drainedRouted,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = await ReadStopDeliveryAsync(connection, null, tenantId, gameId,
            endOperationId, cancellationToken)
            ?? throw new EndOperationConflictException(
                $"The end operation {endOperationId} never issued a stop-delivery command.");
        if (command.RoomId != roomId)
            throw new EndOperationConflictException(
                $"The stop-delivery command for {endOperationId} closes the room {command.RoomId}, not {roomId}.");
        if (command.CloseRouteVersion != closeRouteVersion)
            throw new EndOperationConflictException(
                $"The stop-delivery command for {roomId} stands at version {command.CloseRouteVersion}; " +
                $"the ack names {closeRouteVersion}.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE control.stop_delivery_responder
            SET state='Stopped', answered_at=clock_timestamp(),
                cutoff_sequence=@cutoff, drained_out_of_round=@outOfRound, drained_routed=@routed
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
              AND process_instance_id=@instance AND state='Pending'
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("game", gameId);
            update.Parameters.AddWithValue("operation", endOperationId);
            update.Parameters.AddWithValue("instance", processInstanceId);
            update.Parameters.AddWithValue("cutoff", cutoffSequence);
            update.Parameters.AddWithValue("outOfRound", drainedOutOfRound);
            update.Parameters.AddWithValue("routed", drainedRouted);
            if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new DeliveryStopAck(true, false, command.CloseRouteVersion);
            }
        }
        // Zero rows: the responder either already answered at this version — the recorded
        // answer is the answer — or holds no row at all.
        var responder = (await ListStopDeliveryRespondersAsync(connection, transaction, tenantId, gameId,
                endOperationId, cancellationToken))
            .SingleOrDefault(row => row.ProcessInstanceId == processInstanceId);
        if (responder is null)
        {
            // FIX-END-08: a holder that authorized after the issue may hold no row yet — its
            // ack proves possession of the command (the room, the operation, the exact frozen
            // version), so the confirmation is counted, not refused. An instance without
            // route authorization remains a stranger.
            await using (var holder = new NpgsqlCommand("""
                SELECT p.endpoint FROM control.process p
                JOIN control.deployment d ON d.slot_id=p.slot_id
                JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
                WHERE d.role='platform-gateway' AND p.process_instance_id=@instance
                """, connection, transaction))
            {
                holder.Parameters.AddWithValue("instance", processInstanceId);
                var endpoint = await holder.ExecuteScalarAsync(cancellationToken) as string;
                if (endpoint is not null)
                {
                    await using (var insert = new NpgsqlCommand("""
                        INSERT INTO control.stop_delivery_responder(tenant_id,game_id,end_operation_id,
                            process_instance_id,endpoint)
                        VALUES (@tenant,@game,@operation,@instance,@endpoint)
                        ON CONFLICT (tenant_id,game_id,end_operation_id,process_instance_id) DO NOTHING
                        """, connection, transaction))
                    {
                        insert.Parameters.AddWithValue("tenant", tenantId);
                        insert.Parameters.AddWithValue("game", gameId);
                        insert.Parameters.AddWithValue("operation", endOperationId);
                        insert.Parameters.AddWithValue("instance", processInstanceId);
                        insert.Parameters.AddWithValue("endpoint", endpoint);
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using (var late = new NpgsqlCommand("""
                        UPDATE control.stop_delivery_responder
                        SET state='Stopped', answered_at=clock_timestamp(),
                            cutoff_sequence=@cutoff, drained_out_of_round=@outOfRound, drained_routed=@routed
                        WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
                          AND process_instance_id=@instance AND state='Pending'
                        """, connection, transaction))
                    {
                        late.Parameters.AddWithValue("tenant", tenantId);
                        late.Parameters.AddWithValue("game", gameId);
                        late.Parameters.AddWithValue("operation", endOperationId);
                        late.Parameters.AddWithValue("instance", processInstanceId);
                        late.Parameters.AddWithValue("cutoff", cutoffSequence);
                        late.Parameters.AddWithValue("outOfRound", drainedOutOfRound);
                        late.Parameters.AddWithValue("routed", drainedRouted);
                        if (await late.ExecuteNonQueryAsync(cancellationToken) > 0)
                        {
                            await transaction.CommitAsync(cancellationToken);
                            return new DeliveryStopAck(true, false, command.CloseRouteVersion);
                        }
                    }
                }
            }
            await transaction.CommitAsync(cancellationToken);
            throw new EndOperationConflictException(
                $"The process {processInstanceId} is not a stop-delivery responder for {endOperationId}.");
        }
        if (responder.State != "Stopped")
            throw new EndOperationConflictException(
                $"The responder {processInstanceId} stands at {responder.State}, which no ack records.");
        await transaction.CommitAsync(cancellationToken);
        return new DeliveryStopAck(false, true, command.CloseRouteVersion);
    }

    private static async Task<StopDeliveryCommand?> ReadStopDeliveryAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenantId, string gameId,
        string endOperationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT room_id,round_generation,close_route_version,issued_at
            FROM control.stop_delivery
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StopDeliveryCommand(tenantId, gameId, endOperationId, reader.GetString(0),
            reader.GetInt64(1), reader.GetInt64(2), reader.GetDateTime(3));
    }

    private static async Task<List<StopDeliveryResponder>> ListStopDeliveryRespondersAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenantId, string gameId,
        string endOperationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT process_instance_id,endpoint,state,answered_at,
                cutoff_sequence,drained_out_of_round,drained_routed
            FROM control.stop_delivery_responder
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            ORDER BY process_instance_id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var responders = new List<StopDeliveryResponder>();
        while (await reader.ReadAsync(cancellationToken))
            responders.Add(new StopDeliveryResponder(reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        return responders;
    }

    /// <summary>The close version is derived from the operation's own recorded facts — the
    /// room, its round generation, and the mount generations the begin froze. The same
    /// operation always derives the same version; a different room or a newer round derives a
    /// different one, so a replayed command can never close a newer round.</summary>
    private static long DeriveCloseRouteVersion(EndOperation operation) =>
        ContentHash(new[] { $"room|{operation.RoomId}|{operation.RoundGeneration}" }
            .Concat(operation.BindingGenerations
                .OrderBy(binding => binding.BindingId)
                .Select(binding =>
                    $"binding|{binding.BindingId}|{binding.Generation}|{binding.RoundGeneration}")));
}
