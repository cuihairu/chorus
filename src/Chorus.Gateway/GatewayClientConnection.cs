using System.Net.WebSockets;
using Chorus.Gateway.Core;
using Chorus.Protocol.Messages;

namespace Chorus.Gateway;

public sealed class GatewayClientConnection
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public GatewayClientConnection(string sessionId, string clientId, WebSocket ws)
    {
        SessionId = sessionId;
        ClientId = clientId;
        _ws = ws;
    }

    public string SessionId { get; }
    public string ClientId { get; }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async ValueTask SendSnapshotAsync(ServerSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await WebSocketHelpers.SendJsonAsync(_ws, snapshot, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
