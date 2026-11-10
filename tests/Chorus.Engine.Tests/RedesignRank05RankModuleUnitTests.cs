using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-05, the ControlServer personal-rank and around-me business module
/// (developer-capabilities.md「查询」「个人名次、附近名次」「实时变化榜的个人名次必须对应同一次读取视图」):
/// the caller names the board and the subject; the module validates the request (tenant, board
/// shape, subject, bounded window) and asks the data plane to read, mapping an invalid read to
/// the business exception. An absent subject is null, never a thrown rank.
/// </summary>
public sealed class RedesignRank05RankModuleUnitTests
{
    private static LeaderboardBoard Board() => LeaderboardBoard.Default("tenant-a", "game-1", "board-1");

    [Fact]
    public async Task AValidRankQueryAnswersRankEntryAndVersion()
    {
        var plane = new FakePlane(new RankReply
        {
            Found = true, Rank = 2, ReadVersion = "2026-01-01T00:00:00+00:00",
            Entry = new RankEntryProto { Subject = (int)RankSubject.Player, SubjectId = "b", Score = 8 }
        });
        var module = new LeaderboardQueryService(plane);

        var result = await module.GetRankAsync("tenant-a", Board(), "b");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Rank);
        Assert.Equal("b", result.Entry.SubjectId);
        Assert.Equal(8, result.Entry.Score);
        Assert.Equal(RankSubject.Player, result.Entry.Subject);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture), result.ReadVersion);
        // The data plane was asked for the caller's board and the subject.
        var request = plane.RankRequest;
        Assert.NotNull(request);
        Assert.Equal("tenant-a", request!.TenantId);
        Assert.Equal("game-1", request.GameId);
        Assert.Equal("board-1", request.Board.BoardId);
        Assert.Equal((int)RankSubject.Player, request.Board.Subject);
        Assert.Equal("b", request.SubjectId);
    }

    [Fact]
    public async Task AnAbsentSubjectAnswersNullNotAThrownRank()
    {
        var plane = new FakePlane(new RankReply { Found = false });
        var module = new LeaderboardQueryService(plane);
        Assert.Null(await module.GetRankAsync("tenant-a", Board(), "absent"));
    }

    [Fact]
    public async Task AnEmptyTenantOrBoardOrSubjectIsRefusedBeforeTheDataPlane()
    {
        var plane = new FakePlane(new RankReply { Found = true });
        var module = new LeaderboardQueryService(plane);

        await Assert.ThrowsAsync<ArgumentException>(() => module.GetRankAsync("", Board(), "a"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => module.GetRankAsync("tenant-a", LeaderboardBoard.Default("tenant-a", "game-1", ""), "a"));
        await Assert.ThrowsAsync<ArgumentException>(() => module.GetRankAsync("tenant-a", Board(), ""));
        Assert.Null(plane.RankRequest);
    }

    [Fact]
    public async Task AValidAroundQueryAnswersTheWindow()
    {
        var plane = new FakePlane(aroundReply: new AroundReply
        {
            Found = true, ReadVersion = "2026-01-01T00:00:00+00:00"
        });
        plane.AroundReply.Entries.Add(new RankedEntryProto
        {
            Rank = 1, Entry = new RankEntryProto { Subject = (int)RankSubject.Player, SubjectId = "a", Score = 10 }
        });
        plane.AroundReply.Entries.Add(new RankedEntryProto
        {
            Rank = 2, Entry = new RankEntryProto { Subject = (int)RankSubject.Player, SubjectId = "b", Score = 8 }
        });
        var module = new LeaderboardQueryService(plane);

        var result = await module.GetAroundMeAsync("tenant-a", Board(), "b", 1);

        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 2 }, result!.Entries.Select(e => e.Rank));
        Assert.Equal(new[] { "a", "b" }, result.Entries.Select(e => e.Entry.SubjectId));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture), result.ReadVersion);
        var request = plane.AroundRequest;
        Assert.NotNull(request);
        Assert.Equal("b", request!.SubjectId);
        Assert.Equal(1, request.Window);
    }

    [Fact]
    public async Task AnAroundWindowOutsideTheBoundIsRefusedBeforeTheDataPlane()
    {
        var plane = new FakePlane(aroundReply: new AroundReply { Found = true });
        var module = new LeaderboardQueryService(plane);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.GetAroundMeAsync("tenant-a", Board(), "b", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.GetAroundMeAsync("tenant-a", Board(), "b", LeaderboardQueryLimits.MaxAroundWindow + 1));
        Assert.Null(plane.AroundRequest);
    }

    [Fact]
    public async Task AnInvalidReadMapsToAnArgumentException()
    {
        var plane = new FakePlane(new RankReply { Found = true })
        {
            Fault = new RpcException(new Status(StatusCode.InvalidArgument, "bad board"))
        };
        var module = new LeaderboardQueryService(plane);
        await Assert.ThrowsAsync<ArgumentException>(() => module.GetRankAsync("tenant-a", Board(), "a"));
    }

    private sealed class FakePlane(RankReply? rankReply = null, AroundReply? aroundReply = null)
        : ILeaderboardQueryDataPlane
    {
        public RankReply RankReply { get; } = rankReply ?? new RankReply { Found = true };
        public AroundReply AroundReply { get; } = aroundReply ?? new AroundReply { Found = true };
        public RpcException? Fault { get; init; }
        public GetRankRequest? RankRequest { get; private set; }
        public GetAroundMeRequest? AroundRequest { get; private set; }

        public Task<RankReply> GetRankAsync(GetRankRequest request, CancellationToken cancellationToken = default)
        {
            RankRequest = request;
            if (Fault is not null) throw Fault;
            return Task.FromResult(RankReply);
        }

        public Task<AroundReply> GetAroundMeAsync(GetAroundMeRequest request, CancellationToken cancellationToken = default)
        {
            AroundRequest = request;
            if (Fault is not null) throw Fault;
            return Task.FromResult(AroundReply);
        }

        // RANK-05 tests only exercise the rank paths; the TopN path is RANK-04's tests.
        public Task<TopNReply> GetTopAsync(GetTopRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // The RANK-08 command paths are FIX-WEB-14's tests.
        public Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
