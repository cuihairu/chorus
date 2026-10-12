using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;

namespace Chorus.Server;

public sealed class SessionManager : IAsyncDisposable
{
    private readonly Dictionary<string, SessionActor> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly IGameModuleFactory _gameFactory;

    public SessionConfig Config { get; }

    public SessionManager(IGameModuleFactory gameFactory, SessionConfig config)
    {
        _gameFactory = gameFactory;
        Config = config;
    }

    public string CreateSessionId() => $"s_{Guid.NewGuid():N}";

    public SessionActor GetOrCreate(string sessionId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var s))
            {
                return s;
            }

            var game = _gameFactory.Create(sessionId);
            var actor = new SessionActor(sessionId, Config, game);
            actor.Start();
            _sessions[sessionId] = actor;
            return actor;
        }
    }

    public IReadOnlyList<SessionDiagnostics> GetDiagnostics()
    {
        lock (_lock)
        {
            return _sessions.Values.Select(s => s.Diagnostics).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SessionActor[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var s in sessions)
        {
            await s.DisposeAsync();
        }
    }
}
