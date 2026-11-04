using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-01, the board metadata contract (developer-capabilities.md「排行榜定义」): the
/// key isolates tenants (and periods and scopes), negative scores and overflow are controlled
/// (declared opt-in and checked arithmetic, never clamped), equal scores keep one stable order
/// with unique gapless ranks, and the interface never confuses a streamer subject with a viewer
/// subject — the subject type rides on every entry and the board stamps it.
/// </summary>
public sealed class RedesignRank01MetadataUnitTests
{
    private static DateTimeOffset At(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    // ── 跨租户键隔离 ────────────────────────────────────────────────────────────────

    [Fact]
    public void BoardsThatDifferOnlyByTenantAreNeverEqual()
    {
        var a = LeaderboardBoard.Default("tenant-a", "game-1", "board-1");
        var b = LeaderboardBoard.Default("tenant-b", "game-1", "board-1");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void TheKeyNamesOneBoardOnOnePeriodInOneScope()
    {
        var allTime = LeaderboardBoard.Default("tenant-a", "game-1", "board-1");
        var daily = allTime with { Period = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", At(2026, 11, 4)) };
        var scoped = allTime with { Scope = new RankScope("room-9") };
        Assert.NotEqual(allTime.Key, daily.Key);
        Assert.NotEqual(allTime.Key, scoped.Key);
        Assert.Equal(("tenant-a", "game-1", "board-1", "alltime", ""),
            (allTime.Key.TenantId, allTime.Key.GameId, allTime.Key.BoardId, allTime.Key.PeriodId, allTime.Key.ScopeId));
    }

    // ── 负分与溢出受控 ───────────────────────────────────────────────────────────────

    [Fact]
    public void NegativeScoresAreRejectedUnlessDeclared()
    {
        var bounds = ScoreBounds.Default;
        Assert.False(bounds.Contains(-1));
        Assert.False(LeaderboardScoring.TryAccumulate(0, -5, RankAggregation.Sum, bounds, out _));
        // A negative minimum without the opt-in is a malformed definition.
        Assert.Throws<ArgumentException>(() => new ScoreBounds(-10, 100, AllowNegative: false).Validate());
        // With the opt-in, a penalty is a legal delta and a legal score.
        var penalties = new ScoreBounds(-10, 100, AllowNegative: true);
        penalties.Validate();
        Assert.True(LeaderboardScoring.TryAccumulate(0, -5, RankAggregation.Sum, penalties, out var scored));
        Assert.Equal(-5, scored);
    }

    [Fact]
    public void OverflowIsRejectedNotWrapped()
    {
        var bounds = new ScoreBounds(0, long.MaxValue, AllowNegative: false);
        Assert.False(LeaderboardScoring.TryAccumulate(long.MaxValue, 1, RankAggregation.Sum, bounds, out _));
        // The edge itself is still a legal state: adding zero never overflows.
        Assert.True(LeaderboardScoring.TryAccumulate(long.MaxValue, 0, RankAggregation.Sum, bounds, out var same));
        Assert.Equal(long.MaxValue, same);
    }

    [Fact]
    public void OutOfBoundsResultsAreRejectedNotClamped()
    {
        var bounds = new ScoreBounds(0, 100, AllowNegative: false);
        // A sum past the declared maximum is refused, never silently clamped to it.
        Assert.False(LeaderboardScoring.TryAccumulate(50, 60, RankAggregation.Sum, bounds, out _));
        // A delta beyond the bounds is refused before it is ever combined.
        Assert.False(LeaderboardScoring.TryAccumulate(50, 101, RankAggregation.Max, bounds, out _));
        // Max keeps the larger of the two legal values.
        Assert.True(LeaderboardScoring.TryAccumulate(50, 60, RankAggregation.Max, bounds, out var kept));
        Assert.Equal(60, kept);
        Assert.True(LeaderboardScoring.TryAccumulate(50, 40, RankAggregation.Max, bounds, out var smaller));
        Assert.Equal(50, smaller);
    }

    // ── 同分稳定排序 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EqualScoresOrderBySubjectIdAscendingWithUniqueGaplessRanks()
    {
        var ranked = LeaderboardOrdering.OrdinalRanks(
        [
            new RankEntry(RankSubject.Player, "b", 100),
            new RankEntry(RankSubject.Player, "c", 100),
            new RankEntry(RankSubject.Player, "d", 50),
            new RankEntry(RankSubject.Player, "a", 100)
        ]);
        // The higher score first (a,b,c at 100, then d at 50); the three-way tie keeps
        // subject-id ascending order; ranks are unique and gapless 1..4.
        Assert.Equal([1, 2, 3, 4], ranked.Select(r => r.Rank));
        Assert.Equal(["a", "b", "c", "d"], ranked.Select(r => r.Entry.SubjectId));
        Assert.Equal(100, ranked[0].Entry.Score);
        Assert.Equal(50, ranked[3].Entry.Score);
    }

    // ── 接口不混淆主播与观众主体 ─────────────────────────────────────────────────────

    [Fact]
    public void AStreamerEntryIsNeverEqualToAPlayerEntryWithTheSameId()
    {
        var player = new RankEntry(RankSubject.Player, "same-id", 10);
        var streamer = new RankEntry(RankSubject.Streamer, "same-id", 10);
        Assert.NotEqual(player, streamer);
        Assert.NotEqual(player.Subject, streamer.Subject);
    }

    [Fact]
    public void EntriesAreStampedWithTheBoardsSubjectNotTheCallersGuess()
    {
        var board = LeaderboardBoard.Default("tenant-a", "game-1", "board-1");
        Assert.Equal(RankSubject.Player, board.Subject);
        var entry = board.Entry("viewer-1", 5);
        Assert.Equal(RankSubject.Player, entry.Subject);
        Assert.Equal("viewer-1", entry.SubjectId);
        // A score outside the declared bounds never mints an entry.
        Assert.Throws<ArgumentOutOfRangeException>(() => board.Entry("viewer-1", -1));
        Assert.Throws<ArgumentException>(() => board.Entry("", 5));
    }

    // ── 周期 ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalendarBucketsAreDerivedFromTheMomentWithMonotonicVersions()
    {
        var morning = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", At(2026, 11, 4, 9));
        var evening = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", At(2026, 11, 4, 23));
        Assert.Equal(morning, evening); // the same day is one bucket

        var nextDay = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", At(2026, 11, 5));
        Assert.NotEqual(morning, nextDay);
        Assert.True(string.CompareOrdinal(nextDay.Version, morning.Version) > 0); // monotonic
        Assert.Equal(At(2026, 11, 5), nextDay.StartUtc!.Value); // half-open [start, end)
        Assert.Equal(At(2026, 11, 6), nextDay.EndUtc!.Value);

        var week = RankPeriod.Resolve(RankPeriodKind.Weekly, "UTC", At(2026, 11, 4));
        Assert.StartsWith("weekly-", week.PeriodId);
        Assert.Equal(DayOfWeek.Monday, week.StartUtc!.Value.DayOfWeek);

        Assert.Equal(RankPeriod.AllTime, RankPeriod.Resolve(RankPeriodKind.AllTime, "UTC", At(2026, 11, 4)));
        Assert.True(RankPeriod.AllTime.Contains(At(1990, 1, 1)));
        Assert.True(RankPeriod.AllTime.Contains(At(2200, 1, 1)));
    }

    [Fact]
    public void AMomentBucketsByTheBoardsTimezoneNotUtc()
    {
        // 2026-11-04 23:30 UTC is already 2026-11-05 07:30 in Asia/Shanghai (+8, no DST).
        var utc = RankPeriod.Resolve(RankPeriodKind.Daily, "UTC", At(2026, 11, 4, 23).AddMinutes(30));
        var shanghai = RankPeriod.Resolve(RankPeriodKind.Daily, "Asia/Shanghai", At(2026, 11, 4, 23).AddMinutes(30));
        Assert.Equal("daily-2026-11-04", utc.PeriodId);
        Assert.Equal("daily-2026-11-05", shanghai.PeriodId);
    }

    [Fact]
    public void ASeasonIsADeclaredWindowNotADerivedBucket()
    {
        var season = RankPeriod.Season("s1", "UTC", At(2026, 1, 1), At(2026, 2, 1), "2026-s1");
        Assert.Equal("season-s1", season.PeriodId);
        Assert.True(season.Contains(At(2026, 1, 15)));
        Assert.False(season.Contains(At(2026, 2, 1))); // the end is exclusive
        Assert.False(season.Contains(At(2025, 12, 31)));
        Assert.Throws<ArgumentException>(() =>
            RankPeriod.Season("s2", "UTC", At(2026, 2, 1), At(2026, 1, 1), "bad"));
        // A season is not derivable from a moment alone — the game declares its window.
        Assert.Throws<ArgumentException>(() =>
            RankPeriod.Resolve(RankPeriodKind.Season, "UTC", At(2026, 1, 15)));
    }

    // ── 定义校验 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADefinitionWithoutIdsOrWithABadShapeIsRefused()
    {
        Assert.Throws<ArgumentException>(() => LeaderboardBoard.Default("", "game-1", "board-1").Validate());
        Assert.Throws<ArgumentException>(() => LeaderboardBoard.Default("tenant-a", "", "board-1").Validate());
        Assert.Throws<ArgumentException>(() => LeaderboardBoard.Default("tenant-a", "game-1", "").Validate());
        Assert.Throws<ArgumentException>(() => new ScoreBounds(10, 0, AllowNegative: false).Validate());
        Assert.Throws<ArgumentException>(() => RankPeriod.Resolve(RankPeriodKind.Daily, "", At(2026, 11, 4)));
    }

    [Fact]
    public void TheDefaultDefinitionValidates()
    {
        LeaderboardBoard.Default("tenant-a", "game-1", "board-1").Validate();
    }
}
