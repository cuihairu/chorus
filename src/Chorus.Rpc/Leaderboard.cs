using System.Globalization;
using Google.Protobuf;

namespace Chorus.Rpc;

/// <summary>
/// FIX-RANK-01: the leaderboard subject type (developer-capabilities.md「排名主体」). A board
/// declares exactly one subject type and every entry of that board is stamped with it — a
/// streamer identity and a viewer identity are different namespaces even when their string ids
/// collide, so the interface never confuses a streamer subject with an audience subject. The
/// default is <see cref="Player"/>; the other values are explicit.
/// </summary>
public enum RankSubject
{
    /// <summary>The default: a viewer/player subject, identified by its player id.</summary>
    Player = 0,

    /// <summary>A streamer subject, identified by its streamer id.</summary>
    Streamer = 1,

    /// <summary>A faction subject, identified by its faction id (a room's faction, not a person).</summary>
    Faction = 2
}

/// <summary>
/// FIX-RANK-01: the aggregation a board applies to its entries (developer-capabilities.md
/// 「计分」). The initial capabilities are <see cref="Sum"/> (accumulate deltas) and
/// <see cref="Max"/> (keep the single largest score). The default is <see cref="Sum"/>.
/// </summary>
public enum RankAggregation
{
    Sum = 0,
    Max = 1
}

/// <summary>
/// FIX-RANK-01: the period shape a board is bucketed into (developer-capabilities.md
/// 「周期」). <see cref="AllTime"/> is the default and is unbounded; day, week and season
/// buckets are computed by an explicit timezone and carry a monotonic version — a period
/// change is a new bucket and version, never an in-place clear that fakes a switch.
/// </summary>
public enum RankPeriodKind
{
    AllTime = 0,
    Daily = 1,
    Weekly = 2,
    Season = 3
}

