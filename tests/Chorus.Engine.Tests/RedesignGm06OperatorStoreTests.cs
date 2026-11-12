using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The operator store and the room-epoch read (FIX-GM-06). An operator identity is resolved
/// only from a registered, enabled bearer token; a disabled operator and an unknown token both
/// resolve to nothing, so a process certificate can never stand in for an operator. The epoch
/// read treats the ownership row as the authoritative fence and falls back to the room row for
/// a room with no ownership yet.
/// </summary>
public sealed class RedesignGm06OperatorStoreTests
{
    [Fact]
    public async Task ARegisteredOperatorResolvesFromItsTokenWithRoleAndScope()
    {
        await using var db = await Database.CreateAsync();
        var store = new RedesignOperatorStore(db.ConnectionString);

        await store.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "secret-1");

        var identity = await store.ResolveOperatorAsync("secret-1");
        Assert.NotNull(identity);
        Assert.Equal("gm-alice", identity.OperatorId);
        Assert.Equal("gm", identity.Role);
        Assert.Equal(["tenant-a"], identity.Tenants);
        Assert.Equal(["game-a"], identity.Games);
    }

    [Fact]
    public async Task AnUnknownOrWrongTokenResolvesToNothing()
    {
        await using var db = await Database.CreateAsync();
        var store = new RedesignOperatorStore(db.ConnectionString);
        await store.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "secret-1");

        Assert.Null(await store.ResolveOperatorAsync("wrong-token"));
        Assert.Null(await store.ResolveOperatorAsync(""));
        Assert.Null(await store.ResolveOperatorAsync(null!));
    }

    [Fact]
    public async Task ADisabledOperatorNoLongerAuthenticates()
    {
        await using var db = await Database.CreateAsync();
        var store = new RedesignOperatorStore(db.ConnectionString);
        await store.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "secret-1");

        Assert.True(await store.SetOperatorEnabledAsync("gm-alice", false));
        Assert.Null(await store.ResolveOperatorAsync("secret-1"));

        // Re-enabling restores the credential.
        Assert.True(await store.SetOperatorEnabledAsync("gm-alice", true));
        Assert.NotNull(await store.ResolveOperatorAsync("secret-1"));
    }

    [Fact]
    public async Task ReRegisteringRotatesTheCredentialAndScope()
    {
        await using var db = await Database.CreateAsync();
        var store = new RedesignOperatorStore(db.ConnectionString);
        await store.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "old-token");

        await store.RegisterOperatorAsync("gm-alice", "super", [], [], "new-token");

        Assert.Null(await store.ResolveOperatorAsync("old-token"));
        var rotated = await store.ResolveOperatorAsync("new-token");
        Assert.NotNull(rotated);
        Assert.Equal("super", rotated.Role);
        Assert.Empty(rotated.Tenants);
        Assert.Empty(rotated.Games);
    }

    [Fact]
    public async Task GetOperatorReturnsTheIdentityOrNothing()
    {
        await using var db = await Database.CreateAsync();
        var store = new RedesignOperatorStore(db.ConnectionString);
        await store.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "secret-1");

        Assert.NotNull(await store.GetOperatorAsync("gm-alice"));
        Assert.Null(await store.GetOperatorAsync("nobody"));
    }

    [Fact]
    public async Task GetRoomEpochReadsTheOwnershipFenceAndFallsBackToTheRoom()
    {
        await using var db = await Database.CreateAsync();
        var control = new RedesignControlStore(db.ConnectionString);
        await db.SeedRoomAsync("room-1", "tenant-a", "game-a", "gs-1", 1);
        await db.SeedRoomRowOnlyAsync("room-2", "tenant-a", "game-a", "Running", 2);

        // The ownership row is the authority: its epoch, owner and status.
        var byOwnership = await control.GetRoomEpochAsync("tenant-a", "game-a", "room-1");
        Assert.True(byOwnership.Found);
        Assert.Equal(1, byOwnership.Epoch);
        Assert.Equal("gs-1", byOwnership.OwnerInstanceId);
        Assert.Equal("Active", byOwnership.Status);

        // A room with no ownership row falls back to the room row's own epoch.
        var byRoom = await control.GetRoomEpochAsync("tenant-a", "game-a", "room-2");
        Assert.True(byRoom.Found);
        Assert.Equal(2, byRoom.Epoch);
        Assert.Null(byRoom.OwnerInstanceId);
        Assert.Equal("Running", byRoom.Status);

        // An unknown room is not found.
        Assert.False((await control.GetRoomEpochAsync("tenant-a", "game-a", "room-ghost")).Found);
    }

    /// <summary>A temporary migrated database with one claimed room (ownership Active epoch 1,
    /// owner gs-1) and one room row with no ownership (epoch 2).</summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly string _name = "chorus_gm06_" + Guid.NewGuid().ToString("N");
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
        }

        public async Task SeedRoomRowOnlyAsync(string roomId, string tenantId, string gameId,
            string status, long epoch)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var room = new NpgsqlCommand("""
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,
                    package_hash,module_version,config_hash)
                VALUES(@room,@tenant,@game,@status,@epoch,'pkg-1','1.0.0','cfg-1')
                """, connection);
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            room.Parameters.AddWithValue("status", status);
            room.Parameters.AddWithValue("epoch", epoch);
            await room.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
