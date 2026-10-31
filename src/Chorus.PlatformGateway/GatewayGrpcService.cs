using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.PlatformGateway;

/// <summary>
/// The PlatformGateway boundary. The gateway is an executor, not a data owner: its face carries
/// readiness for supervisors and routing consumers while the task protocol it drives lives on
/// the DataServer's PlatformTask service. The stop-delivery command (FIX-END-05) is recorded
/// per room at the exact version it names — the record is the input the later stop steps
/// (FIX-END-07) install the actual delivery stop from; a repeated command at the same version
/// answers duplicate, and a command naming a different version than the gateway already closed
/// is refused so a replay can never close a newer round.
/// </summary>
public sealed class GatewayGrpcService(TrustedProcessCertificates trustedCertificates,
    StopDeliveryRecord stopDeliveries) : PlatformGatewayFace.PlatformGatewayFaceBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "node-agent", "platform-gateway", "control-server", "data-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override Task<StopDeliveryReply> StopDelivery(StopDeliveryRequest request,
        ServerCallContext context)
    {
        // Only the control plane issues the command — the gateway never accepts a peer's word
        // for a round's closure.
        Require(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.EndOperationId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The stop-delivery command is incomplete."));
        if (request.RoundGeneration <= 0 || request.CloseRouteVersion == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The stop-delivery command must name its round generation and close version."));

        if (!stopDeliveries.TryClose(request.RoomId, request.EndOperationId, request.CloseRouteVersion,
                out var duplicate, out var closedVersion))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The room {request.RoomId} already closed its delivery at version {closedVersion}; " +
                $"the command names {request.CloseRouteVersion}."));
        // Host-visible evidence on the console channel the test harness captures.
        Console.WriteLine(
            $"stop-delivery received room={request.RoomId} version={request.CloseRouteVersion} " +
            $"operation={request.EndOperationId} duplicate={duplicate}");
        return Task.FromResult(new StopDeliveryReply
        {
            EndOperationId = request.EndOperationId,
            CloseRouteVersion = request.CloseRouteVersion,
            Duplicate = duplicate
        });
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
