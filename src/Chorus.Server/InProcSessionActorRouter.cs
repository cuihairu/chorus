using Chorus.Engine.Session;
using Chorus.Gateway.Core.Actors;
using Chorus.Protocol.Models;

namespace Chorus.Server;

/// <summary>
/// In-process routing table for the monolith topology (todo §3.5): Resolve is
/// a <see cref="SessionRegistry"/> lookup-or-create, so the cap and idle
/// eviction live in the shared session lifecycle and every caller — ingress
/// face today, future Tell callers later — gets the same sessionId → actor
/// table the streamer-ws face uses.
/// </summary>
public sealed class InProcSessionActorRouter : ISessionActorRouter
{
    private readonly SessionManager _sessions;

    public InProcSessionActorRouter(SessionManager sessions)
    {
        _sessions = sessions;
    }

    public ISessionActorRef Resolve(string sessionId)
    {
        return new InProcSessionActorRef(_sessions.GetOrCreate(sessionId));
    }
}

/// <summary>
/// ActorRef over a live in-process actor. Tell maps the bounded lane's
/// backpressure to <see cref="SessionTellOutcome.Rejected"/>; the actor is
/// always reachable in-proc, so Unavailable never happens here.
/// </summary>
public sealed class InProcSessionActorRef : ISessionActorRef
{
    private readonly SessionActor _session;

    public InProcSessionActorRef(SessionActor session)
    {
        _session = session;
    }

    public string SessionId => _session.SessionId;

    public long? TickId => _session.TickId;

    public async ValueTask<SessionTellOutcome> TellAsync(AudienceEvent e, CancellationToken cancellationToken)
    {
        // The token is intentionally unused: in-proc lane writes are
        // synchronous, and the gift overflow wait carries its own budget.
        return await _session.TryIngestAudienceEventAsync(e).ConfigureAwait(false)
            ? SessionTellOutcome.Accepted
            : SessionTellOutcome.Rejected;
    }
}
