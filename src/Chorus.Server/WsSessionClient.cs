using System.Net.WebSockets;
using System.Text.Json;
using Chorus.Engine.Session;
using Chorus.Gateway.Core;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.Server;

public sealed class WsSessionClient : ISessionClient
{
    private readonly string _sessionId;
    private readonly WebSocket _ws;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WsSessionClient(string clientId, string sessionId, WebSocket ws, ILogger logger)
    {
        ClientId = clientId;
        _sessionId = sessionId;
        _ws = ws;
        _logger = logger;
    }

    public string ClientId { get; }

    public async ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await SendSnapshotAsync(Chorus.Engine.Session.WireSnapshot.Encode(snapshot), cancellationToken);
    }

    /// <summary>
    /// Fast path: the actor encoded the wire snapshot once for the whole
    /// broadcast; this transport just writes the bytes (no per-client JSON).
    /// </summary>
    public async ValueTask SendSnapshotAsync(Chorus.Engine.Session.WireSnapshot wire, CancellationToken cancellationToken)
    {
        var snapshot = wire.Snapshot;
        if (_ws.State != WebSocketState.Open)
        {
            return;
        }

        if (!string.Equals(snapshot.SessionId, _sessionId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _ws.SendAsync(wire.EncodedJson, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send snapshot session={SessionId} clientId={ClientId} tick={TickId}",
                snapshot.SessionId, ClientId, snapshot.TickId);
        }
    }
}