/// <summary>
/// FIX-RANK-01: the identity of one board period (developer-capabilities.md「标识」「周期」).
/// A period is <see cref="Kind"/> + its bucket id + the timezone that computed it + the
/// half-open bucket window [<see cref="StartUtc"/>, <see cref="EndUtc"/>) (null = unbounded)
/// + a <see cref="Version"/> that is monotonic across a board's consecutive periods, so a
/// frozen board keeps its version and a reader never mistakes a switched period for a
/// continuation. The period id and version are derived from the bucket, never from clearing
/// an old board online.
/// </summary>
public sealed record RankPeriod(
    RankPeriodKind Kind,
    string PeriodId,
    string Timezone,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    string Version)
{
    /// <summary>The default unbounded period.</summary>
    public static readonly RankPeriod AllTime = new(RankPeriodKind.AllTime, "alltime", "", null, null, "alltime-1");

    /// <summary>Resolves the calendar period bucket for a moment: <see cref="RankPeriodKind.Daily"/>
    /// buckets by the calendar day in <paramref name="timezoneId"/>, <see cref="RankPeriodKind.Weekly"/>
    /// by the ISO week (Monday start) in that timezone. <see cref="RankPeriodKind.AllTime"/> returns
    /// <see cref="AllTime"/>. <see cref="RankPeriodKind.Season"/> is not derivable from a moment
    /// alone (a season's window is declared, see <see cref="Season"/>). The version is the bucket's
    /// start instant, so it is strictly monotonic across consecutive buckets.</summary>
    public static RankPeriod Resolve(RankPeriodKind kind, string timezoneId, DateTimeOffset atUtc)
    {
        if (kind == RankPeriodKind.AllTime) return AllTime;
        if (kind == RankPeriodKind.Season)
            throw new ArgumentException("A season period needs its declared window; use RankPeriod.Season.", nameof(kind));
        RequireTimezone(timezoneId);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        var local = TimeZoneInfo.ConvertTime(atUtc, timezone);
        DateTime startLocal;
        string periodId;
        if (kind == RankPeriodKind.Daily)
        {
            var day = DateOnly.FromDateTime(local.DateTime);
            startLocal = day.ToDateTime(TimeOnly.MinValue);
            periodId = "daily-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            var iso = ISOWeek.GetYear(local.DateTime);
            var week = ISOWeek.GetWeekOfYear(local.DateTime);
            startLocal = ISOWeek.ToDateTime(iso, week, DayOfWeek.Monday);
            periodId = "weekly-" + iso.ToString(CultureInfo.InvariantCulture) + "-w" + week.ToString(CultureInfo.InvariantCulture);
        }
        // The bucket start is the wall-clock boundary of the day/week in the timezone, converted
        // to UTC at that boundary's own offset — a DST transition inside the bucket never shifts
        // the boundary the wrong way. ConvertTimeToUtc treats an Unspecified DateTime as the
        // zone's local time, which is exactly the boundary semantics a bucket needs.
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, timezone));
        var endUtc = startUtc.AddDays(kind == RankPeriodKind.Daily ? 1 : 7);
        // The version is the bucket's start instant as a fixed-width count, so string comparison
        // is chronological: a later period always has a larger version, never an in-place clear.
        return new RankPeriod(kind, periodId, timezoneId, startUtc, endUtc,
            startUtc.UtcTicks.ToString("D19", CultureInfo.InvariantCulture));
    }

    /// <summary>Declares a season period: an explicitly bounded window in a timezone, with a
    /// caller-chosen version that is monotonic across the board's seasons. A season is never
    /// derived from a moment — the game defines its window.</summary>
    public static RankPeriod Season(string seasonId, string timezoneId, DateTimeOffset startUtc,
        DateTimeOffset endUtc, string version)
    {
        Require(seasonId, nameof(seasonId)); Require(version, nameof(version));
        RequireTimezone(timezoneId);
        if (endUtc <= startUtc) throw new ArgumentException("A season window must be non-empty (end after start).");
        return new RankPeriod(RankPeriodKind.Season, "season-" + seasonId, timezoneId, startUtc, endUtc, version);
    }

    /// <summary>Whether a moment falls in this period's half-open window [start, end).</summary>
    public bool Contains(DateTimeOffset atUtc) =>
        (StartUtc is not { } start || atUtc >= start) && (EndUtc is not { } end || atUtc < end);

    private static void RequireTimezone(string timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId))
            throw new ArgumentException("A period bucket needs an explicit timezone.", nameof(timezoneId));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Must not be empty.", name);
    }
}

/// <summary>
/// FIX-RANK-01: the board scope (developer-capabilities.md「作用域」). The default is the
/// game-global board; a live-session or room board names its scope explicitly, and a
/// cross-platform account merge never opens implicitly. <see cref="ScopeId"/> empty means
/// game-global.</summary>
public sealed record RankScope(string ScopeId)
{
    /// <summary>The game-global scope.</summary>
    public static readonly RankScope Global = new("");

    public bool IsGlobal => string.IsNullOrEmpty(ScopeId);
}

/// <summary>
/// FIX-RANK-01: the integer bounds a board's scores must stay inside (developer-capabilities.md
/// 「计分」「整数边界与溢出校验」). Negative scores (penalties) are only valid when
/// <see cref="AllowNegative"/> is declared — a board never silently permits a negative score it
/// did not opt into. A delta outside [<see cref="Min"/>, <see cref="Max"/>] is rejected by the
/// accumulation, never clamped (a clamped score would invent a value the game never granted).
/// The default bounds disallow negatives and do not cap the high end.</summary>
public sealed record ScoreBounds(long Min, long Max, bool AllowNegative)
{
    /// <summary>The default: non-negative scores, unbounded above.</summary>
    public static readonly ScoreBounds Default = new(0, long.MaxValue, AllowNegative: false);

    public bool Contains(long value) => value >= Min && value <= Max;

