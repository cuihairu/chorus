using Chorus.Engine.Session;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Messages;

namespace Chorus.EngineHost;

public sealed class EngineBusSessionClient : ISessionClient
{
    private readonly IMessageBus _outgoing;
    private readonly string _sessionId;

    public EngineBusSessionClient(IMessageBus outgoing, string sessionId, string clientId)
    {
        _outgoing = outgoing;
        _sessionId = sessionId;
        ClientId = clientId;
    }

    public string ClientId { get; }

    public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
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
