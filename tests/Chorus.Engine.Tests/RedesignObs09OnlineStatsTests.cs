using System.Text.Json;
using Chorus.Control;
using Chorus.Data;
using Chorus.Messaging.Ipc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Online by dimension (FIX-OBS-09): the totals answer from the control plane's connection
/// records under the routing face's freshness rule (healthy gateway lease, matching gateway
/// instance, live connection lease) and count distinct users, not sessions; the per-dimension
/// splits join the data plane's game.room by room id for tenancy and read game.participation
/// for "rooms with at least one participant". A non-PostgreSQL control database answers the
/// splits empty while the totals keep answering.
/// </summary>
public sealed class RedesignObs09OnlineStatsTests
{
    private const string LiveGateway = "gw-1";

    private static NodeReport Gateway(string id, long heartbeat) =>
        new(id, "gw-inst-1", "machine-1", "gateway",
            new IpcNetworkEndpoint("127.0.0.1", 9000), 100, heartbeat);

    private static ConnectionRecord Connection(string session, string user, string role, long seen) =>
        new(session, "client-" + session, user, role, LiveGateway, "gw-inst-1", "gen-1", seen);

    private static string NewSqliteDatabase() =>
        Path.Combine(Path.GetTempPath(), "chorus_obs09_" + Guid.NewGuid().ToString("N"), "control.db");

    /// <summary>The clock the store reads is the system clock: fixtures place heartbeats and
    /// last-seens just behind the leases so freshness holds on any machine.</summary>
    private static long Now => TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

    private static void Seed(ControlDatabase database, params object[] records)
    {
        database.Write(t =>
        {
            foreach (var record in records)
            {
                switch (record)
                {
                    case NodeReport n: t.Put("node", n.Id, n); break;
                    case ConnectionRecord c: t.Put("connection", JsonSerializer.Serialize(new[] { c.SessionId, c.ClientId }), c); break;
                }
            }
            return true;
        });
    }

    [Fact]
    public void TotalsCountDistinctUsersUnderTheRoutingFreshnessRule()
    {
        var path = NewSqliteDatabase();
        var database = new ControlDatabase(path);
        var now = Now;
        Seed(database,
            Gateway(LiveGateway, now - 1_000),
            Connection("room-a", "player-1", "player", now - 1_000),
            Connection("room-b", "player-1", "player", now - 1_000), // same user, second session
            Connection("room-c", "player-2", "player", now - 1_000),
            Connection("room-a", "star-1", "streamer", now - 1_000),
            // Stale: dead gateway heartbeat, mismatched instance, expired lease.
            Gateway("gw-dead", now - RoomControlStore.NodeLeaseMs - 5_000),
            new ConnectionRecord("room-d", "client-room-d", "player-3", "player", "gw-dead", "gw-inst-1", "gen-1", now - 1_000),
            new ConnectionRecord("room-e", "client-room-e", "player-3", "player", LiveGateway, "gw-inst-2", "gen-1", now - 1_000),
            new ConnectionRecord("room-f", "client-room-f", "player-3", "player", LiveGateway, "gw-inst-1", "gen-1",
                now - RoomControlStore.ConnectionLeaseMs - 5_000));

        var stats = new OnlineStatsStore(path, ControlDatabaseProvider.Sqlite, database).Stats();

        Assert.Equal(2, stats.PlayersTotal); // player-1 once (two sessions), player-2 once; player-3 never fresh
        Assert.Equal(1, stats.StreamersTotal);
        Assert.Empty(stats.PlayersByTenantGame); // Sqlite has no game.room: splits absent, not zero
        Assert.Empty(stats.StreamersByTenant);
        Assert.Empty(stats.RoomsByTenantGame);
    }

    [Fact]
    public void AnEmptyClusterAnswersZeroTotalsWithoutSplits()
    {
        var path = NewSqliteDatabase();
        var database = new ControlDatabase(path);

        var stats = new OnlineStatsStore(path, ControlDatabaseProvider.Sqlite, database).Stats();

        Assert.Equal(0, stats.PlayersTotal);
        Assert.Equal(0, stats.StreamersTotal);
        Assert.Empty(stats.PlayersByTenantGame);
    }

    [Fact]
    public async Task SplitsJoinDataPlaneTenancyAndReadParticipation()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var database = new ControlDatabase(temp.ConnectionString, ControlDatabaseProvider.PostgreSql);
        var now = Now;
        Seed(database, Gateway(LiveGateway, now - 1_000),
            Connection("g-1", "player-1", "player", now - 1_000),
            Connection("g-2", "player-1", "player", now - 1_000), // same user, second session, same bucket
            Connection("g-3", "player-2", "player", now - 1_000),
            Connection("g-4", "star-1", "streamer", now - 1_000),
            Connection("g-6", "player-4", "player", now - 1_000),
            Connection("g-9", "player-3", "player", now - 1_000)); // room not prepared: no tenancy
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var (room, tenant, game, status) in new[]
            {
                ("g-1", "tenant-a", "chess", "Running"), ("g-2", "tenant-a", "chess", "Running"),
                ("g-3", "tenant-b", "go", "Running"), ("g-4", "tenant-a", "chess", "Preparing"),
                ("g-5", "tenant-a", "go", "Running"), ("g-6", "tenant-a", "chess", "Running"),
            })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO game.room(room_id, tenant_id, game_id, status, epoch,
                        package_hash, module_version, config_hash)
                    VALUES (@room, @tenant, @game, @status, 1, 'p', 'm', 'c')
                    """, connection);
                insert.Parameters.AddWithValue("room", room);
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("game", game);
                insert.Parameters.AddWithValue("status", status);
                await insert.ExecuteNonQueryAsync();
            }
            // Participation truth: g-1 and g-5 active (one (tenant-a,chess), one (tenant-a,go)).
            foreach (var (room, participant, state) in new[]
            {
                ("g-1", "p-1", "Active"), ("g-1", "p-2", "Offline"),
                ("g-5", "p-3", "Active"), ("g-3", "p-4", "Left"),
            })
            {
                await using var part = new NpgsqlCommand("""
                    INSERT INTO game.participation(room_id, participant_id, role, state)
                    VALUES (@room, @participant, 'player', @state)
                    """, connection);
                part.Parameters.AddWithValue("room", room);
                part.Parameters.AddWithValue("participant", participant);
                part.Parameters.AddWithValue("state", state);
                await part.ExecuteNonQueryAsync();
            }
        }

        var stats = new OnlineStatsStore(temp.ConnectionString, ControlDatabaseProvider.PostgreSql, database).Stats();

        Assert.Equal(4, stats.PlayersTotal); // player-1, player-2, player-3, player-4 (totals ignore tenancy)
        // tenant-a/chess: player-1 once (two sessions deduped) + player-4 = 2;
        // tenant-b/go: player-2; g-9 has no game.room row so player-3 splits nowhere.
        Assert.Equal(
            [new TenantGameCount("tenant-a", "chess", 2), new TenantGameCount("tenant-b", "go", 1)],
            stats.PlayersByTenantGame);
        Assert.Equal([new TenantCount("tenant-a", 1)], stats.StreamersByTenant);
        // "Rooms with at least one participant": only active participations count.
        Assert.Equal(
            [new TenantGameCount("tenant-a", "chess", 1), new TenantGameCount("tenant-a", "go", 1)],
            stats.RoomsByTenantGame);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs09_online_" + Guid.NewGuid().ToString("N");
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