    /// <summary>Validates the bounds shape: min at most max, and a negative minimum is only
    /// allowed when negatives were explicitly opted into.</summary>
    public void Validate()
    {
        if (Min > Max) throw new ArgumentException("The score minimum must not exceed the maximum.");
        if (!AllowNegative && Min < 0)
            throw new ArgumentException("Negative scores require AllowNegative to be declared.");
    }
}

/// <summary>
/// FIX-RANK-01: one leaderboard entry, stamped with its subject type
/// (developer-capabilities.md「排名主体」「接口不混淆主播与观众主体」). Two entries that share a
/// string id but differ in <see cref="Subject"/> are different subjects — a streamer entry is
/// never equal to a viewer entry, so a board's single declared subject type cannot be silently
/// mixed with another. The score is the board's integer projection value.</summary>
public sealed record RankEntry(RankSubject Subject, string SubjectId, long Score);

/// <summary>
/// FIX-RANK-01: the deterministic board ordering (developer-capabilities.md「排序」). A tie is
/// broken by subject id ascending, so equal scores keep one stable, unique order — the tie
/// strategy is a declared rule, not whatever order a database happened to return. Ordinal ranks
/// are unique and gapless (1, 2, 3, …), never shared between equal scores.</summary>
public static class LeaderboardOrdering
{
    /// <summary>Compares two entries: score descending first, then subject id ascending, then
    /// subject type ascending as the final total-order tiebreak (a board is single-subject, so
    /// this last term only orders a genuinely mixed collection).</summary>
    public static int Compare(RankEntry left, RankEntry right)
    {
        var byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0) return byScore;
        var byId = string.CompareOrdinal(left.SubjectId, right.SubjectId);
        if (byId != 0) return byId;
        return left.Subject.CompareTo(right.Subject);
    }

    /// <summary>Assigns unique ordinal ranks 1..N to the entries in board order. The caller
    /// passes entries in the order they were produced; this method orders them and assigns the
    /// ordinal position as the rank, so equal scores get distinct consecutive ranks — the
    /// board's declared order is the only rank source.</summary>
    public static IReadOnlyList<(int Rank, RankEntry Entry)> OrdinalRanks(IEnumerable<RankEntry> entries)
    {
        var ordered = entries.ToList();
        ordered.Sort(Compare);
        var ranked = new List<(int, RankEntry)>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++) ranked.Add((i + 1, ordered[i]));
        return ranked;
    }
}

/// <summary>
/// FIX-RANK-01: the board's scoring rule (developer-capabilities.md「计分」). Accumulation is
/// checked — a <see cref="Sum"/> that would overflow the long range is rejected, not wrapped,
/// and a result outside the board's <see cref="ScoreBounds"/> is rejected, not clamped. A
/// rejected delta never changes the projection, so the visible score is always a value the
/// game actually granted within the board's declared integer range.</summary>
public static class LeaderboardScoring
{
    /// <summary>Accumulates one delta into a score under the board's aggregation and bounds.
    /// Returns false (and leaves <paramref name="next"/> unset) when the delta itself or the
    /// resulting score is outside the bounds, or when a <see cref="RankAggregation.Sum"/> would
    /// overflow. <see cref="RankAggregation.Max"/> never overflows beyond the delta itself,
    /// which the bounds already checked.</summary>
    public static bool TryAccumulate(long current, long delta, RankAggregation aggregation,
        ScoreBounds bounds, out long next)
    {
        if (!bounds.Contains(delta))
        {
            next = default;
            return false;
        }
        long value;
        if (aggregation == RankAggregation.Max)
        {
            value = Math.Max(current, delta);
        }
        else
        {
            try { value = checked(current + delta); }
            catch (OverflowException)
            {
                next = default;
                return false;
            }
        }
        if (!bounds.Contains(value))
        {
            next = default;
            return false;
        }
        next = value;
        return true;
    }
}

