using Chorus.Engine.Session;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Messages;

namespace Chorus.EngineHost;

public sealed class EngineBusSessionClient : ISessionClient
{
    private readonly IMessageBus _outgoing;
    private readonly string _sessionId;
    private readonly Func<bool> _isBroadcastOwner;

    public EngineBusSessionClient(IMessageBus outgoing, string sessionId, string clientId, Func<bool> isBroadcastOwner)
    {
        _outgoing = outgoing;
        _sessionId = sessionId;
        ClientId = clientId;
        _isBroadcastOwner = isBroadcastOwner;
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
            State: System.Text.Json.JsonSerializer.SerializeToElement(snapshot.State, Chorus.Protocol.ProtocolJson.SerializerOptions),
            Forced: snapshot.Forced,
            Reason: snapshot.Reason,
            TargetClientId: snapshot.TargetClientId,
            ClientLagTicks: snapshot.ClientLagTicks,
            ClientLastAckTickId: snapshot.ClientLastAckTickId
        );

        var env = EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, _sessionId, msg);
        return SafePublishAsync(env, cancellationToken);
    }

    private async ValueTask SafePublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        try
        {
            await _outgoing.PublishAsync(env, cancellationToken);
        }
        catch
        {
        }
    }
}
