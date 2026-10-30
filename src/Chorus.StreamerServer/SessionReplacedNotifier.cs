using System.Security.Cryptography.X509Certificates;
using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>
/// The active leg of session replacement (contracts 3.3): a grant or logout that displaced a
/// connection names the gateway holding it, and this notifier tells that gateway to deliver
/// sessionReplaced and close it. Best-effort by design — the gateway's endpoint comes from the
/// routing snapshot, and a gateway that never registered is covered by the passive leg (its
/// next RPC fails with the replaced status). Failures are swallowed: replacement itself is
/// already durable in the session grant.
/// </summary>
public sealed class SessionReplacedNotifier(
    Func<GrpcChannel> controlChannelFactory,
    X509Certificate2 clientCertificate,
    X509Certificate2 root,
    X509Certificate2 nodeAuthority,
    string clusterId)
{
    public async Task NotifyAsync(SessionReplacement replacement, CancellationToken cancellationToken = default)
    {
        try
        {
            using var control = controlChannelFactory();
            var routing = new Routing.RoutingClient(control);
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: cancellationToken);
            var endpoint = snapshot.Instances
                .Where(instance => instance.Role == "streamer-gateway"
                    && StringComparer.Ordinal.Equals(instance.ProcessInstanceId, replacement.GatewayInstanceId))
                .Select(instance => instance.Endpoint)
                .FirstOrDefault(address => !string.IsNullOrEmpty(address));
            if (endpoint is null) return; // not registered: the passive leg bounds the old connection.
            using var channel = ProcessGrpcClientFactory.Create(new Uri(endpoint), clientCertificate,
                root, nodeAuthority, clusterId, "streamer-gateway");
            var face = new StreamerGatewayFace.StreamerGatewayFaceClient(channel);
            await face.NotifySessionReplacedAsync(new SessionReplacedNotice
            {
                SessionId = replacement.SessionId,
                ConnectionId = replacement.ConnectionId,
                ConnectionGeneration = replacement.ConnectionGeneration,
                StreamerId = replacement.StreamerId,
                Reason = replacement.Reason
            }, deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // Best-effort: the durable fact is the grant, not this delivery.
        }
    }
}
