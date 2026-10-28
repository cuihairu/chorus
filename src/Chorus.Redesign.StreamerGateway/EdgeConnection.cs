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
/// id), the hello facts, and the per-class rate counters. Client-uplink identity fields are
/// never stored here — the session facts a login establishes arrive from the StreamerServer
/// and are the only ones trusted (contracts 3.1: the gateway does not believe client-provided
/// identity).
/// </summary>
public sealed class EdgeConnection
{
    private readonly EdgeRateCounter _login;
    private readonly EdgeRateCounter _control;
    private readonly EdgeRateCounter _ack;

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

    public void MarkHelloAccepted() => HelloAccepted = true;

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
