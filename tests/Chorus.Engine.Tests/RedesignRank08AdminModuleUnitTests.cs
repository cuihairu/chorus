using Chorus.ControlServer;
using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-08, the GM business module (developer-capabilities.md 「GM adjustment/reset names
/// the board, period and scope and includes expected version, operation ID, reason and audit
/// evidence」): the module validates the request and maps the data plane's distinct rejections
/// to the module's exceptions — Aborted is a stale read, AlreadyExists is an idempotency
/// conflict, FailedPrecondition is a rejected board and InvalidArgument is a caller error — so
/// the caller never sees a raw gRPC status.
/// </summary>
public sealed class RedesignRank08AdminModuleUnitTests
{
    private static readonly LeaderboardBoard Board = LeaderboardBoard.Default("tenant-a", "game-1", "board-a");

    [Fact]
    public async Task AdjustValidatesTheRequest()
    {
        var plane = new FakeAdminPlane();
        var service = new LeaderboardAdminService(plane);

        await Assert.ThrowsAsync<ArgumentException>(() => service.AdjustAsync("", Board, "red", 1, "", "op-1", "r"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AdjustAsync("tenant-a", Board, "", 1, "", "op-1", "r"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AdjustAsync("tenant-a", Board, "red", 1, "", "", "r"));
        Assert.Null(plane.Adjust);
    }

    [Fact]
    public async Task AdjustAsksThePlaneWithTheNamedBoardAndReturnsTheOutcome()
    {
        var plane = new FakeAdminPlane { AdjustResult = new AdjustBoardReply { Score = 42, Duplicate = false, Version = "2026-11-05T10:00:00+00:00" } };
        var service = new LeaderboardAdminService(plane);

        var result = await service.AdjustAsync("tenant-a", Board, "red", 3, "v1", "op-2", "award fix");

        Assert.Equal(42, result.Score);
        Assert.False(result.Duplicate);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T10:00:00+00:00"), result.Version);
        Assert.NotNull(plane.Adjust);
        Assert.Equal("tenant-a", plane.Adjust!.TenantId);
        Assert.Equal("game-1", plane.Adjust.GameId);
        Assert.Equal("board-a", plane.Adjust.Board.BoardId);
        Assert.Equal("red", plane.Adjust.SubjectId);
        Assert.Equal(3, plane.Adjust.Delta);
        Assert.Equal("v1", plane.Adjust.ExpectedVersion);
        Assert.Equal("op-2", plane.Adjust.OperationId);
        Assert.Equal("award fix", plane.Adjust.Reason);
    }

    [Fact]
    public async Task AdjustMapsTheDistinctRejections()
    {
        await Assert.ThrowsAsync<LeaderboardStaleReadException>(
            () => ServiceWith(StatusCode.Aborted, "stale").AdjustAsync("tenant-a", Board, "red", 1, "v1", "op-3", "r"));
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => ServiceWith(StatusCode.AlreadyExists, "dup").AdjustAsync("tenant-a", Board, "red", 1, "v1", "op-3", "r"));
        await Assert.ThrowsAsync<LeaderboardRejectedException>(
            () => ServiceWith(StatusCode.FailedPrecondition, "rejected").AdjustAsync("tenant-a", Board, "red", 1, "v1", "op-3", "r"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ServiceWith(StatusCode.InvalidArgument, "bad").AdjustAsync("tenant-a", Board, "red", 1, "v1", "op-3", "r"));
    }

    [Fact]
    public async Task ResetValidatesAndPassesAnEmptySubjectForAWholeBoardReset()
    {
        var plane = new FakeAdminPlane { ResetResult = new ResetBoardReply { Duplicate = false, Version = "2026-11-05T10:00:00+00:00" } };
        var service = new LeaderboardAdminService(plane);

        var result = await service.ResetAsync("tenant-a", Board, null, "v1", "op-4", "voided");

        Assert.False(result.Duplicate);
        Assert.NotNull(plane.Reset);
        Assert.Equal("", plane.Reset!.SubjectId);
        Assert.Equal("voided", plane.Reset.Reason);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResetAsync("", Board, null, "v1", "op-4", "r"));
    }

    [Fact]
    public async Task RebuildValidatesAndReturnsTheEntryCount()
    {
        var plane = new FakeAdminPlane { RebuildResult = new RebuildBoardReply { Entries = 7, Version = "2026-11-05T10:00:00+00:00" } };
        var service = new LeaderboardAdminService(plane);

        var result = await service.RebuildAsync("tenant-a", Board);

        Assert.Equal(7, result.Entries);
        Assert.NotNull(plane.Rebuild);
        Assert.Equal("board-a", plane.Rebuild!.Board.BoardId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RebuildAsync("", Board));
        await Assert.ThrowsAsync<LeaderboardRejectedException>(() => ServiceWith(StatusCode.FailedPrecondition, "no").RebuildAsync("tenant-a", Board));
        await Assert.ThrowsAsync<ArgumentException>(() => ServiceWith(StatusCode.InvalidArgument, "bad").RebuildAsync("tenant-a", Board));
    }

    private static LeaderboardAdminService ServiceWith(StatusCode status, string detail) =>
        new(new FakeAdminPlane
        {
            AdjustError = new RpcException(new Status(status, detail)),
            ResetError = new RpcException(new Status(status, detail)),
            RebuildError = new RpcException(new Status(status, detail))
        });

    private sealed class FakeAdminPlane : ILeaderboardAdminDataPlane
    {
        public AdjustBoardReply? AdjustResult { get; init; }
        public ResetBoardReply? ResetResult { get; init; }
        public RebuildBoardReply? RebuildResult { get; init; }
        public RpcException? AdjustError { get; init; }
        public RpcException? ResetError { get; init; }
        public RpcException? RebuildError { get; init; }
        public AdjustBoardRequest? Adjust { get; private set; }
        public ResetBoardRequest? Reset { get; private set; }
        public RebuildBoardRequest? Rebuild { get; private set; }

        public Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request, CancellationToken cancellationToken = default)
        {
            Adjust = request;
            return AdjustError is not null ? Task.FromException<AdjustBoardReply>(AdjustError) : Task.FromResult(AdjustResult!);
        }

        public Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request, CancellationToken cancellationToken = default)
        {
            Reset = request;
            return ResetError is not null ? Task.FromException<ResetBoardReply>(ResetError) : Task.FromResult(ResetResult!);
        }

        public Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request, CancellationToken cancellationToken = default)
        {
            Rebuild = request;
            return RebuildError is not null ? Task.FromException<RebuildBoardReply>(RebuildError) : Task.FromResult(RebuildResult!);
        }
    }
}
