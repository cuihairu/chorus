using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-03, the idempotent effect projection (developer-capabilities.md 「Score changes
/// derive from immutable committed server effects」): a committed score effect applies exactly
/// once per immutable EffectId — a concurrent, replayed or lost-response retry never
/// double-scores; the same EffectId re-projected to a different board, subject or delta is a
/// visible conflict or rejection; and a score without a committed score-kind source (a client
/// self-report, an unrelated asset change, an inflated delta) is refused before any
/// projection write.
/// </summary>
public sealed class RedesignRank03EffectStoreTests
{
    private static LeaderboardBoard DefaultBoard(string tenant = "tenant-a", string game = "game-1",
        string board = "board-1") => LeaderboardBoard.Default(tenant, game, board);

    [Fact]
    public async Task ACommittedScoreEffectAppliesOnceAndReplaysAsDuplicate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await SeedEffectAsync(database, "room-1/1/0", "score", """{"faction":"red","delta":7,"total":7}""");

        var first = await ApplyAsync(database, store, board, "red", "room-1/1/0", 7);
        Assert.Equal(7, first.Score);
        Assert.False(first.Duplicate);
        Assert.Equal(7, (await store.ReadEntryAsync(board, "red"))!.Score);

        // A replay of the same effect is a duplicate no-op: the score stays 7.
        var replay = await ApplyAsync(database, store, board, "red", "room-1/1/0", 7);
        Assert.Equal(7, replay.Score);
        Assert.True(replay.Duplicate);
        Assert.Equal(7, (await store.ReadEntryAsync(board, "red"))!.Score);
    }

    [Fact]
    public async Task ConcurrentApplicationsOfOneEffectNeverDoubleScore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await SeedEffectAsync(database, "room-1/5/0", "score", """{"faction":"red","delta":9,"total":9}""");

        var results = await Task.WhenAll(
            ApplyAsync(database, store, board, "red", "room-1/5/0", 9),
            ApplyAsync(database, store, board, "red", "room-1/5/0", 9),
            ApplyAsync(database, store, board, "red", "room-1/5/0", 9));

        // Exactly one claimant accumulated; the others answered the stored score as duplicates.
        Assert.Equal(1, results.Count(r => !r.Duplicate));
        Assert.Equal(2, results.Count(r => r.Duplicate));
        Assert.All(results, r => Assert.Equal(9, r.Score));
        Assert.Equal(9, (await store.ReadEntryAsync(board, "red"))!.Score);
    }

    [Fact]
    public async Task ALostResponseRetryAnswersTheStoredScore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await SeedEffectAsync(database, "room-1/2/0", "score", """{"faction":"red","delta":5,"total":5}""");

        // The apply commits but its response is lost — the caller cannot tell it succeeded.
        await ApplyAsync(database, store, board, "red", "room-1/2/0", 5);
        // The retry (the lost-response recovery) answers the stored score, never a second delta.
        var retry = await ApplyAsync(database, store, board, "red", "room-1/2/0", 5);
        Assert.True(retry.Duplicate);
        Assert.Equal(5, retry.Score);
        Assert.Equal(5, (await store.ReadEntryAsync(board, "red"))!.Score);
    }

    [Fact]
    public async Task AnEffectIdCannotBeReusedForADifferentProjection()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await SeedEffectAsync(database, "room-1/3/0", "score", """{"faction":"red","delta":7,"total":7}""");

        await ApplyAsync(database, store, board, "red", "room-1/3/0", 7);

        // The same EffectId re-projected to a different subject is a visible conflict...
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => ApplyAsync(database, store, board, "blue", "room-1/3/0", 7));
        // ...and to a different board is too — an EffectId can never be moved or re-amounted.
        var otherBoard = DefaultBoard(board: "board-2");
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => ApplyAsync(database, store, otherBoard, "red", "room-1/3/0", 7));
        Assert.Equal(7, (await store.ReadEntryAsync(board, "red"))!.Score);
        Assert.Null(await store.ReadEntryAsync(otherBoard, "red"));
    }

    [Fact]
    public async Task ANonCommittedOrNonScoreEffectIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        // A committed effect of an unrelated kind (an asset change, a like) never enters a board.
        await SeedEffectAsync(database, "room-1/4/0", "like", """{"faction":"red","count":3}""");
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => ApplyAsync(database, store, board, "red", "room-1/4/0", 3));

        // A score that names an effect the server never committed (a client self-report)
        // never projects.
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => ApplyAsync(database, store, board, "red", "room-1/99/0", 3));
        Assert.Null(await store.ReadEntryAsync(board, "red"));
    }

    [Fact]
    public async Task ADeltaMismatchingTheCommittedEffectIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        await SeedEffectAsync(database, "room-1/6/0", "score", """{"faction":"red","delta":7,"total":7}""");

        // A caller cannot choose an amount: the delta must equal the committed effect's own.
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => ApplyAsync(database, store, board, "red", "room-1/6/0", 9));
        Assert.Null(await store.ReadEntryAsync(board, "red"));
    }

    [Fact]
    public async Task TheEffectAccumulatesUnderSumAndMaxAndRecordsItsProjection()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        var board = DefaultBoard();
        var maxBoard = board with { BoardId = "board-max", Aggregation = RankAggregation.Max };
        await SeedEffectAsync(database, "room-1/7/0", "score", """{"faction":"red","delta":5,"total":5}""");
        await SeedEffectAsync(database, "room-1/8/0", "score", """{"faction":"red","delta":7,"total":7}""");
        await SeedEffectAsync(database, "room-1/9/0", "score", """{"faction":"red","delta":3,"total":3}""");

        Assert.Equal(5, (await ApplyAsync(database, store, board, "red", "room-1/7/0", 5)).Score);
        Assert.Equal(12, (await ApplyAsync(database, store, board, "red", "room-1/8/0", 7)).Score);
        Assert.Equal(15, (await ApplyAsync(database, store, board, "red", "room-1/9/0", 3)).Score);
        Assert.Equal(15, (await store.ReadEntryAsync(board, "red"))!.Score);

        // Max keeps the largest score; the ledger records the resulting projection per effect.
        // An effect is immutable and projects onto exactly one board, so the Max board gets
        // its own effect ids (reusing the Sum board's would be a conflict).
        await SeedEffectAsync(database, "room-1/17/0", "score", """{"faction":"red","delta":5,"total":5}""");
        await SeedEffectAsync(database, "room-1/18/0", "score", """{"faction":"red","delta":7,"total":7}""");
        await SeedEffectAsync(database, "room-1/19/0", "score", """{"faction":"red","delta":3,"total":3}""");
        Assert.Equal(5, (await ApplyAsync(database, store, maxBoard, "red", "room-1/17/0", 5)).Score);
        Assert.Equal(7, (await ApplyAsync(database, store, maxBoard, "red", "room-1/18/0", 7)).Score);
        Assert.Equal(7, (await ApplyAsync(database, store, maxBoard, "red", "room-1/19/0", 3)).Score);
        Assert.Equal(7, (await store.ReadEntryAsync(maxBoard, "red"))!.Score);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand("""
                SELECT effect_id,board_id,scope_id,subject_type,subject_id,delta,score
                FROM game.leaderboard_effect ORDER BY effect_id
                """, connection);
            await using var reader = await ledger.ExecuteReaderAsync();
            var rows = new List<(string Effect, string Board, string Scope, short Subject, string SubjectId, long Delta, long Score)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt16(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6)));
            Assert.Equal(6, rows.Count);
            var effect = rows.Single(r => r.Effect == "room-1/7/0" && r.Board == "board-1");
            Assert.Equal(board.Scope.ScopeId, effect.Scope);
            Assert.Equal((short)RankSubject.Player, effect.Subject);
            Assert.Equal("red", effect.SubjectId);
            Assert.Equal(5, effect.Delta);
            Assert.Equal(5, effect.Score);
            var maxEffect = rows.Single(r => r.Effect == "room-1/18/0" && r.Board == "board-max");
            Assert.Equal(7, maxEffect.Score);
        }
    }

    private static async Task<LeaderboardEffectResult> ApplyAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store, LeaderboardBoard board, string subjectId, string effectId, long delta)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await store.ApplyScoreEffectAsync(board, subjectId, effectId, delta,
            connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task SeedEffectAsync(TemporaryDatabase database, string effectId,
        string kind, string payloadJson)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@effect,@kind,@payload::jsonb,'Pending')
            ON CONFLICT (effect_id) DO NOTHING
            """, connection);
        seed.Parameters.AddWithValue("effect", effectId);
        seed.Parameters.AddWithValue("kind", kind);
        seed.Parameters.AddWithValue("payload", payloadJson);
        await seed.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_rank03_" + Guid.NewGuid().ToString("N");
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
