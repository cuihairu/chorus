using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>
/// Reports each committed mount as the control plane's route source (FIX-END-02, architecture
/// 6.4): the binding id and its generation, right after the mount commits. The report names no
/// platform identity — the control plane derives it from the mount's verified live session, so
/// the recorded route always carries the identity the platform login verified. A failure never
/// fails the mount: the data plane owns the binding, the registration is the routing picture,
/// and the next mount (or a restart's recovery pass) reports again.
/// </summary>
public sealed class StreamerRouteSourceRegistrar(Func<GrpcChannel> channelFactory)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public async Task RegisterAsync(string tenantId, string gameId, string streamerId,
        string bindingId, long bindingGeneration, CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = channelFactory();
            var routing = new Routing.RoutingClient(channel);
            var reply = await routing.RegisterRouteSourceAsync(new RegisterRouteSourceRequest
            {
                TenantId = tenantId, GameId = gameId, StreamerId = streamerId,
                BindingId = bindingId, BindingGeneration = bindingGeneration
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);
            if (!reply.Duplicate)
                Console.WriteLine(
                    $"route source for {streamerId} on {reply.Platform}/{reply.CanonicalLiveSessionKey} " +
                    $"registered at binding generation {reply.BindingGeneration}");
        }
        catch (Exception error) when (error is RpcException or OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"route source registration for {streamerId} binding {bindingId} " +
                $"generation {bindingGeneration} failed: {error.Message}");
        }
    }
}
