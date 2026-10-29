using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Persistent room-recovery operations (FIX-TAKE-04): the transfer transaction records the
/// operation the same instant it fences the old owner. A retry after a lost reply replays
/// the recorded outcome by the operation id — the epoch never bumps twice for one recovery —
/// and a restarted control plane reads the open phase and the recorded outcome instead of
/// inferring them from RPC replies that may never have arrived.
/// </summary>
public sealed class RedesignRecoveryOperationTests
{
    [Fact]
    public async Task TheTransferRecordsTheOperationInTheSameTransactionAsTheFence()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        var takeover = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        Assert.Equal("Recovering", takeover.Status);

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand("""
                SELECT phase,to_instance,target_epoch,from_instance,deadline_at,last_error
                FROM control.recovery_operation WHERE operation_id='recovery-op-1'
                """, connection);
            await using var reader = await operation.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Transferred", reader.GetString(0));
            Assert.Equal("gs-2", reader.GetString(1));
            Assert.Equal(takeover.Epoch, reader.GetInt64(2));
            Assert.Equal("gs-1", reader.GetString(3));
            Assert.True(reader.GetDateTime(4) > DateTime.UtcNow.AddSeconds(10));
            Assert.True(reader.IsDBNull(5));
        }
    }

    [Fact]
    public async Task ALostTransferReplyReplaysByTheSameOperationIdWithoutASecondBump()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        var moved = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        Assert.Equal("Recovering", moved.Status);

        // The reply was lost, not the transfer: the same id replays the recorded outcome
        // where the from-Active guard alone would have refused the room.
        var replay = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        Assert.Equal(moved.Epoch, replay.Epoch);
        Assert.Equal("gs-2", replay.OwnerInstanceId);
        Assert.Equal("Recovering", replay.Status);

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var epoch = new NpgsqlCommand(
                "SELECT epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'", connection);
            Assert.Equal(moved.Epoch, Convert.ToInt64(await epoch.ExecuteScalarAsync()));
            await using var rows = new NpgsqlCommand(
                "SELECT count(*) FROM control.recovery_operation WHERE room_id='room-1'", connection);
            Assert.Equal(1L, Convert.ToInt64(await rows.ExecuteScalarAsync()));
        }

        // Once the recovery activated, the same id still replays — now reporting Active.
        await arrange.RoomStore.ConfirmRoomOwnershipAsync("tenant-a", "game-a", "room-1", "gs-2");
        var activated = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        Assert.Equal(moved.Epoch, activated.Epoch);
        Assert.Equal("Active", activated.Status);
    }

    [Fact]
    public async Task TheSameIdWithADifferentTargetConflicts()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
                "gs-1", "gs-3", 15, "recovery-op-1"));
    }

    [Fact]
    public async Task ANewIdCannotBumpTheEpochOfAnOpenRecovery()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        var moved = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        // The first recovery is still open: a second transfer under a new id is refused —
        // continuing the open operation, not replacing it, is the legal path.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
                "gs-1", "gs-3", 15, "recovery-op-2"));
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var epoch = new NpgsqlCommand(
                "SELECT epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'", connection);
            Assert.Equal(moved.Epoch, Convert.ToInt64(await epoch.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ARestartedControlPlaneReadsTheOpenOperationAndRecordsTheOutcome()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        var moved = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");

        // A fresh store instance is the restarted control plane: the open operation is
        // queryable without any RPC reply having survived the crash.
        var restarted = new RedesignControlStore(arrange.Database.ConnectionString);
        var open = await restarted.GetOpenRoomRecoveryAsync("tenant-a", "game-a", "room-1");
        Assert.NotNull(open);
        Assert.Equal("recovery-op-1", open!.OperationId);
        Assert.Equal("gs-2", open.ToInstance);
        Assert.Equal(moved.Epoch, open.TargetEpoch);
        Assert.Equal("Transferred", open.Phase);
        Assert.True(open.DeadlineAt > DateTime.UtcNow.AddSeconds(10));

        // The completed recovery closes the operation with its committed progress.
        await restarted.CompleteRoomRecoveryAsync("tenant-a", "game-a", "room-1",
            recovered: true, lastTick: 7, lastError: null);
        Assert.Null(await restarted.GetOpenRoomRecoveryAsync("tenant-a", "game-a", "room-1"));
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand(
                "SELECT phase,last_tick,last_error FROM control.recovery_operation WHERE operation_id='recovery-op-1'",
                connection);
            await using var reader = await operation.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Activated", reader.GetString(0));
            Assert.Equal(7L, reader.GetInt64(1));
            Assert.True(reader.IsDBNull(2));
        }

        // Repeating the completion is a no-op: the record stays closed as it stands.
        await restarted.CompleteRoomRecoveryAsync("tenant-a", "game-a", "room-1",
            recovered: false, lastTick: 9, lastError: "late duplicate");
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand(
                "SELECT phase FROM control.recovery_operation WHERE operation_id='recovery-op-1'", connection);
            Assert.Equal("Activated", Convert.ToString(await operation.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task AFailedRecoveryRecordsItsReasonAndItsCommittedProgress()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");

        var control = new RedesignControlStore(arrange.Database.ConnectionString);
        await control.CompleteRoomRecoveryAsync("tenant-a", "game-a", "room-1",
            recovered: false, lastTick: 4, lastError: "The activation was refused: the lease expired");
        Assert.Null(await control.GetOpenRoomRecoveryAsync("tenant-a", "game-a", "room-1"));
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand(
                "SELECT phase,last_tick,last_error FROM control.recovery_operation WHERE operation_id='recovery-op-1'",
                connection);
            await using var reader = await operation.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Failed", reader.GetString(0));
            Assert.Equal(4L, reader.GetInt64(1));
            Assert.Contains("lease expired", reader.GetString(2));
        }
    }

    [Fact]
    public async Task AnExpiredOpenOperationReassignsToANewInstanceWithANewerEpoch()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        var control = new RedesignControlStore(arrange.Database.ConnectionString);
        var moved = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 1, "recovery-op-1");
        // The one-second deadline is the whole budget; nothing renews it (FIX-TAKE-05).
        await Task.Delay(1100);
        var expired = await control.ListExpiredOpenRoomRecoveriesAsync();
        Assert.Contains(expired, operation => operation.OperationId == "recovery-op-1");
        Assert.Equal(1, await control.CountRoomRecoveryOperationsAsync("tenant-a", "game-a", "room-1"));

        var reassigned = await arrange.RoomStore.ReassignRoomRecoveryAsync("tenant-a", "game-a", "room-1",
            "recovery-op-1", "recovery-op-2", "gs-2", "gs-3", 15);
        Assert.Equal(moved.Epoch + 1, reassigned.Epoch);
        Assert.Equal("gs-3", reassigned.OwnerInstanceId);
        Assert.Equal("Recovering", reassigned.Status);

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand("""
                SELECT (SELECT owner_process_instance_id FROM control.ownership WHERE kind='room' AND entity_id='room-1'),
                       (SELECT epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'),
                       (SELECT epoch FROM game.room WHERE room_id='room-1'),
                       (SELECT phase||'/'||coalesce(last_error,'') FROM control.recovery_operation
                        WHERE operation_id='recovery-op-1'),
                       (SELECT phase FROM control.recovery_operation WHERE operation_id='recovery-op-2'),
                       (SELECT to_instance FROM control.recovery_operation WHERE operation_id='recovery-op-2'),
                       (SELECT target_epoch FROM control.recovery_operation WHERE operation_id='recovery-op-2'),
                       (SELECT deadline_at>clock_timestamp()+interval '10 seconds'
                        FROM control.recovery_operation WHERE operation_id='recovery-op-2')
                """, connection);
            await using var reader = await ownership.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("gs-3", reader.GetString(0));
            Assert.Equal(reassigned.Epoch, reader.GetInt64(1));
            Assert.Equal(reassigned.Epoch, reader.GetInt64(2));
            Assert.StartsWith("Failed/The recovery deadline passed", reader.GetString(3));
            Assert.Equal("Transferred", reader.GetString(4));
            Assert.Equal("gs-3", reader.GetString(5));
            Assert.Equal(reassigned.Epoch, reader.GetInt64(6));
            Assert.True(reader.GetBoolean(7));
        }
        // The replacement has its own fresh deadline, so the sweep list is empty again.
        Assert.DoesNotContain(await control.ListExpiredOpenRoomRecoveriesAsync(),
            operation => operation.RoomId == "room-1");
        Assert.Equal(2, await control.CountRoomRecoveryOperationsAsync("tenant-a", "game-a", "room-1"));
    }

    [Fact]
    public async Task AReassignmentBeforeTheDeadlineIsRefused()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        var moved = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        Assert.Empty(await new RedesignControlStore(arrange.Database.ConnectionString)
            .ListExpiredOpenRoomRecoveriesAsync());
        // The target keeps its chance: the deadline, not the caller, decides.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            arrange.RoomStore.ReassignRoomRecoveryAsync("tenant-a", "game-a", "room-1",
                "recovery-op-1", "recovery-op-2", "gs-2", "gs-3", 15));
        Assert.Contains("deadline", refused.Message);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT epoch,status FROM control.ownership WHERE kind='room' AND entity_id='room-1'", connection);
            await using var reader = await ownership.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(moved.Epoch, reader.GetInt64(0));
            Assert.Equal("Recovering", reader.GetString(1));
        }
    }

    [Fact]
    public async Task ACompletedRecoveryCannotBeReassigned()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "recovery-op-1");
        await arrange.RoomStore.ConfirmRoomOwnershipAsync("tenant-a", "game-a", "room-1", "gs-2");
        var control = new RedesignControlStore(arrange.Database.ConnectionString);
        await control.CompleteRoomRecoveryAsync("tenant-a", "game-a", "room-1",
            recovered: true, lastTick: 5, lastError: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            arrange.RoomStore.ReassignRoomRecoveryAsync("tenant-a", "game-a", "room-1",
                "recovery-op-1", "recovery-op-2", "gs-2", "gs-3", 15));
    }

    [Fact]
    public async Task AReassignmentForADifferentTargetIsRefused()
    {
        await using var arrange = await Arrange.RunningRoomAsync();
        await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 1, "recovery-op-1");
        await Task.Delay(1100);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            arrange.RoomStore.ReassignRoomRecoveryAsync("tenant-a", "game-a", "room-1",
                "recovery-op-1", "recovery-op-2", "gs-3", "gs-1", 15));
    }

    /// <summary>Test arrange: an admitted, activated room under a live ownership fence.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;

        public static async Task<Arrange> RunningRoomAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var arrange = new Arrange
                {
                    Database = database,
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString)
                };
                var streamer = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "recovery-op-open-1");
                var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-recovery-op");
                var session = await arrange.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var binding = await arrange.BindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-1");
                var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                    "gs-1", 60);
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
                    [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-1");
                await arrange.RoomStore.PrepareRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
                    claim.Epoch, [1, 2, 3], [4, 5, 6], 1, "prepare-1");
                await arrange.RoomStore.ActivateRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
                    claim.Epoch, "activate-1");
                return arrange;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_recovery_op_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
