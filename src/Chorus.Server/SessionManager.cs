using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;

namespace Chorus.Server;

public sealed class SessionManager : IAsyncDisposable
{
    private sealed class Entry
    {
        public required SessionActor Actor { get; init; }
        public long LastSeenAtMs { get; set; }
    }

    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly IGameModuleFactory _gameFactory;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweepLoop;

    public SessionConfig Config { get; }

    public SessionManager(IGameModuleFactory gameFactory, SessionConfig config)
    {
        _gameFactory = gameFactory;
        Config = config;
        _sweepLoop = SweepLoopAsync(_sweepCts.Token);
    }

    public string CreateSessionId() => $"s_{Guid.NewGuid():N}";

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

    public SessionActor GetOrCreate(string sessionId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SessionActor? evicted = null;
        SessionActor actor;

        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                existing.LastSeenAtMs = now;
                return existing.Actor;
            }

            // Session cap (review stage-2): make room for new streams by
            // evicting the least-recently-seen session; the victim's clients
            // reconnect via a fresh session id.
            if (Config.MaxSessions > 0 && _sessions.Count >= Config.MaxSessions)
            {
                evicted = EvictIdlestLocked();
            }

            var game = _gameFactory.Create(sessionId);
            actor = new SessionActor(sessionId, Config, game);
            actor.Start();
            _sessions[sessionId] = new Entry { Actor = actor, LastSeenAtMs = now };
        }

        if (evicted != null)
        {
            _ = DisposeQuietlyAsync(evicted);
        }

        return actor;
    }

    private SessionActor? EvictIdlestLocked()
    {
        string? idlestId = null;
        var idlestSeen = long.MaxValue;
        foreach (var (id, entry) in _sessions)
        {
            if (entry.LastSeenAtMs < idlestSeen)
            {
                idlestSeen = entry.LastSeenAtMs;
                idlestId = id;
            }
        }

        if (idlestId == null || !_sessions.Remove(idlestId, out var victim))
        {
            return null;
        }

        return victim.Actor;
    }

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
                var clientless = entry.Actor.Diagnostics.Clients.Count == 0;
                var idle = now - entry.LastSeenAtMs >= Config.IdleEvictAfterMs;
                if (clientless && idle)
                {
                    victims.Add(entry.Actor);
                    _sessions.Remove(id);
                }
            }
        }

        foreach (var victim in victims)
        {
            await DisposeQuietlyAsync(victim).ConfigureAwait(false);
        }

        return victims.Count;
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
