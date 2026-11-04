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
public sealed record CommittedEffect(string Kind, string PayloadJson);

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
            return await ReadAppliedEffectAsync(board, subjectId, effectId, delta, connection, transaction, cancellationToken);

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
    /// The duplicate path of <see cref="ApplyScoreEffectAsync"/>: the effect was already
    /// applied. The same projection (board key, subject, delta) answers the stored score
    /// with <see cref="LeaderboardEffectResult.Duplicate"/> set; a different one is a
    /// visible conflict — an EffectId can never be re-projected, moved to another board or
    /// silently re-amounted.
    /// </summary>
    private static async Task<LeaderboardEffectResult> ReadAppliedEffectAsync(LeaderboardBoard board,
        string subjectId, string effectId, long delta,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand("""
            SELECT tenant_id,game_id,board_id,period_id,scope_id,subject_type,subject_id,delta,score
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
            && reader.GetInt64(7) == delta;
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
            SELECT kind,payload::text FROM infra.outbox WHERE effect_id=@effect
            """, connection);
        read.Parameters.AddWithValue("effect", effectId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CommittedEffect(reader.GetString(0), reader.GetString(1))
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
    /// SELECT computes the page and the version — the window <c>max(updated_at) OVER ()</c>
    /// covers the whole board (windows run before <c>LIMIT</c>), so the answered entries and
    /// the version are one consistent view, never a version from before or after the page.
    /// The version is the board's last write marker: it changes whenever any entry of the
    /// board changes, so a paginated continuation that carries it back detects a changed
    /// board. An empty board answers an empty page and the version sentinel
    /// (<see cref="DateTimeOffset.MinValue"/>).
    /// </summary>
    public async Task<LeaderboardTopResult> ReadTopWithVersionAsync(LeaderboardBoard board, int maxEntries,
        CancellationToken cancellationToken = default)
    {
        board.Validate();
        if (maxEntries is <= 0 or > MaxTopEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var entries = new List<RankEntry>();
        var version = DateTimeOffset.MinValue;
        await using (var read = new NpgsqlCommand("""
            SELECT subject_id, score, max(updated_at) OVER () AS board_version
            FROM game.leaderboard_entry
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
        return new LeaderboardTopResult(entries, version);
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
