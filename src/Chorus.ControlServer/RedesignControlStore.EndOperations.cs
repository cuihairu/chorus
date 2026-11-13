using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

/// <summary>The end sequence's phase ladder (architecture 6.4, FIX-END-01): one row walks
/// these in order — the begin records the frozen result, every later step records its own
/// completion, and Done is terminal. The names are the persisted contract the later end
/// items drive; the row is progress, never authority.</summary>
public static class EndOperationPhases
{
    /// <summary>Step 1: the final frame and result are committed and the actor stopped its
    /// battle ticks — the begin's own evidence.</summary>
    public const string ResultFrozen = "ResultFrozen";
    /// <summary>Step 2: the closing input window — accepted inputs reach their terminal
    /// disposition without changing the outcome.</summary>
    public const string ClosingInput = "ClosingInput";
    /// <summary>Step 3: the stop-delivery command is persisted and issued to the gateways.
    /// </summary>
    public const string StopDeliveryIssued = "StopDeliveryIssued";
    /// <summary>Step 4: every authorized gateway confirmed DeliveryStopped.</summary>
    public const string DeliveryStopped = "DeliveryStopped";
    /// <summary>Step 5: the platform end task is authorized against the closed barrier.
    /// </summary>
    public const string PlatformTaskAuthorized = "PlatformTaskAuthorized";
    /// <summary>Step 6: the platform close answered success or a grounded NotRequired.
    /// </summary>
    public const string PlatformClosed = "PlatformClosed";
    /// <summary>Step 7: the finalize released the participations, pointers and capacity.
    /// </summary>
    public const string Released = "Released";
    /// <summary>Step 8: the room's runtime entities are destroyed.</summary>
    public const string Destroyed = "Destroyed";
    /// <summary>The sequence is complete; the operation is history.</summary>
    public const string Done = "Done";

    private static readonly string[] Ladder =
    [
        ResultFrozen, ClosingInput, StopDeliveryIssued, DeliveryStopped,
        PlatformTaskAuthorized, PlatformClosed, Released, Destroyed, Done
    ];

    /// <summary>The phase a begin starts at.</summary>
    public const string First = ResultFrozen;

    public static bool IsNext(string from, string to)
    {
        var fromIndex = Array.IndexOf(Ladder, from);
        return fromIndex >= 0 && fromIndex + 1 < Ladder.Length && Ladder[fromIndex + 1] == to;
    }
}

/// <summary>One participant mount's generations as the end operation recorded them at
/// begin (FIX-END-01): the binding's own generation and the round generation it entered
/// this round with.</summary>
public sealed record EndMountGeneration(string BindingId, long Generation, long RoundGeneration);

/// <summary>A persistent end operation (FIX-END-01, architecture 6.4): the business id
/// every end step binds itself to, the room's round generation and the mount generations
/// that entered it, the phase the sequence stands at, and the last error. The row is
/// progress, never authority — ownership, routing and the settlement stores stay the
/// fences. The release block (FIX-SETTLE-05) records the evidence reason the data plane
/// refused, when the continuous block began and the cumulative dwell across all block
/// episodes — the total freezes on the release and the reason clears.</summary>
public sealed record EndOperation(
    string TenantId, string GameId, string EndOperationId, string RoomId, long RoundGeneration,
    IReadOnlyList<EndMountGeneration> BindingGenerations, long FinalTick, long ResultVersion,
    string Phase, string? LastError, DateTime CreatedAt, DateTime UpdatedAt,
    string? BlockedReason, DateTime? BlockedSince, long BlockedTotalMs);

/// <summary>Thrown when a begin names a generation the mounts have moved past (or no
/// longer point at the room): a late request records nothing and cannot touch the newer
/// round the mounts entered.</summary>
public sealed class StaleRoundGenerationException(string message) : Exception(message);

/// <summary>A room whose result froze without its end operation (FIX-END-04): the persisted
/// receipt the discovery sweep turns into a begin — the evidence is the room's own last
/// tick and state version, not a caller's claim.</summary>
public sealed record UnreportedFinishingRoom(
    string TenantId, string GameId, string RoomId, long LastTick, long StateVersion);

