using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// Coordinator term boundary (architecture 7.1, 10.1). Only control-plane instances manage
/// match partitions; a lost lease surfaces as a visible failure so a partitioned coordinator
/// cannot quietly keep serving.
/// </summary>
public sealed class CoordinatorGrpcService(
    RedesignCoordinatorStore store, TrustedProcessCertificates trustedCertificates)
    : Coordinator.CoordinatorBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<TermReply> ClaimTerm(TermRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            return Reply(await store.ClaimTermAsync(request.PartitionKey, request.HolderInstanceId,
                request.LeaseSeconds, context.CancellationToken));
        }
        catch (RedesignTermLostException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The term is no longer held by this coordinator."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<TermReply> RenewTerm(TermRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            return Reply(await store.RenewTermAsync(request.PartitionKey, request.HolderInstanceId,
                request.LeaseSeconds, context.CancellationToken));
        }
        catch (RedesignTermLostException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The term is no longer held by this coordinator."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<TermReply> ReleaseTerm(TermRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            return Reply(await store.ReleaseTermAsync(request.PartitionKey, request.HolderInstanceId,
                context.CancellationToken));
        }
        catch (RedesignTermLostException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The term is no longer held by this coordinator."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<TermReply> GetTerm(GetTermRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            var status = await store.GetTermAsync(request.PartitionKey, context.CancellationToken);
            return status is null ? new TermReply { Found = false } : Reply(status);
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    private static TermReply Reply(TermStatus status) => new()
    {
        Found = true,
        HolderInstanceId = status.HolderInstanceId,
        Term = status.Term,
        LeaseUntilUnixMs = new DateTimeOffset(status.LeaseUntilUtc).ToUnixTimeMilliseconds()
    };

    private void Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
    }
}
