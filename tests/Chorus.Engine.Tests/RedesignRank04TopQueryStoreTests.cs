using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-04, the TopN query (developer-capabilities.md「查询」「TopN、个人名次、附近名次；
/// 有限页大小；分页绑定榜单读取版本」): a bounded page in the board's declared order, answered
/// together with the board's read version at the same snapshot — a paginated continuation
/// carries the version back and a changed board is a stale read, never a cross-version page.
/// </summary>
public sealed class RedesignRank04TopQueryStoreTests
{
    private static LeaderboardBoard DefaultBoard(string tenant = "tenant-a", string game = "game-1",
        string board = "board-1") => LeaderboardBoard.Default(tenant, game, board);

    [Fact]
    public async Task AGetTopAnswersTheBoundedPageInDeclaredOrder()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await AccumulateAsync(database, store, board, "a", 5);
        await AccumulateAsync(database, store, board, "b", 3);
        await AccumulateAsync(database, store, board, "c", 5);

        // score DESC, subject_id ASC: 5(a), 5(c), 3(b). The bounded page takes the top two.
        var page = await store.ReadTopWithVersionAsync(board, 2);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(("a", 5L), (page.Entries[0].SubjectId, page.Entries[0].Score));
        Assert.Equal(("c", 5L), (page.Entries[1].SubjectId, page.Entries[1].Score));
        // Each entry is stamped with the board's single subject type.
        Assert.All(page.Entries, entry => Assert.Equal(RankSubject.Player, entry.Subject));
        // The version is a real board write marker, not the empty sentinel.
        Assert.NotEqual(DateTimeOffset.MinValue, page.ReadVersion);
    }

    [Fact]
    public async Task TheReadVersionIsStableAndChangesWithTheBoard()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();

        var empty = await store.ReadTopWithVersionAsync(board, 10);
        Assert.Empty(empty.Entries);
        Assert.Equal(DateTimeOffset.MinValue, empty.ReadVersion);

        // A first score makes the board version real; a repeat read sees the same version.
        await AccumulateAsync(database, store, board, "a", 5);
        var first = await store.ReadTopWithVersionAsync(board, 10);
        Assert.NotEqual(DateTimeOffset.MinValue, first.ReadVersion);
        Assert.NotEqual(empty.ReadVersion, first.ReadVersion);
        var stable = await store.ReadTopWithVersionAsync(board, 10);
        Assert.Equal(first.ReadVersion, stable.ReadVersion);

        // A second score (a new entry, and a score update on an existing one) moves it.
        await AccumulateAsync(database, store, board, "b", 3);
        var second = await store.ReadTopWithVersionAsync(board, 10);
        Assert.NotEqual(first.ReadVersion, second.ReadVersion);
        await AccumulateAsync(database, store, board, "a", 2);
        var third = await store.ReadTopWithVersionAsync(board, 10);
        Assert.NotEqual(second.ReadVersion, third.ReadVersion);
    }

    [Fact]
    public async Task TheVersionIsPerBoardNotAcrossBoards()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        var otherBoard = DefaultBoard(board: "board-2");

        await AccumulateAsync(database, store, board, "a", 5);
        var before = await store.ReadTopWithVersionAsync(board, 10);

        // A write to another board must not move this board's read version (cross-board leak).
        await AccumulateAsync(database, store, otherBoard, "x", 9);
        var after = await store.ReadTopWithVersionAsync(board, 10);
        Assert.Equal(before.ReadVersion, after.ReadVersion);
        Assert.Single(after.Entries);
        Assert.Equal("a", after.Entries[0].SubjectId);
    }

    [Fact]
    public async Task AnEmptyBoardAnswersAnEmptyPageAndTheVersionSentinel()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var result = await store.ReadTopWithVersionAsync(DefaultBoard(), 10);
        Assert.Empty(result.Entries);
        Assert.Equal(DateTimeOffset.MinValue, result.ReadVersion);
    }

    [Fact]
    public async Task APageSizeOutsideTheBoundIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadTopWithVersionAsync(board, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ReadTopWithVersionAsync(board, LeaderboardQueryLimits.MaxTopEntries + 1));
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
        private readonly string _name = "chorus_rank04_" + Guid.NewGuid().ToString("N");
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
