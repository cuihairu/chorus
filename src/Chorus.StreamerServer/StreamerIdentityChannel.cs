using Chorus.Rpc;
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

    public async Task<StreamerConnection> AttachConnectionAsync(string tenantId, string sessionId,
        string gatewayInstanceId, string connectionId, string operationId, byte[] currentSecret,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.AttachConnectionAsync(new SessionAttachRequest
        {
            TenantId = tenantId, SessionId = sessionId, OperationId = operationId,
            GatewayInstanceId = gatewayInstanceId, ConnectionId = connectionId,
            CurrentSecret = Google.Protobuf.ByteString.CopyFrom(currentSecret)
        }, cancellationToken: cancellationToken));
        return new StreamerConnection(reply.SessionId, reply.StreamerId, reply.SessionVersion,
            reply.ConnectionGeneration, reply.Duplicate);
    }

    public async Task<bool> ConnectionClosedAsync(string tenantId, string sessionId, string gatewayInstanceId,
        string connectionId, long connectionGeneration, CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.ConnectionClosedAsync(new SessionCloseRequest
        {
            TenantId = tenantId, SessionId = sessionId, GatewayInstanceId = gatewayInstanceId,
            ConnectionId = connectionId, ConnectionGeneration = connectionGeneration
        }, cancellationToken: cancellationToken));
        return reply.Applied;
    }

    public async Task<StreamerSessionState?> GetSessionStateAsync(string tenantId, string sessionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await CallAsync(client => client.GetSessionStateAsync(new SessionStateRequest
            {
                TenantId = tenantId, SessionId = sessionId
            }, cancellationToken: cancellationToken));
            return new StreamerSessionState(reply.StreamerId, reply.TenantId, reply.SessionVersion,
                reply.GatewayInstanceId, reply.ConnectionId, reply.ConnectionGeneration,
                reply.Revoked, reply.Closed, reply.Expired, reply.Banned, reply.Current);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform,
        string appId, string platformRoomKey, string canonicalLiveSessionKey,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.OpenLiveSessionAsync(new OpenLiveRequest
        {
            TenantId = tenantId, StreamerId = streamerId, Platform = platform, AppId = appId,
            PlatformRoomKey = platformRoomKey, CanonicalLiveSessionKey = canonicalLiveSessionKey
        }, cancellationToken: cancellationToken));
        return reply.LiveSessionId;
    }

    public async Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.GetLiveSessionAsync(new LiveSessionRequest
        {
            TenantId = tenantId, StreamerId = streamerId
        }, cancellationToken: cancellationToken));
        return string.IsNullOrEmpty(reply.LiveSessionId) ? null : reply.LiveSessionId;
    }

    public async Task<StreamerBinding> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
        string gameId, string operationId, CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.StartGameplayBindingAsync(new BindingStartRequest
        {
            TenantId = tenantId, LiveSessionId = liveSessionId, SessionId = sessionId,
            GameId = gameId, OperationId = operationId
        }, cancellationToken: cancellationToken));
        return new StreamerBinding(reply.BindingId, reply.LiveSessionId, reply.GameId, reply.Generation,
            reply.State, reply.Duplicate);
    }

    public async Task<StreamerBindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.GetBindingStatusAsync(new BindingStatusRequest
        {
            TenantId = tenantId, StreamerId = streamerId
        }, cancellationToken: cancellationToken));
        return string.IsNullOrEmpty(reply.BindingId)
            ? null
            : new StreamerBindingStatus(reply.LiveSessionId, reply.BindingId, reply.GameId,
                reply.Generation, reply.State);
    }

    public async Task<StreamerSessionRevocation> RevokeSessionAsync(string tenantId, string sessionId,
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(client => client.RevokeSessionAsync(new SessionRevokeRequest
        {
            SessionId = sessionId, TenantId = tenantId
        }, cancellationToken: cancellationToken));
        return new StreamerSessionRevocation(reply.SessionId, reply.StreamerId, reply.SessionVersion,
            reply.Applied,
            string.IsNullOrEmpty(reply.DisplacedGatewayInstanceId) ? null : reply.DisplacedGatewayInstanceId,
            string.IsNullOrEmpty(reply.DisplacedConnectionId) ? null : reply.DisplacedConnectionId,
            reply.DisplacedConnectionGeneration);
    }

    public async Task<string?> GetSessionTenantAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await CallAsync(client => client.GetSessionTenantAsync(new SessionTenantRequest
            {
                SessionId = sessionId
            }, cancellationToken: cancellationToken));
            return string.IsNullOrEmpty(reply.TenantId) ? null : reply.TenantId;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    private static StreamerSessionGrant Grant(SessionGrantReply reply) => new(reply.StreamerId, reply.SessionId,
        reply.SessionVersion, DateTimeOffset.FromUnixTimeMilliseconds(reply.ExpiresAtUnixMs).UtcDateTime,
        reply.Duplicate,
        string.IsNullOrEmpty(reply.PreviousSessionId) ? null : reply.PreviousSessionId,
        string.IsNullOrEmpty(reply.PreviousGatewayInstanceId) ? null : reply.PreviousGatewayInstanceId,
        string.IsNullOrEmpty(reply.PreviousConnectionId) ? null : reply.PreviousConnectionId,
        reply.PreviousConnectionGeneration);

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
        catch (RpcException error) when (error.StatusCode == StatusCode.Aborted)
        {
            throw new BindingDeniedException();
        }
    }
}
