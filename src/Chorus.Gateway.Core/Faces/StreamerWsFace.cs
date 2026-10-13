using System.Net.WebSockets;
using System.Text.Json;
using Chorus.Gateway.Core;
using Chorus.Protocol;
using Chorus.Protocol.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Chorus.Gateway.Core.Faces;

/// <summary>
/// Per-client lifecycle owned by the host: the monolith keeps the session
/// actor in-process, the gateway routes through the engine IPC. Both hosts
/// implement this against their own router.
/// </summary>
public interface IStreamerSessionBinding : IAsyncDisposable
{
    ValueTask AckAsync(long lastAppliedTickId, long clientTimeMs, CancellationToken cancellationToken);

    /// <summary>Client-initiated gap-fill request (frame-sync P2): the client reports
    /// the last frame it applied; the engine replays the retained window.</summary>
    ValueTask ResyncAsync(long fromFrameId, CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a (sessionId, clientId) streamer client for its host topology.
/// </summary>
public interface IStreamerSessionRouter
{
    /// <summary>
    /// Registers the streamer client and returns the connection context. The
    /// welcome is null when registration failed and the failure reason was
    /// already written to <paramref name="ws"/>; the binding stays valid either
    /// way so the caller can run disconnect cleanup.
    /// </summary>
    Task<StreamerWsConnection> ConnectAsync(string sessionId, string clientId, WebSocket ws, CancellationToken cancellationToken);
}

/// <summary>
/// A registered streamer connection: the welcome to send plus the binding that
/// owns the per-client lifecycle on the host.
/// </summary>
public sealed record StreamerWsConnection(ServerWelcome? Welcome, IStreamerSessionBinding Binding);

/// <summary>
/// The streamer-ws listener face shared by the monolith and the gateway:
/// hello token auth (fail-closed), protocol version gate, message body cap,
/// ACK rate limit and disconnect cleanup. The only host-specific pieces are
/// <see cref="IStreamerSessionRouter"/> and the logger category the host
/// injects, so the two /ws endpoints can no longer drift apart.
/// </summary>
public sealed class StreamerWsFace
{
    private readonly StreamerWsFaceOptions _options;
    private readonly IStreamerSessionRouter _router;
    private readonly FaceCounters _faces;
    private readonly ILogger _logger;

    public StreamerWsFace(
        StreamerWsFaceOptions options,
        IStreamerSessionRouter router,
        FaceCounters faces,
        ILogger logger)
    {
        _options = options;
        _router = router;
        _faces = faces;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected WebSocket request.");
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _faces.WsConnection();

        string helloText;
        try
        {
            helloText = await WebSocketHelpers.ReceiveTextAsync(ws, context.RequestAborted, _options.MaxMessageBytes);
        }
        catch (WsMessageTooLargeException)
        {
            _faces.WsMessageTooLarge();
            _logger.LogWarning("WS hello exceeded {MaxBytes} bytes; closing connection", _options.MaxMessageBytes);
            await RejectConnectionAsync(ws, WebSocketCloseStatus.MessageTooBig, "hello too large", error: null, context.RequestAborted);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to receive hello");
            await RejectConnectionAsync(ws, WebSocketCloseStatus.NormalClosure, "invalid hello",
                new ServerError(ProtocolTypes.Error, "Invalid hello"), context.RequestAborted);
            return;
        }

        ClientHello? hello;
        try
        {
            hello = JsonSerializer.Deserialize<ClientHello>(helloText, ProtocolJson.SerializerOptions);
        }
        catch (JsonException)
        {
            hello = null;
        }

        if (hello == null || !string.Equals(hello.Type, ProtocolTypes.Hello, StringComparison.Ordinal))
        {
            await RejectConnectionAsync(ws, WebSocketCloseStatus.NormalClosure, "first message must be hello",
                new ServerError(ProtocolTypes.Error, "First message must be hello"), context.RequestAborted);
            return;
        }

        var versionReason = ProtocolVersionPolicy.CheckRejectionReason(hello.ProtocolVersion);
        if (versionReason != null)
        {
            _faces.WsProtocolMismatch();
            _logger.LogWarning("WS connection rejected ({Reason}) session={SessionId}", versionReason, hello.SessionId);
            await RejectConnectionAsync(ws, WebSocketCloseStatus.PolicyViolation, versionReason,
                new ServerError(ProtocolTypes.Error, versionReason), context.RequestAborted);
            return;
        }

        var auth = _options.Authenticate(hello.Token, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!auth.Ok)
        {
            _faces.WsUnauthorized();
            _logger.LogWarning("WS connection rejected ({Reason}) for session {SessionId}", auth.Reason, hello.SessionId);
            await RejectConnectionAsync(ws, WebSocketCloseStatus.PolicyViolation, auth.Reason,
                new ServerError(ProtocolTypes.Error, $"unauthorized: {auth.Reason}"), context.RequestAborted);
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(hello.SessionId) ? $"s_{Guid.NewGuid():N}" : hello.SessionId!;
        var clientId = string.IsNullOrWhiteSpace(hello.ClientId) ? $"c_{Guid.NewGuid():N}" : hello.ClientId!;

        var connection = await _router.ConnectAsync(sessionId, clientId, ws, context.RequestAborted);

        try
        {
            // A null welcome means the router rejected the registration and
            // already wrote the failure reason to the socket; cleanup below
            // still unregisters/disposes the client.
            if (connection.Welcome == null)
            {
                return;
            }

            await WebSocketHelpers.SendJsonAsync(ws, connection.Welcome, context.RequestAborted);

            var binding = connection.Binding;
            var ackWindowStartMs = 0L;
            var ackCount = 0;
            var resyncWindowStartMs = 0L;
            var resyncCount = 0;
            while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                string text;
                try
                {
                    text = await WebSocketHelpers.ReceiveTextAsync(ws, context.RequestAborted, _options.MaxMessageBytes);
                }
                catch (WsMessageTooLargeException)
                {
                    _faces.WsMessageTooLarge();
                    _logger.LogWarning("WS message exceeded {MaxBytes} bytes; closing connection", _options.MaxMessageBytes);
                    await RejectConnectionAsync(ws, WebSocketCloseStatus.MessageTooBig, "message too large", error: null, context.RequestAborted);
                    return;
                }

                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (string.Equals(type, ProtocolTypes.Ack, StringComparison.Ordinal))
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (ackWindowStartMs == 0 || nowMs - ackWindowStartMs >= 60_000)
                    {
                        ackWindowStartMs = nowMs;
                        ackCount = 1;
                    }
                    else if (ackCount >= _options.AckPerMinute)
                    {
                        _faces.WsAckDropped();
                        continue;
                    }
                    else
                    {
                        ackCount++;
                    }

                    _faces.WsAckAccepted();
                    var ack = doc.RootElement.Deserialize<ClientAck>(ProtocolJson.SerializerOptions);
                    if (ack != null)
                    {
                        await binding.AckAsync(ack.LastAppliedTickId, ack.ClientTimeMs ?? 0, context.RequestAborted);
                    }
                }
                else if (string.Equals(type, ProtocolTypes.Resync, StringComparison.Ordinal))
                {
                    // Frame-sync P2: client-initiated gap-fill. Same fixed-window
                    // shape as the ACK limiter - the engine applies its own
                    // per-client cooldown, this only bounds the request rate.
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (resyncWindowStartMs == 0 || nowMs - resyncWindowStartMs >= 60_000)
                    {
                        resyncWindowStartMs = nowMs;
                        resyncCount = 1;
                    }
                    else if (resyncCount >= _options.ResyncPerMinute)
                    {
                        _faces.WsResyncDropped();
                        continue;
                    }
                    else
                    {
                        resyncCount++;
                    }

                    var resync = doc.RootElement.Deserialize<ClientResync>(ProtocolJson.SerializerOptions);
                    if (resync != null)
                    {
                        await binding.ResyncAsync(resync.FromFrameId, context.RequestAborted);
                    }
                }
                else if (string.Equals(type, ProtocolTypes.Ping, StringComparison.Ordinal))
                {
                    await WebSocketHelpers.SendJsonAsync(ws, new ServerPong(ProtocolTypes.Pong, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), context.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket loop error session={SessionId} clientId={ClientId}", sessionId, clientId);
        }
        finally
        {
            await connection.Binding.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await connection.Binding.DisposeAsync().ConfigureAwait(false);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private static async Task RejectConnectionAsync(
        WebSocket ws,
        WebSocketCloseStatus closeStatus,
        string reason,
        object? error,
        CancellationToken cancellationToken)
    {
        try
        {
            if (error != null)
            {
                await WebSocketHelpers.SendJsonAsync(ws, error, cancellationToken);
            }

            await ws.CloseAsync(closeStatus, reason, cancellationToken);
        }
        catch
        {
            // Client already gone; nothing to reject.
        }
    }
}
