using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

/// <summary>
/// FIX-RANK-02: the default persistent leaderboard provider (developer-capabilities.md
/// 「排行榜定义」). One projection table, one write interface and one read interface, all
/// keyed by the RANK-01 contract — a board is its five-part <see cref="BoardKey"/> (tenant,
/// game, board, period, scope) plus its declared <see cref="RankSubject"/>, and an entry is
/// one subject id's <see cref="RankEntry.Score"/>. The write accumulates one delta under a
/// row lock with the board's declared scoring rule (RANK-01 <see cref="LeaderboardScoring"/>:
/// checked Sum or Max, bounds reject-not-clamp, overflow reject-not-wrap), so a refused delta
/// never changes the projection. The read answers one entry or a bounded TopN in the board's
/// declared order (score descending, subject id ascending) over the scope-covering rank index.
///
/// This is the persistence layer only: no idempotence (RANK-03), no query authorization or
/// read version (RANK-04), no per-period/late attribution (RANK-07), no GM rebuild (RANK-08).
/// The caller owns the transaction of the write; the score only becomes visible when the
/// transaction that grants it commits, so a visible score is always backed by a committed
/// source (the same discipline the settlement projection follows, architecture 9.2).
/// </summary>
public sealed class RedesignLeaderboardStore(string connectionString)
{
    /// <summary>The largest TopN one read answers (mirrors the settlement board read's bound):
    /// a query page is bounded, never one unbounded answer over a whole board.</summary>
    public const int MaxTopEntries = 1000;

    /// <summary>
    /// Accumulates one delta into one subject's entry of one board, under the caller's
    /// transaction. The row is locked for update so two concurrent accumulations of the same
    /// subject serialize; the board's scoring rule decides the result (a <see cref="Sum"/>
    /// overflow or an out-of-bounds result is refused, never clamped or wrapped). A subject
    /// with no row yet starts at the board's implicit baseline of 0 — for the default
    /// non-negative board the first delta is the entry's score; for a <see cref="RankAggregation.Max"/>
    /// board the baseline is the additive identity under max, so a first penalty (when the
    /// board declared negatives) scores 0. Returns the new score.
    /// </summary>
    public async Task<long> AccumulateAsync(LeaderboardBoard board, string subjectId, long delta,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        if (!board.Bounds.Contains(delta))
            throw new ArgumentOutOfRangeException(nameof(delta), "The delta is outside the board's declared bounds.");

        long current = 0;
        await using (var row = new NpgsqlCommand("""
            SELECT score FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope
              AND subject_type=@subject AND subject_id=@subject_id
            FOR UPDATE
            """, connection, transaction))
        {
            AddKeyParameters(row, board, subjectId);
            var value = await row.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value is not DBNull) current = Convert.ToInt64(value);
        }

        if (!LeaderboardScoring.TryAccumulate(current, delta, board.Aggregation, board.Bounds, out var next))
            throw new InvalidOperationException(
                "The delta cannot be accumulated into the current score under the board's bounds.");

        await using (var write = new NpgsqlCommand("""
            INSERT INTO game.leaderboard_entry(tenant_id,game_id,board_id,period_id,scope_id,
                subject_type,subject_id,score)
            VALUES (@tenant,@game,@board,@period,@scope,@subject,@subject_id,@score)
            ON CONFLICT (tenant_id,game_id,board_id,period_id,scope_id,subject_type,subject_id)
            DO UPDATE SET score=@score, updated_at=clock_timestamp()
            """, connection, transaction))
        {
            AddKeyParameters(write, board, subjectId);
            write.Parameters.AddWithValue("score", next);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        return next;
    }

    /// <summary>Reads one subject's entry of one board (null when the subject has not scored
    /// on that board). The entry is stamped with the board's declared subject type.</summary>
    public async Task<RankEntry?> ReadEntryAsync(LeaderboardBoard board, string subjectId,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var read = new NpgsqlCommand("""
            SELECT score FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope
              AND subject_type=@subject AND subject_id=@subject_id
            """, connection);
        AddKeyParameters(read, board, subjectId);
        var value = await read.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : new RankEntry(board.Subject, subjectId, Convert.ToInt64(value));
    }

    /// <summary>
    /// Reads the bounded TopN of one board in its declared order (score descending, subject
    /// id ascending — ties keep one stable order, RANK-01). The range read walks the rank
    /// index that covers exactly the board's scope, so a board's answer never scans another
    /// board's or another period's rows. Empty board answers an empty list.
    /// </summary>
    public async Task<IReadOnlyList<RankEntry>> ReadTopAsync(LeaderboardBoard board, int maxEntries,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (maxEntries is <= 0 or > MaxTopEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var entries = new List<RankEntry>();
        await using (var read = new NpgsqlCommand("""
            SELECT subject_id,score FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope
              AND subject_type=@subject
            ORDER BY score DESC, subject_id ASC
            LIMIT @max
            """, connection))
        {
            AddKeyParameters(read, board, null);
            read.Parameters.AddWithValue("max", maxEntries);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new RankEntry(board.Subject, reader.GetString(0), reader.GetInt64(1)));
        }
        return entries;
    }

    private static void AddKeyParameters(NpgsqlCommand command, LeaderboardBoard board, string? subjectId)
    {
        command.Parameters.AddWithValue("tenant", board.TenantId);
        command.Parameters.AddWithValue("game", board.GameId);
        command.Parameters.AddWithValue("board", board.BoardId);
        command.Parameters.AddWithValue("period", board.Period.PeriodId);
        command.Parameters.AddWithValue("scope", board.Scope.ScopeId);
        command.Parameters.AddWithValue("subject", (short)board.Subject);
        if (subjectId is not null) command.Parameters.AddWithValue("subject_id", subjectId);
    }
}
