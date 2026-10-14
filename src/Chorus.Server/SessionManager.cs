using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;

namespace Chorus.Server;

/// <summary>
/// Monolith-mode session table. Session lifecycle (cap, idle eviction, sweep)
/// lives in <see cref="SessionRegistry"/>, shared with the split mode's
/// EngineIpcService; only the actor factory and the client-presence probe are
/// monolith-specific here.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly SessionRegistry _registry;
    private readonly IGameModuleFactory _gameFactory;
    private readonly ISnapshotRecoverySource? _recoverySource;

    public SessionConfig Config => _registry.Config;

    /// <summary>
    /// Session start failures (e.g. snapshot restore refusing a session,
    /// fail-closed) - observable because the failed session has no actor
    /// diagnostics to read.
    /// </summary>
    public long StartFailures => _registry.StartFailures;

    public SessionManager(
        IGameModuleFactory gameFactory,
        SessionConfig config,
        ISnapshotRecoverySource? recoverySource = null)
    {
        _gameFactory = gameFactory;
        _recoverySource = recoverySource;
        // The probe reads the actor's own diagnostics: a session with
        // connected clients is never idle-evicted. Monitor locks are
        // reentrant, so the registry calling back into TryGet under its own
        // lock is safe.
        _registry = new SessionRegistry(
            config,
            CreateActor,
            id => _registry.TryGet(id, out var actor) && actor!.Diagnostics.Clients.Count > 0);
    }

    private SessionActor CreateActor(string sessionId)
    {
        var game = _gameFactory.Create(sessionId);
        // Restore before the actor exists: no tick and no ingress event can
        // reach an unrestored module (parity with EngineIpcService - both
        // call the same single-point restorer inside the factory).
        GameModuleRestorer.Restore(game, sessionId, _recoverySource, _registry.Config);
        var actor = new SessionActor(sessionId, _registry.Config, game);
        actor.Start();
        return actor;
    }

    /// <summary>Live session count (test/observability helper).</summary>
    public int Count => _registry.Count;

    public SessionActor GetOrCreate(string sessionId) => _registry.GetOrCreate(sessionId);

    /// <summary>
    /// Idle eviction (review stage-2): removes sessions with no connected
    /// clients whose last activity is older than <see cref="SessionConfig.IdleEvictAfterMs"/>.
    /// Returns the number of evicted sessions.
    /// </summary>
    public Task<int> SweepIdleAsync(long? nowMs = null) => _registry.SweepIdleAsync(nowMs);

    public IReadOnlyList<SessionDiagnostics> GetDiagnostics() => _registry.GetDiagnostics();

    public async ValueTask DisposeAsync()
    {
        await _registry.DisposeAsync().ConfigureAwait(false);
    }
}