/// <summary>
/// FIX-RANK-01: the leaderboard board definition (developer-capabilities.md「排行榜定义」) — the
/// key (tenant/game/board/period/scope), the single subject type, the aggregation, the integer
/// bounds and the period. The key is part of the value, so two boards that differ only in
/// tenant are never equal (跨租户键隔离); the period and scope are part of the key so the same
/// board id in two periods or scopes is two distinct boards, never one board silently
/// re-banded.</summary>
public sealed record LeaderboardBoard(
    string TenantId,
    string GameId,
    string BoardId,
    RankSubject Subject,
    RankAggregation Aggregation,
    ScoreBounds Bounds,
    RankPeriod Period,
    RankScope Scope)
{
    /// <summary>The default definition: a player-summed, non-negative, unbounded, all-time,
    /// game-global board with the given ids.</summary>
    public static LeaderboardBoard Default(string tenantId, string gameId, string boardId) =>
        new(tenantId, gameId, boardId, RankSubject.Player, RankAggregation.Sum,
            ScoreBounds.Default, RankPeriod.AllTime, RankScope.Global);

    /// <summary>The identity key (tenant/game/board/period/scope) that names one board on one
    /// period in one scope — the value the storage layer keys on.</summary>
    public BoardKey Key => new(TenantId, GameId, BoardId, Period.PeriodId, Scope.ScopeId);

    /// <summary>Stamps an entry with this board's single subject type. The board declares the
    /// subject, so a caller cannot accidentally mint a streamer entry into a player board —
    /// the subject is always the board's, never the caller's guess.</summary>
    public RankEntry Entry(string subjectId, long score)
    {
        if (string.IsNullOrWhiteSpace(subjectId)) throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        if (!Bounds.Contains(score)) throw new ArgumentOutOfRangeException(nameof(score), "The score is outside the board's declared bounds.");
        return new RankEntry(Subject, subjectId, score);
    }

    /// <summary>Validates the definition: non-empty ids, a valid period and a well-formed
    /// bounds shape. A definition that does not validate is refused before it is ever stored
    /// or queried.</summary>
    public void Validate()
    {
        Require(TenantId, nameof(TenantId)); Require(GameId, nameof(GameId)); Require(BoardId, nameof(BoardId));
        Require(Period.PeriodId, nameof(Period.PeriodId));
        Bounds.Validate();
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Must not be empty.", name);
    }
}

/// <summary>
/// FIX-RANK-01: the identity key of one board on one period in one scope
/// (developer-capabilities.md「标识」). All five components are part of the value: two keys that
/// differ in any component — including tenant — are distinct boards, so a board never bleeds
/// across tenants, games, periods or scopes.</summary>
public sealed record BoardKey(
    string TenantId,
    string GameId,
    string BoardId,
    string PeriodId,
    string ScopeId);

/// <summary>
/// FIX-RANK-03: refused before any projection write (developer-capabilities.md「Score changes
/// derive from immutable committed server effects」). The effect is not a committed
/// <c>score</c>-kind server effect, or its requested delta differs from the committed one —
/// a client self-report, an unrelated asset change or an inflated delta never reaches the
/// board. This is the visible boundary the business module and the storage floor keep.</summary>
public sealed class LeaderboardRejectedException(string message) : Exception(message);

/// <summary>FIX-RANK-03: the result of projecting one committed scoring effect onto its
/// board — the board's resulting score and whether the effect had already been applied
/// (a concurrent, replayed or lost-response retry answers <see cref="Duplicate"/> and the
/// stored score, never a second delta).</summary>
public sealed record LeaderboardEffectResult(long Score, bool Duplicate);

/// <summary>
/// FIX-RANK-04: the bounded query page size (developer-capabilities.md「查询」「有限页大小」).
/// One TopN read answers at most <see cref="MaxTopEntries"/> entries — a query page is bounded,
/// never one unbounded answer over a whole board (mirrors the settlement board read's bound).
/// </summary>
public static class LeaderboardQueryLimits
{
    /// <summary>The largest TopN one read answers.</summary>
    public const int MaxTopEntries = 1000;

