using System.Globalization;
using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// FIX-RANK-08: the data-plane face of the leaderboard GM business module. The production
/// implementation rides the <see cref="Leaderboard.LeaderboardClient"/> (the data host's gRPC
/// face); tests fake it, so the module's validation and error mapping are unit-testable
/// without a cluster.
/// </summary>
public interface ILeaderboardAdminDataPlane
{
    Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request, CancellationToken cancellationToken = default);

    Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request, CancellationToken cancellationToken = default);

    Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The production data-plane implementation: the data host's leaderboard face.</summary>
public sealed class GrpcLeaderboardAdminDataPlane(Leaderboard.LeaderboardClient client)
    : ILeaderboardAdminDataPlane
{
    public Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request, CancellationToken cancellationToken = default) =>
        client.AdjustBoardAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request, CancellationToken cancellationToken = default) =>
        client.ResetBoardAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request, CancellationToken cancellationToken = default) =>
        client.RebuildBoardAsync(request, cancellationToken: cancellationToken).ResponseAsync;
}

/// <summary>
/// FIX-RANK-08: the ControlServer business module for GM correction, reset and projection
/// rebuild (developer-capabilities.md 「GM adjustment/reset names the board, period and scope
/// and includes expected version, operation ID, reason and audit evidence」). The caller names
/// the board (the RANK-01 definition) and the correction or the reset scope; this module
/// validates the request (a non-empty tenant, a well-formed board, a non-empty operation id)
/// and asks the data plane to act. The data plane stores the correction or reset as a ledger
/// row (so a rebuild reproduces it and original scoring evidence stays available), enforces
/// the expected version (a changed board is a stale read) and the operation-id idempotency.
/// This module selects and validates; it never bypasses the storage floor.
/// </summary>
public sealed class LeaderboardAdminService(ILeaderboardAdminDataPlane dataPlane)
{
    /// <summary>Applies one GM correction — a signed delta onto one subject's entry of a named
    /// board/period/scope. The operation id is the idempotency and audit anchor; the expected
    /// version refuses a correction over a board that changed since the operator last read it.
    /// Returns the resulting score, whether it had already been applied and the board's new
    /// version.</summary>
    public async Task<LeaderboardAdminResult> AdjustAsync(string tenantId, LeaderboardBoard board,
        string subjectId, long delta, string expectedVersion, string operationId, string reason,
        CancellationToken cancellationToken = default)
    {
        Validate(tenantId, board, operationId, subjectId);
        try
        {
            var reply = await dataPlane.AdjustBoardAsync(new AdjustBoardRequest
            {
                TenantId = tenantId, GameId = board.GameId, Board = LeaderboardScoringService.ToProto(board),
                SubjectId = subjectId, Delta = delta, ExpectedVersion = expectedVersion ?? "",
                OperationId = operationId, Reason = reason ?? ""
            }, cancellationToken);
            return new LeaderboardAdminResult(reply.Score, reply.Duplicate, ParseVersion(reply.Version));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Aborted)
        {
            throw new LeaderboardStaleReadException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.AlreadyExists)
        {
            throw new IdempotencyConflictException();
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new LeaderboardRejectedException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>Resets one subject's entry (or the whole board's when <paramref name="subjectId"/>
    /// is null or empty), with the same guards and evidence as <see cref="AdjustAsync"/>.</summary>
    public async Task<LeaderboardAdminResult> ResetAsync(string tenantId, LeaderboardBoard board,
        string? subjectId, string expectedVersion, string operationId, string reason,
        CancellationToken cancellationToken = default)
    {
        Validate(tenantId, board, operationId, null);
        try
        {
            var reply = await dataPlane.ResetBoardAsync(new ResetBoardRequest
            {
                TenantId = tenantId, GameId = board.GameId, Board = LeaderboardScoringService.ToProto(board),
                SubjectId = subjectId ?? "", ExpectedVersion = expectedVersion ?? "",
                OperationId = operationId, Reason = reason ?? ""
            }, cancellationToken);
            return new LeaderboardAdminResult(0, reply.Duplicate, ParseVersion(reply.Version));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Aborted)
        {
            throw new LeaderboardStaleReadException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.AlreadyExists)
        {
            throw new IdempotencyConflictException();
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new LeaderboardRejectedException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>Rebuilds one board's projection from its effect ledger — a re-derivation, so it
    /// cannot add a score twice, and original scoring evidence is untouched.</summary>
    public async Task<LeaderboardRebuildResult> RebuildAsync(string tenantId, LeaderboardBoard board,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        board.Validate();
        try
        {
            var reply = await dataPlane.RebuildBoardAsync(new RebuildBoardRequest
            {
                TenantId = tenantId, GameId = board.GameId, Board = LeaderboardScoringService.ToProto(board)
            }, cancellationToken);
            return new LeaderboardRebuildResult(reply.Entries, ParseVersion(reply.Version));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new LeaderboardRejectedException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    private static void Validate(string tenantId, LeaderboardBoard board, string operationId, string? subjectId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        board.Validate();
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A GM operation needs its operation id (the idempotency and audit anchor).", nameof(operationId));
        if (subjectId is not null && string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
    }

    private static DateTimeOffset ParseVersion(string value) =>
        value.Length == 0
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>FIX-RANK-08: the result of one GM correction or reset — the resulting score (0 for
/// a reset), whether the operation had already been applied and the board's new version.</summary>
public sealed record LeaderboardAdminResult(long Score, bool Duplicate, DateTimeOffset Version);
