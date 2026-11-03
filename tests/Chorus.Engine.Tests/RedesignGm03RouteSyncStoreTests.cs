using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The route-change publication state (FIX-GM-03): the storage commit is one surface and the
/// platform gateway's confirmation of the routing version that carries the room's new epoch is
/// another. A room in the snapshot is Pending until a gateway confirms exactly the current
/// version (the epoch is part of the snapshot content hash, so the bump moves the version);
/// only then is it Synced. A room no longer in the snapshot is Removed.
/// </summary>
public sealed class RedesignGm03RouteSyncStoreTests
{
    [Fact]
    public async Task ARoomInTheSnapshotIsPendingUntilAGatewayConfirmsTheCurrentVersion()
    {
        await using var db = await Database.CreateAsync();
        await db.SeedRoomAsync("room-1", "tenant-a", "game-a", "gs-1", epoch: 1);
        var control = new RedesignControlStore(db.ConnectionString);

        // No gateway has confirmed the current version: the route change is not synced.
        Assert.Equal("Pending", await control.GetRoomRouteSyncStatusAsync("tenant-a", "game-a", "room-1"));

        // A gateway installs and confirms exactly the current version.
        var snapshot = await control.ReadRoutingSnapshotAsync();
        Assert.Contains(snapshot.Rooms, room => room.RoomId == "room-1" && room.Epoch == 1);
        await control.ConfirmRoutingVersionAsync("gw-1", snapshot.Version);

        Assert.Equal("Synced", await control.GetRoomRouteSyncStatusAsync("tenant-a", "game-a", "room-1"));
    }

    [Fact]
    public async Task ConfirmingADifferentVersionLeavesTheRoomPending()
    {
        await using var db = await Database.CreateAsync();
        await db.SeedRoomAsync("room-1", "tenant-a", "game-a", "gs-1", epoch: 1);
        var control = new RedesignControlStore(db.ConnectionString);

        // A gateway confirmed some other version (an older snapshot or a different fleet).
        await control.ConfirmRoutingVersionAsync("gw-1", version: 1);

        Assert.Equal("Pending", await control.GetRoomRouteSyncStatusAsync("tenant-a", "game-a", "room-1"));
    }

    [Fact]
    public async Task ARoomNotInTheSnapshotIsRemovedNotSynced()
    {
        await using var db = await Database.CreateAsync();
        // A room with no ownership and no room row never publishes a route.
        var control = new RedesignControlStore(db.ConnectionString);
        Assert.Equal("Removed", await control.GetRoomRouteSyncStatusAsync("tenant-a", "game-a", "room-ghost"));
    }

    /// <summary>A temporary migrated database with one running room (ownership Active + room
    /// Running, both epoch 1, owner gs-1) — the shape the routing snapshot publishes.</summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly string _name = "chorus_gm03_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin;
        public string ConnectionString { get; private set; } = "";

        public static async Task<Database> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
            var database = new Database(source);
            await database.InitializeAsync();
            return database;
        }

        private Database(string source) => _admin = new(source) { Pooling = false };

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

        public async Task SeedRoomAsync(string roomId, string tenantId, string gameId, string owner, long epoch)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand("""
                INSERT INTO control.ownership(kind,entity_id,owner_process_instance_id,epoch,
                    lease_until,status,version,tenant_id,game_id)
                VALUES('room',@room,@owner,@epoch,now()+interval '60 second','Active',1,@tenant,@game)
                """, connection);
            ownership.Parameters.AddWithValue("room", roomId);
            ownership.Parameters.AddWithValue("owner", owner);
            ownership.Parameters.AddWithValue("epoch", epoch);
            ownership.Parameters.AddWithValue("tenant", tenantId);
            ownership.Parameters.AddWithValue("game", gameId);
            await ownership.ExecuteNonQueryAsync();
            await using var room = new NpgsqlCommand("""
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,
                    package_hash,module_version,config_hash)
                VALUES(@room,@tenant,@game,'Running',@epoch,'pkg-1','1.0.0','cfg-1')
                """, connection);
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            room.Parameters.AddWithValue("epoch", epoch);
            await room.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
