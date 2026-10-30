using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The deployment plan boundary (architecture 10.2): the control plane issues versioned
/// plans, the node agent watches them. ApplyPlan is control-plane only — the M3-9 admin face
/// wraps it with audit; WatchPlans is the agent's reconnection point: the stream re-emits the
/// node's full plan set on change and on connect, and a re-delivered command must never pull
/// a second process on the node.
/// </summary>
public sealed class DeploymentGrpcService(
    RedesignControlStore store, TrustedProcessCertificates trustedCertificates)
    : Deployment.DeploymentBase
{
    private static readonly TimeSpan WatchPollInterval = TimeSpan.FromMilliseconds(500);

    public override async Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "node-agent", "control-server");
        return new ReadyReply { Ready = true, SchemaVersion = "4" };
    }

    public override async Task<DeploymentPlanProto> ApplyPlan(ApplyPlanRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.NodeId) || string.IsNullOrWhiteSpace(request.SlotId) ||
            string.IsNullOrWhiteSpace(request.Role) || string.IsNullOrWhiteSpace(request.DesiredState) ||
            string.IsNullOrWhiteSpace(request.DesiredVersion) || string.IsNullOrWhiteSpace(request.PackageHash) ||
            request.PlanGeneration <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The deployment plan is incomplete."));
        var plan = await store.ApplyPlanAsync(request.NodeId, request.SlotId, request.Role,
            request.DesiredState, request.DesiredVersion, request.PackageHash, request.PlanGeneration,
            request.CommandId is { Length: > 0 } commandId ? commandId : null,
            request.CommandExpiresUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(request.CommandExpiresUnixMs).UtcDateTime
                : null,
            context.CancellationToken);
        return Reply(plan);
    }

    public override async Task WatchPlans(WatchPlansRequest request,
        IServerStreamWriter<DeploymentPlanProto> responseStream, ServerCallContext context)
    {
        var caller = Require(context, "node-agent");
        if (caller.NodeId != request.NodeId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "An agent only watches the plans of its own node."));
        var lastFingerprint = "";
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var plans = await store.ListPlansAsync(request.NodeId, context.CancellationToken);
            var fingerprint = string.Join(';',
                plans.Select(plan => $"{plan.SlotId}|{plan.PlanGeneration}|{plan.CommandId}|{plan.DesiredState}"));
            if (fingerprint != lastFingerprint)
            {
                lastFingerprint = fingerprint;
                foreach (var plan in plans)
                    await responseStream.WriteAsync(Reply(plan), context.CancellationToken);
            }
            await Task.Delay(WatchPollInterval, context.CancellationToken);
        }
    }

    private static DeploymentPlanProto Reply(DeploymentPlan plan) => new()
    {
        SlotId = plan.SlotId, NodeId = plan.NodeId, Role = plan.Role,
        DesiredState = plan.DesiredState, DesiredVersion = plan.DesiredVersion,
        PackageHash = plan.PackageHash, PlanGeneration = plan.PlanGeneration,
        CommandId = plan.CommandId ?? "",
        CommandExpiresUnixMs = plan.CommandExpiresAtUtc is { } expiry
            ? new DateTimeOffset(expiry).ToUnixTimeMilliseconds() : 0
    };

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
}
