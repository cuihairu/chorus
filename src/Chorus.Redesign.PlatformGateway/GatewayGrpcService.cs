using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.Redesign.PlatformGateway;

/// <summary>
/// The PlatformGateway boundary. The gateway is an executor, not a data owner: its face carries
/// readiness for supervisors and routing consumers while the task protocol it drives lives on
/// the DataServer's PlatformTask service.
/// </summary>
public sealed class GatewayGrpcService(TrustedProcessCertificates trustedCertificates)
    : PlatformGatewayFace.PlatformGatewayFaceBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "node-agent", "platform-gateway", "control-server", "data-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
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
