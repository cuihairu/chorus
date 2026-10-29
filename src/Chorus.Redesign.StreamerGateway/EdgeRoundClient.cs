using System.Runtime.CompilerServices;
using Chorus.Redesign.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The round store as the edge reads it (m2 RoundStore): the round's facts — epoch, status,
/// and the participation list that tells a connection which side it plays. The stream itself
/// is served by the room's owning game host, not by this store; the channel pool below is
/// how the fan-out reaches those hosts.
/// </summary>
public sealed class EdgeRoundClient(Func<GrpcChannel> channelFactory)
{
    private readonly Lazy<GrpcChannel> _channel = new(channelFactory);

    /// <summary>The round's status, or null when the store has no such round. A cluster-scoped
    /// gateway must name the tenant — the room rows are tenant-keyed and the certificate is not.</summary>
    public async Task<RoundStatusReply?> GetRoundAsync(string tenantId, string roomId, string gameId,
        CancellationToken cancellationToken)
    {
        var reply = await new RoundStore.RoundStoreClient(_channel.Value).GetRoundAsync(
            new GetRoundRequest { RoomId = roomId, GameId = gameId, TenantId = tenantId },
            cancellationToken: cancellationToken);
        return reply.Found ? reply : null;
    }

    /// <summary>The sync subscription against one game host. The call object lives as long as
    /// the enumeration: abandoning or completing the stream disposes it, and the channel is
    /// released separately by the pool the caller acquired it from.</summary>
    public static async IAsyncEnumerable<SyncMessage> SubscribeStream(GrpcChannel channel,
        SubscribeRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = new RoundSync.RoundSyncClient(channel).Subscribe(
            request, cancellationToken: cancellationToken);
        await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken))
            yield return message;
    }
}

/// <summary>
/// One channel per game-host endpoint, shared by every room that host owns and disposed when
/// the last subscription to it ends. Acquire is called when a subscription opens; Release is
/// the fan-out's stream-closed notice — the two are balanced per subscription, so a host the
/// edge stopped watching releases its channel without waiting for process shutdown.
/// </summary>
public sealed class EdgeGameChannelPool(Func<string, GrpcChannel> channelFactory)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (GrpcChannel Channel, int References)> _channels =
        new(StringComparer.Ordinal);

    public GrpcChannel Acquire(string endpoint)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(endpoint, out var held))
            {
                _channels[endpoint] = (held.Channel, held.References + 1);
                return held.Channel;
            }
            var channel = channelFactory(endpoint);
            _channels[endpoint] = (channel, 1);
            return channel;
        }
    }

    public void Release(string endpoint)
    {
        GrpcChannel? expired = null;
        lock (_gate)
        {
            if (!_channels.TryGetValue(endpoint, out var held)) return;
            if (held.References > 1)
            {
                _channels[endpoint] = (held.Channel, held.References - 1);
                return;
            }
            _channels.Remove(endpoint);
            expired = held.Channel;
        }
        // Disposed outside the gate: a slow teardown must not stall the next acquisition.
        expired?.Dispose();
    }
}
