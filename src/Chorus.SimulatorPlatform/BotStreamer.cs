using System.Security.Cryptography;
using Chorus.Platform;

namespace Chorus.SimulatorPlatform;

/// <summary>One mounted simulator live session: its platform identity, gameplay binding,
/// and the live key audience events attribute by (that key is the ingress payload's
/// sessionId — the routing snapshot's canonical live session key).</summary>
public sealed record BotStreamerHandle(
    string StreamerId, string SessionId, string BindingId, string OpenId, string LiveKey);

/// <summary>
/// One simulated streamer on the real launch chain (BOTS-1 discipline: bots ride the
/// actual takeover/resume path, never a side door). Mounting issues a signed development
/// credential, logs the session in through the streamer gateway's gRPC face, and starts
/// gameplay — the binding the room's participants and the audience-event attribution
/// both consume.
/// </summary>
public sealed class BotStreamer
{
    private readonly DevelopmentPlatformSimulator _simulator;
    private readonly Chorus.Rpc.StreamerSession.StreamerSessionClient _session;
    private readonly string _appId;
    private readonly string _gameId;
    private readonly string _openId;
    private readonly string _liveKey;

    public BotStreamer(DevelopmentPlatformSimulator simulator,
        Chorus.Rpc.StreamerSession.StreamerSessionClient session,
        string appId, string gameId, string openId, string liveKey)
    {
        _simulator = simulator;
        _session = session;
        _appId = appId;
        _gameId = gameId;
        _openId = openId;
        _liveKey = liveKey;
    }

    public BotStreamerHandle? Handle { get; private set; }

    /// <summary>Issues the credential, logs in, and starts gameplay. One mount per
    /// streamer; the handle carries everything the room roster and the fleet poster need.</summary>
    public async Task<BotStreamerHandle> MountAsync(TimeSpan? credentialLifetime = null,
        CancellationToken cancellationToken = default)
    {
        if (Handle is not null)
            throw new InvalidOperationException("A bot streamer mounts once.");
        var credential = _simulator.IssueCredential(_appId, _openId, _liveKey,
            credentialLifetime ?? TimeSpan.FromMinutes(10));
        var login = await _session.LoginAsync(new Chorus.Rpc.LoginRequest
        {
            Platform = DevelopmentPlatformSimulator.PlatformName,
            AppId = _appId,
            LaunchCredential = credential,
            LoginAttemptId = "attempt-" + _openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1",
            ConnectionId = "connection-" + _openId
        }, cancellationToken: cancellationToken);
        var gameplay = await _session.StartGameplayAsync(new Chorus.Rpc.StartGameplayRequest
        {
            AccessCredential = login.AccessCredential,
            GameId = _gameId,
            OperationId = "mount-" + _openId
        }, cancellationToken: cancellationToken);
        Handle = new BotStreamerHandle(login.StreamerId, login.SessionId, gameplay.BindingId,
            _openId, _liveKey);
        return Handle;
    }
}
