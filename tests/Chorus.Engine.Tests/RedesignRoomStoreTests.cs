using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignRoomStoreTests
{
    [Fact]
    public async Task RoundAdmissionIsAtomicIdempotentAndFenced()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);

        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-p1", "room-open-p1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-p2", "room-open-p2");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 30);
        Assert.Equal(1, claim.Epoch);

        var participants = new[] { new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
            new RoundParticipant(b2.BindingId, p2.StreamerId, "Guest") };
        var admission = await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            participants, "1.0.0", "cfg-1", "pkg-1", "start-op-1");
        Assert.Equal("room-1", admission.RoomId);
        Assert.Equal(RedesignRoomStore.Preparing, admission.Status);
        Assert.Equal(1, admission.RoundGeneration);
        Assert.Equal(claim.Epoch, admission.Epoch);
        Assert.False(admission.Duplicate);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var room = new NpgsqlCommand("""
                SELECT status,epoch,module_version,config_hash,package_hash FROM game.room WHERE room_id='room-1'
                """, connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(RedesignRoomStore.Preparing, reader.GetString(0));
                Assert.Equal(claim.Epoch, reader.GetInt64(1));
                Assert.Equal("1.0.0", reader.GetString(2));
                Assert.Equal("cfg-1", reader.GetString(3));
                Assert.Equal("pkg-1", reader.GetString(4));
            }
            await using (var pointers = new NpgsqlCommand("""
                SELECT binding_id,current_room_id,round_generation FROM live.binding
                WHERE binding_id=ANY(@ids) ORDER BY binding_id
                """, connection))
            {
                pointers.Parameters.AddWithValue("ids", new[] { b1.BindingId, b2.BindingId });
                await using var reader = await pointers.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("room-1", reader.GetString(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("room-1", reader.GetString(1));
                Assert.Equal(1L, reader.GetInt64(2));
            }
            await using (var participations = new NpgsqlCommand(
                "SELECT count(*) FROM game.participation WHERE room_id='room-1' AND state='Active'", connection))
                Assert.Equal(2L, Convert.ToInt64(await participations.ExecuteScalarAsync()));
            await using (var outbox = new NpgsqlCommand("""
                SELECT kind FROM infra.outbox WHERE effect_id='room-1/0/start'
                """, connection))
                Assert.Equal("round.started", await outbox.ExecuteScalarAsync());
        }

        // An exact operation retry replays the committed admission.
        var retry = await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            participants, "1.0.0", "cfg-1", "pkg-1", "start-op-1");
        Assert.True(retry.Duplicate);
        Assert.Equal(admission.RoundGeneration, retry.RoundGeneration);

        // The same operation with a different participant set conflicts.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => roomStore.StartRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-op-1"));

        // The ownership fence rejects a wrong instance or a moved epoch.
        var other = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-2", "gs-2", 30);
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => roomStore.StartRoundAsync(
            "tenant-a", "game-a", "room-2", "gs-1", other.Epoch,
            participants, "1.0.0", "cfg-1", "pkg-1", "start-op-2"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => roomStore.StartRoundAsync(
            "tenant-a", "game-a", "room-2", "gs-2", other.Epoch + 5,
            participants, "1.0.0", "cfg-1", "pkg-1", "start-op-3"));

        // A binding still holding a live participation is rejected visibly, as is an unknown binding.
        var third = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-3", "gs-1", 30);
        await Assert.ThrowsAsync<RoundAdmissionException>(() => roomStore.StartRoundAsync(
            "tenant-a", "game-a", "room-3", "gs-1", third.Epoch, participants,
            "1.0.0", "cfg-1", "pkg-1", "start-op-4"));
        await Assert.ThrowsAsync<RoundAdmissionException>(() => roomStore.StartRoundAsync(
            "tenant-a", "game-a", "room-3", "gs-1", third.Epoch,
            [new RoundParticipant("binding-none", "streamer-none", "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-op-5"));

        var status = await roomStore.GetRoundAsync("tenant-a", "game-a", "room-1");
        Assert.NotNull(status);
        Assert.Equal(RedesignRoomStore.Preparing, status!.Status);
        Assert.Equal(2, status.Participants.Count);
        Assert.All(status.Participants, participant => Assert.Equal("Active", participant.State));
        Assert.Null(await roomStore.GetRoundAsync("tenant-a", "game-a", "room-none"));
    }

    [Fact]
    public async Task AbortReleasesPointersOwnershipAndAdmitsTheNextGeneration()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);

        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-p1", "room-open-p1");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-op-1");

        var aborted = await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            "host-left", "abort-op-1");
        Assert.Equal(RedesignRoomStore.Aborted, aborted.Status);
        Assert.Equal(claim.Epoch + 1, aborted.Epoch);
        Assert.False(aborted.Duplicate);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var room = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-1'", connection))
                Assert.Equal(RedesignRoomStore.Aborted, await room.ExecuteScalarAsync());
            await using (var participation = new NpgsqlCommand(
                "SELECT state FROM game.participation WHERE room_id='room-1'", connection))
                Assert.Equal("Closed", await participation.ExecuteScalarAsync());
            await using (var pointer = new NpgsqlCommand("""
                SELECT current_room_id,round_generation FROM live.binding WHERE binding_id=@binding
                """, connection))
            {
                pointer.Parameters.AddWithValue("binding", b1.BindingId);
                await using var reader = await pointer.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using (var ownership = new NpgsqlCommand("""
                SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'
                """, connection))
            await using (var reader = await ownership.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Released", reader.GetString(0));
                Assert.Equal(claim.Epoch + 1, reader.GetInt64(1));
            }
            await using (var outbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-1/0/abort'", connection))
                Assert.Equal("round.aborted", await outbox.ExecuteScalarAsync());
        }

        // Idempotent retry replays the release; a further abort under any operation is fenced
        // out by the released ownership (single release, architecture 6.6).
        var retry = await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            "host-left", "abort-op-1");
        Assert.True(retry.Duplicate);
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => roomStore.AbortRoundAsync(
            "tenant-a", "game-a", "room-1", "gs-1", claim.Epoch, "again", "abort-op-2"));

        // The next generation: a fresh claim admits the same mount into a new round.
        var next = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-2", "gs-2", 30);
        var second = await roomStore.StartRoundAsync("tenant-a", "game-a", "room-2", "gs-2", next.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-op-2");
        Assert.Equal(2, second.RoundGeneration);
        var status = await roomStore.GetRoundAsync("tenant-a", "game-a", "room-2");
        Assert.Equal(RedesignRoomStore.Preparing, status!.Status);
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
        private readonly string _name = "chorus_m2_rooms_" + Guid.NewGuid().ToString("N");
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
