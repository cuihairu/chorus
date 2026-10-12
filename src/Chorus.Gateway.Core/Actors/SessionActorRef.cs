using Chorus.Protocol.Models;

namespace Chorus.Gateway.Core.Actors;

/// <summary>
/// Outcome of a Tell to a session's actor inbox. Explicit because the two
/// topologies fail differently: the monolith's bounded lane can reject
/// (Rejected) but its actor is always reachable, while the split gateway
/// publishes onto the engine IPC and only sees transport failures
/// (Unavailable). Callers treat both as "not delivered": roll back the dedup
/// claim and answer 503 so platform retries are fresh deliveries.
/// </summary>
public enum SessionTellOutcome
{
    /// <summary>Event accepted into the session's inbox (or published onward).</summary>
    Accepted,

    /// <summary>The session's bounded inbox rejected the event (lane full). Monolith only.</summary>
    Rejected,

    /// <summary>The delivery path is unavailable (transport down). Split gateway only.</summary>
    Unavailable,
}

/// <summary>
/// Tell handle for one session's actor inbox (todo §3.5): callers deliver
/// audience events without knowing whether the actor lives in-process
/// (monolith) or behind the engine IPC (split gateway).
/// </summary>
public interface ISessionActorRef
{
    string SessionId { get; }

    /// <summary>
    /// Engine tick when the topology exposes it (monolith: yes, its refs wrap
    /// the live actor; split gateway: null — the engine's tick travels only
    /// with welcomes/snapshots, not per event).
    /// </summary>
    long? TickId { get; }

    /// <summary>
    /// Tell semantics: never throws for delivery failure (Rejected/Unavailable
    /// are the outcomes); cancellation still propagates as
    /// OperationCanceledException. The monolith implementation ignores the
    /// token: in-proc lane writes are synchronous and the gift overflow wait
    /// carries its own budget.
    /// </summary>
    ValueTask<SessionTellOutcome> TellAsync(AudienceEvent e, CancellationToken cancellationToken);
}

/// <summary>
/// Routing table: sessionId → actor ref. Monolith: SessionRegistry-backed
/// (lookup + create, cap eviction and idle sweep inside). Split gateway:
/// session-scoped refs that publish to the engine bus — the engine side owns
/// the real table (its own SessionRegistry).
/// </summary>
public interface ISessionActorRouter
{
    ISessionActorRef Resolve(string sessionId);
}
