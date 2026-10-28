using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The internal face of the streamer gateway (m4.proto). The public WSS edge carries the
/// client protocol; this is what the cluster reaches: readiness for supervisors and routing
/// consumers, and the session-replaced notice a StreamerServer pushes when a login takes over
/// a session whose old connection lives on this gateway (contracts 3.3).
/// </summary>
public sealed class GatewayGrpcService(
    TrustedProcessCertificates trustedCertificates, EdgeSessionRegistry registry)
    : StreamerGatewayFace.StreamerGatewayFaceBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "node-agent", "streamer-gateway", "control-server", "data-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override Task<SessionReplacedReply> NotifySessionReplaced(
        SessionReplacedNotice request, ServerCallContext context)
    {
        // Only the streamer session authority may say a session moved on: the notice names
        // one connection at one generation, and the gateway closes exactly that.
        Require(context, "streamer-server");
        var connection = registry.FindByConnection(request.ConnectionId);
        if (connection is null) return Task.FromResult(new SessionReplacedReply { Delivered = false });
        // The session facts and the replaced downlink land with the login round (M4-2); the
        // registry hit is the delivery proof this face answers today.
        return Task.FromResult(new SessionReplacedReply { Delivered = true });
    }

    private void Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
    }
}