    /// <summary>FIX-RANK-05: the largest around-me window one read answers — the subject
    /// plus this many neighbours on each side of its rank (a bounded window, never one
    /// unbounded answer over a whole board).</summary>
    public const int MaxAroundWindow = 50;

    /// <summary>FIX-RANK-06: the bounded lifetime of one pagination snapshot
    /// (developer-capabilities.md「分页实现可返回有期限的快照版本/游标」). A snapshot is issued
    /// at a page's first read and expires this many seconds later; a continuation after the
    /// expiry is a stale read (an explicit error), even when the board has not changed — a
    /// caller cannot hold a snapshot open forever and page across an arbitrarily old view.
    /// The whole walk is bounded, not refreshed per page.</summary>
    public const int SnapshotTtlSeconds = 60;
}

/// <summary>
/// FIX-RANK-04: refused because the board changed after the caller's read version
/// (developer-capabilities.md「分页绑定榜单读取版本，榜变更时明确要求重启读取」). The caller
/// bound a paginated read to a read version it saw; the board changed since, so the page is
/// no longer one consistent view — the caller must restart the read from a fresh version,
/// never page across a change invisibly.
/// </summary>
public sealed class LeaderboardStaleReadException(string message) : Exception(message);

/// <summary>
/// FIX-RANK-04: the answer to a TopN read (developer-capabilities.md「TopN、个人名次、附近名次；
/// 有限页大小；分页绑定榜单读取版本」). The bounded page in the board's declared order, plus the
/// board's read version at the same snapshot (the last write marker of the board's entries) —
/// a paginated continuation carries <see cref="ReadVersion"/> back and a mismatch is a stale
/// read, never a cross-version page. An empty board answers an empty page and the version
/// sentinel (<see cref="DateTimeOffset.MinValue"/> — the board has never scored).
/// </summary>
public sealed record LeaderboardTopResult(IReadOnlyList<RankEntry> Entries, DateTimeOffset ReadVersion);

/// <summary>
/// FIX-RANK-05: one entry with its unique ordinal rank in the board's declared order
/// (developer-capabilities.md「排序」「个人名次」— RANK-01 <see cref="LeaderboardOrdering"/>:
/// score descending, subject id ascending; ranks are unique and gapless 1..N).</summary>
public sealed record RankedEntry(int Rank, RankEntry Entry);

/// <summary>
/// FIX-RANK-05: one subject's position on a board — its entry, its unique ordinal rank and
/// the board's read version at the same snapshot (developer-capabilities.md「实时变化榜的个人
/// 名次必须对应同一次读取视图」). The rank and the score are one consistent view, never a rank
/// from a different version than the score. Null (the read answers none) when the subject
/// has not scored on the board.
/// </summary>
public sealed record LeaderboardRankResult(int Rank, RankEntry Entry, DateTimeOffset ReadVersion);

/// <summary>
/// FIX-RANK-05: the around-me answer (developer-capabilities.md「个人名次、附近名次」) — the
/// entries within the bounded window of a subject's rank (the subject included), in rank
/// order, plus the read version of the same snapshot. The subject has not scored on the
/// board when the read answers none.
/// </summary>
public sealed record LeaderboardAroundResult(IReadOnlyList<RankedEntry> Entries, DateTimeOffset ReadVersion);

