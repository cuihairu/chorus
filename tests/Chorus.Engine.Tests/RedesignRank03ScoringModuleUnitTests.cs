using Chorus.ControlServer;
using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-03, the ControlServer scoring business module (developer-capabilities.md 「A
/// business leaderboard/settlement module within ControlServer validates its source and
/// selects board/period/scope」). The module's only entry names an effect id — never a score
/// value: the source, the subject, the delta and the board all come from the committed
/// evidence and the declared mapping, so a client self-report or an unrelated asset change
/// is refused before any projection, and the caller cannot choose a board or an amount.
/// </summary>
public sealed class RedesignRank03ScoringModuleUnitTests
{
    [Fact]
    public async Task ACommittedScoreEffectProjectsOntoTheDeclaredBoard()
    {
        var plane = new FakePlane(new CommittedScoreEffectReply
        {
            Found = true, Kind = LeaderboardScoringService.ScoreEffectKind,
            PayloadJson = """{"faction":"red","delta":7,"total":7}"""
        });
        var module = new LeaderboardScoringService(plane);

        var result = await module.ProjectScoreEffectAsync("tenant-a", "game-1", "room-1/1/0");

        Assert.Equal(7, result.Score);
        Assert.False(result.Duplicate);
        var applied = Assert.IsType<ApplyScoreEffectRequest>(plane.Applied);
        Assert.Equal("room-1/1/0", applied.EffectId);
        Assert.Equal("tenant-a", applied.TenantId);
        Assert.Equal("game-1", applied.GameId);
        // The subject and the delta come from the committed payload, not from the caller.
        Assert.Equal("red", applied.SubjectId);
        Assert.Equal(7, applied.Delta);
        // The board is the declared mapping — the caller never names one.
        Assert.Equal("faction-score", applied.Board.BoardId);
        Assert.Equal((int)RankSubject.Faction, applied.Board.Subject);
        Assert.Equal((int)RankAggregation.Sum, applied.Board.Aggregation);
        Assert.Equal("room-1", applied.Board.ScopeId);
        Assert.Equal((int)RankPeriodKind.AllTime, applied.Board.PeriodKind);
        Assert.Equal("alltime", applied.Board.PeriodId);
    }

    [Fact]
    public async Task AClientSelfReportOrAnUnrelatedEffectIsRefused()
    {
        // A score naming an effect the server never committed (a client self-report) —
        // the source read answers not-found, the projection is refused.
        var notCommitted = new FakePlane(new CommittedScoreEffectReply { Found = false });
        var module = new LeaderboardScoringService(notCommitted);
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => module.ProjectScoreEffectAsync("tenant-a", "game-1", "room-1/1/0"));
        Assert.Null(notCommitted.Applied);

        // An unrelated committed effect (an asset change, a like) never enters a board.
        var like = new FakePlane(new CommittedScoreEffectReply
        {
            Found = true, Kind = "like", PayloadJson = """{"faction":"red","count":3}"""
        });
        var likeModule = new LeaderboardScoringService(like);
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => likeModule.ProjectScoreEffectAsync("tenant-a", "game-1", "room-1/1/0"));
        Assert.Null(like.Applied);
    }

    [Fact]
    public async Task ACommittedPayloadWithoutTheDeclaredShapeIsRefused()
    {
        var plane = new FakePlane(new CommittedScoreEffectReply
        {
            Found = true, Kind = LeaderboardScoringService.ScoreEffectKind,
            PayloadJson = """{"n":3}"""
        });
        var module = new LeaderboardScoringService(plane);
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => module.ProjectScoreEffectAsync("tenant-a", "game-1", "room-1/1/0"));
        Assert.Null(plane.Applied);
    }

    [Fact]
    public async Task AnEffectIdWithoutARoomSegmentIsRefused()
    {
        var plane = new FakePlane(new CommittedScoreEffectReply
        {
            Found = true, Kind = LeaderboardScoringService.ScoreEffectKind,
            PayloadJson = """{"faction":"red","delta":7,"total":7}"""
        });
        var module = new LeaderboardScoringService(plane);
        // The board scope is the effect's room; an id without one cannot be resolved.
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => module.ProjectScoreEffectAsync("tenant-a", "game-1", "no-room-segment"));
        Assert.Null(plane.Applied);
    }

    [Fact]
    public void TheDeclaredPolicyReadsTheScoreAndResolvesTheRoomScope()
    {
        var policy = new LeaderboardScoringPolicy();
        using var payload = System.Text.Json.JsonDocument.Parse("""{"faction":"blue","delta":4,"total":4}""");
        Assert.True(policy.TryReadScore(payload.RootElement, out var subject, out var delta));
        Assert.Equal("blue", subject);
        Assert.Equal(4, delta);

        var board = policy.ResolveBoard("tenant-a", "game-1", "room-42/7/1");
        Assert.Equal("room-42", board.Scope.ScopeId);
        Assert.Equal("faction-score", board.BoardId);
        Assert.Equal(RankSubject.Faction, board.Subject);
        Assert.Equal(RankAggregation.Sum, board.Aggregation);
        Assert.Equal(RankPeriod.AllTime, board.Period);

        // A payload without the declared shape is not a score this family projects.
        using var empty = System.Text.Json.JsonDocument.Parse("""{"n":3}""");
        Assert.False(policy.TryReadScore(empty.RootElement, out _, out _));
    }

    private sealed class FakePlane(CommittedScoreEffectReply committed) : ILeaderboardScoringDataPlane
    {
        public CommittedScoreEffectReply Committed { get; } = committed;
        public ApplyScoreEffectRequest? Applied { get; private set; }

        public Task<CommittedScoreEffectReply> ReadCommittedScoreEffectAsync(
            ReadCommittedScoreEffectRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed);

        public Task<ApplyScoreEffectReply> ApplyScoreEffectAsync(
            ApplyScoreEffectRequest request, CancellationToken cancellationToken = default)
        {
            Applied = request;
            return Task.FromResult(new ApplyScoreEffectReply { Score = 7 });
        }
    }
}
