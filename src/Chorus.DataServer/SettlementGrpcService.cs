using Chorus.Data.Redesign;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// Settlement and round finalization boundary (architecture 9.3). The room owner settles
/// the committed reward manifest; the control plane finalizes the round — the terminator
/// does not depend on the original game host. The tenant is the caller's certificate
/// tenant; every mutating call lands in receipt transactions.
/// </summary>
public sealed class SettlementGrpcService(
    RedesignSettlementStore settlementStore, TrustedProcessCertificates trustedCertificates)
    : Settlement.SettlementBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "1" });
    }

    public override async Task<CommitResultReply> CommitResult(CommitResultRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "game-server", "control-server");
        try
        {
            var progress = await settlementStore.CommitResultAsync(
                ResolveTenant(caller, request.TenantId), request.GameId,
                request.RoomId, request.OperationId, context.CancellationToken);
            return new CommitResultReply
            {
                RoomId = progress.RoomId, ResultVersion = progress.ResultVersion,
                GrantsTotal = progress.Total, GrantsGranted = progress.Granted,
                GrantsPending = progress.Pending, Duplicate = progress.Duplicate
            };
        }
        catch (SettlementRejectedException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The operation was retried with a different request."));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    public override async Task<FinalizeRoundReply> FinalizeRound(FinalizeRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var finalized = await settlementStore.FinalizeRoundAsync(
                ResolveTenant(caller, request.TenantId), request.GameId,
                request.RoomId, request.OperationId, context.CancellationToken);
            return new FinalizeRoundReply
            {
                RoomId = finalized.RoomId, Status = finalized.Status, Epoch = finalized.Epoch,
                Duplicate = finalized.Duplicate
            };
        }
        catch (SettlementRejectedException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The operation was retried with a different request."));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    public override async Task<LeaderboardReply> ReadLeaderboard(ReadLeaderboardRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server", "streamer-server");
        try
        {
            var entries = await settlementStore.ReadLeaderboardAsync(
                ResolveTenant(caller, request.TenantId), request.GameId,
                request.BoardId, request.MaxEntries, context.CancellationToken);
            var reply = new LeaderboardReply { BoardId = request.BoardId };
            reply.Entries.Add(entries.Select(entry => new LeaderboardEntryProto
            {
                PlayerId = entry.PlayerId, Score = entry.Score
            }));
            return reply;
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    private ProcessIdentity Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }

    /// <summary>
    /// Cluster-scope callers (the control plane holds an @cluster certificate) name the
    /// tenant they act for; tenant-scope callers inherit their certificate tenant and are
    /// denied the moment they name a different one — no cross-tenant reach-through.
    /// </summary>
    private static string ResolveTenant(ProcessIdentity caller, string requested)
    {
        if (caller.TenantId == "@cluster")
        {
            if (string.IsNullOrWhiteSpace(requested))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "A cluster caller must name the tenant it acts for."));
            return requested;
        }
        if (requested.Length > 0 && !string.Equals(requested, caller.TenantId, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "A tenant caller cannot act for another tenant."));
        return caller.TenantId;
    }
}
