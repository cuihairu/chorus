using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-04, the ControlServer TopN query business module (developer-capabilities.md
/// 「查询」「有限页大小；分页绑定榜单读取版本」): the caller names the board and the page size;
/// the module validates the request (tenant, board shape, bounded page) and asks the data
/// plane to read, mapping a stale read and an invalid read to the business exceptions. The
/// module selects and validates; the data plane re-authorizes by role and tenant.
/// </summary>
public sealed class RedesignRank04QueryModuleUnitTests
{
    private static LeaderboardBoard Board() => LeaderboardBoard.Default("tenant-a", "game-1", "board-1");

    [Fact]
    public async Task AValidQueryAnswersEntriesAndVersion()
    {
        var plane = new FakePlane(new TopNReply
        {
            BoardId = "board-1", ReadVersion = "2026-01-01T00:00:00+00:00"
        });
        plane.Reply.Entries.Add(new RankEntryProto
        {
            Subject = (int)RankSubject.Player, SubjectId = "a", Score = 5
        });
        var module = new LeaderboardQueryService(plane);

        var result = await module.GetTopAsync("tenant-a", Board(), 10);

        Assert.Equal(5, result.Entries[0].Score);
        Assert.Equal("a", result.Entries[0].SubjectId);
        Assert.Equal(RankSubject.Player, result.Entries[0].Subject);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture), result.ReadVersion);
        // The data plane was asked for the caller's board and bounded page.
        var request = plane.Request;
        Assert.NotNull(request);
        Assert.Equal("tenant-a", request!.TenantId);
        Assert.Equal("game-1", request.GameId);
        Assert.Equal("board-1", request.Board.BoardId);
        Assert.Equal((int)RankSubject.Player, request.Board.Subject);
        Assert.Equal(10, request.MaxEntries);
        Assert.Equal("", request.ExpectedVersion);
    }

    [Fact]
    public async Task AnEmptyTenantOrInvalidBoardIsRefusedBeforeTheDataPlane()
    {
        var plane = new FakePlane(new TopNReply());
        var module = new LeaderboardQueryService(plane);

        await Assert.ThrowsAsync<ArgumentException>(() => module.GetTopAsync("", Board(), 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => module.GetTopAsync("tenant-a", LeaderboardBoard.Default("tenant-a", "game-1", ""), 10));
        Assert.Null(plane.Request);
    }

    [Fact]
    public async Task AnOutOfBoundsPageSizeIsRefusedBeforeTheDataPlane()
    {
        var plane = new FakePlane(new TopNReply());
        var module = new LeaderboardQueryService(plane);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => module.GetTopAsync("tenant-a", Board(), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.GetTopAsync("tenant-a", Board(), LeaderboardQueryLimits.MaxTopEntries + 1));
        Assert.Null(plane.Request);
    }

    [Fact]
    public async Task AStaleVersionMapsToAStaleReadException()
    {
        var plane = new FakePlane(new TopNReply())
        {
            Fault = new RpcException(new Status(StatusCode.FailedPrecondition, "board changed"))
        };
        var module = new LeaderboardQueryService(plane);
        await Assert.ThrowsAsync<LeaderboardStaleReadException>(
            () => module.GetTopAsync("tenant-a", Board(), 10, "stale-version"));
    }

    [Fact]
    public async Task AnInvalidReadMapsToAnArgumentException()
    {
        var plane = new FakePlane(new TopNReply())
        {
            Fault = new RpcException(new Status(StatusCode.InvalidArgument, "bad board"))
        };
        var module = new LeaderboardQueryService(plane);
        await Assert.ThrowsAsync<ArgumentException>(
            () => module.GetTopAsync("tenant-a", Board(), 10));
    }

    private sealed class FakePlane(TopNReply reply) : ILeaderboardQueryDataPlane
    {
        public TopNReply Reply { get; } = reply;
        public RpcException? Fault { get; init; }
        public GetTopRequest? Request { get; private set; }

        public Task<TopNReply> GetTopAsync(GetTopRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Fault is not null) throw Fault;
            return Task.FromResult(Reply);
        }

        // RANK-04 tests only exercise the TopN path; the rank paths are RANK-05's tests.
        public Task<RankReply> GetRankAsync(GetRankRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AroundReply> GetAroundMeAsync(GetAroundMeRequest request, CancellationToken cancellationToken = default) =>
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
