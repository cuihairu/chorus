using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Protocol;

namespace Chorus.Gateway.Core;

public static class WebSocketHelpers
{
    /// <summary>
    /// Receives one full text message with a hard size cap: a message larger
    /// than <paramref name="maxBytes"/> throws <see cref="WsMessageTooLargeException"/>
    /// (callers close the connection). Guards the streamer-ws listener face
    /// against oversized client messages.
    /// </summary>
    public static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken cancellationToken, int maxBytes)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Closed");
            }

            if (result.Count > 0)
            {
                if (ms.Length + result.Count > maxBytes)
                {
                    throw new WsMessageTooLargeException(maxBytes);
                }

                ms.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, ProtocolJson.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }
}

/// <summary>Thrown when a WS text message exceeds the configured size cap.</summary>
public sealed class WsMessageTooLargeException : Exception
{
    public WsMessageTooLargeException(int maxBytes)
        : base($"WebSocket message exceeds the {maxBytes} byte limit")
    {
        MaxBytes = maxBytes;
    }

    public int MaxBytes { get; }
}
