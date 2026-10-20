using Chorus.Engine.Session;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Messages;

namespace Chorus.EngineHost;

public sealed class EngineBusSessionClient : ISessionClient
{
    private readonly IMessageBus _outgoing;
    private readonly long? _authorityEpoch;
    private readonly string _sessionId;
    private readonly Func<bool> _isBroadcastOwner;
    private readonly Action<Envelope, Exception> _onPublishFailure;

    public EngineBusSessionClient(IMessageBus outgoing, string sessionId, string clientId, Func<bool> isBroadcastOwner, Action<Envelope, Exception> onPublishFailure, long? authorityEpoch = null)
    {
        _outgoing = outgoing;
        _authorityEpoch = authorityEpoch;
        _sessionId = sessionId;
        ClientId = clientId;
        _isBroadcastOwner = isBroadcastOwner;
        _onPublishFailure = onPublishFailure;
    }

    public string ClientId { get; }

    public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Publish dedup (docs/design/shared-memory-transport.md alt 1): a
        // broadcast snapshot (no target client) is identical for every client
        // and the gateway already fans one null-target snapshot out to the
        // whole session, so only the session's broadcast owner publishes it.
        // Directed resync snapshots publish from the targeted client alone —
        // same WS behavior, N× fewer eng→gw messages.
        if (snapshot.TargetClientId is null)
        {
            if (!_isBroadcastOwner())
            {
                return ValueTask.CompletedTask;
            }
        }
        else if (!string.Equals(snapshot.TargetClientId, ClientId, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        var msg = new ServerSnapshot(
            Type: Chorus.Protocol.ProtocolTypes.Snapshot,
            SessionId: snapshot.SessionId,
            TickId: snapshot.TickId,
            ServerTimeMs: snapshot.ServerTimeMs,
            // Written inline, not via SerializeToElement: the DOM copy made the
            // state serialize twice per snapshot on the eng→gw path too.
            State: snapshot.State,
            Forced: snapshot.Forced,
            Reason: snapshot.Reason,
            TargetClientId: snapshot.TargetClientId,
            ClientLagTicks: snapshot.ClientLagTicks,
            ClientLastAckTickId: snapshot.ClientLastAckTickId,
            V: snapshot.V,
            FrameEvents: snapshot.FrameEvents is { Count: > 0 } ? new List<FrameEvent>(snapshot.FrameEvents) : null
        );

        var env = EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, _sessionId, msg) with { AuthorityEpoch = _authorityEpoch };
        return SafePublishAsync(env, cancellationToken);
    }

    private async ValueTask SafePublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        try
        {
            await _outgoing.PublishAsync(env, cancellationToken);
        }
        catch (Exception ex)
        {
            // Snapshot delivery is best-effort at the engine boundary — the
            // tick must not die on a gateway hiccup — but the failure is
            // reported so it is counted and logged (parity with the gateway
            // side's chorus_ipc_publish_failures_total) instead of vanishing.
            _onPublishFailure(env, ex);
        }
    }
}
