using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed record RouteEventsItem(string EventId, string? RoomId, long? Sequence, string Disposition);

public sealed record RouteEventsResult(IReadOnlyList<RouteEventsItem> Events, bool Duplicate);

public sealed record ClientInputReceipt(
    string CommandId, string RoomId, string EventId, long Sequence, long RouteVersion, bool Duplicate);

public sealed record RoomEventReceipt(
    string EventId, string RoomId, long Sequence, long RouteVersion, bool Duplicate);

public sealed record ParticipationWithdrawn(string RoomId, string State, bool Duplicate);

// FIX-GM-07: the delegated dispatch's pre-flight read and the operation status lookup.
public sealed record BindingRead(bool Found, string? CurrentRoomId, long RoundGeneration);

public sealed record InputLookup(bool Found, string Platform, string Disposition,
    string? RoomId, string? DeliveryState, long AppliedTick);

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
public sealed partial class RedesignRoundInputStore(string connectionString, Action? crashAfterCursorAllocation = null)
{
    public const string Routed = "Routed";
    public const string OutOfRound = "OutOfRound";
    public const string AmbiguousRound = "AmbiguousRound";
    public const string Skipped = "Skipped";
    // FIX-SYNC-07: terminal dispositions for inputs that reach a paused room — the room answers
    // its declared paused-input policy instead of the live/terminal split.
    public const string PausedBufferFull = "PausedBufferFull";
    public const string PausedRejected = "PausedRejected";
    public const string Compensated = "Compensated";
    public const string PausedPolicyUnset = "PausedPolicyUnset";
    // The policy values ride the canonical offline policy's fifth field; this store cannot
    // reference the ExampleGame enum, so the wire names are duplicated here.
    public const string PausedPolicyBuffer = "Buffer";
    public const string PausedPolicyCompensate = "Compensate";
    public const string PausedPolicyReject = "Reject";
    /// <summary>The bounded durable buffer a paused room accepts before overflow is observable
    /// (FIX-SYNC-07), aligned with the routing and inbox windows. A paused room has no frame
    /// commits, so its pending count only moves under the routing cursor lock.</summary>
    public const int MaxPausedBufferPerRoom = 500;
    public const string ClientPlatform = "@chorus";
    /// <summary>FIX-SYNC-09: the per-participant client-command intake budget over the rolling
    /// <see cref="ClientCommandWindowSeconds"/> window. The store counts the binding's own
    /// <c>@chorus</c> rows in the window before the insert — the frequency cap of the
    /// anti-cheat table's 重放 row, bounding the abuse surface an accepted-but-untrusted
    /// channel can occupy before the room's tick budget does.</summary>
    public const int MaxClientCommandsPerWindow = 100;
    /// <summary>FIX-SYNC-09: the rolling window the client-command budget counts over.</summary>
    public const int ClientCommandWindowSeconds = 10;
    private const string ClusterTenantId = "@cluster";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Routes up to <paramref name="maxEvents"/> attributed platform events into their rounds.
    /// Idempotent per (app, operation); the same operation with a different window conflicts.
    /// Pending inbox events are reconciled into Attributed (existing inbox reconciliation)
    /// before routing runs.
    /// </summary>
    public async Task<RouteEventsResult> RouteEventsAsync(string platform, string appId, string operationId,
        int maxEvents, bool pausedOnly = false, CancellationToken cancellationToken = default)
    {
        Require(platform, nameof(platform)); Require(appId, nameof(appId)); Require(operationId, nameof(operationId));
        if (maxEvents is <= 0 or > 500)
            throw new ArgumentException("The routing window is 1..500 events.", nameof(maxEvents));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Platform = platform, AppId = appId, MaxEvents = maxEvents, PausedOnly = pausedOnly
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(ClusterTenantId, OperationIdentity.ClusterGameId, "input.route",
                appId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyRoutingAsync(platform, maxEvents, pausedOnly,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RouteEventsItem[]>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored routing receipt is unreadable.");
        return new RouteEventsResult(result, receipt.Duplicate);
    }

    private async Task<string> ApplyRoutingAsync(string platform, int maxEvents, bool pausedOnly,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Pass 1 (no locks): the attributed candidate window in receive order. Pending events
        // are reconciled into Attributed by the inbox reconciliation before routing runs. A
        // paused-only pass (FIX-SYNC-07) narrows the window to events whose binding's current
        // room stands paused — the only events the gateway's post-ingest trigger is for; a
        // live room's attributed events keep waiting for their owner (the push leg) or a
        // closed round's drain pass. The round-hint path is outside this filter: a webhook
        // gift carries no hint, and a binding whose current room is elsewhere is not this
        // pass's business.
        var candidates = new List<(string EventId, string BindingId)>();
        await using (var select = new NpgsqlCommand(pausedOnly ? """
            SELECT e.event_id,e.binding_id FROM input.event e
            JOIN live.binding b ON b.binding_id=e.binding_id
            JOIN game.room r ON r.room_id=b.current_room_id
            WHERE e.platform=@platform AND e.attributed_room_id IS NULL
              AND e.disposition='Attributed' AND r.status=@paused
            ORDER BY e.received_at,e.event_id LIMIT @max
            """ : """
            SELECT event_id,binding_id FROM input.event
            WHERE platform=@platform AND attributed_room_id IS NULL AND disposition='Attributed'
            ORDER BY received_at,event_id LIMIT @max
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("platform", platform);
            select.Parameters.AddWithValue("max", maxEvents);
            if (pausedOnly)
                select.Parameters.AddWithValue("paused", RedesignRoomStore.Paused);
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

        // Pass 3: lock the events themselves, skipping anything routed concurrently (audit
        // P0-3). Pass 2's binding locks already serialize any two passes that share an event
        // (an event's delivery always follows its own binding's lock), so this claim is
        // hardening, not the load-bearing wall: it makes the safety local to the event row
        // itself — a pass can never build a delivery row on a stale read of an event another
        // pass holds, whatever future edits do to the lock order around it.
        var locked = new List<(string EventId, string BindingId, string PayloadText)>();
        await using (var events = new NpgsqlCommand("""
            SELECT event_id,binding_id,payload::text FROM input.event
            WHERE event_id=ANY(@ids) AND attributed_room_id IS NULL AND disposition='Attributed'
            ORDER BY event_id FOR UPDATE SKIP LOCKED
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

        // Pass 4a: resolve each target room — a trusted round hint wins, otherwise the
        // binding's current room does. A hint the binding never participated in is a mapping
        // conflict for review; hint trust is immutable history, safe to read unlocked.
        var resolved = new List<(string EventId, string BindingId, string? Target, bool Ambiguous)>();
        foreach (var (eventId, bindingId, payloadText) in orderedEvents)
        {
            string? hint = null;
            using (var document = JsonDocument.Parse(payloadText))
            {
                if (document.RootElement.TryGetProperty("round_hint", out var hintElement)
                    && hintElement.ValueKind == JsonValueKind.String)
                    hint = hintElement.GetString();
            }
            if (hint is null)
            {
                resolved.Add((eventId, bindingId, bindings[bindingId].CurrentRoomId, Ambiguous: false));
                continue;
            }
            await using (var trust = new NpgsqlCommand("""
                SELECT 1 FROM game.participation WHERE room_id=@room AND binding_id=@binding
                """, connection, transaction))
            {
                trust.Parameters.AddWithValue("room", hint);
                trust.Parameters.AddWithValue("binding", bindingId);
                resolved.Add(await trust.ExecuteScalarAsync(cancellationToken) is null
                    ? (eventId, bindingId, null, true)
                    : (eventId, bindingId, hint, false));
            }
        }

        // Pass 4b: lock every target room in the unified order — room before any delivery or
        // cursor lock (architecture 6.5). Without this the delivery insert's foreign-key share
        // is the first room touch, taken after the cursor, and deadlocks a concurrent frame
        // commit that holds the room and wants the same cursor; the locked read also keeps a
        // room that settles mid-batch from accepting deliveries.
        var roomStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var pausedPolicies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in resolved.Select(item => item.Target).Where(id => id is not null)
                     .Cast<string>().Distinct().OrderBy(id => id, StringComparer.Ordinal))
        {
            await using var room = new NpgsqlCommand(
                "SELECT status,paused_input_policy FROM game.room WHERE room_id=@room FOR UPDATE",
                connection, transaction);
            room.Parameters.AddWithValue("room", target);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RoundAdmissionException($"The target room {target} does not exist.");
            roomStatuses[target] = reader.GetString(0);
            pausedPolicies[target] = reader.GetString(1);
        }

        // FIX-END-06: the standing end operations of the batch's targets, read after the room
        // locks — an operation standing for a target closes that round's input generation and
        // the items fall to the terminal out-of-round disposition, never a delivery.
        var standingOperations = new Dictionary<string, (string OperationId, long Generation)>(
            StringComparer.Ordinal);
        foreach (var target in roomStatuses.Keys)
        {
            var standing = await StandingEndOperationAsync(target, connection, transaction,
                cancellationToken);
            if (standing is { } operation)
                standingOperations[target] = operation;
        }

        // Pass 4c: allocate sequences under the cursor lock and write delivery rows and
        // terminal dispositions.
        var results = new List<RouteEventsItem>();
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        // FIX-SYNC-07: per-scope pending-delivery counts for buffered paused rooms, read once
        // under the scope's cursor lock and accumulated per accepted event — a paused room has
        // no frame commits to consume them, so the count is exact across the batch.
        var pendingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (eventId, bindingId, target, ambiguous) in resolved)
        {
            var bindingInfo = bindings[bindingId];
            if (ambiguous)
            {
                await MarkEventAsync(connection, transaction, eventId, AmbiguousRound, null, cancellationToken);
                results.Add(new RouteEventsItem(eventId, null, null, AmbiguousRound));
                continue;
            }
            if (target is null)
            {
                await MarkEventAsync(connection, transaction, eventId, OutOfRound, null, cancellationToken);
                results.Add(new RouteEventsItem(eventId, null, null, OutOfRound));
                continue;
            }
            // FIX-SYNC-07: a paused room answers its declared paused-input policy instead of the
            // live/terminal split — Buffer falls through to the normal accept path (the event
            // lands as a Pending delivery the resumed room drains in sequence order under its
            // per-tick budget), Compensate records a durable out-of-round disposition, Reject
            // refuses explicitly, and an undeclared or unknown policy fails closed. Every other
            // non-live status stays out of round.
            var buffering = false;
            if (roomStatuses[target] == RedesignRoomStore.Paused)
            {
                switch (pausedPolicies[target])
                {
                    case PausedPolicyBuffer:
                        buffering = true;
                        break;
                    case PausedPolicyCompensate:
                        await MarkEventAsync(connection, transaction, eventId, Compensated, target, cancellationToken);
                        results.Add(new RouteEventsItem(eventId, target, null, Compensated));
                        continue;
                    case PausedPolicyReject:
                        await MarkEventAsync(connection, transaction, eventId, PausedRejected, target, cancellationToken);
                        results.Add(new RouteEventsItem(eventId, target, null, PausedRejected));
                        continue;
                    default:
                        await MarkEventAsync(connection, transaction, eventId, PausedPolicyUnset, target, cancellationToken);
                        results.Add(new RouteEventsItem(eventId, target, null, PausedPolicyUnset));
                        continue;
                }
            }
            if (!buffering && roomStatuses[target] is not (RedesignRoomStore.Preparing or RedesignRoomStore.Running))
            {
                await MarkEventAsync(connection, transaction, eventId, OutOfRound, target, cancellationToken);
                results.Add(new RouteEventsItem(eventId, target, null, OutOfRound));
                continue;
            }
            // FIX-END-06: a standing end operation closes the target's input generation
            // regardless of the status column — the binding entered this room's only round, so
            // its generation is at most the operation's (per-mount counters diverge; the begin
            // records the max), which makes the comparison below exact membership in the
            // closed round.
            if (standingOperations.TryGetValue(target, out var closedOperation)
                && bindingInfo.RoundGeneration <= closedOperation.Generation)
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
                if (buffering)
                {
                    await using (var pending = new NpgsqlCommand(
                        "SELECT count(*) FROM input.delivery WHERE consumer_scope=@scope AND state='Pending'",
                        connection, transaction))
                    {
                        pending.Parameters.AddWithValue("scope", scope);
                        pendingCounts[scope] = Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken));
                    }
                }
            }
            // FIX-SYNC-07: the bounded buffer is observable, not silent — a paused room over the
            // cap records the overflow disposition with the room attributed and no delivery, and
            // the cursor is not advanced (no accepted event is dropped).
            if (buffering && pendingCounts.GetValueOrDefault(scope) >= MaxPausedBufferPerRoom)
            {
                await MarkEventAsync(connection, transaction, eventId, PausedBufferFull, target, cancellationToken);
                results.Add(new RouteEventsItem(eventId, target, null, PausedBufferFull));
                continue;
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
            if (buffering) pendingCounts[scope] = pendingCounts.GetValueOrDefault(scope) + 1;
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
    /// FIX-END-06: the standing end operation for a room, if one has not finished. The row is
    /// the persisted input-generation closure — the receiver-side fence reads it instead of
    /// trusting the room's mutable status (a recovered room may show Running again while its
    /// result is frozen and its operation stands). A plain read inside the caller's admission
    /// transaction: the operation row is never locked before room or binding rows anywhere, so
    /// this adds no lock-order surface, and a begin that lands mid-admission implies the room
    /// was still Running until then — the admitted event predates the freeze. The room id is a
    /// global primary key, so a room-only lookup (no tenant or game predicates) is exact.
    /// </summary>
    private static async Task<(string OperationId, long Generation)?> StandingEndOperationAsync(
        string roomId, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var operation = new NpgsqlCommand("""
            SELECT end_operation_id,round_generation FROM control.end_operation
            WHERE room_id=@room AND phase<>'Done'
            """, connection, transaction);
        operation.Parameters.AddWithValue("room", roomId);
        await using var reader = await operation.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return (reader.GetString(0), reader.GetInt64(1));
    }

    /// <summary>The generation of the round a room currently hosts: the highest round
    /// generation among the bindings pointing at it — the same stateless join the end
    /// operation's begin derives its generation from.</summary>
    private static async Task<long> RoomGenerationAsync(string roomId, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var generation = new NpgsqlCommand("""
            SELECT COALESCE(max(b.round_generation),0) FROM game.participation p
            JOIN live.binding b ON b.binding_id=p.binding_id
            WHERE p.room_id=@room
            """, connection, transaction);
        generation.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await generation.ExecuteScalarAsync(cancellationToken));
    }
}
