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
    /// LeaveRound as the data plane owns it: the participation withdraws while the room runs
    /// to its natural settlement — no abort, no early result, the other seats' round is
    /// untouched. 'Left' frees the mount's room slot (the active-participation index only
    /// holds Active/Offline) and clears the binding pointer. Idempotent per (tenant, game,
    /// room, operation); a second operation on an already-Left participation withdraws
    /// quietly; a participation the room already ended is a visible rejection.
    /// </summary>
    public async Task<ParticipationWithdrawn> WithdrawParticipationAsync(string tenantId, string gameId,
        string sessionId, string roomId, string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(sessionId, nameof(sessionId)); Require(roomId, nameof(roomId));
        Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            SessionId = sessionId, RoomId = roomId
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.withdraw", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyWithdrawalAsync(tenantId, gameId, sessionId, roomId,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<ParticipationWithdrawn>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored withdrawal receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyWithdrawalAsync(string tenantId, string gameId, string sessionId,
        string roomId, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // The session fence is the client-input one: the withdrawal is a mount's act, and a
        // mount speaks with the session it logged in with.
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

        // Binding before participation (unified lock order): the binding names the tenant the
        // row belongs to, the participation decides whether a withdrawal is left to do.
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
        string stateAfter;
        await using (var binding = new NpgsqlCommand("""
            SELECT tenant_id,game_id FROM live.binding
            WHERE binding_id=@binding FOR UPDATE
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue("binding", participationBinding);
            await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != tenantId || reader.GetString(1) != gameId)
                throw new RoundAdmissionException("The participant binding does not belong to this game.");
        }
        await using (var participation = new NpgsqlCommand("""
            UPDATE game.participation SET state='Left'
            WHERE room_id=@room AND participant_id=@streamer AND binding_id=@binding
                AND state IN ('Active','Offline')
            RETURNING state
            """, connection, transaction))
        {
            participation.Parameters.AddWithValue("room", roomId);
            participation.Parameters.AddWithValue("streamer", streamerId);
            participation.Parameters.AddWithValue("binding", participationBinding);
            if (await participation.ExecuteScalarAsync(cancellationToken) is null)
            {
                // Either already Left (an earlier withdrawal won) or the room ended the
                // participation itself — the row's current state says which.
                await using (var existing = new NpgsqlCommand("""
                    SELECT state FROM game.participation
                    WHERE room_id=@room AND participant_id=@streamer AND binding_id=@binding
                    """, connection, transaction))
                {
                    existing.Parameters.AddWithValue("room", roomId);
                    existing.Parameters.AddWithValue("streamer", streamerId);
                    existing.Parameters.AddWithValue("binding", participationBinding);
                    var existingState = await existing.ExecuteScalarAsync(cancellationToken) as string
                        ?? throw new RoundAdmissionException("The streamer does not participate in this room.");
                    if (existingState != "Left")
                        throw new RoundAdmissionException(
                            $"A participation in state {existingState} cannot withdraw.");
                }
            }
            stateAfter = "Left";
        }
        await using (var pointer = new NpgsqlCommand("""
            UPDATE live.binding SET current_room_id=NULL
            WHERE binding_id=@binding AND current_room_id=@room
            """, connection, transaction))
        {
            pointer.Parameters.AddWithValue("binding", participationBinding);
            pointer.Parameters.AddWithValue("room", roomId);
            await pointer.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(new ParticipationWithdrawn(roomId, stateAfter, Duplicate: false));
    }

    /// <summary>
    /// FIX-GM-07: the delegated dispatch's pre-flight read — a binding's current room and round
    /// generation, the same row (and the same columns) the attributed-push fence checks its
    /// claim against. Read-only and unlocked: the fence itself re-reads under lock, so a race
    /// between this read and the push closes as a refused claim, never a misplaced command.
    /// </summary>
    public async Task<BindingRead> GetBindingAsync(string bindingId,
        CancellationToken cancellationToken = default)
    {
        Require(bindingId, nameof(bindingId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var select = new NpgsqlCommand("""
            SELECT current_room_id,round_generation FROM live.binding WHERE binding_id=@binding
            """, connection);
        select.Parameters.AddWithValue("binding", bindingId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new BindingRead(Found: false, CurrentRoomId: null, RoundGeneration: 0);
        return new BindingRead(Found: true,
            CurrentRoomId: reader.IsDBNull(0) ? null : reader.GetString(0),
            RoundGeneration: reader.GetInt64(1));
    }

    /// <summary>
    /// FIX-GM-07: the effect query behind the admin's operation status — one operation id,
    /// answered from the durable record. The input row's existence and disposition say whether
    /// the operation was accepted at all; the attributed delivery's state and the committed
    /// frame whose input refs name the event id say whether — and at which tick — the room
    /// applied it. Three separate facts, each truthful alone: an event can be routed with no
    /// delivery yet, delivered-but-pending, or consumed into a frame.
    /// </summary>
    public async Task<InputLookup> LookupOperationAsync(string eventId,
        CancellationToken cancellationToken = default)
    {
        Require(eventId, nameof(eventId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string platform;
        string? disposition;
        await using (var select = new NpgsqlCommand("""
            SELECT platform,disposition FROM input.event WHERE event_id=@event
            """, connection))
        {
            select.Parameters.AddWithValue("event", eventId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new InputLookup(Found: false, Platform: "", Disposition: "",
                    RoomId: null, DeliveryState: null, AppliedTick: 0);
            platform = reader.GetString(0);
            disposition = reader.GetString(1);
        }
        string? roomId = null;
        string? deliveryState = null;
        await using (var delivery = new NpgsqlCommand("""
            SELECT d.room_id,d.state FROM input.delivery d
            WHERE d.event_id=@event
            ORDER BY d.consumer_scope LIMIT 1
            """, connection))
        {
            delivery.Parameters.AddWithValue("event", eventId);
            await using var reader = await delivery.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                roomId = reader.IsDBNull(0) ? null : reader.GetString(0);
                deliveryState = reader.GetString(1);
            }
        }
        var appliedTick = 0L;
        if (deliveryState == RedesignFrameStore.Consumed && roomId is not null)
            await using (var frame = new NpgsqlCommand("""
                SELECT tick_id FROM game.frame
                WHERE room_id=@room AND input_refs @> to_jsonb(ARRAY[@event]::text[])
                ORDER BY tick_id LIMIT 1
                """, connection))
            {
                frame.Parameters.AddWithValue("room", roomId);
                frame.Parameters.AddWithValue("event", eventId);
                var found = await frame.ExecuteScalarAsync(cancellationToken);
                if (found is not null) appliedTick = Convert.ToInt64(found);
            }
        return new InputLookup(Found: true, Platform: platform, Disposition: disposition,
            RoomId: roomId, DeliveryState: deliveryState, AppliedTick: appliedTick);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
