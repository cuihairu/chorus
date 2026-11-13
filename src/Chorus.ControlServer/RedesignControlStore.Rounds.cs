using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

/// <summary>A round.started outbox event claimed for dispatch (FIX-LINK-04): the effect the
/// dispatcher must deliver and the commit facts the payload carries — the room, its scope, the
/// ownership epoch, and the participants with the round generation their bindings advanced to
/// (the payload's Generation; the mount generation and the owning instance are resolved per binding
/// at dispatch time).</summary>
public sealed record RoundStartedEvent(
    string EffectId, string RoomId, string TenantId, string GameId, long Epoch,
    IReadOnlyList<RoundStartedParticipant> Participants);

/// <summary>One participant of a claimed round.started event, as the payload carries it: the
/// binding id and the round generation the binding's round pointer advanced to.</summary>
public sealed record RoundStartedParticipant(string BindingId, long RoundGeneration);

/// <summary>The per-participant dispatch target (FIX-LINK-04): the streamer, the mount it is
/// on (binding + mount generation), the room round generation, and the owning StreamerServer
/// instance and its endpoint when the process reports resolved them. Endpoint is null while
/// the owner is unknown — the dispatcher retries rather than guess an address.</summary>
public sealed record ParticipationTarget(
    string StreamerId, string BindingId, long BindingGeneration, long RoundGeneration,
    string? OwnerInstanceId, string? Endpoint);

public sealed partial class RedesignControlStore
{
    /// <summary>
    /// Claims a batch of round.started outbox events for dispatch (FIX-LINK-04): a pending
    /// event is taken with a lease — the claim is atomic, and an event stranded in Dispatching
    /// (the control plane died between claim and completion) is reclaimable once its lease
    /// lapses. The dispatch is exactly the event's durable record: only a claimed event is
    /// delivered, and only a completed one is retired.
    /// </summary>
    public async Task<IReadOnlyList<RoundStartedEvent>> ClaimRoundStartedAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var list = new NpgsqlCommand("""
            SELECT effect_id,payload
            FROM infra.outbox
            WHERE kind='round.started'
              AND (status='Pending' OR (status='Dispatching' AND next_attempt < clock_timestamp()))
            ORDER BY committed_at
            LIMIT @batch
            """, connection);
        list.Parameters.AddWithValue("batch", batchSize);
        var claimed = new List<RoundStartedEvent>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var effectId = reader.GetString(0);
                var payload = JsonSerializer.Deserialize<RoundStartedPayload>(reader.GetString(1));
                if (payload is null) continue;
                claimed.Add(new RoundStartedEvent(effectId, payload.RoomId, payload.TenantId,
                    payload.GameId, payload.Epoch, payload.Participants.Select(participant =>
                        new RoundStartedParticipant(participant.BindingId, participant.Generation)).ToList()));
            }
        }
        foreach (var eventEntry in claimed.ToArray())
        {
            await using var claim = new NpgsqlCommand("""
                UPDATE infra.outbox
                SET status='Dispatching', next_attempt=clock_timestamp()+interval '30 seconds',
                    attempt_count=attempt_count+1
                WHERE effect_id=@effect
                  AND (status='Pending' OR (status='Dispatching' AND next_attempt < clock_timestamp()))
                """, connection);
            claim.Parameters.AddWithValue("effect", eventEntry.EffectId);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                // Another dispatcher took it (or the lease moved); the list was a snapshot.
                claimed.RemoveAll(entry => entry.EffectId == eventEntry.EffectId);
            }
        }
        return claimed;
    }

    /// <summary>Retires a claimed event (success → Done) or returns it to the pending queue
    /// (failure → Pending, next round retries). The dispatcher calls exactly one of these per
    /// claimed event; the lease makes a lost completion retryable, never lost.</summary>
    public async Task CompleteRoundStartedAsync(string effectId, bool delivered,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE infra.outbox
            SET status=@status, next_attempt=NULL
            WHERE effect_id=@effect
            """, connection);
        command.Parameters.AddWithValue("status", delivered ? "Done" : "Pending");
        command.Parameters.AddWithValue("effect", effectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves a claimed round.started event's dispatch targets (FIX-LINK-04): the durable
    /// participant set of the room (game.participation, the authority the commit wrote) joined
    /// to each mount's streamer, its mount generation, and the owning StreamerServer instance
    /// and endpoint from the process reports. A participant whose mount no longer points at the
    /// room is moot — the round released it before the dispatch; the caller skips it rather
    /// than deliver stale evidence. Endpoint stays null while the owner is unknown (retry).
    /// </summary>
    public async Task<IReadOnlyList<ParticipationTarget>> ResolveParticipationTargetsAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.streamer_id,b.binding_id,b.generation,b.round_generation,b.current_room_id,
                o.owner_process_instance_id,pr.endpoint
            FROM game.participation p
            JOIN live.binding b ON b.binding_id=p.binding_id
            JOIN live.session s ON s.live_session_id=b.live_session_id
            LEFT JOIN control.ownership o ON o.kind='streamer' AND o.tenant_id=b.tenant_id
                AND o.game_id=b.game_id AND o.entity_id=s.streamer_id
            LEFT JOIN control.process pr ON pr.process_instance_id=o.owner_process_instance_id
            WHERE p.room_id=@room AND p.state='Active'
            ORDER BY s.streamer_id,b.binding_id
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        var targets = new List<ParticipationTarget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bindingId = reader.GetString(1);
            var roundGeneration = reader.GetInt64(3);
            // A mount whose pointer moved away was released by the round's terminal path — the
            // commit no longer describes this streamer, so the event is moot for it.
            if (!StringComparer.Ordinal.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), roomId))
                continue;
            targets.Add(new ParticipationTarget(
                reader.GetString(0), bindingId, reader.GetInt64(2), roundGeneration,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return targets;
    }

    /// <summary>The round.started payload exactly as the commit wrote it: PascalCase
    /// anonymous-shape properties and participants as anonymous objects (BindingId, Generation —
    /// the binding id and the round generation it advanced to). The row was write-only until this
    /// dispatcher — this is the first consumer, so the wire shape is parsed verbatim and mapped.</summary>
    private sealed record RoundStartedPayload(
        string RoomId, string TenantId, string GameId, long Epoch,
        List<RoundStartedParticipantPayload> Participants);

    private sealed record RoundStartedParticipantPayload(string BindingId, long Generation);
}
