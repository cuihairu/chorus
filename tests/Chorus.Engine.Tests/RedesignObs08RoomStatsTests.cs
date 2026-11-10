using Chorus.Control;
using Chorus.Data;
using Chorus.Engine.Routing;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Room family gauges (FIX-OBS-08): total/active/by_state come from the control plane's room
/// records with the live rule (a destroyed room stops counting), the state histogram carries
/// only the states actually present, and the tenant dimension reads the data plane's
/// game.room rows with the same non-terminal filter the admission counter uses. A
/// non-PostgreSQL control database answers the tenant dimension empty — an absent dimension
/// is a stat, not an error.
/// </summary>
public sealed class RedesignObs08RoomStatsTests
{
    private static RoomRecord Room(string id, RoomLifecycleState state) =>
        new(id, "worker-1", "worker-i1", 1, state, 1_000);

    private static string NewSqliteDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chorus_obs08_" + Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "control.db");
    }

    private static void SeedRoom(ControlDatabase database, string id, RoomLifecycleState state) =>
        database.Write(t => { t.Put("room", id, Room(id, state)); return true; });

    [Fact]
    public void ControlPlaneAnswersTotalActiveAndThePresentStateHistogram()
    {
        var path = NewSqliteDatabase();
        var database = new ControlDatabase(path);
        SeedRoom(database, "r-active-1", RoomLifecycleState.Active);
        SeedRoom(database, "r-active-2", RoomLifecycleState.Active);
        SeedRoom(database, "r-waiting", RoomLifecycleState.Waiting);
        SeedRoom(database, "r-migrating", RoomLifecycleState.Migrating);

        var stats = new RoomStatsStore(path, ControlDatabaseProvider.Sqlite, database).Stats();

        Assert.Equal(4, stats.Total);
        Assert.Equal(2, stats.Active);
        Assert.Equal(4, stats.ByState.Values.Sum()); // every live room lands in exactly one state
        Assert.Equal(2, stats.ByState[nameof(RoomLifecycleState.Active)]);
        Assert.Equal(1, stats.ByState[nameof(RoomLifecycleState.Waiting)]);
        Assert.Equal(1, stats.ByState[nameof(RoomLifecycleState.Migrating)]);
        Assert.False(stats.ByState.ContainsKey(nameof(RoomLifecycleState.Destroyed))); // absent states stay absent
    }

    [Fact]
    public void ADestroyedRoomStopsCounting()
    {
        var path = NewSqliteDatabase();
        var database = new ControlDatabase(path);
        SeedRoom(database, "r-live", RoomLifecycleState.Active);
        SeedRoom(database, "r-dead", RoomLifecycleState.Destroyed);

        var stats = new RoomStatsStore(path, ControlDatabaseProvider.Sqlite, database).Stats();

        Assert.Equal(1, stats.Total);
        Assert.False(stats.ByState.ContainsKey(nameof(RoomLifecycleState.Destroyed)));
    }

    [Fact]
    public void AnEmptyClusterAnswersZeroesWithoutATenantDimension()
    {
        var path = NewSqliteDatabase();
        var database = new ControlDatabase(path);

        var stats = new RoomStatsStore(path, ControlDatabaseProvider.Sqlite, database).Stats();

        Assert.Equal(0, stats.Total);
        Assert.Equal(0, stats.Active);
        Assert.Empty(stats.ByState);
        Assert.Empty(stats.ByTenant); // Sqlite has no game.room: the dimension is absent, not zero
    }

    [Fact]
    public async Task TenantDimensionCountsNonTerminalDataPlaneRoomsPerTenant()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var database = new ControlDatabase(temp.ConnectionString, ControlDatabaseProvider.PostgreSql);
        SeedRoom(database, "r-control-1", RoomLifecycleState.Active);
        SeedRoom(database, "r-control-2", RoomLifecycleState.Waiting);
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var (room, tenant, status) in new[]
            {
                ("g-1", "tenant-a", "Running"), ("g-2", "tenant-a", "Preparing"),
                ("g-3", "tenant-b", "Running"), ("g-4", "tenant-a", "Settled"),
                ("g-5", "tenant-b", "Aborted"), ("g-6", "tenant-c", "Closed"),
            })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO game.room(room_id, tenant_id, game_id, status, epoch,
                        package_hash, module_version, config_hash)
                    VALUES (@room, @tenant, 'game-1', @status, 1, 'p', 'm', 'c')
                    """, connection);
                insert.Parameters.AddWithValue("room", room);
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("status", status);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var stats = new RoomStatsStore(temp.ConnectionString, ControlDatabaseProvider.PostgreSql, database).Stats();

        // Only the non-terminal statuses count; tenants come back sorted.
        Assert.Equal([new TenantRooms("tenant-a", 2), new TenantRooms("tenant-b", 1)], stats.ByTenant);
        Assert.Equal(2, stats.Total); // the control plane side is independent of game.room rows
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs08_room_stats_" + Guid.NewGuid().ToString("N");
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
