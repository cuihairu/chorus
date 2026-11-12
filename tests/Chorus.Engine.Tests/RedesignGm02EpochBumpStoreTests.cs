using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// GM room-epoch bump (FIX-GM-02, architecture 4.5): one transaction validates the ownership is
/// Active and that BOTH epoch columns agree with the expected value, then moves both to the
/// resolved target (unstated = expected + 1) keeping the owner and parking Recovering, recording
/// the idempotent receipt and audit row in the same transaction. The acceptance: a replay by
/// operation id returns the recorded outcome without a second bump; a different request under the
/// same id conflicts; a stale expected, a disagreeing pair, or a non-Active ownership is rejected
/// with no state change; a rollback leaves both columns consistent; two concurrent bumps of the
/// same expected epoch yield exactly one logical operation.
/// </summary>
public sealed class RedesignGm02EpochBumpStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Room = "room-1";
    private const string Owner = "gs-1";

    [Fact]
    public async Task ABumpMovesBothColumnsAndParksRecoveringKeepingTheOwner()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();

        var outcome = await db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-1", operatorId: "operator-1",
            reason: "test stale epoch", leaseSeconds: 30);

        Assert.False(outcome.Duplicate);
        Assert.Equal(1, outcome.OldEpoch);
        Assert.Equal(2, outcome.NewEpoch);
        Assert.Equal(Owner, outcome.OwnerInstanceId);
        Assert.Equal("Recovering", outcome.OwnershipStatus);
        await AssertEpochsAsync(db, newEpoch: 2, status: "Recovering", owner: Owner);
        // The receipt carries the request shape (idempotency identity) and the recorded outcome.
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var receipt = new NpgsqlCommand("""
                SELECT expected_epoch,target_epoch,old_epoch,new_epoch,owner_instance_id,reason
                FROM control.epoch_operation WHERE operation_id='bump-1'
                """, connection);
            await using var reader = await receipt.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(2L, reader.GetInt64(3));
            Assert.Equal(Owner, reader.GetString(4));
            Assert.Equal("test stale epoch", reader.GetString(5));
        }
        await AssertAuditAsync(db, "bump-1");
    }

    [Fact]
    public async Task AStatedTargetBumpsBothColumnsToExactlyThatValue()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();

        var outcome = await db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 5, operationId: "bump-jump", operatorId: "operator-1",
            reason: "jump", leaseSeconds: 30);

        Assert.Equal(5, outcome.NewEpoch);
        await AssertEpochsAsync(db, newEpoch: 5, status: "Recovering", owner: Owner);
    }

    [Fact]
    public async Task AnEqualTargetIsRejectedByTheRuleAndChangesNothing()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();

        // target == expected holds the epoch still: a bump that never moves is an invalid request.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 1, operationId: "bump-hold", operatorId: "operator-1",
            reason: "hold", leaseSeconds: 30));
        await AssertEpochsAsync(db, newEpoch: 1, status: "Active", owner: Owner);
        // No receipt is written for a rejected move.
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM control.epoch_operation WHERE operation_id='bump-hold'", connection);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task AReplayReturnsTheRecordedOutcomeWithoutASecondBump()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();

        var first = await db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-replay", operatorId: "operator-1",
            reason: "bump", leaseSeconds: 30);
        // The reply was lost, not the bump: the same id replays the recorded outcome.
        var replay = await db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-replay", operatorId: "operator-1",
            reason: "bump", leaseSeconds: 30);

        Assert.True(replay.Duplicate);
        Assert.Equal(first.NewEpoch, replay.NewEpoch);
        Assert.Equal(first.OldEpoch, replay.OldEpoch);
        Assert.Equal(first.OwnerInstanceId, replay.OwnerInstanceId);
        Assert.Equal("Recovering", replay.OwnershipStatus);
        // No second bump: the epoch and the receipt table each moved once.
        await AssertEpochsAsync(db, newEpoch: 2, status: "Recovering", owner: Owner);
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM control.epoch_operation WHERE operation_id='bump-replay'", connection);
            Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ASameIdWithDifferentContentConflicts()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();
        await db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-conflict", operatorId: "operator-1",
            reason: "bump", leaseSeconds: 30);

        // Same id, different target: a different canonical request under the spent id.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 3, operationId: "bump-conflict", operatorId: "operator-1",
            reason: "bump", leaseSeconds: 30));
        // Same id, different expected: also a different request.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 2, targetEpoch: 0, operationId: "bump-conflict", operatorId: "operator-1",
            reason: "bump", leaseSeconds: 30));
        // The conflicting replay did not move the epoch.
        await AssertEpochsAsync(db, newEpoch: 2, status: "Recovering", owner: Owner);
    }

    [Fact]
    public async Task AStaleExpectedEpochIsRejectedWithNoStateChange()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();

        // The room sits at epoch 1; claiming 2 is a stale command, not a bump.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 2, targetEpoch: 0, operationId: "bump-stale", operatorId: "operator-1",
            reason: "stale", leaseSeconds: 30));
        await AssertEpochsAsync(db, newEpoch: 1, status: "Active", owner: Owner);
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM control.epoch_operation WHERE operation_id='bump-stale'", connection);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ANonActiveOwnershipCannotBeBumpedByANewOperation()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();
        await db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-1", operatorId: "operator-1",
            reason: "bump", leaseSeconds: 30);

        // The first bump parked the room Recovering; without the explicit recovery (FIX-GM-05)
        // a second bump under a new id is refused — it cannot inflate the epoch of a parked room.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 2, targetEpoch: 0, operationId: "bump-2", operatorId: "operator-1",
            reason: "bump again", leaseSeconds: 30));
        await AssertEpochsAsync(db, newEpoch: 2, status: "Recovering", owner: Owner);
    }

    [Fact]
    public async Task ADisagreeingOwnershipAndRoomEpochIsRejected()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();
        // Corrupt the invariant directly: the room column drifts ahead of the ownership.
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var corrupt = new NpgsqlCommand(
                "UPDATE game.room SET epoch=5 WHERE room_id='room-1'", connection);
            await corrupt.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-disagree", operatorId: "operator-1",
            reason: "disagree", leaseSeconds: 30));
        // Both columns are left exactly as the corruption left them — the bump does not paper
        // over the disagreement, and no column was moved toward the other.
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT epoch,status FROM control.ownership WHERE kind='room' AND entity_id='room-1'",
                connection);
            await using var reader = await ownership.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, Convert.ToInt64(reader.GetValue(0)));
            Assert.Equal("Active", reader.GetString(1));
            await reader.DisposeAsync();
            await using var room = new NpgsqlCommand(
                "SELECT epoch FROM game.room WHERE room_id='room-1'", connection);
            Assert.Equal(5L, Convert.ToInt64(await room.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ARollbackLeavesBothColumnsUnchanged()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();
        // Take the room out of the bumpable set: the ownership bump succeeds, the room update
        // affects no row, and the whole transaction rolls back.
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var parked = new NpgsqlCommand(
                "UPDATE game.room SET status='Completed' WHERE room_id='room-1'", connection);
            await parked.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
            expectedEpoch: 1, targetEpoch: 0, operationId: "bump-rollback", operatorId: "operator-1",
            reason: "rollback", leaseSeconds: 30));
        // The ownership bump rolled back with the failed room update: still Active at epoch 1.
        await AssertEpochsAsync(db, newEpoch: 1, status: "Active", owner: Owner);
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM control.epoch_operation WHERE operation_id='bump-rollback'", connection);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ConcurrentBumpsOfTheSameExpectedEpochYieldExactlyOneSuccess()
    {
        await using var db = await RunningRoomDatabase.CreateAsync();

        // The two bumps race on the ownership row; one wins the epoch move, the other reads the
        // already-moved epoch and is refused.
        var tasks = new[]
        {
            db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
                expectedEpoch: 1, targetEpoch: 0, operationId: "bump-c1", operatorId: "operator-1",
                reason: "concurrent", leaseSeconds: 30),
            db.Control.BumpRoomEpochAsync(Tenant, Game, Room,
                expectedEpoch: 1, targetEpoch: 0, operationId: "bump-c2", operatorId: "operator-1",
                reason: "concurrent", leaseSeconds: 30)
        };
        var outcomes = new List<EpochBumpOutcome>();
        var failures = new List<Exception>();
        foreach (var task in tasks)
        {
            try { outcomes.Add(await task); }
            catch (Exception ex) { failures.Add(ex); }
        }

        Assert.Single(outcomes);
        Assert.Single(failures);
        Assert.IsType<InvalidOperationException>(failures[0]);
        await AssertEpochsAsync(db, newEpoch: 2, status: "Recovering", owner: Owner);
        await using (var connection = new NpgsqlConnection(db.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM control.epoch_operation", connection);
            // Exactly one receipt row: only the successful bump recorded an operation.
            Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    /// <summary>Asserts both epoch columns now agree at <paramref name="newEpoch"/>, with the
    /// expected ownership status and the (unchanged) owner.</summary>
    private static async Task AssertEpochsAsync(RunningRoomDatabase db, long newEpoch, string status, string owner)
    {
        await using var connection = new NpgsqlConnection(db.Database.ConnectionString);
        await connection.OpenAsync();
        await using var ownership = new NpgsqlCommand("""
            SELECT epoch,status,owner_process_instance_id
            FROM control.ownership WHERE kind='room' AND entity_id='room-1'
            """, connection);
        await using (var reader = await ownership.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(newEpoch, Convert.ToInt64(reader.GetValue(0)));
            Assert.Equal(status, reader.GetString(1));
            Assert.Equal(owner, reader.GetString(2));
        }
        await using var room = new NpgsqlCommand(
            "SELECT epoch FROM game.room WHERE room_id='room-1'", connection);
        Assert.Equal(newEpoch, Convert.ToInt64(await room.ExecuteScalarAsync()));
    }

    private static async Task AssertAuditAsync(RunningRoomDatabase db, string operationId)
    {
        await using var connection = new NpgsqlConnection(db.Database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            "SELECT count(*) FROM infra.audit WHERE operation_id=@op AND action='RoomEpochBump'",
            connection);
        audit.Parameters.AddWithValue("op", operationId);
        Assert.Equal(1L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
    }

    /// <summary>A temporary database with one running room (ownership Active + room Running, both
    /// epoch 1, owner gs-1) — the minimal shape the bump transaction reads.</summary>
    private sealed class RunningRoomDatabase : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignControlStore Control = null!;

        public static async Task<RunningRoomDatabase> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var store = new RedesignControlStore(database.ConnectionString);
                await using (var connection = new NpgsqlConnection(database.ConnectionString))
                {
                    await connection.OpenAsync();
                    await using var ownership = new NpgsqlCommand("""
                        INSERT INTO control.ownership(kind,entity_id,owner_process_instance_id,epoch,
                            lease_until,status,version,tenant_id,game_id)
                        VALUES('room','room-1','gs-1',1,now()+interval '60 second','Active',1,
                            'tenant-a','game-a')
                        """, connection);
                    await ownership.ExecuteNonQueryAsync();
                    await using var room = new NpgsqlCommand("""
                        INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,
                            package_hash,module_version,config_hash)
                        VALUES('room-1','tenant-a','game-a','Running',1,'pkg-1','1.0.0','cfg-1')
                        """, connection);
                    await room.ExecuteNonQueryAsync();
                }
                return new RunningRoomDatabase { Database = database, Control = store };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _name = "chorus_gm02_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin;
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private TemporaryDatabase(string source) => _admin = new(source) { Pooling = false };

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
