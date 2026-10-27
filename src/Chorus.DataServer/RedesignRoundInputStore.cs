using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed record RouteEventsItem(string EventId, string? RoomId, long? Sequence, string Disposition);

public sealed record RouteEventsResult(IReadOnlyList<RouteEventsItem> Events, bool Duplicate);

public sealed record ClientInputReceipt(
    string CommandId, string RoomId, long Sequence, long RouteVersion, bool Duplicate);

/// <summary>
/// Delivery-record routing into rounds (architecture 6.3). Attributed platform events move into
/// exactly one consumer scope: a trusted round hint wins, otherwise the binding's current room
/// does; terminal rounds record an out-of-round disposition instead of a delivery, and a hint the
/// binding never participated in is a mapping conflict that waits for review — never silently
/// isolated. Client commands enter the same inbox after the current session and the active
/// participation validate, with the command id as the idempotency key. Delivery sequences are
/// allocated under the consumer cursor row lock and committed with the delivery insert.
/// </summary>
/// <param name="crashAfterCursorAllocation">
/// Fault-injection seam for restart tests: invoked inside the transaction after every delivery
/// insert, before the commit. An exception there plays a process dying between routing and the
/// acknowledgment; the rollback leaves no partial batch behind.
/// </param>
public sealed class RedesignRoundInputStore(string connectionString, Action? crashAfterCursorAllocation = null)
{
    public const string Routed = "Routed";
    public const string OutOfRound = "OutOfRound";
    public const string AmbiguousRound = "AmbiguousRound";
    public const string Skipped = "Skipped";
    public const string ClientPlatform = "@chorus";
    private const string ClusterTenantId = "@cluster";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Routes up to <paramref name="maxEvents"/> attributed platform events into their rounds.
    /// Idempotent per (app, operation); the same operation with a different window conflicts.
    /// Pending inbox events are reconciled into Attributed (existing inbox reconciliation)
    /// before routing runs.
    /// </summary>
    public async Task<RouteEventsResult> RouteEventsAsync(string platform, string appId, string operationId,
        int maxEvents, CancellationToken cancellationToken = default)
    {
        Require(platform, nameof(platform)); Require(appId, nameof(appId)); Require(operationId, nameof(operationId));
        if (maxEvents is <= 0 or > 500)
            throw new ArgumentException("The routing window is 1..500 events.", nameof(maxEvents));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Platform = platform, AppId = appId, MaxEvents = maxEvents
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(ClusterTenantId, OperationIdentity.ClusterGameId, "input.route",
                appId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyRoutingAsync(platform, maxEvents,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RouteEventsItem[]>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored routing receipt is unreadable.");
        return new RouteEventsResult(result, receipt.Duplicate);
    }

    private async Task<string> ApplyRoutingAsync(string platform, int maxEvents,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Pass 1 (no locks): the attributed candidate window in receive order. Pending events
        // are reconciled into Attributed by the inbox reconciliation before routing runs.
        var candidates = new List<(string EventId, string BindingId)>();
        await using (var select = new NpgsqlCommand("""
            SELECT event_id,binding_id FROM input.event
            WHERE platform=@platform AND attributed_room_id IS NULL AND disposition='Attributed'
            ORDER BY received_at,event_id LIMIT @max
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("platform", platform);
            select.Parameters.AddWithValue("max", maxEvents);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetString(0), reader.GetString(1)));
        }
        if (candidates.Count == 0) return JsonSerializer.Serialize(Array.Empty<RouteEventsItem>());

        // Pass 2: lock the bindings in the unified order (before any input row lock).
        var bindings = new Dictionary<string, (string? CurrentRoomId, long RoundGeneration)>(StringComparer.Ordinal);
        foreach (var bindingId in candidates.Select(candidate => candidate.BindingId).Distinct()
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            await using var binding = new NpgsqlCommand("""
                SELECT current_room_id,round_generation FROM live.binding
                WHERE binding_id=@binding FOR UPDATE
                """, connection, transaction);
            binding.Parameters.AddWithValue("binding", bindingId);
            await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RoundAdmissionException("An attributed binding disappeared.");
            bindings[bindingId] = (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
        }

        // Pass 3: lock the events themselves, skipping anything routed concurrently.
        var locked = new List<(string EventId, string BindingId, string PayloadText)>();
        await using (var events = new NpgsqlCommand("""
            SELECT event_id,binding_id,payload::text FROM input.event
            WHERE event_id=ANY(@ids) AND attributed_room_id IS NULL AND disposition='Attributed'
            ORDER BY event_id
            """, connection, transaction))
        {
            events.Parameters.AddWithValue("ids", candidates.Select(candidate => candidate.EventId).ToArray());
            await using var reader = await events.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                locked.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        var orderedEvents = candidates
            .Where(candidate => locked.Any(item => item.EventId == candidate.EventId))
            .Select(candidate => locked.First(item => item.EventId == candidate.EventId))
            .ToList();

        // Pass 4: resolve each target room, allocate sequences under the cursor lock and write
        // delivery rows and terminal dispositions.
        var results = new List<RouteEventsItem>();
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (eventId, bindingId, payloadText) in orderedEvents)
        {
            var bindingInfo = bindings[bindingId];
            string? target = null;
            string? hint = null;
            using (var document = JsonDocument.Parse(payloadText))
            {
                if (document.RootElement.TryGetProperty("round_hint", out var hintElement)
                    && hintElement.ValueKind == JsonValueKind.String)
                    hint = hintElement.GetString();
            }
            if (hint is not null)
            {
                await using (var trust = new NpgsqlCommand("""
                    SELECT 1 FROM game.participation WHERE room_id=@room AND binding_id=@binding
                    """, connection, transaction))
                {
                    trust.Parameters.AddWithValue("room", hint);
                    trust.Parameters.AddWithValue("binding", bindingId);
                    if (await trust.ExecuteScalarAsync(cancellationToken) is null)
                    {
                        await MarkEventAsync(connection, transaction, eventId, AmbiguousRound, null, cancellationToken);
                        results.Add(new RouteEventsItem(eventId, null, null, AmbiguousRound));
                        continue;
                    }
                }
                target = hint;
            }
            else
            {
                target = bindingInfo.CurrentRoomId;
            }

            if (target is null)
            {
                await MarkEventAsync(connection, transaction, eventId, OutOfRound, null, cancellationToken);
                results.Add(new RouteEventsItem(eventId, null, null, OutOfRound));
                continue;
            }
            string roomStatus;
            await using (var room = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id=@room", connection, transaction))
            {
                room.Parameters.AddWithValue("room", target);
                roomStatus = await room.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new RoundAdmissionException($"The target room {target} does not exist.");
            }
            if (roomStatus is not (RedesignRoomStore.Preparing or "Running"))
            {
                await MarkEventAsync(connection, transaction, eventId, OutOfRound, target, cancellationToken);
                results.Add(new RouteEventsItem(eventId, target, null, OutOfRound));
                continue;
            }
            var scope = "room:" + target;
            if (!cursors.TryGetValue(scope, out var next))
            {
                await using (var ensure = new NpgsqlCommand(
                    "INSERT INTO input.cursor(consumer_scope) VALUES (@scope) ON CONFLICT DO NOTHING",
                    connection, transaction))
                {
                    ensure.Parameters.AddWithValue("scope", scope);
                    await ensure.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var cursor = new NpgsqlCommand("""
                    SELECT next_delivery_sequence FROM input.cursor
                    WHERE consumer_scope=@scope FOR UPDATE
                    """, connection, transaction))
                {
                    cursor.Parameters.AddWithValue("scope", scope);
                    next = Convert.ToInt64(await cursor.ExecuteScalarAsync(cancellationToken));
                }
                cursors[scope] = next;
            }
            var sequence = cursors[scope];
            cursors[scope] = sequence + 1;
            await using (var delivery = new NpgsqlCommand("""
                INSERT INTO input.delivery(event_id,consumer_scope,room_id,route_version,sequence,state)
                VALUES (@event,@scope,@room,@version,@sequence,'Pending')
                """, connection, transaction))
            {
                delivery.Parameters.AddWithValue("event", eventId);
                delivery.Parameters.AddWithValue("scope", scope);
                delivery.Parameters.AddWithValue("room", target);
                delivery.Parameters.AddWithValue("version", bindingInfo.RoundGeneration);
                delivery.Parameters.AddWithValue("sequence", sequence);
                await delivery.ExecuteNonQueryAsync(cancellationToken);
            }
            await MarkEventAsync(connection, transaction, eventId, Routed, target, cancellationToken);
            results.Add(new RouteEventsItem(eventId, target, sequence, Routed));
        }
        foreach (var (scope, next) in cursors)
        {
            await using (var update = new NpgsqlCommand(
                "UPDATE input.cursor SET next_delivery_sequence=@next WHERE consumer_scope=@scope",
                connection, transaction))
            {
                update.Parameters.AddWithValue("scope", scope);
                update.Parameters.AddWithValue("next", next);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        crashAfterCursorAllocation?.Invoke();
        return JsonSerializer.Serialize(results);
    }

    private static async Task MarkEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string eventId, string disposition, string? roomId, CancellationToken cancellationToken)
    {
        await using var mark = new NpgsqlCommand(
            "UPDATE input.event SET disposition=@disposition, attributed_room_id=@room WHERE event_id=@event",
            connection, transaction);
        mark.Parameters.AddWithValue("disposition", disposition);
        if (roomId is null) mark.Parameters.AddWithValue("room", DBNull.Value);
        else mark.Parameters.AddWithValue("room", roomId);
        mark.Parameters.AddWithValue("event", eventId);
        await mark.ExecuteNonQueryAsync(cancellationToken);
    }

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
        return JsonSerializer.Serialize(new ClientInputReceipt(commandId, roomId, next, routeVersion,
            Duplicate: false));
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
