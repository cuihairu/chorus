using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Room lifecycle after admission (architecture 7.2): the tick-0 snapshot is pinned exactly
/// once while Preparing, activation alone opens the frame window at state version 1, and a
/// suspension parks a damaged round visibly while abort stays the terminal escape hatch.
/// </summary>
public sealed class RedesignRoundLifecycleTests
{
    [Fact]
    public async Task PrepareActivateSuspendAreFencedIdempotentAndStatusGated()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var frameStore = new RedesignFrameStore(database.ConnectionString);

        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-p1", "room-open-p1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-p2", "room-open-p2");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-op-1");

        // Activation without a prepared initial state is refused: a room never runs unbacked.
        var second = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-2", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-2", "gs-1", second.Epoch,
            [new RoundParticipant(b2.BindingId, p2.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-op-2");
        await Assert.ThrowsAsync<RoundAdmissionException>(() => roomStore.ActivateRoundAsync(
            "tenant-a", "game-a", "room-2", "gs-1", second.Epoch, "act-op-2"));

        // Prepare pins the tick-0 snapshot exactly once, unverified until recovery (M2-4).
        var payload = new byte[] { 1, 2, 3 };
        var hash = new byte[] { 4, 5, 6 };
        var prepared = await roomStore.PrepareRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, payload, hash, 1, "prep-op-1");
        Assert.Equal(RedesignRoomStore.Preparing, prepared.Status);
        Assert.False(prepared.Duplicate);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var snapshot = new NpgsqlCommand("""
                SELECT schema_version,payload,payload_hash,verified FROM game.snapshot
                WHERE room_id='room-1' AND tick_id=0
                """, connection);
            await using var reader = await snapshot.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(payload, (byte[])reader[1]);
            Assert.Equal(hash, (byte[])reader[2]);
            Assert.False(reader.GetBoolean(3));
        }

        var retry = await roomStore.PrepareRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, payload, hash, 1, "prep-op-1");
        Assert.True(retry.Duplicate);
        // The receipt commits to the snapshot hash; a different initial state under the same
        // operation conflicts. (A payload that differs while hashing the same is a collision,
        // not a request change.)
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => roomStore.PrepareRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-1", claim.Epoch, [7, 8, 9], [7, 8, 9], 1, "prep-op-1"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => roomStore.PrepareRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-9", claim.Epoch, payload, hash, 1, "prep-op-9"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => roomStore.PrepareRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-1", claim.Epoch + 3, payload, hash, 1, "prep-op-9"));

        // Activation flips the room to Running at state version 1 — and only then do frames land.
        var activated = await roomStore.ActivateRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, "act-op-1");
        Assert.Equal(RedesignRoomStore.Running, activated.Status);
        Assert.False(activated.Duplicate);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var room = new NpgsqlCommand(
                "SELECT status,state_version FROM game.room WHERE room_id='room-1'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(RedesignRoomStore.Running, reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using var outbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-1/0/activate'", connection);
            Assert.Equal("round.activated", await outbox.ExecuteScalarAsync());
        }
        var activateRetry = await roomStore.ActivateRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, "act-op-1");
        Assert.True(activateRetry.Duplicate);
        await Assert.ThrowsAsync<RoundAdmissionException>(() => roomStore.ActivateRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-1", claim.Epoch, "act-op-1b"));

        var frame = await frameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-1", "gs-1", claim.Epoch, 0, 1, [], [9, 9, 9], [],
                "1.0.0", "cfg-1"), CancellationToken.None);
        Assert.Equal(1, frame.Tick);
        Assert.Equal(2, frame.StateVersion);

        // Suspension parks the damaged room visibly: no further frames, state kept for M2-4.
        var suspended = await roomStore.SuspendRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, "module failed 3 consecutive ticks", "susp-op-1");
        Assert.Equal(RedesignRoomStore.Suspended, suspended.Status);
        Assert.False(suspended.Duplicate);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var room = new NpgsqlCommand(
                "SELECT status,last_tick FROM game.room WHERE room_id='room-1'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(RedesignRoomStore.Suspended, reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using var outbox = new NpgsqlCommand("""
                SELECT kind FROM infra.outbox WHERE effect_id='room-1/0/suspend'
                """, connection);
            Assert.Equal("round.suspended", await outbox.ExecuteScalarAsync());
        }
        var suspendRetry = await roomStore.SuspendRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, "module failed 3 consecutive ticks", "susp-op-1");
        Assert.True(suspendRetry.Duplicate);
        await Assert.ThrowsAsync<RoundAdmissionException>(() => roomStore.SuspendRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-1", claim.Epoch, "again", "susp-op-2"));
        await Assert.ThrowsAsync<FrameConflictException>(() => frameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-2", "gs-1", claim.Epoch, 1, 2, [], [8, 8, 8], [],
                "1.0.0", "cfg-1"), CancellationToken.None));

        // Abort stays the terminal escape hatch for a room that will never recover.
        var aborted = await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            claim.Epoch, "operator-abort", "abort-op-1");
        Assert.Equal(RedesignRoomStore.Aborted, aborted.Status);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var room = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-1'", connection);
            Assert.Equal(RedesignRoomStore.Aborted, await room.ExecuteScalarAsync());
        }
    }

    private static async Task<(ResolvedStreamer Streamer, BindingGrant Binding)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey, string openId)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", openId);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator", "app-1",
            "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        return (streamer, binding);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_lifecycle_" + Guid.NewGuid().ToString("N");
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
