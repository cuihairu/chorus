using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-07, the period-bucketed store (developer-capabilities.md 「周期」「换期旧榜可查」):
/// the leaderboard entry key already carries the period id, so two daily buckets of one board
/// are two separate boards — one day's scores never bleed into another day's board, and writing
/// a new bucket never changes an earlier bucket's entries or its snapshot version. An old
/// period stays queryable after a later period is written (switching periods does not erase
/// old boards).
/// </summary>
public sealed class RedesignRank07PeriodStoreTests
{
    private const string BoardId = "faction-score-daily";

    private static LeaderboardBoard DailyBoard(DateTimeOffset inBucket)
    {
        var period = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", inBucket);
        return new LeaderboardBoard("tenant-a", "game-1", BoardId, RankSubject.Faction,
            RankAggregation.Sum, ScoreBounds.Default, period, new RankScope("room-1"));
    }

    [Fact]
    public async Task TwoDailyBucketsAreSeparateRowsAndTheOldBucketStaysQueryable()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var day1 = DailyBoard(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var day2 = DailyBoard(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

        await AccumulateAsync(database, store, day1, "red", 5);
        await AccumulateAsync(database, store, day1, "blue", 3);
        // The next day's scores land on a different bucket — never the same rows.
        await AccumulateAsync(database, store, day2, "red", 9);

        var old = await store.ReadTopWithVersionAsync(day1, 100);
        Assert.Equal(new[] { "red", "blue" }, old.Entries.Select(e => e.SubjectId));
        Assert.Equal(5, old.Entries[0].Score);
        var current = await store.ReadTopWithVersionAsync(day2, 100);
        Assert.Equal(new[] { "red" }, current.Entries.Select(e => e.SubjectId));
        Assert.Equal(9, current.Entries[0].Score);
        // Two buckets are two boards: distinct periods, distinct versions.
        Assert.NotEqual(old.ReadVersion, current.ReadVersion);
    }

    [Fact]
    public async Task AWriteToOneBucketDoesNotChangeAnotherBucketsVersion()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var day1 = DailyBoard(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var day2 = DailyBoard(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        await AccumulateAsync(database, store, day1, "red", 5);
        var before = await store.ReadTopWithVersionAsync(day1, 100);

        await AccumulateAsync(database, store, day2, "red", 9);
        var after = await store.ReadTopWithVersionAsync(day1, 100);
        Assert.Equal(before.ReadVersion, after.ReadVersion);
        Assert.Equal(new[] { "red" }, after.Entries.Select(e => e.SubjectId));
        Assert.Equal(5, after.Entries[0].Score);

        var day2Read = await store.ReadTopWithVersionAsync(day2, 100);
        Assert.NotEqual(before.ReadVersion, day2Read.ReadVersion);
    }

    [Fact]
    public async Task TheOldBucketAnswersTheBoardAfterTheNewBucketIsWritten()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var day1 = DailyBoard(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var day2 = DailyBoard(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        await AccumulateAsync(database, store, day1, "red", 5);
        await AccumulateAsync(database, store, day2, "blue", 7);

        // The old period is still readable after a later period exists — switching periods
        // never erases an old board.
        var old = await store.ReadTopWithVersionAsync(day1, 100);
        Assert.Equal("daily-2026-11-05", day1.Period.PeriodId);
        Assert.Equal(new[] { "red" }, old.Entries.Select(e => e.SubjectId));
    }

    [Fact]
    public async Task TheCommittedEffectAnswersItsImmutableSourceTime()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var committedAt = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id,kind,payload,status,committed_at)
                VALUES (@effect,'score',@payload::jsonb,'Pending',@at)
                """, connection);
            insert.Parameters.AddWithValue("effect", "room-1/1/0");
            insert.Parameters.AddWithValue("payload", """{"faction":"red","delta":7,"total":7}""");
            insert.Parameters.AddWithValue("at", committedAt);
            await insert.ExecuteNonQueryAsync();
        }

        // The source read answers the same immutable source time every time — a replayed or
        // delayed delivery never re-stamps it.
        var first = await store.ReadCommittedEffectAsync("room-1/1/0");
        var second = await store.ReadCommittedEffectAsync("room-1/1/0");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(committedAt, first!.CommittedAt);
        Assert.Equal(committedAt, second!.CommittedAt);
        Assert.Equal("score", first.Kind);
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
        private readonly string _name = "chorus_rank07_" + Guid.NewGuid().ToString("N");
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