/// <summary>One tenant-and-game domain the settlement executor walks (FIX-END-11). A room
/// with unapplied grants is always Finishing — the terminator refuses an incomplete
/// manifest — so the Finishing rooms name every domain the settle queue can hold.</summary>
public sealed record SettleDomain(string TenantId, string GameId);

/// <summary>
/// The settlement executor's persistent task state for one room (FIX-SETTLE-03): the claim
/// lease, the bounded retry position and the queryable failure reason. A row exists only
/// while the room has unapplied grants — a completed settlement deletes it.
/// </summary>
public sealed record SettleTaskState(
    string RoomId, string Status, string? OwnerInstanceId, DateTime? LeaseUntil,
    int AttemptCount, string? LastError, DateTime? NextAttempt);

/// <summary>Thrown when a second, different business id wants to begin the end of a
/// round that already has its one operation.</summary>
public sealed class EndOperationConflictException(string message) : Exception(message);

public sealed partial class RedesignControlStore
{
    /// <summary>
    /// Begins the room's end operation (FIX-END-01). Creation is idempotent by request
    /// identity: the same id with the same room and generations replays the recorded
    /// operation, a second id for the same room refuses, and the same id naming another
    /// room refuses. The generation fence runs before any write — a begin naming a round
    /// generation that is not exactly what the room's participant bindings currently hold
    /// is late evidence from a round the mounts have moved past, and it writes nothing.
    /// </summary>
    public async Task<(EndOperation Operation, bool Duplicate)> BeginEndOperationAsync(
        string tenantId, string gameId, string roomId, string endOperationId,
        long finalTick, long resultVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The replay path first: an existing row answers by identity without touching the
        // fences, so a retry after a lost reply — or after the mounts released the round —
        // still replays the one operation.
        await using (var existing = new NpgsqlCommand("""
            SELECT room_id,round_generation,final_tick,result_version
            FROM control.end_operation
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("tenant", tenantId);
            existing.Parameters.AddWithValue("game", gameId);
            existing.Parameters.AddWithValue("operation", endOperationId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var recordedRoom = reader.GetString(0);
                if (recordedRoom != roomId)
                    throw new EndOperationConflictException(
                        $"The end operation {endOperationId} belongs to the room {recordedRoom}.");
                await reader.DisposeAsync();
                var replayed = await ReadEndOperationAsync(
                    connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (replayed!, true);
            }
        }

        // One operation per round: a second business id for the same room refuses visibly
        // instead of racing two coordinators through the same steps.
        await using (var room = new NpgsqlCommand(
            "SELECT 1 FROM control.end_operation WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room",
            connection, transaction))
        {
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            room.Parameters.AddWithValue("room", roomId);
            if (await room.ExecuteScalarAsync(cancellationToken) is not null)
                throw new EndOperationConflictException(
                    $"The room {roomId} already has its end operation.");
        }

        // The lateness fence (FIX-END-04): the room's participant bindings must still point at
        // this room. A binding that moved on — its pointer moved to another room or was
        // released — is evidence the mounts entered a newer round, and the late begin writes
        // nothing. The generations are derived in this transaction from the mounts that still
        // sit here: per-binding counters diverge as mounts carry round history into a room, so
        // no caller can name one generation that satisfies every mount — the record takes the
        // evidence the begin actually found, not what the caller claims.
        var generations = new List<EndMountGeneration>();
        await using (var participants = new NpgsqlCommand("""
            SELECT b.binding_id,b.generation,b.round_generation,b.current_room_id
            FROM game.participation p JOIN live.binding b ON b.binding_id=p.binding_id
            WHERE p.room_id=@room
            ORDER BY b.binding_id
            """, connection, transaction))
        {
            participants.Parameters.AddWithValue("room", roomId);
            await using var reader = await participants.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(3) || reader.GetString(3) != roomId)
                    throw new StaleRoundGenerationException(
                        $"The mount {reader.GetString(0)} no longer points at the room {roomId}; " +
                        "the begin is late.");
                generations.Add(new EndMountGeneration(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        if (generations.Count == 0)
            throw new StaleRoundGenerationException(
                $"The room {roomId} has no participant mounts; the begin is late.");
        var roundGeneration = generations.Max(mount => mount.RoundGeneration);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.end_operation(tenant_id,game_id,end_operation_id,room_id,
                round_generation,binding_generations,final_tick,result_version,phase)
            VALUES (@tenant,@game,@operation,@room,@generation,@bindings::jsonb,@tick,@version,@phase)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("operation", endOperationId);
            insert.Parameters.AddWithValue("room", roomId);
            insert.Parameters.AddWithValue("generation", roundGeneration);
            insert.Parameters.AddWithValue("bindings", JsonSerializer.Serialize(
                generations.Select(binding => new
                {
                    bindingId = binding.BindingId, generation = binding.Generation,
                    roundGeneration = binding.RoundGeneration
                })));
            insert.Parameters.AddWithValue("tick", finalTick);
            insert.Parameters.AddWithValue("version", resultVersion);
            insert.Parameters.AddWithValue("phase", EndOperationPhases.First);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var created = await ReadEndOperationAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (created!, false);
    }

    /// <summary>
    /// Rooms whose result froze but whose end operation never landed (FIX-END-04): the game
    /// host's step-1 report is the fast path, this query is the net — a host killed after the
    /// final commit, or a report whose retries all failed, still yields the operation. The
    /// evidence comes from the room's own persisted receipt (last tick and state version),
    /// and the grace keeps the sweep behind the live host's own report: a racing begin on the
    /// same business id replays one row either way.
    /// </summary>
    public async Task<List<UnreportedFinishingRoom>> ListUnreportedResultRoomsAsync(
        int graceSeconds, CancellationToken cancellationToken = default)
    {
        if (graceSeconds < 0) throw new ArgumentOutOfRangeException(nameof(graceSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT g.tenant_id,g.game_id,g.room_id,g.last_tick,g.state_version
            FROM game.room g
            WHERE g.status IN ('Finishing','Settled','Closed')
              AND g.updated_at < clock_timestamp() - @grace * interval '1 second'
              AND NOT EXISTS (
                  SELECT 1 FROM control.end_operation o
                  WHERE o.tenant_id=g.tenant_id AND o.game_id=g.game_id AND o.room_id=g.room_id)
            ORDER BY g.tenant_id,g.game_id,g.room_id
            """, connection);
        command.Parameters.AddWithValue("grace", graceSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rooms = new List<UnreportedFinishingRoom>();
        while (await reader.ReadAsync(cancellationToken))
            rooms.Add(new UnreportedFinishingRoom(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        return rooms;
    }

    /// <summary>The settle executor's domain walk (FIX-END-11): every tenant and game that
    /// still holds a Finishing room. This read only names where to ask — the queue's truth
    /// stays behind the data plane's ListPendingSettlements face.</summary>
    public async Task<List<SettleDomain>> ListSettleDomainsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
"""
            SELECT DISTINCT tenant_id,game_id FROM game.room WHERE status='Finishing'
            ORDER BY tenant_id,game_id
""", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var domains = new List<SettleDomain>();
        while (await reader.ReadAsync(cancellationToken))
            domains.Add(new SettleDomain(reader.GetString(0), reader.GetString(1)));
        return domains;
    }

    /// <summary>
    /// Atomically claims the room's settlement task for one executor (FIX-SETTLE-03). The
    /// insert creates the task on first discovery; the conflict path re-claims only a task
    /// that is Retryable and due, or Executing whose lease expired — a live lease, an
    /// unexpired backoff or a NeedsAttention task is left untouched and the claim returns
    /// null. The attempt counter bumps on every claim, so a reclaim after a killed holder
    /// is a new attempt with the same idempotent floor under it.
    /// </summary>
    public async Task<SettleTaskState?> ClaimSettleTaskAsync(string tenantId, string gameId,
        string roomId, string instanceId, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(instanceId);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("The lease duration must be positive.", nameof(leaseDuration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.settle_task
                (room_id,tenant_id,game_id,status,owner_instance_id,lease_until,attempt_count,next_attempt,last_error)
            VALUES (@room,@tenant,@game,'Executing',@owner,@until,1,NULL,NULL)
            ON CONFLICT (room_id) DO UPDATE
            SET status='Executing', owner_instance_id=@owner, lease_until=@until,
                attempt_count=control.settle_task.attempt_count+1,
                next_attempt=NULL, last_error=NULL, updated_at=clock_timestamp()
            WHERE (control.settle_task.status='Retryable'
                    AND (control.settle_task.next_attempt IS NULL
                         OR control.settle_task.next_attempt<=clock_timestamp()))
               OR (control.settle_task.status='Executing'
                    AND control.settle_task.lease_until IS NOT NULL
                    AND control.settle_task.lease_until<clock_timestamp())
            RETURNING room_id,status,owner_instance_id,lease_until,attempt_count,last_error,next_attempt
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("owner", instanceId);
        command.Parameters.AddWithValue("until", DateTime.UtcNow + leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettleTask(reader) : null;
    }

    /// <summary>
    /// Records a failed attempt (FIX-SETTLE-03): the row becomes Retryable with a bounded
    /// exponential next attempt, or NeedsAttention once the attempt budget is spent — the
    /// failure reason stays on the row either way. Only the current lease holder may record
    /// (a stale holder whose lease was reclaimed cannot overwrite the new holder's state).
    /// </summary>
    public async Task RecordSettleFailureAsync(string roomId, string instanceId, string error,
        int maxAttempts, TimeSpan backoff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(error);
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.settle_task
            SET status=CASE WHEN attempt_count>=@max THEN 'NeedsAttention' ELSE 'Retryable' END,
                last_error=@error,
                next_attempt=CASE WHEN attempt_count>=@max THEN NULL
                                  ELSE clock_timestamp()+make_interval(secs=>@backoff) END,
                lease_until=NULL, owner_instance_id=NULL, updated_at=clock_timestamp()
            WHERE room_id=@room AND owner_instance_id=@owner
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("owner", instanceId);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("max", maxAttempts);
        command.Parameters.AddWithValue("backoff", backoff.TotalSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The room's grants are fully applied — the task is obsolete and leaves the
    /// table, so the settle_task set stays proportional to in-flight and failed work.</summary>
    public async Task CompleteSettleTaskAsync(string roomId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM control.settle_task WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The queryable settlement task state for a room (FIX-SETTLE-03): the status,
    /// the attempt count, the last failure reason and the next attempt. Null when the room
    /// has no task — either never discovered or already settled.</summary>
    public async Task<SettleTaskState?> GetSettleTaskAsync(string roomId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT room_id,status,owner_instance_id,lease_until,attempt_count,last_error,next_attempt
            FROM control.settle_task WHERE room_id=@room
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettleTask(reader) : null;
    }

    private static SettleTaskState ReadSettleTask(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetDateTime(6));

    /// <summary>The room's end operation, whatever phase it stands at.</summary>
    public async Task<EndOperation?> GetEndOperationAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT end_operation_id FROM control.end_operation
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        var id = await command.ExecuteScalarAsync(cancellationToken) as string;
        return id is null ? null : await ReadEndOperationAsync(
            connection, null, tenantId, gameId, id, cancellationToken);
    }

    /// <summary>
    /// Records the next phase of the end sequence on the operation row (FIX-END-01): the
    /// ladder advances one step at a time under the recorded business id — a skip ahead, a
    /// step back or a phase the operation never held refuses, so a late or reordered step
    /// cannot rewrite a newer operation's progress. The error column records the last
    /// failure the phase produced without leaving the phase. The release transition
    /// (FIX-SETTLE-05) closes the block episode in the same transaction: the dwell since
    /// blocked_since joins the cumulative total and the reason clears — the total survives
    /// the release, the reason only matters while the room is blocked.
    /// </summary>
    public async Task<EndOperation> AdvanceEndOperationPhaseAsync(string tenantId, string gameId,
        string endOperationId, string fromPhase, string toPhase, string? lastError = null,
        CancellationToken cancellationToken = default)
    {
        if (!EndOperationPhases.IsNext(fromPhase, toPhase))
            throw new EndOperationConflictException(
                $"The end sequence does not advance from {fromPhase} to {toPhase}.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET phase=@to, last_error=@error, updated_at=clock_timestamp(),
                blocked_total_ms = blocked_total_ms +
                    COALESCE((EXTRACT(EPOCH FROM (clock_timestamp()-blocked_since))*1000)::bigint, 0),
                blocked_since = NULL, blocked_reason = NULL
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation AND phase=@from
            RETURNING room_id
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("from", fromPhase);
        command.Parameters.AddWithValue("to", toPhase);
        command.Parameters.AddWithValue("error", lastError is null ? DBNull.Value : lastError);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new EndOperationConflictException(
                $"The end operation {endOperationId} is not at the phase {fromPhase}.");
        return (await ReadEndOperationAsync(
            connection, null, tenantId, gameId, endOperationId, cancellationToken))!;
    }

    /// <summary>
    /// Records the release block (FIX-SETTLE-05): the data plane's finalize transaction
    /// refused because evidence is still gathering — undisposed inputs, an incomplete
    /// manifest, a platform precondition — and the refusal's reason becomes the queryable
    /// block. The first refusal of an episode sets blocked_since; later refusals of the
    /// same episode keep it and refresh the reason, so the dwell measures the whole wait.
    /// The write only lands while the row still stands at the release phase: a pass that
    /// already released the room must not have a stale refusal paint a block onto it.
    /// </summary>
    public async Task RecordEndOperationBlockAsync(string tenantId, string gameId,
        string endOperationId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET blocked_reason=@reason,
                blocked_since=COALESCE(blocked_since, clock_timestamp()),
                last_error=@reason,
                updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
                AND phase=@releasePhase
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("releasePhase", EndOperationPhases.PlatformClosed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records a failure on the operation row without leaving the phase — the
    /// restart reads the phase and the reason and retries the same step.</summary>
    public async Task RecordEndOperationErrorAsync(string tenantId, string gameId,
        string endOperationId, string lastError, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET last_error=@error, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("error", lastError);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The open end operations a restarted control plane continues — every phase
    /// the sequence has not finished, oldest first.</summary>
    public async Task<List<EndOperation>> ListOpenEndOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var ids = new List<(string Tenant, string Game, string Id)>();
        await using (var command = new NpgsqlCommand("""
            SELECT tenant_id,game_id,end_operation_id FROM control.end_operation
            WHERE phase<>@done ORDER BY created_at LIMIT 50
            """, connection))
        {
            command.Parameters.AddWithValue("done", EndOperationPhases.Done);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        var open = new List<EndOperation>();
        foreach (var (tenant, game, id) in ids)
            open.Add((await ReadEndOperationAsync(
                connection, null, tenant, game, id, cancellationToken))!);
        return open;
    }
    private static async Task<EndOperation?> ReadEndOperationAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenantId, string gameId, string endOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT room_id,round_generation,binding_generations,final_tick,result_version,
                phase,last_error,created_at,updated_at,
                blocked_reason,blocked_since,blocked_total_ms
            FROM control.end_operation
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var generations = new List<EndMountGeneration>();
        using (var document = JsonDocument.Parse(reader.GetString(2)))
            foreach (var entry in document.RootElement.EnumerateArray())
                generations.Add(new EndMountGeneration(
                    entry.GetProperty("bindingId").GetString()!,
                    entry.GetProperty("generation").GetInt64(),
                    entry.GetProperty("roundGeneration").GetInt64()));
        return new EndOperation(tenantId, gameId, endOperationId, reader.GetString(0),
            reader.GetInt64(1), generations, reader.GetInt64(3), reader.GetInt64(4),
            reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetDateTime(7), reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            reader.GetInt64(11));
    }
}
