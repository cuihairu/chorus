using Chorus.Data.Redesign;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// The retention boundary (architecture 9 data retention): the control plane releases a
/// settled round's frame prefix below its verified anchor. Every guard lives in the store —
/// a refusal reaches the caller as data, never as a quiet no.
/// </summary>
public sealed class RetentionGrpcService(
    RedesignRetentionStore retentionStore, TrustedProcessCertificates trustedCertificates)
    : Retention.RetentionBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "1" });
    }

    public override async Task<PruneRoundReply> PruneRound(PruneRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var pruned = await retentionStore.PruneRoundAsync(
                ResolveTenant(caller, request.TenantId), request.GameId, request.RoomId,
                request.OperationId, request.OverrideRetentionWindow, context.CancellationToken);
            return new PruneRoundReply
            {
                RoomId = pruned.RoomId, KeepTick = pruned.KeepTick,
                FramesDeleted = pruned.FramesDeleted, SnapshotsDeleted = pruned.SnapshotsDeleted,
                Duplicate = pruned.Duplicate
            };
        }
        catch (RedesignRetentionException error)
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
