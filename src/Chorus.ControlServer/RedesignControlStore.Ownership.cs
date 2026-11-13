using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

public sealed record OwnerStatus(string OwnerInstanceId, long Epoch, DateTime LeaseUntilUtc, string Status);

/// <summary>The outcome of a GM room-epoch bump (FIX-GM-02): the old and new epoch, the room's
/// owner (unchanged by the bump) and the ownership status it was parked at — Recovering. A
/// replay of the same operation returns the recorded outcome with <see cref="Duplicate"/> set.</summary>
public sealed record EpochBumpOutcome(long OldEpoch, long NewEpoch, string OwnerInstanceId,
    string OwnershipStatus, bool Duplicate);

/// <summary>A room's epoch as the authority reads it (FIX-GM-06): the ownership row is the
/// fence, so its epoch and owner are authoritative; a room with no ownership row yet falls
/// back to the room row's own epoch (the two must agree once both exist, FIX-GM-02).</summary>
public sealed record RoomEpochStatus(bool Found, long Epoch, string? OwnerInstanceId, string? Status);

public sealed partial class RedesignControlStore
{
    public async Task<OwnerStatus> ClaimOwnershipAsync(
        string tenantId, string gameId, string kind, string entityId,
        string ownerInstanceId, int leaseSeconds, CancellationToken cancellationToken = default,
        string? sessionId = null, long sessionVersion = 0)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        // A streamer claim is versioned (FIX-AUTH-02): it must name the acting session and is
        // admitted only while that session is the streamer's current one, so a displaced
        // holder's late claim cannot flip routing back once a rotation moved the session on.
        if (StringComparer.Ordinal.Equals(kind, "streamer"))
        {
            await using var streamerConnection = new NpgsqlConnection(connectionString);
            await streamerConnection.OpenAsync(cancellationToken);
            await using var transaction =
                await streamerConnection.BeginTransactionAsync(cancellationToken);
            await using (var state = new NpgsqlCommand("""
                SELECT tenant_id,current_session_id,current_session_version
                FROM auth.streamer_state WHERE streamer_id=@entity FOR UPDATE
                """, streamerConnection, transaction))
            {
                state.Parameters.AddWithValue("entity", entityId);
                await using var stateReader = await state.ExecuteReaderAsync(cancellationToken);
                if (!await stateReader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException(
                        $"The streamer {entityId} has no session state; a routing claim names the current session.");
                if (!StringComparer.Ordinal.Equals(stateReader.GetString(0), tenantId))
                    throw new InvalidOperationException(
                        "The streamer belongs to another tenant; the routing claim is out of scope.");
                var currentSessionId = stateReader.IsDBNull(1) ? null : stateReader.GetString(1);
                var currentSessionVersion = stateReader.GetInt64(2);
                if (string.IsNullOrEmpty(sessionId) || sessionVersion <= 0)
                    throw new InvalidOperationException(
                        "A streamer routing claim names the acting session and its version; this claim carries none.");
                if (!StringComparer.Ordinal.Equals(sessionId, currentSessionId) ||
                    sessionVersion != currentSessionVersion)
                    throw new InvalidOperationException(
                        $"The claim names session {sessionId} (version {sessionVersion}), which is not the streamer's current session (version {currentSessionVersion}); routing stays with the current holder.");
            }
            await using var claimCommand = new NpgsqlCommand("""
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
                """, streamerConnection, transaction);
            BindOwner(claimCommand, tenantId, gameId, kind, entityId);
            claimCommand.Parameters.AddWithValue("owner", ownerInstanceId);
            claimCommand.Parameters.AddWithValue("lease", leaseSeconds);
            OwnerStatus status;
            await using (var claimReader = await claimCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (!await claimReader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Ownership scope conflicts with an existing entity.");
                status = new OwnerStatus(claimReader.GetString(0), claimReader.GetInt64(1),
                    claimReader.GetDateTime(2), claimReader.GetString(3));
            }
            await transaction.CommitAsync(cancellationToken);
            return status;
        }
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

    /// <summary>Reads a room's epoch for the GM entry (FIX-GM-06). The ownership row is the
    /// authoritative fence (epoch, owner, status); a room with no ownership row yet falls back
    /// to the room row's epoch. An unknown room reads as not found.</summary>
    public async Task<RoomEpochStatus> GetRoomEpochAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken = default)
    {
        RoomEpochRule.ValidateIdentifiers(tenantId, gameId, roomId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var ownership = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,status FROM control.ownership
            WHERE tenant_id=@tenant AND game_id=@game AND kind='room' AND entity_id=@room
            """, connection);
        ownership.Parameters.AddWithValue("tenant", tenantId);
        ownership.Parameters.AddWithValue("game", gameId);
        ownership.Parameters.AddWithValue("room", roomId);
        await using (var reader = await ownership.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                return new RoomEpochStatus(true, reader.GetInt64(1), reader.GetString(0), reader.GetString(2));
        }
        await using var room = new NpgsqlCommand("""
            SELECT epoch,status FROM game.room
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
            """, connection);
        room.Parameters.AddWithValue("tenant", tenantId);
        room.Parameters.AddWithValue("game", gameId);
        room.Parameters.AddWithValue("room", roomId);
        await using var roomReader = await room.ExecuteReaderAsync(cancellationToken);
        return await roomReader.ReadAsync(cancellationToken)
            ? new RoomEpochStatus(true, roomReader.GetInt64(0), null, roomReader.GetString(1))
            : new RoomEpochStatus(false, 0, null, null);
    }

    /// <summary>The route-change publication state of one room (FIX-GM-03): the storage commit
    /// is a separate fact from the platform gateway's confirmation of the routing version that
    /// carries the room's new epoch. "Synced" only when a gateway confirmed the current snapshot
    /// version (the one that includes the room); "Pending" when the storage committed and the
    /// version moved (the room's epoch is part of the content hash) but no gateway confirmed it
    /// yet; "Removed" when the room is no longer in the snapshot at all. A caller never sees
    /// "Synced" before the gateway confirms, so the storage commit and the route sync are
    /// distinct surfaces.</summary>
    public async Task<string> GetRoomRouteSyncStatusAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadRoutingSnapshotAsync(cancellationToken);
        var room = snapshot.Rooms.FirstOrDefault(route =>
            route.TenantId == tenantId && route.GameId == gameId && route.RoomId == roomId);
        if (room is null) return "Removed";
        return await IsRoutingVersionConfirmedAsync(snapshot.Version, cancellationToken)
            ? "Synced" : "Pending";
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
    /// 4.2: the registrar renews on the owner's behalf while the owner is observable). Live
    /// means Running or Draining (FIX-SCHED-02): a drained host keeps serving the rooms it
    /// holds, so their leases keep renewing — drain stops NEW assignments, never the rooms
    /// already running. An owner whose process row went stale is not renewed — its lease runs
    /// out and the entity becomes takeable. A legacy owner without any process row is never
    /// renewed: it keeps its own renewal discipline.
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
              AND process.lifecycle IN ('Running','Draining')
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
    /// GM room-epoch bump (FIX-GM-02, architecture 4.5): one transaction validates that the
    /// ownership is Active and that BOTH epoch columns agree with the caller's expected value,
    /// then moves both to the resolved target (unstated target = expected + 1, the FIX-GM-01
    /// rule), keeping the owner and parking the ownership Recovering, and records the idempotent
    /// receipt and the audit row in the same transaction. A retry by operation id returns the
    /// recorded outcome without a second bump; a different request under the same id is an
    /// <see cref="Chorus.Data.IdempotencyConflictException"/>. Two concurrent bumps of the same
    /// expected epoch serialize on the ownership row, so exactly one logical operation succeeds;
    /// a rollback leaves both columns at their pre-bump value. The room is recovered explicitly
    /// afterwards (FIX-GM-05), never by the takeover sweep — the receipt lives apart from
    /// <c>control.recovery_operation</c> for exactly that reason.
    /// </summary>
    public async Task<EpochBumpOutcome> BumpRoomEpochAsync(string tenantId, string gameId, string roomId,
        long expectedEpoch, long targetEpoch, string operationId, string operatorId, string reason,
        int leaseSeconds, CancellationToken cancellationToken = default)
    {
        RoomEpochRule.ValidateIdentifiers(tenantId, gameId, roomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId, nameof(operatorId));
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        var resolvedTarget = RoomEpochRule.ResolveTarget(expectedEpoch, targetEpoch);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Idempotency first: a replay returns the recorded outcome, never a second bump. The
        // request shape (expected, resolved target) is the identity — the same id under a
        // different shape is a conflict.
        await using (var replay = new NpgsqlCommand("""
            SELECT owner_instance_id,old_epoch,new_epoch,expected_epoch,target_epoch
            FROM control.epoch_operation
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
            """, connection))
        {
            replay.Parameters.AddWithValue("tenant", tenantId);
            replay.Parameters.AddWithValue("game", gameId);
            replay.Parameters.AddWithValue("operation", operationId);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var owner = reader.GetString(0);
                var old = reader.GetInt64(1);
                var @new = reader.GetInt64(2);
                var recordedExpected = reader.GetInt64(3);
                var recordedTarget = reader.GetInt64(4);
                await reader.DisposeAsync();
                if (recordedExpected != expectedEpoch || recordedTarget != resolvedTarget)
                    throw new Chorus.Data.IdempotencyConflictException();
                return new EpochBumpOutcome(old, @new, owner, "Recovering", true);
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string holder;
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT owner_process_instance_id,status,epoch FROM control.ownership
            WHERE tenant_id=@tenant AND game_id=@game AND kind='room' AND entity_id=@room
            FOR UPDATE
            """, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("tenant", tenantId);
            lockCommand.Parameters.AddWithValue("game", gameId);
            lockCommand.Parameters.AddWithValue("room", roomId);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The ownership row is missing for this room.");
            holder = reader.GetString(0);
            var status = reader.GetString(1);
            var current = reader.GetInt64(2);
            await reader.DisposeAsync();
            if (!StringComparer.Ordinal.Equals(status, "Active"))
                throw new InvalidOperationException($"The ownership is {status}; only an Active room bumps.");
            if (current != expectedEpoch)
                throw new InvalidOperationException($"The ownership epoch is {current}, not {expectedEpoch}.");
        }
        // Architecture 4.5: both columns must agree before the bump — an inconsistent pair is an
        // invariant break the operator must see, never something the bump papers over.
        await using (var roomCheck = new NpgsqlCommand("""
            SELECT epoch FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            """, connection, transaction))
        {
            roomCheck.Parameters.AddWithValue("room", roomId);
            roomCheck.Parameters.AddWithValue("tenant", tenantId);
            roomCheck.Parameters.AddWithValue("game", gameId);
            var roomEpoch = await roomCheck.ExecuteScalarAsync(cancellationToken);
            if (roomEpoch is null)
                throw new InvalidOperationException("The game.room row is missing for this room.");
            if (Convert.ToInt64(roomEpoch) != expectedEpoch)
                throw new InvalidOperationException(
                    $"The room epoch is {roomEpoch}, not {expectedEpoch}; ownership and room disagree.");
        }
        await using (var bump = new NpgsqlCommand("""
            UPDATE control.ownership
            SET epoch=@target, status='Recovering',
                lease_until=clock_timestamp()+@lease*interval '1 second', version=version+1
            WHERE tenant_id=@tenant AND game_id=@game AND kind='room' AND entity_id=@room
              AND epoch=@expected AND status='Active'
            """, connection, transaction))
        {
            bump.Parameters.AddWithValue("target", resolvedTarget);
            bump.Parameters.AddWithValue("expected", expectedEpoch);
            bump.Parameters.AddWithValue("lease", leaseSeconds);
            bump.Parameters.AddWithValue("tenant", tenantId);
            bump.Parameters.AddWithValue("game", gameId);
            bump.Parameters.AddWithValue("room", roomId);
            if (await bump.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The ownership epoch moved concurrently; no bump applied.");
        }
        await using (var room = new NpgsqlCommand("""
            UPDATE game.room SET epoch=@target, updated_at=clock_timestamp()
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
              AND epoch=@expected AND status IN ('Running','Finishing')
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("target", resolvedTarget);
            room.Parameters.AddWithValue("expected", expectedEpoch);
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            if (await room.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The room is not in a bumpable state; no bump applied.");
        }
        await using (var receipt = new NpgsqlCommand("""
            INSERT INTO control.epoch_operation(tenant_id,game_id,operation_id,room_id,owner_instance_id,
                expected_epoch,target_epoch,old_epoch,new_epoch,reason)
            VALUES (@tenant,@game,@operation,@room,@owner,@expected,@target,@old,@new,@reason)
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("tenant", tenantId);
            receipt.Parameters.AddWithValue("game", gameId);
            receipt.Parameters.AddWithValue("operation", operationId);
            receipt.Parameters.AddWithValue("room", roomId);
            receipt.Parameters.AddWithValue("owner", holder);
            receipt.Parameters.AddWithValue("expected", expectedEpoch);
            receipt.Parameters.AddWithValue("target", resolvedTarget);
            receipt.Parameters.AddWithValue("old", expectedEpoch);
            receipt.Parameters.AddWithValue("new", resolvedTarget);
            receipt.Parameters.AddWithValue("reason", reason);
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO infra.audit(actor_instance_id,operation_id,action,target,reason)
            VALUES (@actor,@operation,'RoomEpochBump',@room,@reason)
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("actor", operatorId);
            audit.Parameters.AddWithValue("operation", operationId);
            audit.Parameters.AddWithValue("room", roomId);
            audit.Parameters.AddWithValue("reason", reason);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new EpochBumpOutcome(expectedEpoch, resolvedTarget, holder, "Recovering", false);
    }
}
