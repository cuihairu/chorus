using System.Net.WebSockets;
using Chorus.Engine.Session;
using Chorus.Gateway.Core.Faces;
using Chorus.Protocol;
using Chorus.Protocol.Messages;
using Microsoft.Extensions.Logging;

namespace Chorus.Server;

/// <summary>
/// Streamer-session resolution for the monolith topology: the session actor
/// lives in-process and the welcome is built from the session config, so the
/// /ws face needs no IPC at all.
/// </summary>
public sealed class MonolithSessionRouter : IStreamerSessionRouter
{
    private readonly SessionManager _sessions;
    private readonly ILogger _logger;

    public MonolithSessionRouter(SessionManager sessions, ILogger logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<StreamerWsConnection> ConnectAsync(string sessionId, string clientId, WebSocket ws, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrCreate(sessionId);
        var client = new WsSessionClient(clientId, sessionId, ws, _logger);
        await session.AddClientAsync(client).ConfigureAwait(false);

        var welcome = new ServerWelcome(
            Type: ProtocolTypes.Welcome,
            SessionId: sessionId,
            ClientId: clientId,
            TickDurationMs: _sessions.Config.TickDurationMs,
            InputDelayTicks: _sessions.Config.InputDelayTicks,
            SnapshotEveryTicks: _sessions.Config.SnapshotEveryTicks,
            ServerTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ProtocolVersion: ProtocolVersionPolicy.Current);

        return new StreamerWsConnection(welcome, new MonolithSessionBinding(session, clientId));
    }

    private sealed class MonolithSessionBinding : IStreamerSessionBinding
    {
        private readonly SessionActor _session;
        private readonly string _clientId;

        public MonolithSessionBinding(SessionActor session, string clientId)
        {
            _session = session;
            _clientId = clientId;
        }

        public ValueTask AckAsync(long lastAppliedTickId, long clientTimeMs, CancellationToken cancellationToken)
            => _session.AckAsync(_clientId, lastAppliedTickId, clientTimeMs);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken)
            => _session.RemoveClientAsync(_clientId);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