/// <summary>
/// FIX-RANK-06: one page of a paginated TopN walk (developer-capabilities.md「稳定分页」「分页
/// 实现可返回有期限的快照版本/游标」) — the bounded page in the board's declared order, the
/// board's read version at the same snapshot, whether more entries remain, and the opaque
/// continuation cursor that carries the snapshot's write version and bounded lifetime. The
/// <see cref="NextCursor"/> is non-empty exactly when <see cref="HasMore"/> — the store read
/// one row past the page bound to know, so a continuation that would answer an empty page is
/// never issued. A continuation with <see cref="NextCursor"/> is valid only while the board's
/// version is unchanged and the snapshot lifetime has not expired; otherwise the read is a
/// stale read (<see cref="LeaderboardStaleReadException"/>), never a cross-version
/// seemingly-contiguous page.</summary>
public sealed record LeaderboardPageResult(
    IReadOnlyList<RankEntry> Entries, DateTimeOffset ReadVersion, bool HasMore, string? NextCursor);

/// <summary>
/// FIX-RANK-06: the opaque pagination cursor. The data plane issues it with a page (board
/// identity + the snapshot's write version + its bounded lifetime + where to resume) and
/// validates it on continuation, so a walk can only continue within one board, one write
/// version and one lifetime window. The encode/parse/validate are pure and live in the
/// contract layer (unit-testable without a cluster); the data plane only calls them.</summary>
public static class LeaderboardCursor
{
    /// <summary>Encodes a cursor into its opaque string form (base64 of the proto bytes).</summary>
    public static string Encode(LeaderboardCursorProto cursor) => cursor.ToByteString().ToBase64();

    /// <summary>Parses a cursor's opaque string form back into its proto. Returns false when
    /// the token is not a well-formed cursor (a malformed token is refused, never treated as a
    /// first page — that would silently stitch a new snapshot over an old walk).</summary>
    public static bool TryParse(string token, out LeaderboardCursorProto? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var bytes = Convert.FromBase64String(token);
            var parsed = LeaderboardCursorProto.Parser.ParseFrom(bytes);
            if (string.IsNullOrEmpty(parsed.BoardId) || string.IsNullOrEmpty(parsed.LastSubjectId))
                return false;
            cursor = parsed;
            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidProtocolBufferException)
        {
            return false;
        }
    }

    /// <summary>Whether a continuation is still one consistent snapshot: the cursor names the
    /// same board, its write version equals the board's current version, and the snapshot
    /// lifetime has not expired. The returned reason is the stale-read detail.</summary>
    public static bool IsValid(LeaderboardCursorProto cursor, string tenantId, string gameId,
        string boardId, string writeVersion, DateTimeOffset nowUtc, out string reason)
    {
        if (!string.Equals(cursor.TenantId, tenantId, StringComparison.Ordinal)
            || !string.Equals(cursor.GameId, gameId, StringComparison.Ordinal)
            || !string.Equals(cursor.BoardId, boardId, StringComparison.Ordinal))
        {
            reason = "The cursor belongs to another board.";
            return false;
        }
        if (!string.Equals(cursor.WriteVersion, writeVersion, StringComparison.Ordinal))
        {
            reason = "The board changed since the cursor's read version; restart the read.";
            return false;
        }
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(cursor.DeadlineUtcMs);
        if (nowUtc > deadline)
        {
            reason = "The snapshot expired; restart the read.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Makes a continuation cursor for the next page of the same snapshot: the last
    /// entry read becomes the resume keyset, and the lifetime is the one the walk began with
    /// (a fresh <paramref name="freshDeadlineUtc"/> on a first page, the same deadline on a
    /// continuation) — the whole walk is bounded, never refreshed per page.</summary>
    public static LeaderboardCursorProto Make(string tenantId, string gameId, string boardId,
        string writeVersion, DateTimeOffset deadlineUtc, RankEntry last)
    {
        if (string.IsNullOrWhiteSpace(last.SubjectId))
            throw new ArgumentException("A cursor needs a real last entry.", nameof(last));
        return new LeaderboardCursorProto
        {
            TenantId = tenantId, GameId = gameId, BoardId = boardId,
            WriteVersion = writeVersion, DeadlineUtcMs = deadlineUtc.ToUnixTimeMilliseconds(),
            LastScore = last.Score, LastSubjectId = last.SubjectId
        };
    }
}
