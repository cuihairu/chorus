using System.Globalization;
using System.Text.Json;
using Chorus.Rpc;
using IdempotencyConflictException = Chorus.Data.IdempotencyConflictException;
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
/// FIX-RANK-03 adds the idempotent effect entry (developer-capabilities.md「Score changes
/// derive from immutable committed server effects」): <see cref="ApplyScoreEffectAsync"/>
/// verifies the effect is a committed <c>score</c>-kind server effect whose own delta matches
/// the request, then applies it exactly once per immutable EffectId — a concurrent, replayed
/// or lost-response retry never double-scores, and the same EffectId re-projected to a
/// different board or delta is a visible conflict. The caller owns the transaction of the
/// write; the score only becomes visible when the transaction that grants it commits, so a
/// visible score is always backed by a committed source (architecture 9.2).
///
/// This is the persistence layer only: no query authorization or read version (RANK-04),
/// no per-period/late attribution (RANK-07), no GM rebuild (RANK-08). The source validation
/// of <see cref="ApplyScoreEffectAsync"/> is the storage floor; the business module (the
/// ControlServer scoring service) selects the board/period/scope and derives the subject.
/// </summary>
/// <summary>FIX-RANK-03: one committed effect as the committed effect ledger answers it —
/// its kind and its payload verbatim. The business module derives the scoring subject and
/// delta from the declared score-effect shape; the data plane never parses a game-specific
/// payload, it only verifies the kind and the delta.</summary>
public sealed record CommittedEffect(string Kind, string PayloadJson, DateTimeOffset CommittedAt);

public sealed class RedesignLeaderboardStore
{
    private readonly string _connectionString;
    /// <summary>The connection string the store's own reads open; the gRPC face reuses it to
    /// open the caller-owned transaction of an idempotent effect apply.</summary>
    public string ConnectionString { get; }

    public RedesignLeaderboardStore(string connectionString)
    {
        _connectionString = connectionString;
        ConnectionString = connectionString;
    }

    /// <summary>The largest TopN one read answers (mirrors the settlement board read's bound):
    /// a query page is bounded, never one unbounded answer over a whole board.</summary>
    public const int MaxTopEntries = LeaderboardQueryLimits.MaxTopEntries;

    /// <summary>FIX-RANK-05: the largest around-me window one read answers (the subject plus
    /// this many neighbours on each side of its rank) — a bounded window, never one unbounded
    /// answer over a whole board.</summary>
    public const int MaxAroundWindow = LeaderboardQueryLimits.MaxAroundWindow;

    /// <summary>FIX-RANK-03: the committed effect kind that carries a board score (the
    /// modules emit <c>ModuleEffect("score", ...)</c>; the frame commit writes it to the
    /// committed effect ledger under that kind). Only this kind projects; an asset change or
    /// any other committed effect never enters a board.</summary>
    public const string ScoreEffectKind = "score";

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

