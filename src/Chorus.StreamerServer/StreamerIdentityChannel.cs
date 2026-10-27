using Chorus.Redesign.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>Calls DataServer identity/session RPCs over mTLS, mapping RPC failures back to domain errors.</summary>
public sealed class StreamerIdentityChannel(Func<GrpcChannel> channelFactory) : IStreamerIdentityStore
{
    public async Task<string> ResolveIdentityAsync(string tenantId, string platform, string appId, string openId,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = tenantId, Platform = platform, AppId = appId, OpenId = openId
        }, cancellationToken: cancellationToken));
        return reply.StreamerId;
    }

    public async Task<StreamerSessionGrant> LoginAsync(string tenantId, string streamerId, string gatewayInstanceId,
        string connectionId, byte[] nextResumeSecret, CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.LoginAsync(new SessionLoginRequest
        {
            TenantId = tenantId, StreamerId = streamerId, GatewayInstanceId = gatewayInstanceId,
            ConnectionId = connectionId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(nextResumeSecret)
        }, cancellationToken: cancellationToken));
        return Grant(reply);
    }

    public async Task<StreamerSessionGrant> ResumeAsync(string tenantId, string previousSessionId, string gatewayInstanceId,
        string connectionId, string operationId, byte[] previousSecret, byte[] nextSecret,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.ResumeAsync(new SessionResumeRequest
        {
            TenantId = tenantId, PreviousSessionId = previousSessionId, GatewayInstanceId = gatewayInstanceId,
            ConnectionId = connectionId, ResumeOperationId = operationId,
            PreviousSecret = Google.Protobuf.ByteString.CopyFrom(previousSecret),
            NextSecret = Google.Protobuf.ByteString.CopyFrom(nextSecret)
        }, cancellationToken: cancellationToken));
        return Grant(reply);
    }

    private static StreamerSessionGrant Grant(SessionGrantReply reply) => new(reply.StreamerId, reply.SessionId,
        reply.SessionVersion, DateTimeOffset.FromUnixTimeMilliseconds(reply.ExpiresAtUnixMs).UtcDateTime,
        reply.Duplicate);

    private async Task<T> CallAsync<T>(Func<StreamerIdentity.StreamerIdentityClient, AsyncUnaryCall<T>> call)
    {
        using var channel = channelFactory();
        var client = new StreamerIdentity.StreamerIdentityClient(channel);
        try
        {
            return await call(client);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.PermissionDenied)
        {
            // Covers revoked sessions and cross-tenant fencing; the distinction is not leaked.
            throw new SessionRevokedException();
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new SessionReplacedException();
        }
    }
}
