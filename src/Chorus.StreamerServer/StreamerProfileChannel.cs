using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>Entity profile loading through the DataServer identity boundary; init is DataServer-side.</summary>
public sealed class StreamerProfileChannel(Func<GrpcChannel> channelFactory) : IStreamerProfileStore
{
    public async Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        using var channel = channelFactory();
        var client = new StreamerIdentity.StreamerIdentityClient(channel);
        var reply = await client.EnsureEntityProfileAsync(new EnsureProfileRequest
        {
            TenantId = tenantId, GameId = gameId, StreamerId = streamerId
        }, cancellationToken: cancellationToken);
        if (!reply.Found)
            throw new InvalidOperationException("The entity profile could not be loaded.");
    }
}
