using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-06, the ControlServer stable-pagination business module (developer-capabilities.md
/// 「稳定分页」「分页实现可返回有期限的快照版本/游标」): the caller names the board and the page
/// size and passes the continuation cursor the previous page carried; the module validates the
/// request (tenant, board shape, bounded page) and asks the data plane to read, mapping a stale
/// read (a board change or an expired snapshot) and an invalid read to the business exceptions.
/// The next page's cursor is surfaced exactly when more entries remain.
/// </summary>
public sealed class RedesignRank06PaginationModuleUnitTests
{
    private static LeaderboardBoard Board() => LeaderboardBoard.Default("tenant-a", "game-1", "board-1");

    [Fact]
    public async Task AFirstPageAnswersEntriesVersionAndNextCursor()
    {
        var plane = new FakePlane("cursor-for-page-2");
        plane.Reply.Entries.Add(new RankEntryProto
        {
            Subject = (int)RankSubject.Player, SubjectId = "a", Score = 10
        });
        var module = new LeaderboardQueryService(plane);

        var result = await module.GetTopPageAsync("tenant-a", Board(), 10);

        Assert.Equal("a", result.Entries[0].SubjectId);
        Assert.Equal(10, result.Entries[0].Score);
        Assert.Equal(RankSubject.Player, result.Entries[0].Subject);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture), result.ReadVersion);
        Assert.True(result.HasMore);
        Assert.Equal("cursor-for-page-2", result.NextCursor);
        // A first page asks the data plane without a cursor.
        var request = plane.Request;
        Assert.NotNull(request);
        Assert.Equal("", request!.Cursor);
        Assert.Equal(10, request.MaxEntries);
    }

    [Fact]
    public async Task AContinuationPassesTheCursorVerbatim()
    {
        var plane = new FakePlane("");
        plane.Reply.Entries.Add(new RankEntryProto
        {
            Subject = (int)RankSubject.Player, SubjectId = "c", Score = 6
        });
        var module = new LeaderboardQueryService(plane);

        var result = await module.GetTopPageAsync("tenant-a", Board(), 10, "cursor-from-page-1");

        Assert.Equal("c", result.Entries[0].SubjectId);
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
        Assert.Equal("cursor-from-page-1", plane.Request!.Cursor);
    }

    [Fact]
    public async Task AnEmptyTenantOrInvalidBoardOrPageBoundIsRefusedBeforeTheDataPlane()
    {
        var plane = new FakePlane("");
        var module = new LeaderboardQueryService(plane);

        await Assert.ThrowsAsync<ArgumentException>(() => module.GetTopPageAsync("", Board(), 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => module.GetTopPageAsync("tenant-a", LeaderboardBoard.Default("tenant-a", "game-1", ""), 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.GetTopPageAsync("tenant-a", Board(), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.GetTopPageAsync("tenant-a", Board(), LeaderboardQueryLimits.MaxTopEntries + 1));
        Assert.Null(plane.Request);
    }

    [Fact]
    public async Task AStaleSnapshotMapsToAStaleReadException()
    {
        var plane = new FakePlane("")
        {
            Fault = new RpcException(new Status(StatusCode.FailedPrecondition, "The snapshot expired."))
        };
        var module = new LeaderboardQueryService(plane);
        await Assert.ThrowsAsync<LeaderboardStaleReadException>(
            () => module.GetTopPageAsync("tenant-a", Board(), 10, "old-cursor"));
    }

    [Fact]
    public async Task AMalformedCursorMapsToAnArgumentException()
    {
        var plane = new FakePlane("")
        {
            Fault = new RpcException(new Status(StatusCode.InvalidArgument, "bad cursor"))
        };
        var module = new LeaderboardQueryService(plane);
        await Assert.ThrowsAsync<ArgumentException>(
            () => module.GetTopPageAsync("tenant-a", Board(), 10, "garbage"));
    }

    [Fact]
    public async Task TheLastPageAnswersWithoutACursor()
    {
        var plane = new FakePlane("");
        var module = new LeaderboardQueryService(plane);
        var result = await module.GetTopPageAsync("tenant-a", Board(), 10, "last-page-cursor");
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
    }

    private sealed class FakePlane(string nextCursor) : ILeaderboardQueryDataPlane
    {
        public TopNReply Reply { get; } = new()
        {
            BoardId = "board-1", ReadVersion = "2026-01-01T00:00:00+00:00", NextCursor = nextCursor
        };
        public RpcException? Fault { get; init; }
        public GetTopRequest? Request { get; private set; }

        public Task<TopNReply> GetTopAsync(GetTopRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Fault is not null) throw Fault;
            return Task.FromResult(Reply);
        }

        // RANK-06 tests only exercise the TopN page path; the rank paths are RANK-05's tests.
        public Task<RankReply> GetRankAsync(GetRankRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AroundReply> GetAroundMeAsync(GetAroundMeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
