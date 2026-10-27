using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

public sealed class ControlGrpcService(
    RedesignControlStore store, TrustedProcessCertificates trustedCertificates) : ControlService.ControlServiceBase
{
    public override async Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        await RequireAsync(context, "node-agent", "data-server", "streamer-server", "game-server", "control-server");
        return new ReadyReply { Ready = true, SchemaVersion = "4" };
    }

    public override async Task<NodeReply> RegisterNode(RegisterNodeRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "node-agent");
        CheckNode(caller, request.NodeId, request.AgentInstanceId);
        var status = await store.RegisterNodeAsync(request.NodeId, request.AgentInstanceId,
            string.IsNullOrWhiteSpace(request.CapabilitiesJson) ? "{}" : request.CapabilitiesJson,
            context.CancellationToken);
        return new NodeReply { Accepted = true, AgentInstanceId = status.AgentInstanceId,
            ReportSequence = status.ReportSequence };
    }

    public override async Task<ProcessReply> ReportProcess(ReportProcessRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "node-agent");
        CheckNode(caller, request.NodeId, request.AgentInstanceId);
        if (request.Role is not ("control-server" or "data-server") ||
            request.SlotId != $"{request.NodeId}/{request.Role}" ||
            request.StartedUnixMs <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid bootstrap slot report."));
        var status = await store.ReportProcessAsync(request.NodeId, request.AgentInstanceId,
            request.SlotId, request.Role, request.ProcessInstanceId, request.ReportSequence,
            request.Lifecycle, request.Readiness, request.Health, request.Admission,
            request.EntityRecovery, DateTimeOffset.FromUnixTimeMilliseconds(request.StartedUnixMs).UtcDateTime,
            context.CancellationToken);
        return new ProcessReply { Accepted = status.Accepted, Readiness = status.Readiness,
            EntityRecovery = status.EntityRecovery };
    }

    public override async Task<OwnershipReply> ClaimOwnership(OwnershipRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.EntityId) ||
            string.IsNullOrWhiteSpace(request.OwnerInstanceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Ownership request is incomplete."));
        var result = await store.ClaimOwnershipAsync(request.TenantId, request.GameId, request.Kind,
            request.EntityId, request.OwnerInstanceId, request.LeaseSeconds, context.CancellationToken);
        return OwnerReply(result);
    }

    public override async Task<OwnershipReply> GetOwnership(OwnershipLookup request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "data-server", "streamer-server", "game-server", "control-server");
        if (caller.TenantId != "@cluster" && caller.TenantId != request.TenantId)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        var result = await store.GetOwnershipAsync(request.TenantId, request.GameId,
            request.Kind, request.EntityId, context.CancellationToken);
        return result is null ? new OwnershipReply { Found = false } : OwnerReply(result);
    }

    private async Task<ProcessIdentity> RequireAsync(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }

    private static void CheckNode(ProcessIdentity caller, string nodeId, string agentInstanceId)
    {
        if (caller.NodeId != nodeId || caller.ProcessInstanceId != agentInstanceId || caller.TenantId != "@cluster")
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Agent identity differs from certificate."));
    }

    private static OwnershipReply OwnerReply(OwnerStatus value) => new()
    {
        Found = true,
        OwnerInstanceId = value.OwnerInstanceId,
        Epoch = value.Epoch,
        LeaseUntilUnixMs = new DateTimeOffset(value.LeaseUntilUtc).ToUnixTimeMilliseconds(),
        Status = value.Status
    };
}
