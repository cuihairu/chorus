using System.Net.WebSockets;
using Chorus.Gateway.Core;
using Chorus.Gateway.Core.Faces;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.Gateway;

/// <summary>
/// Streamer-session resolution for the split topology: the client is registered
/// over the engine IPC and the welcome comes back through it, bounded by a 2s
/// welcome timeout. Registration failures and welcome timeouts surface as
/// error messages without closing the socket, matching the monolith's
/// fail-closed posture.
/// </summary>
public sealed class IpcSessionRouter : IStreamerSessionRouter
{
    private const int WelcomeTimeoutSeconds = 2;

    private readonly GatewayIpcService _ipc;
    private readonly FaceCounters _faces;

    public IpcSessionRouter(GatewayIpcService ipc, FaceCounters faces)
    {
        _ipc = ipc;
        _faces = faces;
    }

    public async Task<StreamerWsConnection> ConnectAsync(string sessionId, string clientId, WebSocket ws, CancellationToken cancellationToken)
    {
        var conn = new GatewayClientConnection(sessionId, clientId, ws, _faces);
        var binding = new IpcSessionBinding(_ipc, sessionId, clientId, conn);

        // The waiter must die with the welcome window: a timed-out registration
        // that left its waiter parked would reject every later reconnect of the
        // same (session, client) with "Welcome waiter already exists" and
        // self-sustain a reconnect storm (2026-10-15 gate run: 185k failed
        // connects in one 120s run, gw->eng lane drowned in the churn).
        var welcomeTask = _ipc.CreateWelcomeWaiter(sessionId, clientId, cancellationToken);
        try
        {
            await _ipc.RegisterClientAsync(conn, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await WebSocketHelpers.SendJsonAsync(ws, new ServerError(ProtocolTypes.Error, "Engine unavailable"), cancellationToken);
            return new StreamerWsConnection(null, binding);
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(WelcomeTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var welcome = await welcomeTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return new StreamerWsConnection(welcome, binding);
        }
        catch
        {
            await WebSocketHelpers.SendJsonAsync(ws, new ServerError(ProtocolTypes.Error, "Engine welcome timeout"), cancellationToken);
            return new StreamerWsConnection(null, binding);
        }
        finally
        {
            // Deterministic cleanup: on the success path the welcome already
            // consumed the waiter and this is a no-op.
            _ipc.TryCancelWelcomeWaiter(sessionId, clientId);
        }
    }

    private sealed class IpcSessionBinding : IStreamerSessionBinding
    {
        private readonly GatewayIpcService _ipc;
        private readonly string _sessionId;
        private readonly string _clientId;
        private readonly GatewayClientConnection _conn;

        public IpcSessionBinding(GatewayIpcService ipc, string sessionId, string clientId, GatewayClientConnection conn)
        {
            _ipc = ipc;
            _sessionId = sessionId;
            _clientId = clientId;
            _conn = conn;
        }

        public ValueTask AckAsync(long lastAppliedTickId, long clientTimeMs, CancellationToken cancellationToken)
            => _ipc.PublishAckAsync(_sessionId, _clientId, lastAppliedTickId, clientTimeMs, cancellationToken);

        public ValueTask ResyncAsync(long fromFrameId, CancellationToken cancellationToken)
            => _ipc.PublishResyncAsync(_sessionId, _clientId, fromFrameId, cancellationToken);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken)
            => _ipc.UnregisterClientAsync(_sessionId, _clientId, cancellationToken);

        public ValueTask DisposeAsync() => _conn.DisposeAsync();
    }
}
