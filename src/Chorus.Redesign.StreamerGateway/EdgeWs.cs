using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Redesign.StreamerGateway.Messages;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The public /ws face (contracts 3). One text frame is one JSON message. The hello is the
/// first frame and gates everything — a connection that never completes it is closed within
/// the pre-auth window, and a hello with an unknown protocol version is refused before any
/// other byte is read. Frames above the size cap close the connection (transport limits are
/// enforced at the transport). This milestone wires the transport contract: hello, ping/pong
/// and the error envelope; the session and gameplay uplinks arrive with the login round and
/// plug into this same dispatch loop.
/// </summary>
public sealed class EdgeWs(EdgeOptions options, EdgeSessionRegistry registry, string gatewayInstanceId)
{
    private const int ReceiveChunkBytes = 8 * 1024;

    /// <summary>RFC 6455 1009 "message too big": the .NET enum stops below it, so the raw
    /// code is sent as-is — the client sees the standard status, not a borrowed one.</summary>
    private const WebSocketCloseStatus TooLargeCloseStatus = (WebSocketCloseStatus)1009;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new EdgeConnection(Guid.NewGuid().ToString("N"), gatewayInstanceId, options);
        if (!registry.TryOpen(connection))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, EdgeErrorCodes.GatewayFull);
            return;
        }
        try
        {
            // The hello has its own deadline: an accepted socket that stays silent is closed
            // when the window lapses — the edge holds no unauthenticated connections.
            using var gate = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            gate.CancelAfter(options.PreAuthTimeout);
            var helloText = await ReceiveTextAsync(socket, gate.Token);
            if (helloText is null) return; // the client left before speaking.
            if (!await TryAcceptHelloAsync(socket, helloText)) return;
            connection.MarkHelloAccepted();
            await DispatchLoopAsync(socket, connection, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client or the host went away; nothing to answer.
        }
        catch (OperationCanceledException)
        {
            // The pre-auth window lapsed while the receive was still pending. A canceled
            // receive leaves the managed websocket past a graceful close handshake — the
            // socket aborts, and the client sees the transport drop. What the contract
            // guarantees is that the edge holds no unauthenticated connection open, not the
            // close code it cannot send anymore.
            socket.Abort();
        }
        catch (EdgeMessageTooLargeException)
        {
            await SendJsonAsync(socket, new EdgeError
            {
                Code = EdgeErrorCodes.MessageTooLarge,
                Message = "The frame exceeds the message size limit."
            }, CancellationToken.None);
            await CloseAsync(socket, TooLargeCloseStatus, EdgeErrorCodes.MessageTooLarge);
        }
        catch (WebSocketException)
        {
            // A transport failure mid-frame: the connection is gone, close is not deliverable.
        }
        catch (JsonException)
        {
            await SendJsonAsync(socket, new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "The frame is not a valid protocol message."
            }, CancellationToken.None);
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, EdgeErrorCodes.InvalidMessage);
        }
        finally
        {
            registry.Close(connection);
        }
    }

    private async Task<bool> TryAcceptHelloAsync(WebSocket socket, string helloText)
    {
        var hello = JsonSerializer.Deserialize<EdgeHello>(helloText, EdgeJson.Options)
            ?? throw new JsonException();
        if (hello.ProtocolVersion != EdgeProtocolVersion.Current)
        {
            await SendJsonAsync(socket, new EdgeError
            {
                Code = EdgeErrorCodes.ProtocolMismatch,
                Message = $"Protocol version {hello.ProtocolVersion} is not supported."
            }, CancellationToken.None);
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, EdgeErrorCodes.ProtocolMismatch);
            return false;
        }
        return true;
    }

    private async Task DispatchLoopAsync(WebSocket socket, EdgeConnection connection,
        CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, cancellation);
            if (text is null) return; // the client closed gracefully.
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var type = typeElement.GetString()!;
            var verdict = connection.Admit(type);
            if (verdict == EdgeRateVerdict.Dropped) continue;
            if (verdict == EdgeRateVerdict.Rejected)
            {
                await SendJsonAsync(socket, new EdgeError
                {
                    Code = EdgeErrorCodes.RateLimited,
                    Message = $"The {type} budget for this window is exhausted."
                }, cancellation);
                continue;
            }
            switch (type)
            {
                case EdgeTypes.Ping:
                    await SendJsonAsync(socket, new EdgePong
                    {
                        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }, cancellation);
                    break;
                default:
                    // A closed type set (contracts 3.1): what is not defined is a violation,
                    // not a future message to skip over.
                    await SendJsonAsync(socket, new EdgeError
                    {
                        Code = EdgeErrorCodes.InvalidMessage,
                        Message = $"Message type {type} is not part of the protocol."
                    }, cancellation);
                    await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation,
                        EdgeErrorCodes.InvalidMessage);
                    return;
            }
        }
    }

    /// <summary>Accumulates one text frame, enforcing the total size cap. Returns null on a
    /// client-initiated close.</summary>
    private async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[ReceiveChunkBytes];
        using var frame = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            frame.Write(buffer, 0, received.Count);
            if (frame.Length > options.MaxMessageBytes) throw new EdgeMessageTooLargeException();
            if (received.EndOfMessage)
            {
                if (frame.Length == 0) throw new JsonException();
                return Encoding.UTF8.GetString(frame.ToArray());
            }
        }
    }

    private static ValueTask SendJsonAsync<T>(WebSocket socket, T message, CancellationToken cancellation)
        => new(socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, EdgeJson.Options)),
            WebSocketMessageType.Text, true, cancellation));

    private static Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
        => socket.CloseAsync(status, reason, CancellationToken.None);

    /// <summary>Thrown when a frame exceeds the transport cap; the connection closes with
    /// MessageTooLarge — a client that oversized one frame may size the next worse.</summary>
    private sealed class EdgeMessageTooLargeException : Exception;
}
