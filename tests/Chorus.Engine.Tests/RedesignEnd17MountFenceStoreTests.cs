using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-17, the repeated-bring-up fence (architecture 6.2): a new mount generation is
/// committed only after the previous round terminated. A re-mount while the live session still
/// holds a live round participation is refused, so a streamer already in a room can never build
/// a second room behind a fresh binding; once the round releases, the mount is allowed again.
/// </summary>
public sealed class RedesignEnd17MountFenceStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";

    [Fact]
    public async Task AMountIsRefusedWhileItsLiveSessionParticipatesInARound()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);

        var (live, session, mount) = await MountAsync(identityStore, bindingStore, "end17-open-a", "live-key-a");
        var claim = await control.ClaimOwnershipAsync(Tenant, Game, "room", "room-end17-a", "gs-1", 60);
        await roomStore.StartRoundAsync(Tenant, Game, "room-end17-a", "gs-1", claim.Epoch,
            [new RoundParticipant(mount.BindingId, mount.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");

        // The streamer is now inside a live round: a re-mount must be refused, not silently
        // committed as a fresh generation that the match fences would not see (binding-scoped).
        await Assert.ThrowsAsync<MountInLiveRoundException>(() => bindingStore.StartGameplayAsync(
            Tenant, live, session, Game, "mount-2"));

        // The original binding is untouched and still points at the live round; no second
        // binding was committed, so the room the streamer is in stays its only one.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var active = new NpgsqlCommand("""
                SELECT count(*) FROM live.binding WHERE live_session_id=@live
                  AND state IN ('Starting','Ready','Running','AwaitingPlatformClose')
                """, connection);
            active.Parameters.AddWithValue("live", live);
            Assert.Equal(1L, Convert.ToInt64(await active.ExecuteScalarAsync()));
            await using var pointer = new NpgsqlCommand("""
                SELECT state,current_room_id FROM live.binding WHERE binding_id=@binding
                """, connection);
            pointer.Parameters.AddWithValue("binding", mount.BindingId);
            await using var reader = await pointer.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Ready", reader.GetString(0));
            Assert.Equal("room-end17-a", reader.GetString(1));
        }
    }

    [Fact]
    public async Task AMountIsAllowedAgainAfterTheRoundReleases()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);

        var (live, session, mount) = await MountAsync(identityStore, bindingStore, "end17-open-b", "live-key-b");
        var claim = await control.ClaimOwnershipAsync(Tenant, Game, "room", "room-end17-b", "gs-1", 60);
        await roomStore.StartRoundAsync(Tenant, Game, "room-end17-b", "gs-1", claim.Epoch,
            [new RoundParticipant(mount.BindingId, mount.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");

        // The round terminates: the participation closes and the mount pointer clears, so the
        // live session is no longer in any round and a fresh mount generation is allowed again.
        await roomStore.AbortRoundAsync(Tenant, Game, "room-end17-b", "gs-1", claim.Epoch,
            "end17-round-over", "abort-1");

        var remounted = await bindingStore.StartGameplayAsync(Tenant, live, session, Game, "mount-2");
        Assert.False(remounted.Duplicate);
        Assert.Equal(2, remounted.Generation);
        Assert.NotEqual(mount.BindingId, remounted.BindingId);

        // The old binding is closed; exactly the fresh one stays active on the slot.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var active = new NpgsqlCommand("""
                SELECT count(*) FROM live.binding WHERE live_session_id=@live
                  AND state IN ('Starting','Ready','Running','AwaitingPlatformClose')
                """, connection);
            active.Parameters.AddWithValue("live", live);
            Assert.Equal(1L, Convert.ToInt64(await active.ExecuteScalarAsync()));
            await using var old = new NpgsqlCommand("""
                SELECT state FROM live.binding WHERE binding_id=@binding
                """, connection);
            old.Parameters.AddWithValue("binding", mount.BindingId);
            Assert.Equal("Closed", Convert.ToString(await old.ExecuteScalarAsync()));
        }
    }

    private static async Task<(string Live, string Session, Mounted Mount)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string openId, string liveKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync(Tenant, "simulator", "app-1", openId);
        var live = await bindingStore.OpenLiveSessionAsync(Tenant, streamer.StreamerId, "simulator",
            "app-1", "platform-room", liveKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var mount = await bindingStore.StartGameplayAsync(Tenant, live, session.SessionId, Game, "mount-1");
        return (live, session.SessionId, new Mounted(mount.BindingId, streamer.StreamerId));
    }

    private sealed record Mounted(string BindingId, string StreamerId);

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_end17_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
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
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
