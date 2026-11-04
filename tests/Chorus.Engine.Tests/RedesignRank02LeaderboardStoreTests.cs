using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-02, the default persistent leaderboard provider (developer-capabilities.md
/// 「排行榜定义」): the migration is idempotent and a restarted store (or a re-applied
/// migration) never loses a committed score; the write accumulates under the board's declared
/// scoring rule and a refused delta never changes the projection; the read answers the board's
/// declared order and walks the scope-covering rank index, so entries of different tenants,
/// periods and scopes never bleed into one board's answer.
/// </summary>
public sealed class RedesignRank02LeaderboardStoreTests
{
    private static LeaderboardBoard DefaultBoard(string tenant = "tenant-a", string game = "game-1",
        string board = "board-1") => LeaderboardBoard.Default(tenant, game, board);

    [Fact]
    public async Task ACommittedScoreSurvivesANewStoreAndARemigration()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();

        await AccumulateAsync(database, store, board, "player-1", 5);
        await AccumulateAsync(database, store, board, "player-1", 7);

        // A restarted store (fresh connections) reads the committed score...
        var restarted = new RedesignLeaderboardStore(database.ConnectionString);
        var entry = await restarted.ReadEntryAsync(board, "player-1");
        Assert.NotNull(entry);
        Assert.Equal(12, entry.Score);
        Assert.Equal(RankSubject.Player, entry.Subject);

        // ...and a re-applied migration (repeated migration on a non-empty database) never
        // clears it — the table is created IF NOT EXISTS and a fresh Apply leaves data intact.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }
        entry = await restarted.ReadEntryAsync(board, "player-1");
        Assert.NotNull(entry);
        Assert.Equal(12, entry.Score);
    }

    [Fact]
    public async Task AccumulationSumsPerSubjectUnderTheBoardKey()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();

        Assert.Equal(5, await AccumulateAsync(database, store, board, "player-a", 5));
        Assert.Equal(3, await AccumulateAsync(database, store, board, "player-b", 3));
        Assert.Equal(9, await AccumulateAsync(database, store, board, "player-a", 4));

        var top = await store.ReadTopAsync(board, 10);
        Assert.Equal(["player-a", "player-b"], top.Select(e => e.SubjectId));
        Assert.Equal([9L, 3L], top.Select(e => e.Score));
        Assert.All(top, e => Assert.Equal(RankSubject.Player, e.Subject));
    }

    [Fact]
    public async Task TopNReadsScoreDescendingThenSubjectIdAscending()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();

        // Equal scores keep one stable order: subject id ascending, regardless of write order.
        await AccumulateAsync(database, store, board, "c", 5);
        await AccumulateAsync(database, store, board, "a", 5);
        await AccumulateAsync(database, store, board, "b", 5);
        await AccumulateAsync(database, store, board, "d", 5);
        await AccumulateAsync(database, store, board, "z", 1);

        var top = await store.ReadTopAsync(board, 10);
        Assert.Equal(["a", "b", "c", "d", "z"], top.Select(e => e.SubjectId));
        Assert.Equal([5, 5, 5, 5, 1], top.Select(e => e.Score));

        // A bounded page answers only that many entries, still in board order.
        var page = await store.ReadTopAsync(board, 3);
        Assert.Equal(["a", "b", "c"], page.Select(e => e.SubjectId));
    }

    [Fact]
    public async Task BoardsDifferingByAnyKeyComponentNeverShareRows()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        var otherTenant = DefaultBoard(tenant: "tenant-b");
        var otherPeriod = board with
        {
            Period = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero))
        };
        var otherScope = board with { Scope = new RankScope("room-9") };

        await AccumulateAsync(database, store, board, "player-1", 10);
        await AccumulateAsync(database, store, otherTenant, "player-1", 99);
        await AccumulateAsync(database, store, otherPeriod, "player-1", 77);
        await AccumulateAsync(database, store, otherScope, "player-1", 55);

        // Each board's answer carries only its own rows — no cross-tenant, cross-period or
        // cross-scope bleed, even with the same board id and subject id.
        Assert.Equal(10, (await store.ReadEntryAsync(board, "player-1"))!.Score);
        Assert.Equal(99, (await store.ReadEntryAsync(otherTenant, "player-1"))!.Score);
        Assert.Equal(77, (await store.ReadEntryAsync(otherPeriod, "player-1"))!.Score);
        Assert.Equal(55, (await store.ReadEntryAsync(otherScope, "player-1"))!.Score);
        Assert.Single(await store.ReadTopAsync(board, 10));
        Assert.Single(await store.ReadTopAsync(otherScope, 10));
    }

    [Fact]
    public async Task ARefusedAccumulationNeverChangesTheProjection()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var bounded = DefaultBoard() with { Bounds = new ScoreBounds(0, 100, AllowNegative: false) };

        // A delta outside the bounds is refused before it ever reaches the row.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => AccumulateAsync(database, store, bounded, "player-1", -5));
        Assert.Null(await store.ReadEntryAsync(bounded, "player-1"));

        Assert.Equal(60, await AccumulateAsync(database, store, bounded, "player-1", 60));
        // A sum past the declared maximum is refused, not clamped — the score stays 60.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AccumulateAsync(database, store, bounded, "player-1", 50));
        Assert.Equal(60, (await store.ReadEntryAsync(bounded, "player-1"))!.Score);

        // A Sum that would overflow the long range is refused, not wrapped.
        var unbounded = DefaultBoard();
        Assert.Equal(long.MaxValue, await AccumulateAsync(database, store, unbounded, "player-2", long.MaxValue));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AccumulateAsync(database, store, unbounded, "player-2", 1));
        Assert.Equal(long.MaxValue, (await store.ReadEntryAsync(unbounded, "player-2"))!.Score);
    }

    [Fact]
    public async Task MaxAggregationKeepsTheLargestScore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard() with { Aggregation = RankAggregation.Max };

        Assert.Equal(5, await AccumulateAsync(database, store, board, "player-1", 5));
        Assert.Equal(5, await AccumulateAsync(database, store, board, "player-1", 3));
        Assert.Equal(7, await AccumulateAsync(database, store, board, "player-1", 7));
        Assert.Equal(7, (await store.ReadEntryAsync(board, "player-1"))!.Score);
    }

    [Fact]
    public async Task TheTopNReadWalksTheScopeCoveringRankIndex()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        var other = DefaultBoard(tenant: "tenant-b");

        // Seed both boards so the planner sees real rows, then analyze.
        for (var i = 0; i < 50; i++)
        {
            await AccumulateAsync(database, store, board, "p-" + i.ToString("D3"), i);
            await AccumulateAsync(database, store, other, "q-" + i.ToString("D3"), i);
        }
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var analyze = new NpgsqlCommand("ANALYZE game.leaderboard_entry", connection))
                await analyze.ExecuteNonQueryAsync();
            // enable_seqscan off proves the rank index alone can serve the scope-range read —
            // exactly the "目标索引覆盖榜单作用域" acceptance — independent of planner
            // preferences on a small table.
            await using (var plan = new NpgsqlCommand("""
                SET enable_seqscan = off;
                EXPLAIN SELECT subject_id,score FROM game.leaderboard_entry
                WHERE tenant_id='tenant-a' AND game_id='game-1' AND board_id='board-1'
                  AND period_id='alltime' AND scope_id='' AND subject_type=0
                ORDER BY score DESC, subject_id ASC LIMIT 10
                """, connection))
            {
                using var reader = await plan.ExecuteReaderAsync();
                var text = new System.Text.StringBuilder();
                while (await reader.ReadAsync())
                    text.AppendLine(reader.GetString(0));
                Assert.Contains("ix_game_leaderboard_entry_rank", text.ToString());
                Assert.DoesNotContain("Seq Scan", text.ToString());
            }
        }
    }

    [Fact]
    public async Task TheReadBoundsTopNAndAnswersAnEmptyBoard()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();

        Assert.Empty(await store.ReadTopAsync(board, 10));
        Assert.Null(await store.ReadEntryAsync(board, "player-1"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadTopAsync(board, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadTopAsync(board, 1001));
    }

    private static async Task<long> AccumulateAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store, LeaderboardBoard board, string subjectId, long delta)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var score = await store.AccumulateAsync(board, subjectId, delta, connection, transaction);
        await transaction.CommitAsync();
        return score;
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_rank02_" + Guid.NewGuid().ToString("N");
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
