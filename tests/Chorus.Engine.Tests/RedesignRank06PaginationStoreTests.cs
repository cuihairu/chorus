using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-06, the keyset pagination read (developer-capabilities.md「稳定分页」「分页实现可
/// 返回有期限的快照版本/游标」): a page resumes strictly after the previous page's last
/// entry's keyset in the board's declared order (score descending, subject id ascending), so a
/// walk never overlaps or skips an entry; one row past the page bound is read to know whether
/// more remain; the page and the read version are one snapshot.
/// </summary>
public sealed class RedesignRank06PaginationStoreTests
{
    private static LeaderboardBoard DefaultBoard(string tenant = "tenant-a", string game = "game-1",
        string board = "board-1") => LeaderboardBoard.Default(tenant, game, board);

    [Fact]
    public async Task AKeysetWalkCoversEveryEntryExactlyOnce()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        foreach (var (id, score) in new[] { ("a", 10L), ("b", 8L), ("c", 6L), ("d", 4L), ("e", 2L) })
            await AccumulateAsync(database, store, board, id, score);

        // Walk page size 2: a,b → c,d → e. Never overlaps, never skips, has-more is exact.
        var page1 = await store.ReadTopPageAsync(board, 2, null, null);
        Assert.Equal(new[] { "a", "b" }, page1.Entries.Select(e => e.SubjectId));
        Assert.True(page1.HasMore);
        var page2 = await store.ReadTopPageAsync(board, 2, page1.Entries[^1].Score, page1.Entries[^1].SubjectId);
        Assert.Equal(new[] { "c", "d" }, page2.Entries.Select(e => e.SubjectId));
        Assert.True(page2.HasMore);
        var page3 = await store.ReadTopPageAsync(board, 2, page2.Entries[^1].Score, page2.Entries[^1].SubjectId);
        Assert.Equal(new[] { "e" }, page3.Entries.Select(e => e.SubjectId));
        Assert.False(page3.HasMore);

        // Every page answered the same snapshot (no writes between pages).
        Assert.Equal(page1.ReadVersion, page2.ReadVersion);
        Assert.Equal(page1.ReadVersion, page3.ReadVersion);

        // The walk agrees with the single read that answers the whole board at once.
        var all = await store.ReadTopWithVersionAsync(board, 100);
        Assert.Equal(page1.ReadVersion, all.ReadVersion);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, all.Entries.Select(e => e.SubjectId));
    }

    [Fact]
    public async Task AContinuationResumesAfterTiedKeysetsWithoutOverlapOrSkip()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        // Three entries tie at 5: the keyset tiebreak is subject id ascending, so the walk
        // crosses the tie deterministically and never repeats or drops a tied entry.
        foreach (var (id, score) in new[] { ("m", 5L), ("n", 5L), ("p", 5L), ("q", 3L) })
            await AccumulateAsync(database, store, board, id, score);

        var page1 = await store.ReadTopPageAsync(board, 2, null, null);
        Assert.Equal(new[] { "m", "n" }, page1.Entries.Select(e => e.SubjectId));
        Assert.True(page1.HasMore);
        var page2 = await store.ReadTopPageAsync(board, 2, page1.Entries[^1].Score, page1.Entries[^1].SubjectId);
        Assert.Equal(new[] { "p", "q" }, page2.Entries.Select(e => e.SubjectId));
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task HasMoreIsFalseWhenThePageExactlyReachesTheBoardEnd()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        foreach (var (id, score) in new[] { ("a", 10L), ("b", 8L), ("c", 6L) })
            await AccumulateAsync(database, store, board, id, score);

        // A page as large as the board is the last page.
        var exact = await store.ReadTopPageAsync(board, 3, null, null);
        Assert.Equal(3, exact.Entries.Count);
        Assert.False(exact.HasMore);
        // A page smaller than the board is not the last page.
        var partial = await store.ReadTopPageAsync(board, 2, null, null);
        Assert.Equal(2, partial.Entries.Count);
        Assert.True(partial.HasMore);
    }

    [Fact]
    public async Task AnEmptyBoardAnswersAnEmptyPageWithoutMore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        var page = await store.ReadTopPageAsync(board, 10, null, null);
        Assert.Empty(page.Entries);
        Assert.False(page.HasMore);
        Assert.Equal(DateTimeOffset.MinValue, page.ReadVersion);
    }

    [Fact]
    public async Task APageOrKeysetOutsideTheContractIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await AccumulateAsync(database, store, board, "a", 5);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ReadTopPageAsync(board, 0, null, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ReadTopPageAsync(board, LeaderboardQueryLimits.MaxTopEntries + 1, null, null));
        // A continuation keyset must be both score and subject, or neither.
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ReadTopPageAsync(board, 10, 5, null));
    }

    [Fact]
    public async Task TheVersionTracksBoardWritesAcrossTheWalk()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await AccumulateAsync(database, store, board, "a", 10);
        await AccumulateAsync(database, store, board, "b", 8);
        var page1 = await store.ReadTopPageAsync(board, 1, null, null);
        // A write to the board (a new entry) changes the snapshot version — the data plane
        // turns this into a stale read on continuation (the module/plane contract tests).
        await AccumulateAsync(database, store, board, "c", 6);
        var pageAfterWrite = await store.ReadTopPageAsync(board, 1, null, null);
        Assert.NotEqual(page1.ReadVersion, pageAfterWrite.ReadVersion);
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
        private readonly string _name = "chorus_rank06_" + Guid.NewGuid().ToString("N");
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
