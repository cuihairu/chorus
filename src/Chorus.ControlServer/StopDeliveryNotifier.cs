using System.Security.Cryptography.X509Certificates;
using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.ControlServer;

/// <summary>
/// The step-3 delivery leg (architecture 6.4, FIX-END-05): the recorded stop-delivery command
/// pushed at one responder gateway. Best-effort by design — the persisted command and the
/// pending responder set are the step's evidence, so a push that cannot land (the gateway is
/// offline, its endpoint is absent, its answer was lost) changes nothing here: the responder
/// stays pending and the coordinator's next pass re-pushes the same recorded command. The
/// ack's per-responder confirmation is the later step's work (FIX-END-07/08).
/// </summary>
public sealed class StopDeliveryNotifier(
    X509Certificate2 clientCertificate,
    X509Certificate2 root,
    X509Certificate2 nodeAuthority,
    string clusterId)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    public async Task<bool> PushAsync(StopDeliveryCommand command, EndOperation operation,
        StopDeliveryResponder responder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(responder.Endpoint)) return false;
        try
        {
            using var channel = ProcessGrpcClientFactory.Create(new Uri(responder.Endpoint),
                clientCertificate, root, nodeAuthority, clusterId, "platform-gateway");
            var face = new PlatformGatewayFace.PlatformGatewayFaceClient(channel);
            var request = new StopDeliveryRequest
            {
                TenantId = command.TenantId,
                GameId = command.GameId,
                RoomId = command.RoomId,
                EndOperationId = command.EndOperationId,
                RoundGeneration = command.RoundGeneration,
                CloseRouteVersion = command.CloseRouteVersion
            };
            request.BindingGenerations.AddRange(operation.BindingGenerations.Select(binding =>
                new EndBindingGeneration
                {
                    BindingId = binding.BindingId, Generation = binding.Generation,
                    RoundGeneration = binding.RoundGeneration
                }));
            await face.StopDeliveryAsync(request,
                deadline: DateTime.UtcNow.Add(Deadline), cancellationToken: cancellationToken);
            return true;
        }
        catch (RpcException)
        {
            // Best-effort: the durable fact is the recorded command, not this delivery.
            return false;
        }
    }
}
