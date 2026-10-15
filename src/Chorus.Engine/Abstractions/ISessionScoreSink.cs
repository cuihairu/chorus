namespace Chorus.Engine.Abstractions;

/// <summary>
/// Host-side sink for authoritative session scores (audit §9.2: only the engine
/// side may project scores; clients never self-report). A game module calls
/// <see cref="ProjectScore"/> for each participant whose authoritative total
/// changed - the host decides the scope (room board keyed by session id) and
/// where the board lives. Push-only: the module never reads rankings back, so
/// the projection cannot influence the game loop.
/// </summary>
public interface ISessionScoreSink
{
    /// <summary>Projects a participant's current authoritative total (replace
    /// semantics downstream). Called on the session's tick path: implementations
    /// must not block and must treat failure as "board not updated", never as a
    /// game error.</summary>
    void ProjectScore(string sessionId, string participantId, long score);
}
