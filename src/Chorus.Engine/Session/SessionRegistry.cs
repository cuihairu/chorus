using Chorus.Engine.Abstractions;

namespace Chorus.Engine.Session;

/// <summary>
/// Shared session lifecycle (todo §3.5 router abstraction, first slice):
/// session table with last-seen tracking, session cap with idlest-first
/// eviction, and client-less idle sweep. Both deployment shapes build on it -
/// the monolith's SessionManager and the split mode's EngineIpcService used to
/// keep independent copies of this exact logic.
/// The shape-specific parts are injected: how to build an actor and how to
/// detect "session still has clients" (monolith reads actor diagnostics, the
/// split engine reads its own IPC client map).
/// </summary>
public sealed class SessionRegistry : IAsyncDisposable
{
    private sealed class Entry
    {
        public required SessionActor Actor { get; init; }
        public long LastSeenAtMs { get; set; }
    }

    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Func<string, SessionActor> _createActor;
    private readonly Func<string, bool> _hasClients;
    private readonly Func<string, bool> _canRelease;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweepLoop;
    private int _disposed;

    public SessionConfig Config { get; }

    public SessionRegistry(
        SessionConfig config,
        Func<string, SessionActor> createActor,
        Func<string, bool> hasClients,
        bool runSweepLoop = true,
        Func<string, bool>? canRelease = null)
    {
        Config = config;
        _createActor = createActor;
        _hasClients = hasClients;
        _canRelease = canRelease ?? (_ => config.CloseClientlessIdleRooms);
        _sweepLoop = runSweepLoop ? SweepLoopAsync(_sweepCts.Token) : Task.CompletedTask;
    }

    /// <summary>Live session count (test/observability helper).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Session creations that failed (e.g. snapshot restore refusing the
    /// session, fail-closed). The failure itself is thrown to the caller and
    /// no actor is inserted - this counter makes the rejections observable.
    /// </summary>
    public long StartFailures => Interlocked.Read(ref _startFailures);

    private long _startFailures;

    /// <summary>Cumulative sessions removed by the client-less idle sweep.</summary>
    public long IdleEvictions => Interlocked.Read(ref _idleEvictions);

    private long _idleEvictions;

    /// <summary>Cumulative sessions evicted to make room under MaxSessions.</summary>
    public long CapacityEvictions => 0;



    public SessionActor GetOrCreate(string sessionId, long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SessionActor actor;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                existing.LastSeenAtMs = now;
                return existing.Actor;
            }

            // Authoritative state is not a cache. Reject new admission without
            // touching existing actors, including client-less business rooms.
            if (Config.MaxSessions > 0 && _sessions.Count >= Config.MaxSessions)
            {
                Interlocked.Increment(ref _capacityRejections);
                throw new SessionCapacityException(Config.MaxSessions);
            }

            try
            {
                actor = _createActor(sessionId);
            }
            catch
            {
                // Factory failure (e.g. restore fail-closed): count it,
                // propagate, and leave nothing behind - no half state actor
                // in the table.
                Interlocked.Increment(ref _startFailures);
                throw;
            }

            _sessions[sessionId] = new Entry { Actor = actor, LastSeenAtMs = now };
        }

        return actor;
    }

    public bool TryGet(string sessionId, out SessionActor? actor)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
            {
                actor = entry.Actor;
                return true;
            }
        }

        actor = null;
        return false;
    }

    private long _capacityRejections;
    /// <summary>New rooms refused at capacity; existing authority is preserved.</summary>
    public long CapacityRejections => Interlocked.Read(ref _capacityRejections);

    /// <summary>
    /// Idle eviction (review stage-2): removes sessions with no connected
    /// clients whose last activity is older than <see cref="SessionConfig.IdleEvictAfterMs"/>.
    /// Returns the number of evicted sessions.
    /// </summary>
    public async Task<int> SweepIdleAsync(long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<SessionActor> victims = new();

        lock (_lock)
        {
            foreach (var (id, entry) in _sessions)
            {
                var clientless = !_hasClients(id);
                var idle = now - entry.LastSeenAtMs >= Config.IdleEvictAfterMs;
                if (clientless && idle && _canRelease(id))
                {
                    victims.Add(entry.Actor);
                    _sessions.Remove(id);
                }
            }

            // Count at the decision point, matching EvictIdlestLocked: after
            // the map removal but before the victims' disposal, the sessions
            // gauge has already dropped while this counter would still lag -
            // a scraper (or a Count-polling caller) sees an inconsistent pair.
            if (victims.Count > 0)
            {
                Interlocked.Add(ref _idleEvictions, victims.Count);
            }
        }

        foreach (var victim in victims)
        {
            await DisposeQuietlyAsync(victim).ConfigureAwait(false);
        }

        return victims.Count;
    }

    /// <summary>Explicit business close; callers must authorize and persist first.</summary>
    public async Task<bool> CloseAsync(string sessionId)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId, out entry)) return false;
        }
        await entry.Actor.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<SessionDiagnostics> GetDiagnostics()
    {
        lock (_lock)
        {
            return _sessions.Values.Select(s => s.Actor.Diagnostics).ToArray();
        }
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(Config.IdleEvictAfterMs / 4, 1_000, 60_000));
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SweepIdleAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DisposeQuietlyAsync(SessionActor actor)
    {
        try
        {
            await actor.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Legal to run twice: EngineIpcService.StopAsync disposes the registry
        // and BackgroundService.DisposeAsync calls StopAsync again when the
        // execute task has not finished. Without the guard the second pass
        // throws ObjectDisposedException cancelling the disposed sweep CTS.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sweepCts.Cancel();
        try
        {
            await _sweepLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        _sweepCts.Dispose();

        SessionActor[] actors;
        lock (_lock)
        {
            actors = _sessions.Values.Select(e => e.Actor).ToArray();
            _sessions.Clear();
        }

        foreach (var actor in actors)
        {
            await actor.DisposeAsync().ConfigureAwait(false);
        }
    }
}
