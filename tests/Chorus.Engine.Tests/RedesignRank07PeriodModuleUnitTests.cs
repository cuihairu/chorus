using Chorus.ControlServer;
using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-07, the period attribution business rule (developer-capabilities.md 「周期」
/// 「迟到结算有明确的周期归属」): a committed score effect belongs to the period of its immutable
/// source time — never to the period of its projection or retry time, which would silently move
/// a late effect to the wrong bucket. A periodic family buckets by its declared timezone and
/// declares how late after a period closes a settlement is still attributed to that period;
/// beyond the window the effect is refused, never re-attributed to the moment it was processed.
/// An AllTime family is unbounded and never late. The daily example family lands its effects on
/// a distinct board id per period family, so one day's scores never bleed into another day's.
/// </summary>
public sealed class RedesignRank07PeriodModuleUnitTests
{
    private static readonly DateTimeOffset Source = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static LeaderboardDailyFactionScoringPolicy Daily() => new();

    [Fact]
    public void ResolvePeriodBucketsByTheSourceMoment()
    {
        var period = Daily().ResolvePeriod(Source, new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(RankPeriodKind.Daily, period.Kind);
        Assert.Equal("daily-2026-11-05", period.PeriodId);
        Assert.Equal("UTC", period.Timezone);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), period.EndUtc);
        // The version is the bucket's start instant, monotonic across consecutive buckets.
        Assert.True(period.Version.Length > 0);
    }

    [Fact]
    public void ResolvePeriodAttributesALateEffectToItsSourceBucket()
    {
        // The source day closed at 2026-11-06T00:00Z; the effect is projected the next
        // morning, within the declared 48-hour window — it belongs to its source bucket,
        // never to the day it was processed.
        var period = Daily().ResolvePeriod(
            new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 6, 5, 0, 0, TimeSpan.Zero));
        Assert.Equal("daily-2026-11-05", period.PeriodId);
    }

    [Fact]
    public void ResolvePeriodRefusesAnEffectBeyondTheLateWindow()
    {
        // The source day closed at 2026-11-06T00:00Z; the 48-hour window ends at
        // 2026-11-08T00:00Z. An effect projected after that is refused — it is not moved to
        // the bucket of the moment it was processed.
        Assert.Throws<LeaderboardRejectedException>(() => Daily().ResolvePeriod(
            Source, new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ResolvePeriodOfTheAllTimeBaseIsNeverLate()
    {
        // The base family is AllTime and unbounded — a source time from long ago still
        // projects, never refused.
        var policy = new LeaderboardScoringPolicy();
        var period = policy.ResolvePeriod(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(RankPeriod.AllTime, period);
    }

    [Fact]
    public async Task AProjectedEffectCarriesItsSourceBucketsPeriod()
    {
        var source = DateTimeOffset.UtcNow.AddHours(-2);
        var plane = new FakePlane(new CommittedScoreEffectReply
        {
            Found = true, Kind = LeaderboardScoringService.ScoreEffectKind,
            PayloadJson = """{"faction":"red","delta":7,"total":7}""",
            CommittedAt = source.ToString("O")
        });
        var module = new LeaderboardScoringService(plane, Daily());

        var result = await module.ProjectScoreEffectAsync("tenant-a", "game-1", "room-1/1/0");

        Assert.Equal(7, result.Score);
        var applied = Assert.IsType<ApplyScoreEffectRequest>(plane.Applied);
        Assert.Equal("faction-score-daily", applied.Board.BoardId);
        Assert.Equal((int)RankPeriodKind.Daily, applied.Board.PeriodKind);
        // The board's period is the source moment's bucket — the module attributed it from
        // the committed source time, never from the projection time.
        var expected = Daily().ResolvePeriod(source, DateTimeOffset.UtcNow);
        Assert.Equal(expected.PeriodId, applied.Board.PeriodId);
        Assert.Equal(expected.StartUtc?.ToString("O"), applied.Board.PeriodStartUtc);
        Assert.Equal(expected.EndUtc?.ToString("O"), applied.Board.PeriodEndUtc);
        Assert.Equal("room-1", applied.Board.ScopeId);
    }

    [Fact]
    public async Task AScoreEffectWithoutASourceTimeIsRefused()
    {
        var plane = new FakePlane(new CommittedScoreEffectReply
        {
            Found = true, Kind = LeaderboardScoringService.ScoreEffectKind,
            PayloadJson = """{"faction":"red","delta":7,"total":7}""",
            CommittedAt = ""
        });
        var module = new LeaderboardScoringService(plane, Daily());
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => module.ProjectScoreEffectAsync("tenant-a", "game-1", "room-1/1/0"));
        Assert.Null(plane.Applied);
    }

    private sealed class FakePlane(CommittedScoreEffectReply committed) : ILeaderboardScoringDataPlane
    {
        public ApplyScoreEffectRequest? Applied { get; private set; }

        public Task<CommittedScoreEffectReply> ReadCommittedScoreEffectAsync(
            ReadCommittedScoreEffectRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(committed);

        public Task<ApplyScoreEffectReply> ApplyScoreEffectAsync(
            ApplyScoreEffectRequest request, CancellationToken cancellationToken = default)
        {
            Applied = request;
            return Task.FromResult(new ApplyScoreEffectReply { Score = 7 });
        }
    }
}
