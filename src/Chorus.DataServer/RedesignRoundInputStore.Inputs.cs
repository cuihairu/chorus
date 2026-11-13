using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed partial class RedesignRoundInputStore
{
    /// <summary>
    /// Durable client command intake (architecture 3.1, 6.3): the current session, the active
    /// participation and the room epoch all validate before the command enters the round inbox.
    /// The receipt means durably accepted, not applied. The command id is the idempotency key.
    /// </summary>
    public async Task<ClientInputReceipt> AcceptClientInputAsync(string tenantId, string gameId,
        string sessionId, string roomId, long roomEpoch, string commandId, string payloadJson,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(sessionId, nameof(sessionId));
        Require(roomId, nameof(roomId)); Require(commandId, nameof(commandId)); Require(payloadJson, nameof(payloadJson));
        if (roomEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(roomEpoch));
        using (JsonDocument.Parse(payloadJson)) { }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            SessionId = sessionId, RoomId = roomId, RoomEpoch = roomEpoch, PayloadJson = payloadJson
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "input.client", roomId, commandId),
            fingerprint,
            (connection, transaction, token) => ApplyClientInputAsync(tenantId, gameId, sessionId, roomId,
                roomEpoch, commandId, payloadJson, hash, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<ClientInputReceipt>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored client input receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyClientInputAsync(string tenantId, string gameId, string sessionId,
        string roomId, long roomEpoch, string commandId, string payloadJson, byte[] hash,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        string streamerId;
        await using (var session = new NpgsqlCommand(
            "SELECT streamer_id FROM auth.session WHERE session_id=@session", connection, transaction))
        {
            session.Parameters.AddWithValue("session", sessionId);
            streamerId = await session.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new SessionRevokedException();
        }
        await using (var state = new NpgsqlCommand("""
            SELECT current_session_id,ban_state FROM auth.streamer_state
            WHERE streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != sessionId || reader.GetString(1) != "Allowed")
                throw new SessionRevokedException();
        }
        await using (var current = new NpgsqlCommand("""
            SELECT revoked_at,expires_at FROM auth.session
            WHERE session_id=@session AND streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            current.Parameters.AddWithValue("session", sessionId);
            current.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.IsDBNull(0)
                || reader.GetDateTime(1) <= DateTime.UtcNow)
                throw new SessionRevokedException();
        }

        // Binding before participation before room (unified lock order); the participation is
        // pre-read unlocked to learn the binding, then re-validated under its lock.
        string participationBinding;
        await using (var preview = new NpgsqlCommand("""
            SELECT binding_id FROM game.participation
            WHERE room_id=@room AND participant_id=@streamer AND binding_id IS NOT NULL
            """, connection, transaction))
        {
            preview.Parameters.AddWithValue("room", roomId);
            preview.Parameters.AddWithValue("streamer", streamerId);
            participationBinding = await preview.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RoundAdmissionException("The streamer does not participate in this room.");
        }
        string liveSessionId; long routeVersion;
        await using (var binding = new NpgsqlCommand("""
            SELECT live_session_id,tenant_id,game_id,current_room_id,round_generation
            FROM live.binding WHERE binding_id=@binding FOR UPDATE
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue("binding", participationBinding);
            await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(1) != tenantId
                || reader.GetString(2) != gameId || reader.IsDBNull(3) || reader.GetString(3) != roomId)
                throw new RoundAdmissionException("The participant binding does not point at this room.");
            liveSessionId = reader.GetString(0);
            routeVersion = reader.GetInt64(4);
        }
        await using (var participation = new NpgsqlCommand("""
            SELECT state FROM game.participation
            WHERE room_id=@room AND participant_id=@streamer AND binding_id=@binding FOR UPDATE
            """, connection, transaction))
        {
            participation.Parameters.AddWithValue("room", roomId);
            participation.Parameters.AddWithValue("streamer", streamerId);
            participation.Parameters.AddWithValue("binding", participationBinding);
            var state = await participation.ExecuteScalarAsync(cancellationToken) as string;
            if (state != "Active")
                throw new RoundAdmissionException("The participation is not active.");
        }
        await using (var room = new NpgsqlCommand("""
            SELECT status,epoch FROM game.room WHERE room_id=@room FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RoundAdmissionException("The room does not exist.");
            if (reader.GetInt64(1) != roomEpoch)
                throw new RedesignStaleOwnerException();
            var status = reader.GetString(0);
            if (status is not (RedesignRoomStore.Preparing or "Running"))
                throw new RoundAdmissionException($"A room in status {status} does not accept input.");
        }
        // FIX-END-06: the standing end operation closes the round's input generation
        // independently of the status column — the persisted row is the fence, so a recovered
        // room that shows Running again still refuses new authoritative input. The binding
        // entered this room's only round, so its generation is at most the operation's.
        if (await StandingEndOperationAsync(roomId, connection, transaction, cancellationToken)
            is { } closedClientOperation && routeVersion <= closedClientOperation.Generation)
            throw new RoundAdmissionException(
                $"The round generation {routeVersion} of room {roomId} is closed by the end operation " +
                $"{closedClientOperation.OperationId}; it no longer accepts authoritative input.");

        // FIX-SYNC-09: the frequency cap — count this binding's own client commands inside the
        // rolling window before inserting. The count runs under the binding lock taken above,
        // so concurrent commands of one participant cannot both read the same below-budget
        // count; the partial index keeps it a range read, not a table scan.
        await using (var rate = new NpgsqlCommand("""
            SELECT count(*) FROM input.event
            WHERE platform=@platform AND binding_id=@binding
              AND received_at >= clock_timestamp() - make_interval(secs => @window)
            """, connection, transaction))
        {
            rate.Parameters.AddWithValue("platform", ClientPlatform);
            rate.Parameters.AddWithValue("binding", participationBinding);
            rate.Parameters.AddWithValue("window", ClientCommandWindowSeconds);
            var inWindow = Convert.ToInt64(await rate.ExecuteScalarAsync(cancellationToken));
            if (inWindow >= MaxClientCommandsPerWindow)
                throw new ClientCommandRateExceededException(
                    $"The participant binding {participationBinding} exceeded {MaxClientCommandsPerWindow} " +
                    $"client commands in {ClientCommandWindowSeconds}s; the command is refused, not queued.");
        }

        string canonicalRoomKey;
        await using (var live = new NpgsqlCommand("""
            SELECT canonical_live_session_key FROM live.session WHERE live_session_id=@live
            """, connection, transaction))
        {
            live.Parameters.AddWithValue("live", liveSessionId);
            canonicalRoomKey = await live.ExecuteScalarAsync(cancellationToken) as string ?? "";
        }
        var eventId = Guid.NewGuid().ToString("N");
        await using (var insertEvent = new NpgsqlCommand("""
            INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,
                disposition,binding_id,binding_generation,payload,canonical_room_key,durability,
                attributed_room_id)
            VALUES (@event,@platform,@app,@command,@hash,@disposition,@binding,@generation,
                @payload::jsonb,@room,'Durable',@attributed)
            """, connection, transaction))
        {
            insertEvent.Parameters.AddWithValue("event", eventId);
            insertEvent.Parameters.AddWithValue("platform", ClientPlatform);
            insertEvent.Parameters.AddWithValue("app", gameId);
            insertEvent.Parameters.AddWithValue("command", commandId);
            insertEvent.Parameters.AddWithValue("hash", hash);
            insertEvent.Parameters.AddWithValue("disposition", Routed);
            insertEvent.Parameters.AddWithValue("binding", participationBinding);
            insertEvent.Parameters.AddWithValue("generation", routeVersion);
            insertEvent.Parameters.AddWithValue("payload", payloadJson);
            insertEvent.Parameters.AddWithValue("room", canonicalRoomKey);
            insertEvent.Parameters.AddWithValue("attributed", roomId);
            try
            {
                await insertEvent.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException error) when (error.SqlState == "23505")
            {
                throw new IdempotencyConflictException();
            }
        }

        var scope = "room:" + roomId;
        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO input.cursor(consumer_scope) VALUES (@scope) ON CONFLICT DO NOTHING",
            connection, transaction))
        {
            ensure.Parameters.AddWithValue("scope", scope);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }
        long next;
        await using (var cursor = new NpgsqlCommand("""
            SELECT next_delivery_sequence FROM input.cursor WHERE consumer_scope=@scope FOR UPDATE
            """, connection, transaction))
        {
            cursor.Parameters.AddWithValue("scope", scope);
            next = Convert.ToInt64(await cursor.ExecuteScalarAsync(cancellationToken));
        }
        await using (var delivery = new NpgsqlCommand("""
            INSERT INTO input.delivery(event_id,consumer_scope,room_id,route_version,sequence,state)
            VALUES (@event,@scope,@room,@version,@sequence,'Pending')
            """, connection, transaction))
        {
            delivery.Parameters.AddWithValue("event", eventId);
            delivery.Parameters.AddWithValue("scope", scope);
            delivery.Parameters.AddWithValue("room", roomId);
            delivery.Parameters.AddWithValue("version", routeVersion);
            delivery.Parameters.AddWithValue("sequence", next);
            await delivery.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var advance = new NpgsqlCommand(
            "UPDATE input.cursor SET next_delivery_sequence=@next WHERE consumer_scope=@scope",
            connection, transaction))
        {
            advance.Parameters.AddWithValue("scope", scope);
            advance.Parameters.AddWithValue("next", next + 1);
            await advance.ExecuteNonQueryAsync(cancellationToken);
        }
        // FIX-LINK-09: the delivery's own event id is what the room's commit must reference —
        // the client's command id is the dedup key, not the delivery row's identity, so a
        // command id reused across rooms never collides on the event row. The seat carries the
        // delivery id as its event reference and surfaces the command id as the Applied word.
        return JsonSerializer.Serialize(new ClientInputReceipt(commandId, roomId, eventId, next,
            routeVersion, Duplicate: false));
    }

    /// <summary>
    /// Durable intake of a directly pushed room event (FIX-INPUT-01, architecture 6.3): the
    /// room ownership row fences the push — the named owner instance and epoch must hold a
    /// live, Active lease — then the event, its inbox delivery and the cursor advance commit
    /// as one transaction. The receipt means durably accepted, not applied. The event id is
    /// the idempotency key; an exact retry replays the stored outcome as Duplicate, the same
    /// id under a different payload conflicts. The binding names the mount the producer
    /// attributed the event to (it must point at this room); an empty binding is a
    /// room-scoped event with no module seat.
    /// </summary>
    public async Task<RoomEventReceipt> AcceptRoomEventAsync(string tenantId, string gameId,
        string roomId, string ownerInstanceId, long epoch, string eventId, string platform,
        string appId, string? bindingId, long roundGeneration, string payloadJson,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(roomId, nameof(roomId)); Require(ownerInstanceId, nameof(ownerInstanceId));
        Require(eventId, nameof(eventId)); Require(platform, nameof(platform)); Require(appId, nameof(appId));
        Require(payloadJson, nameof(payloadJson));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        bindingId = string.IsNullOrEmpty(bindingId) ? null : bindingId;
        // FIX-END-06: an attributed push states the round generation its routing snapshot
        // claimed for the binding — the store checks the claim against the freshly read row,
        // so a stale snapshot's push for a closed round is refused instead of silently
        // landing in a newer one.
        if (bindingId is not null && roundGeneration <= 0)
            throw new ArgumentException(
                "An attributed push must name the round generation its route snapshot claimed.",
                nameof(roundGeneration));
        using (JsonDocument.Parse(payloadJson)) { }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        // FIX-INPUT-07: a pushed event's identity is what the platform retries — platform,
        // app, room, binding and payload. The owner instance and epoch are the admission
        // fence, not the event: a verbatim retry that arrives after a takeover names the
        // new owner and generation, and it must meet its stored receipt as the same event
        // (the receipt keeps the generation that admitted it) instead of a conflict the
        // platform cannot fix by retrying. The fence itself still guards every fresh
        // accept — a new event id at a stale owner or epoch writes nothing. The canonical
        // version bumps because the shape changed: receipts written by the earlier
        // contract conflict once across the deploy boundary, never after.
        var fingerprint = OperationFingerprint.FromCanonicalRequest(2, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Platform = platform, AppId = appId, RoomId = roomId,
            BindingId = bindingId, PayloadJson = payloadJson
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "input.push", roomId, eventId),
            fingerprint,
            (connection, transaction, token) => ApplyRoomEventAsync(tenantId, gameId, roomId,
                ownerInstanceId, epoch, eventId, platform, appId, bindingId, roundGeneration,
                payloadJson, hash, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoomEventReceipt>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored room event receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyRoomEventAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string eventId, string platform, string appId,
        string? bindingId, long roundGeneration, string payloadJson, byte[] hash,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Ownership fences the push before anything else: a caller whose owner instance or
        // epoch is not the room's current authority writes nothing (frames' fence, shared).
        await RedesignFrameStore.LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);

        // Binding before room (unified lock order); the binding must point at this room for
        // the event to reach a module seat. A binding-less push is room-scoped, routed by
        // the epoch that admitted it.
        string? liveSessionId = null; string? canonicalRoomKey = null; long routeVersion = epoch;
        if (bindingId is not null)
        {
            await using (var binding = new NpgsqlCommand("""
                SELECT live_session_id,tenant_id,game_id,current_room_id,round_generation
                FROM live.binding WHERE binding_id=@binding FOR UPDATE
                """, connection, transaction))
            {
                binding.Parameters.AddWithValue("binding", bindingId);
                await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetString(1) != tenantId
                    || reader.GetString(2) != gameId || reader.IsDBNull(3) || reader.GetString(3) != roomId)
                    throw new RoundAdmissionException("The attributed binding does not point at this room.");
                liveSessionId = reader.GetString(0);
                routeVersion = reader.GetInt64(4);
                // FIX-END-06: the route fence — the push must name the round generation its
                // snapshot claimed, and the claim must still be the row's current generation.
                // A stale claim names a round that already closed; re-resolving routing and
                // retrying the same event id is the caller's only honest next step.
                if (roundGeneration != routeVersion)
                    throw new RoundAdmissionException(
                        $"The push claims round generation {roundGeneration} of binding {bindingId} " +
                        $"but the binding holds {routeVersion}; re-resolve routing before retrying.");
            }
            await using (var live = new NpgsqlCommand(
                "SELECT canonical_live_session_key FROM live.session WHERE live_session_id=@live",
                connection, transaction))
            {
                live.Parameters.AddWithValue("live", liveSessionId!);
                canonicalRoomKey = await live.ExecuteScalarAsync(cancellationToken) as string;
            }
        }
        await using (var room = new NpgsqlCommand(
            "SELECT status,epoch FROM game.room WHERE room_id=@room FOR UPDATE", connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RoundAdmissionException("The room does not exist.");
            if (reader.GetInt64(1) != epoch)
                throw new RedesignStaleOwnerException();
            var status = reader.GetString(0);
            if (status is not (RedesignRoomStore.Preparing or "Running"))
                throw new RoundAdmissionException($"A room in status {status} does not accept input.");
        }
        // FIX-END-06: the input-generation fence — a standing end operation closes this
        // round's input no matter what the status column says. Attributed pushes compare the
        // binding's freshly read generation; a bindingless push has no claim, so the room's
        // current generation (the same join the begin derives from) carries the rule. The
        // comparison is <= because per-mount counters diverge while the operation records the
        // max, and a binding pointing at this room entered its only round — anything above
        // the operation's generation is unreachable. Ownership is never touched: the fence
        // lives on the operation row so settlement writes keep their authority.
        if (await StandingEndOperationAsync(roomId, connection, transaction, cancellationToken)
            is { } closedOperation)
        {
            var fencedGeneration = bindingId is null
                ? await RoomGenerationAsync(roomId, connection, transaction, cancellationToken)
                : routeVersion;
            if (fencedGeneration <= closedOperation.Generation)
                throw new RoundAdmissionException(
                    $"The round generation {fencedGeneration} of room {roomId} is closed by the end " +
                    $"operation {closedOperation.OperationId}; it no longer accepts authoritative input.");
        }

        await using (var insertEvent = new NpgsqlCommand("""
            INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,
                disposition,binding_id,binding_generation,payload,canonical_room_key,durability,
                attributed_room_id)
            VALUES (@event,@platform,@app,@event,@hash,@disposition,@binding,@generation,
                @payload::jsonb,@room,'Durable',@attributed)
            """, connection, transaction))
        {
            insertEvent.Parameters.AddWithValue("event", eventId);
            insertEvent.Parameters.AddWithValue("platform", platform);
            insertEvent.Parameters.AddWithValue("app", appId);
            insertEvent.Parameters.AddWithValue("hash", hash);
            insertEvent.Parameters.AddWithValue("disposition", Routed);
            insertEvent.Parameters.AddWithValue("binding", (object?)bindingId ?? DBNull.Value);
            insertEvent.Parameters.AddWithValue("generation",
                bindingId is null ? (object)DBNull.Value : routeVersion);
            insertEvent.Parameters.AddWithValue("payload", payloadJson);
            insertEvent.Parameters.AddWithValue("room", (object?)canonicalRoomKey ?? DBNull.Value);
            insertEvent.Parameters.AddWithValue("attributed", roomId);
            try
            {
                await insertEvent.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException error) when (error.SqlState == "23505")
            {
                throw new IdempotencyConflictException();
            }
        }

        var scope = "room:" + roomId;
        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO input.cursor(consumer_scope) VALUES (@scope) ON CONFLICT DO NOTHING",
            connection, transaction))
        {
            ensure.Parameters.AddWithValue("scope", scope);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }
        long next;
        await using (var cursor = new NpgsqlCommand("""
            SELECT next_delivery_sequence FROM input.cursor WHERE consumer_scope=@scope FOR UPDATE
            """, connection, transaction))
        {
            cursor.Parameters.AddWithValue("scope", scope);
            next = Convert.ToInt64(await cursor.ExecuteScalarAsync(cancellationToken));
        }
        await using (var delivery = new NpgsqlCommand("""
            INSERT INTO input.delivery(event_id,consumer_scope,room_id,route_version,sequence,state)
            VALUES (@event,@scope,@room,@version,@sequence,'Pending')
            """, connection, transaction))
        {
            delivery.Parameters.AddWithValue("event", eventId);
            delivery.Parameters.AddWithValue("scope", scope);
            delivery.Parameters.AddWithValue("room", roomId);
            delivery.Parameters.AddWithValue("version", routeVersion);
            delivery.Parameters.AddWithValue("sequence", next);
            await delivery.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var advance = new NpgsqlCommand(
            "UPDATE input.cursor SET next_delivery_sequence=@next WHERE consumer_scope=@scope",
            connection, transaction))
        {
            advance.Parameters.AddWithValue("scope", scope);
            advance.Parameters.AddWithValue("next", next + 1);
            await advance.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(new RoomEventReceipt(eventId, roomId, next, routeVersion,
            Duplicate: false));
    }
}
