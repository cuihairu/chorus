using System.Globalization;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-08, the GM correction / reset / projection-rebuild store
/// (developer-capabilities.md 「GM adjustment/reset names the board, period and scope and
/// includes expected version, operation ID, reason and audit evidence」): the operation id is
/// the idempotency and audit anchor — a replay answers the stored score as a duplicate and the
/// same id with a different projection is a visible conflict; the expected version refuses a
/// correction over a board that changed; the correction or reset is a ledger row, so a rebuild
/// reproduces it and original scoring evidence stays available; a rebuild is a re-derivation,
/// never a re-apply, so it cannot add a score twice.
/// </summary>
public sealed class RedesignRank08GmStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-1";
    private const string BoardId = "board-a";
    private static readonly LeaderboardBoard Board = LeaderboardBoard.Default(Tenant, Game, BoardId);

    [Fact]
    public async Task AnAdjustAppliesTheDeltaAndRecordsLedgerAndAudit()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);

        var before = await ReadVersionAsync(store, Board);
        var result = await AdjustAsync(database, store, "red", +3, Version(before), "op-adjust-1", "fix a mis-award");

        Assert.False(result.Duplicate);
        Assert.Equal(8, result.Score);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Equal(new[] { "red" }, top.Entries.Select(e => e.SubjectId));
        Assert.Equal(8, top.Entries[0].Score);
        // The adjustment is a ledger row (kind gm-adjust) with its reason, not a silent patch.
        var ledger = await ReadLedgerRowsAsync(database);
        Assert.Contains(ledger, r => r.Kind == RedesignLeaderboardStore.GmAdjustKind
            && r.SubjectId == "red" && r.Delta == 3 && r.Reason == "fix a mis-award");
        // The audit evidence is recorded.
        var audit = await ReadAuditAsync(database);
        Assert.Contains(audit, r => r.RequestId == "op-adjust-1" && r.Command == "leaderboard.adjust");
    }

    [Fact]
    public async Task AnAdjustReplayAnswersDuplicateWithoutReapplying()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);

        var before = await ReadVersionAsync(store, Board);
        var first = await AdjustAsync(database, store, "red", +3, Version(before), "op-adjust-2", "r");
        // A lost-response retry resends under the operator's current view; the row answers the
        // stored score as a duplicate instead of re-amounting.
        var after = await ReadVersionAsync(store, Board);
        var second = await AdjustAsync(database, store, "red", +3, Version(after), "op-adjust-2", "r");

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(8, second.Score);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Equal(8, top.Entries[0].Score); // not 11 — the replay never re-amounts.
        var ledger = await ReadLedgerRowsAsync(database);
        Assert.Single(ledger, r => r.EffectId == "op-adjust-2");
    }

    [Fact]
    public async Task AnAdjustWithTheSameIdButDifferentProjectionIsAConflict()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);

        var before = await ReadVersionAsync(store, Board);
        await AdjustAsync(database, store, "red", +3, Version(before), "op-adjust-3", "r");
        // The same operation id onto a different subject is a visible conflict, never a silent
        // re-amount onto another entry.
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => AdjustAsync(database, store, "blue", +3, "", "op-adjust-3", "r"));
    }

    [Fact]
    public async Task AnAdjustOverAChangedBoardIsAStaleRead()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);

        // The expected version names a board the operator no longer has — the correction is
        // refused, never silently applied over the change.
        await Assert.ThrowsAsync<LeaderboardStaleReadException>(
            () => AdjustAsync(database, store, "red", +3, "2020-01-01T00:00:00+00:00", "op-adjust-4", "r"));
    }

    [Fact]
    public async Task AResetClearsOneSubjectAndRecordsLedgerAndAudit()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);
        await SeedAndApplyAsync(database, store, "blue", "room-1/2/0", 3);

        var before = await ReadVersionAsync(store, Board);
        var result = await ResetAsync(database, store, "red", Version(before), "op-reset-1", "remove cheater");

        Assert.False(result.Duplicate);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Equal(new[] { "blue" }, top.Entries.Select(e => e.SubjectId));
        var ledger = await ReadLedgerRowsAsync(database);
        Assert.Contains(ledger, r => r.Kind == RedesignLeaderboardStore.GmResetKind
            && r.SubjectId == "red" && r.Reason == "remove cheater");
        var audit = await ReadAuditAsync(database);
        Assert.Contains(audit, r => r.RequestId == "op-reset-1" && r.Command == "leaderboard.reset");
    }

    [Fact]
    public async Task AWholeBoardResetClearsEverySubject()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);
        await SeedAndApplyAsync(database, store, "blue", "room-1/2/0", 3);

        var before = await ReadVersionAsync(store, Board);
        var result = await ResetAsync(database, store, null, Version(before), "op-reset-2", "board voided");

        Assert.False(result.Duplicate);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Empty(top.Entries);
        var ledger = await ReadLedgerRowsAsync(database);
        Assert.Contains(ledger, r => r.Kind == RedesignLeaderboardStore.GmResetKind && r.SubjectId == "");
    }

    [Fact]
    public async Task ARebuildReproducesTheLedgerAfterTheEntriesAreCorrupted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);
        await SeedAndApplyAsync(database, store, "blue", "room-1/2/0", 3);
        var before = await ReadVersionAsync(store, Board);
        await AdjustAsync(database, store, "red", +3, Version(before), "op-adjust-5", "r");
        await ResetAsync(database, store, "blue", "", "op-reset-3", "r");

        // The projection drifts from the ledger; a rebuild re-derives it from the ledger alone.
        await DeleteAllEntriesAsync(database);

        var rebuilt = await RebuildAsync(database, store);
        Assert.Equal(1, rebuilt.Entries);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Equal(new[] { "red" }, top.Entries.Select(e => e.SubjectId));
        Assert.Equal(8, top.Entries[0].Score); // 5 (score effect) + 3 (gm-adjust), the reset took blue out.
    }

    [Fact]
    public async Task ARebuildIsIdempotentAndCannotAddAScoreTwice()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);
        var before = await ReadVersionAsync(store, Board);
        await AdjustAsync(database, store, "red", +3, Version(before), "op-adjust-6", "r");

        var first = await RebuildAsync(database, store);
        var second = await RebuildAsync(database, store);

        Assert.Equal(1, first.Entries);
        Assert.Equal(1, second.Entries);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Equal(8, top.Entries[0].Score); // never 13 or 16 — a rebuild re-derives, it never re-applies.
    }

    [Fact]
    public async Task ARebuildLeavesEvidenceUntouched()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);
        var before = await ReadVersionAsync(store, Board);
        await AdjustAsync(database, store, "red", +3, Version(before), "op-adjust-7", "r");

        var outboxBefore = await ReadOutboxAsync(database);
        var ledgerBefore = await ReadLedgerRowsAsync(database);
        await RebuildAsync(database, store);

        Assert.Equal(outboxBefore, await ReadOutboxAsync(database));
        var ledgerAfter = await ReadLedgerRowsAsync(database);
        Assert.Equal(ledgerBefore.Count, ledgerAfter.Count);
        Assert.Equal(ledgerBefore, ledgerAfter);
    }

    [Fact]
    public async Task AResetThenRebuildDoesNotResurrectTheClearedSubject()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignLeaderboardStore(database.ConnectionString);
        await SeedAndApplyAsync(database, store, "red", "room-1/1/0", 5);
        await ResetAsync(database, store, "red", "", "op-reset-4", "r");

        var rebuilt = await RebuildAsync(database, store);
        Assert.Equal(0, rebuilt.Entries);
        var top = await store.ReadTopWithVersionAsync(Board, 100);
        Assert.Empty(top.Entries);
    }

    // --- helpers ---

    private static string Version(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue ? "" : value.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<DateTimeOffset> ReadVersionAsync(RedesignLeaderboardStore store, LeaderboardBoard board)
    {
        await using var connection = new NpgsqlConnection(store.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var version = await store.ReadBoardVersionAsync(board, connection, transaction, default);
        await transaction.CommitAsync();
        return version;
    }

    private static async Task<LeaderboardEffectResult> AdjustAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store, string subjectId, long delta, string expectedVersion,
        string operationId, string reason)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await store.AdjustEntryAsync(Board, subjectId, delta, expectedVersion,
            operationId, reason, "operator-1", connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<LeaderboardEffectResult> ResetAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store, string? subjectId, string expectedVersion,
        string operationId, string reason)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await store.ResetBoardAsync(Board, subjectId, expectedVersion,
            operationId, reason, "operator-1", connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<LeaderboardRebuildResult> RebuildAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await store.RebuildBoardAsync(Board, connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task SeedAndApplyAsync(TemporaryDatabase database,
        RedesignLeaderboardStore store, string subjectId, string effectId, long delta)
    {
        await using (var seedConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await seedConnection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id,kind,payload,status)
                VALUES (@effect,'score',@payload::jsonb,'Pending')
                ON CONFLICT (effect_id) DO NOTHING
                """, seedConnection);
            seed.Parameters.AddWithValue("effect", effectId);
            seed.Parameters.AddWithValue("payload", $$"""{"delta":{{delta}}}""");
            await seed.ExecuteNonQueryAsync();
        }
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.ApplyScoreEffectAsync(Board, subjectId, effectId, delta, connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task DeleteAllEntriesAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var clear = new NpgsqlCommand("DELETE FROM game.leaderboard_entry", connection);
        await clear.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string EffectId, string Kind, string SubjectId, long Delta, string Reason)>> ReadLedgerRowsAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand(
            "SELECT effect_id,kind,subject_id,delta,reason FROM game.leaderboard_effect ORDER BY effect_id", connection);
        await using var reader = await read.ExecuteReaderAsync();
        var rows = new List<(string, string, string, long, string)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
        return rows;
    }

    private static async Task<List<(string RequestId, string Command)>> ReadAuditAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("SELECT request_id,command FROM control.audit ORDER BY request_id", connection);
        await using var reader = await read.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static async Task<string> ReadOutboxAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand(
            "SELECT string_agg(effect_id || ':' || kind || ':' || status, ',' ORDER BY effect_id) FROM infra.outbox", connection);
        return (string?)await read.ExecuteScalarAsync() ?? "";
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_rank08_" + Guid.NewGuid().ToString("N");
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
