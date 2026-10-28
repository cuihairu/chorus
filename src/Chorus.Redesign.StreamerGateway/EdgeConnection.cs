using Chorus.Redesign.StreamerGateway.Messages;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>The verdict of the per-class limiter for one inbound frame.</summary>
public enum EdgeRateVerdict
{
    /// <summary>The frame is within its class budget (or the class is not limited).</summary>
    Allowed,
    /// <summary>Over budget and visible: the client gets a rate_limited error.</summary>
    Rejected,
    /// <summary>Over budget and silent: acknowledgements are dropped and counted, never
    /// answered — flooding acks must not generate a reply flood of its own.</summary>
    Dropped,
}

/// <summary>
/// One accepted connection's edge state: identity the gateway itself minted (the connection
/// id), the hello facts, the per-class rate counters, and — once a login establishes it — the
/// session facts the StreamerServer returned. Client-uplink identity fields are never stored
/// here (contracts 3.1: the gateway does not believe client-provided identity). The downlink
/// side is a single serialized lane: the dispatch loop and a session-replaced notice arriving
/// on the internal gRPC face both send through it, so frames never interleave.
/// </summary>
public sealed class EdgeConnection
{
    private readonly EdgeRateCounter _login;
    private readonly EdgeRateCounter _control;
    private readonly EdgeRateCounter _ack;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Func<string, CancellationToken, Task>? _sendFrameAsync;
    private Func<System.Net.WebSockets.WebSocketCloseStatus, string, Task>? _closeAsync;

    public EdgeConnection(string connectionId, string gatewayInstanceId, EdgeOptions options,
        Func<DateTimeOffset>? now = null)
    {
        ConnectionId = connectionId;
        GatewayInstanceId = gatewayInstanceId;
        var window = TimeSpan.FromMinutes(1);
        _login = new EdgeRateCounter(options.LoginPerMinute, window, now);
        _control = new EdgeRateCounter(options.ControlPerMinute, window, now);
        _ack = new EdgeRateCounter(options.AckPerMinute, window, now);
    }

    public string ConnectionId { get; }
    public string GatewayInstanceId { get; }
    public DateTimeOffset OpenedAtUtc { get; } = DateTimeOffset.UtcNow;
    public bool HelloAccepted { get; private set; }
    /// <summary>Downlink counters the /metrics of later rounds will read; a connection that
    /// never got past hello has no session facts to expose.</summary>
    public long FramesReceived { get; private set; }

    public string? TenantId { get; private set; }
    public string? SessionId { get; private set; }
    public string? StreamerId { get; private set; }
    /// <summary>The access credential names the acting session on every later uplink; it is
    /// held in memory only and never written to logs or metrics.</summary>
    public string? AccessCredential { get; private set; }

    public bool HasSession => SessionId is { Length: > 0 };

    public string? BindingId { get; private set; }
    public string? GameId { get; private set; }
    public string? RulesVersion { get; private set; }

    /// <summary>Queue uplinks need a mount: the binding facts a startGameplay established.</summary>
    public bool HasGameplay => BindingId is { Length: > 0 };

    private string? _lastNotifiedRoomId;

    public void MarkHelloAccepted() => HelloAccepted = true;

    public void MarkSession(string tenantId, string sessionId, string streamerId, string accessCredential)
    {
        TenantId = tenantId;
        SessionId = sessionId;
        StreamerId = streamerId;
        AccessCredential = accessCredential;
    }

    public void MarkGameplay(string bindingId, string gameId, string? rulesVersion)
    {
        BindingId = bindingId;
        GameId = gameId;
        RulesVersion = rulesVersion;
    }

    /// <summary>Claims the right to push one room notice: true only the first time this room
    /// is named for this connection, so a routing flap cannot re-announce the same round.</summary>
    public bool TryClaimRoomNotice(string roomId)
    {
        if (string.Equals(_lastNotifiedRoomId, roomId, StringComparison.Ordinal)) return false;
        _lastNotifiedRoomId = roomId;
        return true;
    }

    /// <summary>Binds the socket's send/close operations behind the serialized lane; called
    /// once right after the socket is accepted, before any frame can flow in either
    /// direction.</summary>
    public void AttachTransport(
        Func<string, CancellationToken, Task> sendFrameAsync,
        Func<System.Net.WebSockets.WebSocketCloseStatus, string, Task> closeAsync)
    {
        _sendFrameAsync = sendFrameAsync;
        _closeAsync = closeAsync;
    }

    /// <summary>Sends one already-serialized frame; every downlink path funnels through here
    /// so the socket never sees concurrent sends.</summary>
    public async Task SendAsync(string frame, CancellationToken cancellation = default)
    {
        if (_sendFrameAsync is null) return;
        await _sendLock.WaitAsync(cancellation);
        try { await _sendFrameAsync(frame, cancellation); }
        finally { _sendLock.Release(); }
    }

    public async Task SendJsonAsync<T>(T message, CancellationToken cancellation = default)
        => await SendAsync(System.Text.Json.JsonSerializer.Serialize(message, EdgeJson.Options), cancellation);

    /// <summary>Closes through the same lane, so a replacement notice mid-flight and the
    /// dispatcher's own close decision serialize instead of colliding on the socket.</summary>
    public async Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus status, string reason)
    {
        if (_closeAsync is null) return;
        await _sendLock.WaitAsync(CancellationToken.None);
        try { await _closeAsync(status, reason); }
        finally { _sendLock.Release(); }
    }

    /// <summary>The active leg of session replacement on this gateway: push sessionReplaced,
    /// then close. Returns false when the connection has no transport bound (it is already
    /// gone) or the push failed — the caller reports delivery, nothing more.</summary>
    public async Task<bool> TryDeliverSessionReplacedAsync(string reason)
    {
        if (_sendFrameAsync is null || _closeAsync is null) return false;
        try
        {
            await SendJsonAsync(new EdgeSessionReplaced { Reason = reason }, CancellationToken.None);
            await CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                EdgeTypes.SessionReplaced);
            return true;
        }
        catch (Exception error) when (error is System.Net.WebSockets.WebSocketException
            or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Classifies a frame and consumes its budget. Frames before the hello gate and
    /// pings are not limited: the gate has its own timeout, and a ping flood proves
    /// reachability at transport cost only (contracts 3.1).</summary>
    public EdgeRateVerdict Admit(string type)
    {
        FramesReceived++;
        return type switch
        {
            EdgeTypes.LoginWithPlatformLaunch or EdgeTypes.Resume
                => _login.TryConsume() ? EdgeRateVerdict.Allowed : EdgeRateVerdict.Rejected,
            EdgeTypes.StartGameplay or EdgeTypes.JoinQueue or EdgeTypes.CancelQueue
                or EdgeTypes.LeaveRound or EdgeTypes.Logout or EdgeTypes.RequestSnapshot
                => _control.TryConsume() ? EdgeRateVerdict.Allowed : EdgeRateVerdict.Rejected,
            EdgeTypes.Ack
                => _ack.TryConsume() ? EdgeRateVerdict.Allowed : EdgeRateVerdict.Dropped,
            _ => EdgeRateVerdict.Allowed,
        };
    }
}
