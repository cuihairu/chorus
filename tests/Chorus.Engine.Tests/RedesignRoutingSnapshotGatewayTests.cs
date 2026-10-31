using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-02, routing half: the versioned snapshot the webhook face consumes carries the
/// canonical live session key of every mount (additive platform + canonical_live_session_key
/// on the streamer rows), so the gateway can resolve a platform callback's live key to the room
/// it belongs to, and that room's owner instance, epoch and serving endpoint — without ever
/// re-deriving the binding from the data plane. The snapshot is the gateway's only route source.
/// </summary>
public sealed class RedesignRoutingSnapshotGatewayTests
{
    [Fact]
    public async Task TheSnapshotCarriesLiveKeyToRoomToOwnerChain()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);

        var (host, binding, _) = await MountAsync(identityStore, bindingStore, "live-key-gateway");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-gateway-1",
            "gs-route", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-gateway-1", "gs-route", claim.Epoch,
            [new RoundParticipant(binding.BindingId, host.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-gateway-1");

        var snapshot = await control.ReadRoutingSnapshotAsync();

        // The mount's route is the gateway's whole key: the live key names the room, the room
        // names the owner instance and epoch — the facts a direct push must carry.
        var streamer = Assert.Single(snapshot.Streamers);
        Assert.Equal("simulator", streamer.Platform);
        Assert.Equal("live-key-gateway", streamer.CanonicalLiveSessionKey);
        Assert.Equal(binding.BindingId, streamer.BindingId);
        Assert.Equal("room-gateway-1", streamer.RoomId);
        var room = Assert.Single(snapshot.Rooms, route => route.RoomId == "room-gateway-1");
        Assert.Equal("tenant-a", room.TenantId);
        Assert.Equal("gs-route", room.OwnerInstanceId);
        Assert.Equal(claim.Epoch, room.Epoch);
        // A room whose owner never reports an endpoint has an empty endpoint — the gateway
        // cannot push to it and must answer the callback with a durable wait.
        Assert.Null(room.Endpoint);
    }

    private static async Task<(ResolvedStreamer Streamer, BindingGrant Binding, SessionGrant Session)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
            "route-open-" + canonicalKey);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator", "app-1",
            "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        return (streamer, binding, session);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_route_gw_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
