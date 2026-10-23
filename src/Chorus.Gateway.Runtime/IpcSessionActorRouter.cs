using Chorus.Gateway.Core.Actors;
using Chorus.Gateway.Core.Faces;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;

namespace Chorus.Gateway;

/// <summary>
/// Routing for the split topology (todo §3.5): every session Tell publishes
/// onto the engine IPC with sessionId as the ordering key; the engine side
/// (EngineIpcService + its SessionRegistry) owns the real routing table.
/// </summary>
public sealed class IpcSessionActorRouter : ISessionActorRouter
{
    private readonly GatewayIpcService _ipc;

    public IpcSessionActorRouter(GatewayIpcService ipc)
    {
        _ipc = ipc;
    }

    public ISessionActorRef Resolve(string sessionId)
    {
        return new IpcSessionActorRef(_ipc, sessionId);
    }
}

/// <summary>
/// ActorRef over the engine IPC. Transport failures map to
/// <see cref="SessionTellOutcome.Unavailable"/> (already counted and logged in
/// the publish path); cancellation propagates so the face can answer 504.
/// </summary>
public sealed class IpcSessionActorRef : ISessionActorRef
{
    private readonly GatewayIpcService _ipc;
    private readonly string _sessionId;

    public IpcSessionActorRef(GatewayIpcService ipc, string sessionId)
    {
        _ipc = ipc;
        _sessionId = sessionId;
    }

    public string SessionId => _sessionId;

    public long? TickId => null;

    public async ValueTask<SessionTellOutcome> TellAsync(AudienceEvent e, CancellationToken cancellationToken)
    {
        try
        {
            await _ipc.PublishAudienceEventAsync(
                EnvelopeFactory.Create(IpcMessageTypes.GatewayAudienceEvent, _sessionId, e, sourceEventId: e.EventId),
                cancellationToken).ConfigureAwait(false);
            return SessionTellOutcome.Accepted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SessionTellOutcome.Unavailable;
        }
    }
}
