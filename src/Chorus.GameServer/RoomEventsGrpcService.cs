using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// The direct-push room-event edge (FIX-INPUT-01, architecture 6.3): the routing layer
/// pushes an attributed platform event straight at the host it believes owns the room. The
/// reply disposition is the contract — DurableAccepted and Duplicate mean the event is
/// durably in the room's inbox (committed, not applied), StaleOwner means the caller's
/// owner picture is old, Overloaded means the host refused before any durable write, and
/// OutcomeUnknown means the durable leg's commit state is unknowable (retry the same event
/// id). Malformed requests and attribution errors surface as gRPC statuses — retrying the
/// same request cannot fix them.
/// </summary>
public sealed class RoomEventsGrpcService(RoomRuntime runtime, TrustedProcessCertificates trustedCertificates)
    : RoomEvents.RoomEventsBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "node-agent", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "1" });
    }

    public override async Task<DeliverRoomEventReply> DeliverEvent(DeliverRoomEventRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "platform-gateway", "control-server");
        // The data plane authenticates the host's certificate, not the pusher's, so the
        // tenant fence lives here: a gateway may only push into its own tenant's rooms.
        if (caller.TenantId != "@cluster" &&
            !string.Equals(caller.TenantId, request.TenantId, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        try
        {
            return await runtime.DeliverEventAsync(request, context.CancellationToken);
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        // Data-plane attribution refusals bubble with their InvalidArgument status — the
        // caller's binding or room picture is wrong, which retrying cannot change.
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
