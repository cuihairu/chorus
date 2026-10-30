using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// The game-host boundary (architecture 7.2): the control plane names an admitted room and its
/// module configuration; this host owns the room actor from there. The tenant is the caller's
/// certificate tenant — the host itself runs under its own tenant-scoped certificate when it
/// talks to the data plane.
/// </summary>
public sealed class GameHostGrpcService(RoomRuntime runtime, TrustedProcessCertificates trustedCertificates)
    : GameHost.GameHostBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "node-agent", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "1" });
    }

    public override async Task<PrepareRoomReply> PrepareRoom(PrepareRoomRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        try
        {
            return await runtime.PrepareRoomAsync(request, context.CancellationToken);
        }
        catch (RoomAdmissionException error)
        {
            // FIX-PERF-07: a saturated host refuses a new allocation as ResourceExhausted —
            // the standard "no capacity" status the scheduler filters the instance on (FIX-
            // SCHED-02) — never a reply the caller could mistake for a duplicate.
            throw new RpcException(new Status(StatusCode.ResourceExhausted, error.Message));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        // Data-plane rejections (fence, room state, idempotency conflicts) bubble with their
        // own status — the caller sees exactly what the store refused.
    }

    public override async Task<ActivateRoomReply> ActivateRoom(ActivateRoomRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        try
        {
            return await runtime.ActivateRoomAsync(request, context.CancellationToken);
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    public override async Task<RecoverRoomReply> RecoverRoom(RecoverRoomRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        try
        {
            return await runtime.RecoverRoomAsync(request, context.CancellationToken);
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
}
