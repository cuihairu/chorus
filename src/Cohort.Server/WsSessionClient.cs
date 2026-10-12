using System.Net.WebSockets;
using System.Text.Json;
using Cohort.Engine.Session;
using Cohort.Gateway.Core;
using Cohort.Protocol;
using Cohort.Protocol.Messages;

namespace Cohort.Server;

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
            using var doc = JsonSerializer.SerializeToDocument(snapshot.State, ProtocolJson.SerializerOptions);
            var payload = new ServerSnapshot(
                Type: ProtocolTypes.Snapshot,
                SessionId: snapshot.SessionId,
                TickId: snapshot.TickId,
                ServerTimeMs: snapshot.ServerTimeMs,
                State: doc.RootElement.Clone(),
                Forced: snapshot.Forced,
                Reason: snapshot.Reason,
                TargetClientId: snapshot.TargetClientId,
                ClientLagTicks: snapshot.ClientLagTicks,
                ClientLastAckTickId: snapshot.ClientLastAckTickId
            );

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await WebSocketHelpers.SendJsonAsync(_ws, payload, cancellationToken);
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
            _logger.LogDebug(ex, "Failed to send snapshot");
        }
    }
}
