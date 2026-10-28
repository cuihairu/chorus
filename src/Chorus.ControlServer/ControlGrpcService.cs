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
        // Two reporters speak for a process: the supervising node agent (bootstrap roles,
        // including a seeded game slot) and the process itself (game/streamer hosts, which
        // know their own endpoint). The caller certificate decides which — an agent cannot
        // claim a self-report and a process cannot impersonate its agent.
        var caller = await RequireAsync(context,
            "node-agent", "data-server", "streamer-server", "game-server", "control-server");
        string agentInstanceId;
        var selfReport = false;
        if (caller.Role == "node-agent")
        {
            CheckNode(caller, request.NodeId, request.AgentInstanceId);
            if (request.Role is not ("control-server" or "data-server" or "game-server") ||
                request.SlotId != $"{request.NodeId}/{request.Role}" ||
                request.StartedUnixMs <= 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid bootstrap slot report."));
            agentInstanceId = request.AgentInstanceId;
        }
        else
        {
            selfReport = true;
            if (request.Role is not ("streamer-server" or "game-server") || caller.Role != request.Role ||
                caller.ProcessInstanceId != request.ProcessInstanceId || caller.NodeId != request.NodeId ||
                caller.TenantId != "@cluster" ||
                !SlotAllowsReplicas(request.SlotId, request.NodeId, request.Role) ||
                request.StartedUnixMs <= 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid self slot report."));
            // A self-reporting process is its own agent: the store's agent identity check
            // matches agent_instance_id, so the process id plays both roles.
            agentInstanceId = request.ProcessInstanceId;
        }
        var status = await store.ReportProcessAsync(request.NodeId, agentInstanceId,
            request.SlotId, request.Role, request.ProcessInstanceId, request.ReportSequence,
            request.Lifecycle, request.Readiness, request.Health, request.Admission,
            request.EntityRecovery, DateTimeOffset.FromUnixTimeMilliseconds(request.StartedUnixMs).UtcDateTime,
            request.Endpoint is { Length: > 0 } endpoint ? endpoint : null, selfReport,
            context.CancellationToken);
        return new ProcessReply { Accepted = status.Accepted, Readiness = status.Readiness,
            EntityRecovery = status.EntityRecovery };
    }

    /// <summary>Self-report slots follow "{node}/{role}" with an optional "#2", "#3"… replica
    /// suffix — one slot per process instance, instances distinguished by the suffix.</summary>
    private static bool SlotAllowsReplicas(string slotId, string nodeId, string role)
    {
        var prefix = $"{nodeId}/{role}";
        if (slotId == prefix) return true;
        return slotId.Length > prefix.Length + 1 && slotId.StartsWith(prefix, StringComparison.Ordinal) &&
            slotId[prefix.Length] == '#' && slotId[(prefix.Length + 1)..].All(char.IsDigit);
    }

    public override async Task<OwnershipReply> ClaimOwnership(OwnershipRequest request, ServerCallContext context)
    {
        var caller = await RequireAsync(context, "control-server", "streamer-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.EntityId) ||
            string.IsNullOrWhiteSpace(request.OwnerInstanceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Ownership request is incomplete."));
        // A streamer server self-registers the entities it hosts: its claim names itself, and it
        // owns streamer entities only — a room's authority is claimed by the control plane.
        if (StringComparer.Ordinal.Equals(caller.Role, "streamer-server"))
        {
            if (!StringComparer.Ordinal.Equals(request.Kind, "streamer"))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "A streamer server owns streamer entities only."));
            if (!StringComparer.Ordinal.Equals(request.OwnerInstanceId, caller.ProcessInstanceId))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "A streamer server can only claim ownership for itself."));
        }
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
