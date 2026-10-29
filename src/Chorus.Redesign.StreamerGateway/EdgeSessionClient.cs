using Chorus.Redesign.Rpc;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The gateway's client of the StreamerSession boundary (m1): login, resume and logout over
/// cluster mTLS. The gateway is cluster-scoped, so it names the tenant on every call — the
/// login resolves it from the application map, the resume leaves it to the server (only the
/// session knows), and the logout uses the facts its own login stored. RpcExceptions flow to
/// the edge dispatcher, which maps them to visible protocol errors.
/// </summary>
public sealed class EdgeSessionClient(Func<GrpcChannel> channelFactory, string gatewayInstanceId)
{
    private readonly Lazy<GrpcChannel> _channel = new(channelFactory);

    public async Task<LoginReadyReply> LoginAsync(string tenantId, string platform, string appId,
        string launchCredential, string loginAttemptId, byte[] nextResumeSecret, string connectionId,
        CancellationToken cancellation)
    {
        var client = new StreamerSession.StreamerSessionClient(_channel.Value);
        return await client.LoginAsync(new LoginRequest
        {
            TenantId = tenantId,
            Platform = platform,
            AppId = appId,
            LaunchCredential = launchCredential,
            LoginAttemptId = loginAttemptId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(nextResumeSecret),
            GatewayInstanceId = gatewayInstanceId,
            ConnectionId = connectionId
        }, cancellationToken: cancellation);
    }

    public async Task<LoginReadyReply> ResumeAsync(string previousSessionId, string resumeOperationId,
        byte[] previousSecret, byte[] nextSecret, string connectionId, CancellationToken cancellation)
    {
        var client = new StreamerSession.StreamerSessionClient(_channel.Value);
        return await client.ResumeAsync(new ResumeRequest
        {
            PreviousSessionId = previousSessionId,
            ResumeOperationId = resumeOperationId,
            PreviousSecret = Google.Protobuf.ByteString.CopyFrom(previousSecret),
            NextSecret = Google.Protobuf.ByteString.CopyFrom(nextSecret),
            GatewayInstanceId = gatewayInstanceId,
            ConnectionId = connectionId
            // No tenant: the resume names no application, so the server resolves the owner
            // from the session itself.
        }, cancellationToken: cancellation);
    }

    public async Task<LogoutReply> LogoutAsync(string tenantId, string accessCredential, string connectionId,
        string operationId, CancellationToken cancellation)
    {
        var client = new StreamerSession.StreamerSessionClient(_channel.Value);
        return await client.LogoutAsync(new LogoutRequest
        {
            TenantId = tenantId,
            AccessCredential = accessCredential,
            GatewayInstanceId = gatewayInstanceId,
            ConnectionId = connectionId,
            OperationId = operationId
        }, cancellationToken: cancellation);
    }

    /// <summary>Mounts the live session onto a game (m1 StartGameplay). The access credential —
    /// not any client-sent identity — names the acting session; the game id is the client's
    /// only free choice here, and the server still checks it against the tenant's apps.</summary>
    public async Task<GameplayStatusReply> StartGameplayAsync(string tenantId, string accessCredential,
        string gameId, string operationId, CancellationToken cancellation)
    {
        var client = new StreamerSession.StreamerSessionClient(_channel.Value);
        return await client.StartGameplayAsync(new StartGameplayRequest
        {
            TenantId = tenantId,
            AccessCredential = accessCredential,
            GameId = gameId,
            OperationId = operationId
        }, cancellationToken: cancellation);
    }

    /// <summary>The mount facts of the acting session (m1 GameplayStatus) — how a resume
    /// restores the binding a fresh connection does not carry. An empty binding id in the
    /// reply is the store's word that there is no mount.</summary>
    public async Task<GameplayStatusReply> GameplayStatusAsync(string tenantId, string accessCredential,
        CancellationToken cancellation)
    {
        var client = new StreamerSession.StreamerSessionClient(_channel.Value);
        return await client.GameplayStatusAsync(new GameplayStatusRequest
        {
            TenantId = tenantId,
            AccessCredential = accessCredential
        }, cancellationToken: cancellation);
    }
}
