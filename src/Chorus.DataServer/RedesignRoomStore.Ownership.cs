using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed partial class RedesignRoomStore
{
    /// <summary>
    /// Transfers a live room's ownership to a new instance (architecture 10.3): the authority
    /// handover bumps the ownership and room epochs in one transaction, marks the room
    /// Recovering and extends the lease — the old owner is fenced from the new epoch, and no
    /// frame or snapshot write passes the fence until the ownership is confirmed Active again.
    /// FIX-TAKE-04: the same transaction records the recovery operation (target owner, epoch,
    /// deadline); a retry after a lost reply replays the recorded outcome by the operation id,
    /// so the epoch never bumps twice for one recovery.
    /// </summary>
    public async Task<RoomTakeover> TransferRoomOwnershipAsync(string tenantId, string gameId,
        string roomId, string fromInstance, string toInstance, int leaseSeconds,
        string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(fromInstance, nameof(fromInstance)); Require(toInstance, nameof(toInstance));
        Require(operationId, nameof(operationId));
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A same-id retry of an already-committed transfer replays its outcome instead of
        // failing the from-Active guard — the reply was lost, not the transfer.
        await using (var recorded = new NpgsqlCommand("""
            SELECT room_id,from_instance,to_instance,target_epoch FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
            """, connection, transaction))
        {
            recorded.Parameters.AddWithValue("tenant", tenantId);
            recorded.Parameters.AddWithValue("game", gameId);
            recorded.Parameters.AddWithValue("operation", operationId);
            await using var reader = await recorded.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var room = reader.GetString(0);
                var from = reader.GetString(1);
                var to = reader.GetString(2);
                var recordedEpoch = reader.GetInt64(3);
                await reader.DisposeAsync();
                if (room != roomId || from != fromInstance || to != toInstance)
                    throw new IdempotencyConflictException();
                return new RoomTakeover(to, recordedEpoch, await OwnershipStatusAsync(
                    tenantId, gameId, roomId, connection, transaction, cancellationToken) ?? "Recovering");
            }
        }
        await using (var ownership = new NpgsqlCommand("""
            SELECT tenant_id,game_id,owner_process_instance_id,status
            FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != tenantId ||
                reader.GetString(1) != gameId)
                throw new RedesignStaleOwnerException();
            var holder = reader.GetString(2);
            var status = reader.GetString(3);
            if (holder != fromInstance)
                throw new InvalidOperationException($"The room is held by {holder}, not {fromInstance}.");
            if (status != "Active")
                throw new InvalidOperationException($"The room ownership is {status}; only an Active room transfers.");
            await reader.DisposeAsync();
        }
        long epoch;
        await using (var move = new NpgsqlCommand("""
            UPDATE control.ownership SET owner_process_instance_id=@to, epoch=epoch+1,
                lease_until=clock_timestamp()+@lease*interval '1 second', status='Recovering',
                version=version+1
            WHERE kind='room' AND entity_id=@room
            RETURNING epoch
            """, connection, transaction))
        {
            move.Parameters.AddWithValue("to", toInstance);
            move.Parameters.AddWithValue("room", roomId);
            move.Parameters.AddWithValue("lease", leaseSeconds);
            var moved = await move.ExecuteScalarAsync(cancellationToken);
            if (moved is null || (epoch = Convert.ToInt64(moved)) <= 1)
                throw new InvalidOperationException("The ownership vanished mid-transfer.");
        }
        await using (var operation = new NpgsqlCommand("""
            INSERT INTO control.recovery_operation(tenant_id,game_id,operation_id,room_id,
                from_instance,to_instance,target_epoch,phase,deadline_at)
            VALUES (@tenant,@game,@operation,@room,@from,@to,@epoch,'Transferred',
                clock_timestamp()+@lease*interval '1 second')
            """, connection, transaction))
        {
            operation.Parameters.AddWithValue("tenant", tenantId);
            operation.Parameters.AddWithValue("game", gameId);
            operation.Parameters.AddWithValue("operation", operationId);
            operation.Parameters.AddWithValue("room", roomId);
            operation.Parameters.AddWithValue("from", fromInstance);
            operation.Parameters.AddWithValue("to", toInstance);
            operation.Parameters.AddWithValue("epoch", epoch);
            operation.Parameters.AddWithValue("lease", leaseSeconds);
            await operation.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var room = new NpgsqlCommand("""
            UPDATE game.room SET epoch=epoch+1, updated_at=clock_timestamp()
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
              AND status IN ('Running','Finishing')
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await room.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new RoomTakeover(toInstance, epoch, "Recovering");
    }

    /// <summary>The ownership row's current status, or null when the room has none.</summary>
    private static async Task<string?> OwnershipStatusAsync(string tenantId, string gameId, string roomId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var status = new NpgsqlCommand("""
            SELECT status FROM control.ownership
            WHERE kind='room' AND entity_id=@room AND tenant_id=@tenant AND game_id=@game
            """, connection, transaction);
        status.Parameters.AddWithValue("room", roomId);
        status.Parameters.AddWithValue("tenant", tenantId);
        status.Parameters.AddWithValue("game", gameId);
        return await status.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// Moves a timed-out open recovery to a different instance (FIX-TAKE-05, architecture
    /// failure table): one transaction closes the expired operation Failed, bumps the
    /// ownership and room epochs again and opens the replacement operation with a fresh
    /// deadline. Database time decides expiry — a live-but-slow target is reassigned exactly
    /// like a silent one, and no path extends a deadline once written. The displaced target
    /// cannot revive its epoch: the confirm path only admits the holder the ownership names.
    /// </summary>
    public async Task<RoomTakeover> ReassignRoomRecoveryAsync(string tenantId, string gameId,
        string roomId, string operationId, string newOperationId, string fromInstance,
        string toInstance, int leaseSeconds, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(operationId, nameof(operationId)); Require(newOperationId, nameof(newOperationId));
        Require(fromInstance, nameof(fromInstance)); Require(toInstance, nameof(toInstance));
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var recorded = new NpgsqlCommand("""
            SELECT room_id,to_instance,target_epoch,phase,deadline_at FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
            FOR UPDATE
            """, connection, transaction))
        {
            recorded.Parameters.AddWithValue("tenant", tenantId);
            recorded.Parameters.AddWithValue("game", gameId);
            recorded.Parameters.AddWithValue("operation", operationId);
            await using var reader = await recorded.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(3) is not ("Transferred" or "Prepared"))
                throw new InvalidOperationException("The recovery operation is no longer open.");
            if (reader.GetString(0) != roomId || reader.GetString(1) != fromInstance)
                throw new InvalidOperationException(
                    "The recovery operation was recorded for a different room or target.");
            if (reader.GetDateTime(4) > DateTime.UtcNow)
                throw new InvalidOperationException(
                    "The recovery operation has not passed its deadline; the target keeps its chance.");
            var recordedEpoch = reader.GetInt64(2);
            await reader.DisposeAsync();
            await using (var ownership = new NpgsqlCommand("""
                SELECT owner_process_instance_id,status,epoch FROM control.ownership
                WHERE kind='room' AND entity_id=@room FOR UPDATE
                """, connection, transaction))
            {
                ownership.Parameters.AddWithValue("room", roomId);
                await using var owner = await ownership.ExecuteReaderAsync(cancellationToken);
                if (!await owner.ReadAsync(cancellationToken))
                    throw new RedesignStaleOwnerException();
                var holder = owner.GetString(0);
                var status = owner.GetString(1);
                var ownerEpoch = owner.GetInt64(2);
                await owner.DisposeAsync();
                if (holder != fromInstance)
                    throw new InvalidOperationException($"The room is held by {holder}, not {fromInstance}.");
                if (status != "Recovering")
                    throw new InvalidOperationException(
                        $"The room ownership is {status}; only a Recovering room reassigns.");
                if (ownerEpoch != recordedEpoch)
                    throw new InvalidOperationException("The recovery operation names a stale epoch.");
            }
        }
        long epoch;
        await using (var move = new NpgsqlCommand("""
            UPDATE control.ownership SET owner_process_instance_id=@to, epoch=epoch+1,
                lease_until=clock_timestamp()+@lease*interval '1 second', version=version+1
            WHERE kind='room' AND entity_id=@room
            RETURNING epoch
            """, connection, transaction))
        {
            move.Parameters.AddWithValue("to", toInstance);
            move.Parameters.AddWithValue("room", roomId);
            move.Parameters.AddWithValue("lease", leaseSeconds);
            var moved = await move.ExecuteScalarAsync(cancellationToken);
            if (moved is null || (epoch = Convert.ToInt64(moved)) <= 1)
                throw new InvalidOperationException("The ownership vanished mid-reassignment.");
        }
        await using (var closed = new NpgsqlCommand("""
            UPDATE control.recovery_operation SET phase='Failed',
                last_error='The recovery deadline passed before activation.',
                updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
              AND phase IN ('Transferred','Prepared')
            """, connection, transaction))
        {
            closed.Parameters.AddWithValue("tenant", tenantId);
            closed.Parameters.AddWithValue("game", gameId);
            closed.Parameters.AddWithValue("operation", operationId);
            await closed.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var operation = new NpgsqlCommand("""
            INSERT INTO control.recovery_operation(tenant_id,game_id,operation_id,room_id,
                from_instance,to_instance,target_epoch,phase,deadline_at)
            VALUES (@tenant,@game,@operation,@room,@from,@to,@epoch,'Transferred',
                clock_timestamp()+@lease*interval '1 second')
            """, connection, transaction))
        {
            operation.Parameters.AddWithValue("tenant", tenantId);
            operation.Parameters.AddWithValue("game", gameId);
            operation.Parameters.AddWithValue("operation", newOperationId);
            operation.Parameters.AddWithValue("room", roomId);
            operation.Parameters.AddWithValue("from", fromInstance);
            operation.Parameters.AddWithValue("to", toInstance);
            operation.Parameters.AddWithValue("epoch", epoch);
            operation.Parameters.AddWithValue("lease", leaseSeconds);
            await operation.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var room = new NpgsqlCommand("""
            UPDATE game.room SET epoch=epoch+1, updated_at=clock_timestamp()
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
              AND status IN ('Running','Finishing')
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await room.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new RoomTakeover(toInstance, epoch, "Recovering");
    }

    /// <summary>
    /// Confirms a transferred ownership: Recovering becomes Active again, so the new owner's
    /// recovery and frame writes pass the fence. Idempotent for an already-Active owner.
    /// </summary>
    public async Task<RoomTakeover> ConfirmRoomOwnershipAsync(string tenantId, string gameId,
        string roomId, string ownerInstance, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstance, nameof(ownerInstance));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var confirm = new NpgsqlCommand("""
            UPDATE control.ownership SET status='Active', version=version+1
            WHERE kind='room' AND entity_id=@room AND tenant_id=@tenant AND game_id=@game
              AND owner_process_instance_id=@owner AND status='Recovering'
              AND lease_until>clock_timestamp()
            """, connection))
        {
            confirm.Parameters.AddWithValue("room", roomId);
            confirm.Parameters.AddWithValue("tenant", tenantId);
            confirm.Parameters.AddWithValue("game", gameId);
            confirm.Parameters.AddWithValue("owner", ownerInstance);
            await confirm.ExecuteNonQueryAsync(cancellationToken);
        }
        var takeover = await GetTakeoverAsync(tenantId, gameId, roomId, cancellationToken)
            ?? throw new InvalidOperationException("The room has no ownership row.");
        if (takeover.OwnerInstanceId != ownerInstance)
            throw new InvalidOperationException($"The room is held by {takeover.OwnerInstanceId}, not {ownerInstance}.");
        if (takeover.Status != "Active")
            throw new InvalidOperationException($"The room ownership is {takeover.Status}; confirm needs Recovering.");
        return takeover;
    }

    private async Task<RoomTakeover?> GetTakeoverAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,status
            FROM control.ownership WHERE kind='room' AND entity_id=@room
              AND tenant_id=@tenant AND game_id=@game
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RoomTakeover(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }

    private static async Task<long> LockRoomOwnershipAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var ownership = new NpgsqlCommand("""
            SELECT tenant_id,game_id,owner_process_instance_id,epoch,lease_until>clock_timestamp(),status
            FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction);
        ownership.Parameters.AddWithValue("room", roomId);
        await using var reader = await ownership.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != tenantId ||
            reader.GetString(1) != gameId || reader.GetString(2) != ownerInstanceId ||
            reader.GetInt64(3) != epoch || !reader.GetBoolean(4) || reader.GetString(5) != "Active")
            throw new RedesignStaleOwnerException();
        return reader.GetInt64(3);
    }

    private static async Task WriteOutboxAsync(string effectId, string kind, object payload,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var outbox = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@effect,@kind,@payload::jsonb,'Pending')
            ON CONFLICT (effect_id) DO NOTHING
            """, connection, transaction);
        outbox.Parameters.AddWithValue("effect", effectId);
        outbox.Parameters.AddWithValue("kind", kind);
        outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await outbox.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