    /// <summary>
    /// FIX-RANK-03: applies one committed scoring effect onto one board, exactly once per
    /// immutable EffectId, under the caller's transaction. The floor the storage keeps, in
    /// order, inside the transaction:
    /// <list type="number">
    /// <item><description>the effect must exist in the committed effect ledger
    /// (<c>infra.outbox</c>, written atomically with its frame) and be a <c>score</c>-kind
    /// effect — a fabricated id (a client self-report), an unrelated effect kind (an asset
    /// change, a like) is refused before anything is written;</description></item>
    /// <item><description>the requested delta must equal the committed effect's own
    /// <c>delta</c> — the caller names an effect, it never chooses an amount;</description></item>
    /// <item><description>the projection-ledger row claims the EffectId first
    /// (<c>ON CONFLICT DO NOTHING RETURNING</c>): a concurrent claimant blocks on the
    /// uncommitted row and then walks the duplicate path, so two racing applies of one
    /// effect produce one delta; a replay or a lost-response retry reads the already-applied
    /// row and answers its stored score without a second accumulation; the same EffectId
    /// with a different board, subject or delta is a visible
    /// <see cref="IdempotencyConflictException"/>.</description></item>
    /// </list>
    /// Only then does the accumulation run (<see cref="AccumulateAsync"/>), and a refused
    /// accumulation rolls the whole transaction back — the ledger claim included, so a
    /// refused effect is never marked applied. Returns the resulting score and whether the
    /// effect had already been applied.
    /// </summary>
    public async Task<LeaderboardEffectResult> ApplyScoreEffectAsync(LeaderboardBoard board,
        string subjectId, string effectId, long delta,
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        if (string.IsNullOrWhiteSpace(effectId))
            throw new ArgumentException("An effect id must not be empty: a score without its committed effect never projects.", nameof(effectId));

        // The source floor: the effect must be a committed score effect whose own delta
        // matches. The outbox row is immutable (its insert is ON CONFLICT DO NOTHING, part
        // of the frame transaction), so this check answers against the committed evidence,
        // never against the caller's word.
        await using (var source = new NpgsqlCommand("""
            SELECT kind,(payload->>'delta')::bigint FROM infra.outbox WHERE effect_id=@effect
            """, connection, transaction))
        {
            source.Parameters.AddWithValue("effect", effectId);
            await using var reader = await source.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new LeaderboardRejectedException(
                    "The effect is not a committed server effect; a score without its committed effect never projects.");
            var kind = reader.GetString(0);
            var committedDelta = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            if (kind != ScoreEffectKind)
                throw new LeaderboardRejectedException(
                    $"The committed effect is a {kind} effect, not a score effect; it never enters a board.");
            if (committedDelta != delta)
                throw new LeaderboardRejectedException(
                    "The requested delta differs from the committed effect's own delta; a caller never chooses an amount.");
        }

        // The ledger claim: the first writer of this EffectId takes the row and accumulates;
        // every other claimant (concurrent, replayed or lost-response) lands on the already
        // applied row instead of the accumulation.
        bool claimed;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO game.leaderboard_effect(effect_id,tenant_id,game_id,board_id,period_id,
                scope_id,subject_type,subject_id,delta,score)
            VALUES (@effect,@tenant,@game,@board,@period,@scope,@subject,@subject_id,@delta,0)
            ON CONFLICT (effect_id) DO NOTHING
            RETURNING effect_id
            """, connection, transaction))
        {
            AddEffectParameters(claim, board, subjectId, effectId, delta);
            claimed = await claim.ExecuteScalarAsync(cancellationToken) is not null;
        }

        if (!claimed)
            return await ReadAppliedEffectAsync(board, subjectId, effectId, delta, ScoreEffectKind,
                connection, transaction, cancellationToken);

        var score = await AccumulateAsync(board, subjectId, delta, connection, transaction, cancellationToken);
        await using (var record = new NpgsqlCommand("""
            UPDATE game.leaderboard_effect SET score=@score WHERE effect_id=@effect
            """, connection, transaction))
        {
            record.Parameters.AddWithValue("score", score);
            record.Parameters.AddWithValue("effect", effectId);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        return new LeaderboardEffectResult(score, Duplicate: false);
    }

    /// <summary>
    /// The duplicate path of an idempotent apply: the row was already applied. The same
    /// projection (board key, subject, delta, kind) answers the stored score with
    /// <see cref="LeaderboardEffectResult.Duplicate"/> set; a different one is a visible
    /// conflict — an id (an EffectId or a GM operation id) can never be re-projected, moved
    /// to another board or silently re-amounted.
    /// </summary>
    private static async Task<LeaderboardEffectResult> ReadAppliedEffectAsync(LeaderboardBoard board,
        string subjectId, string effectId, long delta, string kind,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand("""
            SELECT tenant_id,game_id,board_id,period_id,scope_id,subject_type,subject_id,delta,score,kind
            FROM game.leaderboard_effect WHERE effect_id=@effect
            """, connection, transaction);
        read.Parameters.AddWithValue("effect", effectId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The applied effect row disappeared during the transaction.");
        var key = new BoardKey(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4));
        var same = key == board.Key
            && (RankSubject)reader.GetInt16(5) == board.Subject
            && StringComparer.Ordinal.Equals(reader.GetString(6), subjectId)
            && reader.GetInt64(7) == delta
            && StringComparer.Ordinal.Equals(reader.GetString(9), kind);
        if (!same)
            throw new IdempotencyConflictException();
        return new LeaderboardEffectResult(reader.GetInt64(8), Duplicate: true);
    }

    private static void AddEffectParameters(NpgsqlCommand command, LeaderboardBoard board,
        string subjectId, string effectId, long delta)
    {
        command.Parameters.AddWithValue("effect", effectId);
        command.Parameters.AddWithValue("tenant", board.TenantId);
        command.Parameters.AddWithValue("game", board.GameId);
        command.Parameters.AddWithValue("board", board.BoardId);
        command.Parameters.AddWithValue("period", board.Period.PeriodId);
        command.Parameters.AddWithValue("scope", board.Scope.ScopeId);
        command.Parameters.AddWithValue("subject", (short)board.Subject);
        command.Parameters.AddWithValue("subject_id", subjectId);
        command.Parameters.AddWithValue("delta", delta);
    }

    /// <summary>
    /// FIX-RANK-03: reads one committed effect by its immutable EffectId (null when no such
    /// effect was ever committed). This is the business module's source read: it names an
    /// effect, and the committed effect ledger answers its kind and payload verbatim — a
    /// client self-report names an id that never appears here, so the source is validated
    /// against the committed evidence, never against a caller-supplied score.
    /// </summary>
    public async Task<CommittedEffect?> ReadCommittedEffectAsync(string effectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(effectId)) return null;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var read = new NpgsqlCommand("""
            SELECT kind,payload::text,committed_at FROM infra.outbox WHERE effect_id=@effect
            """, connection);
        read.Parameters.AddWithValue("effect", effectId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CommittedEffect(reader.GetString(0), reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    /// <summary>Reads one subject's entry of one board (null when the subject has not scored
    /// on that board). The entry is stamped with the board's declared subject type.</summary>
    public async Task<RankEntry?> ReadEntryAsync(LeaderboardBoard board, string subjectId,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        await using var connection = new NpgsqlConnection(_connectionString);
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
        await using var connection = new NpgsqlConnection(_connectionString);
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

    /// <summary>
    /// FIX-RANK-04: reads the bounded TopN of one board together with the board's read
    /// version at the same snapshot (developer-capabilities.md「分页绑定榜单读取版本」). One
    /// SELECT computes the page and the version — the version is a scalar subquery over the
    /// whole board (the same statement snapshot as the answered entries), never a version
    /// from before or after the page. The version is the board's last write marker: it
    /// changes whenever any entry of the board changes, so a paginated continuation that
    /// carries it back detects a changed board. An empty board answers an empty page and the
    /// version sentinel (<see cref="DateTimeOffset.MinValue"/>).
    /// </summary>
    public async Task<LeaderboardTopResult> ReadTopWithVersionAsync(LeaderboardBoard board, int maxEntries,
        CancellationToken cancellationToken = default)
    {
        var page = await ReadTopPageAsync(board, maxEntries, null, null, cancellationToken);
        return new LeaderboardTopResult(page.Entries, page.ReadVersion);
    }

    /// <summary>
    /// FIX-RANK-06: one bounded page of a paginated TopN walk (developer-capabilities.md
    /// 「稳定分页」). The first page (<paramref name="afterScore"/> null) reads from the top of
    /// the board; a continuation resumes strictly after the previous page's last entry's
    /// keyset (<paramref name="afterScore"/>/<paramref name="afterSubjectId"/> — score
    /// descending, subject id ascending, the RANK-01 order), so a walk never overlaps or skips
    /// an entry. The page and the board's read version are one snapshot (single SELECT; the
    /// version is the whole-board scalar subquery, NOT a window over the keyset-filtered
    /// rows — a window would make every continuation carry the remainder's newest write,
    /// and a cursor minted from the whole-board marker would false-stale the moment the
    /// board's newest write sat behind the keyset). One row past the page bound is read to
    /// know whether more remain (so a continuation that would answer an empty page is never
    /// issued). The page's <see cref="LeaderboardPageResult.NextCursor"/> is null here — the
    /// data plane issues the cursor (it carries the snapshot deadline the store does not know).
    /// </summary>
    public async Task<LeaderboardPageResult> ReadTopPageAsync(LeaderboardBoard board, int maxEntries,
        long? afterScore, string? afterSubjectId, CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (maxEntries is <= 0 or > MaxTopEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (afterScore is null != (afterSubjectId is null))
            throw new ArgumentException("A continuation keyset is both score and subject, or neither.");
        var keyset = afterScore is not null;
        var sql = keyset
            ? """
              SELECT e1.subject_id, e1.score,
                (SELECT max(e3.updated_at) FROM game.leaderboard_entry e3
                  WHERE e3.tenant_id=e1.tenant_id AND e3.game_id=e1.game_id AND e3.board_id=e1.board_id
                    AND e3.period_id=e1.period_id AND e3.scope_id=e1.scope_id
                    AND e3.subject_type=e1.subject_type) AS board_version
              FROM game.leaderboard_entry e1
              WHERE e1.tenant_id=@tenant AND e1.game_id=@game AND e1.board_id=@board
                AND e1.period_id=@period AND e1.scope_id=@scope AND e1.subject_type=@subject
                AND (e1.score < @after_score OR (e1.score = @after_score AND e1.subject_id > @after_subject))
              ORDER BY e1.score DESC, e1.subject_id ASC
              LIMIT @max
              """
            : """
              SELECT e1.subject_id, e1.score,
                (SELECT max(e3.updated_at) FROM game.leaderboard_entry e3
                  WHERE e3.tenant_id=e1.tenant_id AND e3.game_id=e1.game_id AND e3.board_id=e1.board_id
                    AND e3.period_id=e1.period_id AND e3.scope_id=e1.scope_id
                    AND e3.subject_type=e1.subject_type) AS board_version
              FROM game.leaderboard_entry e1
              WHERE e1.tenant_id=@tenant AND e1.game_id=@game AND e1.board_id=@board
                AND e1.period_id=@period AND e1.scope_id=@scope AND e1.subject_type=@subject
              ORDER BY e1.score DESC, e1.subject_id ASC
              LIMIT @max
              """;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var entries = new List<RankEntry>();
        var version = DateTimeOffset.MinValue;
        await using (var read = new NpgsqlCommand(sql, connection))
        {
            AddKeyParameters(read, board, null);
            if (keyset)
            {
                read.Parameters.AddWithValue("after_score", afterScore!.Value);
                read.Parameters.AddWithValue("after_subject", afterSubjectId!);
            }
            read.Parameters.AddWithValue("max", maxEntries + 1);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (entries.Count == 0)
                {
                    var stamped = reader.GetDateTime(2);
                    version = stamped.Kind == DateTimeKind.Utc
                        ? new DateTimeOffset(stamped)
                        : new DateTimeOffset(DateTime.SpecifyKind(stamped, DateTimeKind.Utc), TimeSpan.Zero);
                }
                entries.Add(new RankEntry(board.Subject, reader.GetString(0), reader.GetInt64(1)));
            }
        }
        var hasMore = entries.Count > maxEntries;
        if (hasMore) entries.RemoveAt(entries.Count - 1);
        return new LeaderboardPageResult(entries, version, hasMore, null);
    }

    /// <summary>
    /// FIX-RANK-05: one subject's position — its entry, its unique ordinal rank in the
    /// board's declared order, and the board's read version at the same snapshot
    /// (developer-capabilities.md「实时变化榜的个人名次必须对应同一次读取视图」). The rank is
    /// 1 + the count of entries that precede it in the board order (score descending, subject
    /// id ascending — RANK-01 <see cref="LeaderboardOrdering"/>), so equal scores get distinct
    /// consecutive ranks; the rank, the score and the version are one consistent view (all
    /// computed in one statement). Answers null when the subject has not scored on the board.
    /// </summary>
    public async Task<LeaderboardRankResult?> ReadRankAsync(LeaderboardBoard board, string subjectId,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var read = new NpgsqlCommand("""
            SELECT e.score,
              (SELECT count(*) FROM game.leaderboard_entry e2
                WHERE e2.tenant_id=e.tenant_id AND e2.game_id=e.game_id AND e2.board_id=e.board_id
                  AND e2.period_id=e.period_id AND e2.scope_id=e.scope_id AND e2.subject_type=e.subject_type
                  AND (e2.score > e.score OR (e2.score = e.score AND e2.subject_id < e.subject_id))) + 1 AS rank,
              (SELECT max(updated_at) FROM game.leaderboard_entry e3
                WHERE e3.tenant_id=e.tenant_id AND e3.game_id=e.game_id AND e3.board_id=e.board_id
                  AND e3.period_id=e.period_id AND e3.scope_id=e.scope_id AND e3.subject_type=e.subject_type) AS board_version
            FROM game.leaderboard_entry e
            WHERE e.tenant_id=@tenant AND e.game_id=@game AND e.board_id=@board
              AND e.period_id=@period AND e.scope_id=@scope
              AND e.subject_type=@subject AND e.subject_id=@subject_id
            """, connection);
        AddKeyParameters(read, board, subjectId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var version = reader.IsDBNull(2) ? DateTimeOffset.MinValue : ToOffset(reader.GetDateTime(2));
        return new LeaderboardRankResult((int)reader.GetInt64(1),
            new RankEntry(board.Subject, subjectId, reader.GetInt64(0)), version);
    }

    /// <summary>
    /// FIX-RANK-05: the entries within a bounded window of a subject's rank — the subject
    /// included, in rank order, at one read view (developer-capabilities.md「个人名次、附近名次」).
    /// One statement assigns the unique ordinal ranks (row_number over the board order), finds
    /// the subject's rank and takes the window around it, so the window and the rank are one
    /// consistent snapshot. Answers null when the subject has not scored on the board.
    /// </summary>
    public async Task<LeaderboardAroundResult?> ReadAroundAsync(LeaderboardBoard board, string subjectId,
        int window, CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        if (window is <= 0 or > MaxAroundWindow)
            throw new ArgumentOutOfRangeException(nameof(window), "An around-me window is bounded.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var entries = new List<RankedEntry>();
        var version = DateTimeOffset.MinValue;
        await using (var read = new NpgsqlCommand("""
            WITH ranked AS MATERIALIZED (
              SELECT subject_id, score,
                     row_number() OVER (ORDER BY score DESC, subject_id ASC) AS rank,
                     max(updated_at) OVER () AS board_version
              FROM game.leaderboard_entry
              WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
                AND period_id=@period AND scope_id=@scope AND subject_type=@subject
            ),
            me AS (SELECT rank FROM ranked WHERE subject_id=@subject_id)
            SELECT r.rank, r.subject_id, r.score, r.board_version
            FROM me, ranked r
            WHERE r.rank >= me.rank - @window AND r.rank <= me.rank + @window
            ORDER BY r.rank ASC
            """, connection))
        {
            AddKeyParameters(read, board, subjectId);
            read.Parameters.AddWithValue("window", window);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (entries.Count == 0) version = ToOffset(reader.GetDateTime(3));
                entries.Add(new RankedEntry((int)reader.GetInt64(0),
                    new RankEntry(board.Subject, reader.GetString(1), reader.GetInt64(2))));
            }
        }
        return entries.Count == 0 ? null : new LeaderboardAroundResult(entries, version);
    }

    /// <summary>FIX-RANK-08: the GM correction ledger kind (a signed delta a GM operator
    /// applied onto one subject's entry — a maintenance row, not a committed game effect).</summary>
    public const string GmAdjustKind = "gm-adjust";

    /// <summary>FIX-RANK-08: the GM reset ledger kind (zero one subject's entry, or the whole
    /// board's when the row carries no subject).</summary>
    public const string GmResetKind = "gm-reset";

    /// <summary>
    /// FIX-RANK-08: one GM correction — a signed delta onto one subject's entry of a named
    /// board/period/scope, exactly once per operation id, under the caller's transaction
    /// (developer-capabilities.md「GM adjustment/reset names the board, period and scope and
    /// includes expected version, operation ID, reason and audit evidence」). The operation id
    /// is the idempotency key and the audit anchor: a replay or a lost-response retry answers
    /// the stored score as a duplicate, and the same id with a different projection is a
    /// visible conflict, never a silent re-amount. The expected version refuses a correction
    /// over a board that changed since the operator last read it. The correction row is a
    /// ledger row (kind <see cref="GmAdjustKind"/>, with its reason and operator), so a
    /// rebuild reproduces it — original scoring evidence stays available and a correction is
    /// never silently lost. Returns the resulting score and whether it was already applied.
    /// </summary>
    public async Task<LeaderboardEffectResult> AdjustEntryAsync(LeaderboardBoard board,
        string subjectId, long delta, string expectedVersion, string operationId, string reason,
        string operatorId, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A GM correction needs its operation id (the idempotency and audit anchor).", nameof(operationId));
        await RequireVersionAsync(board, expectedVersion, connection, transaction, cancellationToken);

        bool claimed;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO game.leaderboard_effect(effect_id,tenant_id,game_id,board_id,period_id,scope_id,
                subject_type,subject_id,delta,score,kind,reason,operator_id)
            VALUES (@effect,@tenant,@game,@board,@period,@scope,@subject,@subject_id,@delta,0,@kind,@reason,@operator)
            ON CONFLICT (effect_id) DO NOTHING
            RETURNING effect_id
            """, connection, transaction))
        {
            AddEffectParameters(claim, board, subjectId, operationId, delta);
            claim.Parameters.AddWithValue("kind", GmAdjustKind);
            claim.Parameters.AddWithValue("reason", reason ?? "");
            claim.Parameters.AddWithValue("operator", operatorId ?? "");
            claimed = await claim.ExecuteScalarAsync(cancellationToken) is not null;
        }
        if (!claimed)
            return await ReadAppliedEffectAsync(board, subjectId, operationId, delta, GmAdjustKind,
                connection, transaction, cancellationToken);

        var score = await AccumulateAsync(board, subjectId, delta, connection, transaction, cancellationToken);
        await using (var record = new NpgsqlCommand("""
            UPDATE game.leaderboard_effect SET score=@score WHERE effect_id=@effect
            """, connection, transaction))
        {
            record.Parameters.AddWithValue("score", score);
            record.Parameters.AddWithValue("effect", operationId);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteAuditAsync(operationId, operatorId ?? "", reason ?? "", board,
            "leaderboard.adjust", new { board.BoardId, board.Period.PeriodId, board.Scope.ScopeId, SubjectId = subjectId, Delta = delta },
            connection, transaction, cancellationToken);
        return new LeaderboardEffectResult(score, Duplicate: false);
    }

    /// <summary>
    /// FIX-RANK-08: one GM reset — zero one subject's entry (or the whole board's when
    /// <paramref name="subjectId"/> is null or empty), exactly once per operation id, under
    /// the caller's transaction. Same idempotency, version guard, evidence and rebuild-fidelity
    /// as <see cref="AdjustEntryAsync"/>; the reset is a ledger row (kind
    /// <see cref="GmResetKind"/>, no subject for a whole-board reset) so a rebuild reproduces
    /// it. Returns whether it was already applied.
    /// </summary>
    public async Task<LeaderboardEffectResult> ResetBoardAsync(LeaderboardBoard board,
        string? subjectId, string expectedVersion, string operationId, string reason, string operatorId,
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A GM reset needs its operation id (the idempotency and audit anchor).", nameof(operationId));
        await RequireVersionAsync(board, expectedVersion, connection, transaction, cancellationToken);
        var subject = subjectId ?? "";

        bool claimed;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO game.leaderboard_effect(effect_id,tenant_id,game_id,board_id,period_id,scope_id,
                subject_type,subject_id,delta,score,kind,reason,operator_id)
            VALUES (@effect,@tenant,@game,@board,@period,@scope,@subject,@subject_id,0,0,@kind,@reason,@operator)
            ON CONFLICT (effect_id) DO NOTHING
            RETURNING effect_id
            """, connection, transaction))
        {
            AddEffectParameters(claim, board, subject, operationId, 0);
            claim.Parameters.AddWithValue("kind", GmResetKind);
            claim.Parameters.AddWithValue("reason", reason ?? "");
            claim.Parameters.AddWithValue("operator", operatorId ?? "");
            claimed = await claim.ExecuteScalarAsync(cancellationToken) is not null;
        }
        if (!claimed)
            return await ReadAppliedEffectAsync(board, subject, operationId, 0, GmResetKind,
                connection, transaction, cancellationToken);

        await using (var clear = new NpgsqlCommand("""
            DELETE FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope AND subject_type=@subject
              AND (@subject_id = '' OR subject_id = @subject_id)
            """, connection, transaction))
        {
            AddKeyParameters(clear, board, subject);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteAuditAsync(operationId, operatorId ?? "", reason ?? "", board,
            "leaderboard.reset",
            new { board.BoardId, board.Period.PeriodId, board.Scope.ScopeId, SubjectId = subject },
            connection, transaction, cancellationToken);
        return new LeaderboardEffectResult(0, Duplicate: false);
    }

    /// <summary>
    /// FIX-RANK-08: the projection rebuild of one board — re-derives the board's entries from
    /// its effect ledger (game effects and GM rows in apply order), under the caller's
    /// transaction (developer-capabilities.md「projection rebuild cannot add the same score
    /// twice」). The ledger is the source and the entries are the derived cache: a rebuild
    /// never reads the entries it is about to rewrite and never touches the committed outbox
    /// rows, so original scoring evidence is untouched and replaying cannot double-add (the
    /// replay is a re-derivation, not a re-apply). The board's existing entry rows are locked
    /// for update first, so a concurrent apply serializes against the rewrite: an apply that
    /// committed before the ledger read is included; one that has not committed is invisible
    /// to the ledger read and re-applies on top of the rebuilt rows after commit — the entries
    /// never silently diverge from the ledger. Returns the number of rebuilt entries and the
    /// board's new version.
    /// </summary>
    public async Task<LeaderboardRebuildResult> RebuildBoardAsync(LeaderboardBoard board,
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        // Lock the existing entry rows so a concurrent apply to an existing subject serializes
        // against the rewrite (the ledger read below is the same snapshot the rewrite commits).
        await using (var lockRows = new NpgsqlCommand("""
            SELECT subject_id FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope AND subject_type=@subject
            FOR UPDATE
            """, connection, transaction))
        {
            AddKeyParameters(lockRows, board, null);
            await lockRows.ExecuteNonQueryAsync(cancellationToken);
        }

        var running = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var ledger = new NpgsqlCommand("""
            SELECT subject_id, delta, kind FROM game.leaderboard_effect
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope AND subject_type=@subject
            ORDER BY applied_at ASC, effect_id ASC
            """, connection, transaction))
        {
            AddKeyParameters(ledger, board, null);
            await using var reader = await ledger.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var subject = reader.GetString(0);
                var delta = reader.GetInt64(1);
                var kind = reader.GetString(2);
                if (kind == GmResetKind)
                {
                    if (subject.Length == 0) running.Clear();
                    else running.Remove(subject);
                    continue;
                }
                var current = running.GetValueOrDefault(subject);
                if (!LeaderboardScoring.TryAccumulate(current, delta, board.Aggregation, board.Bounds, out var next))
                    throw new InvalidOperationException(
                        $"The effect ledger for board {board.BoardId} cannot be replayed under the board's bounds (subject {subject}, delta {delta}).");
                running[subject] = next;
            }
        }

        await using (var clear = new NpgsqlCommand("""
            DELETE FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope AND subject_type=@subject
            """, connection, transaction))
        {
            AddKeyParameters(clear, board, null);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var (subject, score) in running)
        {
            await using var write = new NpgsqlCommand("""
                INSERT INTO game.leaderboard_entry(tenant_id,game_id,board_id,period_id,scope_id,
                    subject_type,subject_id,score)
                VALUES (@tenant,@game,@board,@period,@scope,@subject,@subject_id,@score)
                """, connection, transaction);
            AddKeyParameters(write, board, subject);
            write.Parameters.AddWithValue("score", score);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        var version = await ReadBoardVersionAsync(board, connection, transaction, cancellationToken);
        return new LeaderboardRebuildResult(running.Count, version);
    }

    /// <summary>The board's read version at the caller's transaction view (an empty board answers
    /// <see cref="DateTimeOffset.MinValue"/>).</summary>
    public async Task<DateTimeOffset> ReadBoardVersionAsync(LeaderboardBoard board,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand("""
            SELECT max(updated_at) FROM game.leaderboard_entry
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
              AND period_id=@period AND scope_id=@scope AND subject_type=@subject
            """, connection, transaction);
        AddKeyParameters(read, board, null);
        var value = await read.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? DateTimeOffset.MinValue : ToOffset(Convert.ToDateTime(value));
    }

    /// <summary>The optimistic guard: a non-empty expected version must equal the board's
    /// current version, else the board changed since the operator last read it — the correction
    /// or reset is refused, never silently applied over a change.</summary>
    private async Task RequireVersionAsync(LeaderboardBoard board, string expectedVersion,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (expectedVersion.Length == 0) return;
        var current = await ReadBoardVersionAsync(board, connection, transaction, cancellationToken);
        if (!string.Equals(FormatVersion(current), expectedVersion, StringComparison.Ordinal))
            throw new LeaderboardStaleReadException(
                "The board changed since the operator's read version; reload the board and retry.");
    }

    /// <summary>Writes one control-plane audit row (the GM evidence) in the caller's
    /// transaction. A duplicate (request id, command) pair would mean the operation already
    /// acted, but the operation-id ledger claim runs first and only the first claim reaches
    /// here, so the insert succeeds exactly once per operation.</summary>
    private async Task WriteAuditAsync(string requestId, string operatorId, string reason,
        LeaderboardBoard board, string command, object detail,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO control.audit(request_id,operator,reason,scope,command,detail)
            VALUES (@request,@operator,@reason,@scope,@command,@detail::jsonb)
            """, connection, transaction);
        insert.Parameters.AddWithValue("request", requestId);
        insert.Parameters.AddWithValue("operator", operatorId);
        insert.Parameters.AddWithValue("reason", reason);
        insert.Parameters.AddWithValue("scope",
            $"{board.BoardId}/{board.Period.PeriodId}/{board.Scope.ScopeId}");
        insert.Parameters.AddWithValue("command", command);
        insert.Parameters.AddWithValue("detail", JsonSerializer.Serialize(detail));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatVersion(DateTimeOffset version) =>
        version == DateTimeOffset.MinValue ? "" : version.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ToOffset(DateTime stamped) =>
        stamped.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(stamped)
            : new DateTimeOffset(DateTime.SpecifyKind(stamped, DateTimeKind.Utc), TimeSpan.Zero);

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
