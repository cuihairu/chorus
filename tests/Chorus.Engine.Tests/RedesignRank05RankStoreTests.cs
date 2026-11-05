using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-05, the personal and around-me rank reads (developer-capabilities.md「查询」「个人
/// 名次、附近名次」「实时变化榜的个人名次必须对应同一次读取视图」): one subject's unique ordinal
/// rank in the board's declared order, and the bounded window of entries around that rank —
/// both answered at one read view (rank, score and read version are one consistent snapshot).
/// </summary>
public sealed class RedesignRank05RankStoreTests
{
    private static LeaderboardBoard DefaultBoard(string tenant = "tenant-a", string game = "game-1",
        string board = "board-1") => LeaderboardBoard.Default(tenant, game, board);

    [Fact]
    public async Task TheRankIsTheUniqueOrdinalInBoardOrder()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        // score DESC, subject_id ASC: a(5)=1, c(5)=2, b(3)=3.
        await AccumulateAsync(database, store, board, "a", 5);
        await AccumulateAsync(database, store, board, "b", 3);
        await AccumulateAsync(database, store, board, "c", 5);

        var a = await store.ReadRankAsync(board, "a");
        var b = await store.ReadRankAsync(board, "b");
        var c = await store.ReadRankAsync(board, "c");
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(c);
        Assert.Equal(1, a!.Rank);
        Assert.Equal(2, c!.Rank);
        Assert.Equal(3, b!.Rank);
        Assert.Equal(5, a.Entry.Score);
        Assert.Equal(3, b.Entry.Score);
        Assert.All(new[] { a, b, c }, r => Assert.Equal(RankSubject.Player, r!.Entry.Subject));
        // The read version is a real board write marker at the same view.
        Assert.NotEqual(DateTimeOffset.MinValue, a.ReadVersion);

        // The rank agrees with the RANK-01 ordinal assignment over the whole board.
        var all = await store.ReadTopAsync(board, 100);
        var ordinal = LeaderboardOrdering.OrdinalRanks(all);
        Assert.Equal(a.Rank, ordinal.Single(o => o.Entry.SubjectId == "a").Rank);
        Assert.Equal(b.Rank, ordinal.Single(o => o.Entry.SubjectId == "b").Rank);
        Assert.Equal(c.Rank, ordinal.Single(o => o.Entry.SubjectId == "c").Rank);
    }

    [Fact]
    public async Task AnAbsentSubjectHasNoRankAndNoAround()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await AccumulateAsync(database, store, board, "a", 5);
        Assert.Null(await store.ReadRankAsync(board, "absent"));
        Assert.Null(await store.ReadAroundAsync(board, "absent", 2));
    }

    [Fact]
    public async Task TheAroundWindowCentresOnTheSubjectsRank()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        // order: a(10)=1, b(8)=2, c(6)=3, d(4)=4, e(2)=5.
        foreach (var (id, score) in new[] { ("a", 10L), ("b", 8L), ("c", 6L), ("d", 4L), ("e", 2L) })
            await AccumulateAsync(database, store, board, id, score);

        // Around c (rank 3), window 2: ranks 1..5 = a,b,c,d,e (the whole board, bounded).
        var aroundC = await store.ReadAroundAsync(board, "c", 2);
        Assert.NotNull(aroundC);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, aroundC!.Entries.Select(e => e.Rank));
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, aroundC.Entries.Select(e => e.Entry.SubjectId));
        Assert.NotEqual(DateTimeOffset.MinValue, aroundC.ReadVersion);

        // Around b (rank 2), window 1: ranks 1..3 = a,b,c — the window clamps at the board edge.
        var aroundB = await store.ReadAroundAsync(board, "b", 1);
        Assert.NotNull(aroundB);
        Assert.Equal(new[] { 1, 2, 3 }, aroundB!.Entries.Select(e => e.Rank));

        // Around a (rank 1), window 1: ranks 1..2 = a,b (no rank below 1).
        var aroundA = await store.ReadAroundAsync(board, "a", 1);
        Assert.NotNull(aroundA);
        Assert.Equal(new[] { 1, 2 }, aroundA!.Entries.Select(e => e.Rank));
        Assert.Equal(new[] { "a", "b" }, aroundA.Entries.Select(e => e.Entry.SubjectId));
    }

    [Fact]
    public async Task TheRankAndAroundAreIsolatedPerBoardAndSubject()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        var otherBoard = DefaultBoard(board: "board-2");
        await AccumulateAsync(database, store, board, "a", 5);
        await AccumulateAsync(database, store, board, "b", 3);

        // Another board's entries never change this board's rank or around window.
        await AccumulateAsync(database, store, otherBoard, "x", 9);
        var a = await store.ReadRankAsync(board, "a");
        Assert.NotNull(a);
        Assert.Equal(1, a!.Rank);
        var around = await store.ReadAroundAsync(board, "a", 1);
        Assert.NotNull(around);
        Assert.Equal(new[] { 1, 2 }, around!.Entries.Select(e => e.Rank));

        // The same subject id on another board is a different position.
        var other = await store.ReadRankAsync(otherBoard, "a");
        Assert.Null(other);
    }

    [Fact]
    public async Task AnAroundWindowOutsideTheBoundIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await AccumulateAsync(database, store, board, "a", 5);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ReadAroundAsync(board, "a", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ReadAroundAsync(board, "a", LeaderboardQueryLimits.MaxAroundWindow + 1));
    }

    [Fact]
    public async Task TiesGetDistinctConsecutiveRanks()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        // Two entries tie at 5: the smaller subject id ranks first, the second is next —
        // never a shared rank (RANK-01: ranks are unique, gapless, ordinal).
        await AccumulateAsync(database, store, board, "m", 5);
        await AccumulateAsync(database, store, board, "n", 5);
        await AccumulateAsync(database, store, board, "p", 5);

        var m = await store.ReadRankAsync(board, "m");
        var n = await store.ReadRankAsync(board, "n");
        var p = await store.ReadRankAsync(board, "p");
        Assert.Equal(1, m!.Rank);
        Assert.Equal(2, n!.Rank);
        Assert.Equal(3, p!.Rank);
    }

    private static async Task AccumulateAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store, LeaderboardBoard board, string subjectId, long delta)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.AccumulateAsync(board, subjectId, delta, connection, transaction);
        await transaction.CommitAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_rank05_" + Guid.NewGuid().ToString("N");
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
