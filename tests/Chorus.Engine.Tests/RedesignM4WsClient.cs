using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.StreamerGateway;
using Chorus.StreamerGateway.Messages;

namespace Chorus.Engine.Tests;

/// <summary>
/// A real WSS client for the streamer edge tests: a ClientWebSocket whose TLS validation
/// chains the server certificate against the test root — the same root every host
/// certificate chains to — instead of disabling validation. One frame is one JSON message,
/// serialized with the gateway's own <see cref="EdgeJson"/> options so both sides agree on
/// the wire shape.
/// </summary>
public sealed class RedesignM4WsClient : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Uri _uri;
    private readonly byte[] _buffer = new byte[64 * 1024];

    private RedesignM4WsClient(int wssPort, X509Certificate2 root, X509Certificate2 nodeAuthority)
    {
        _uri = new Uri($"wss://localhost:{wssPort}/ws");
        _socket.Options.RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new X509Certificate2(certificate);
            using var validator = new X509Chain();
            validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            // The server presents its leaf certificate alone, so the issuing node authority
            // must be supplied to build the intermediate hop.
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }

    public static async Task<RedesignM4WsClient> ConnectAsync(int wssPort, string rootPem,
        string nodeAuthorityPem)
    {
        using var root = X509Certificate2.CreateFromPem(rootPem);
        using var nodeAuthority = X509Certificate2.CreateFromPem(nodeAuthorityPem);
        var client = new RedesignM4WsClient(wssPort, root, nodeAuthority);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client._socket.ConnectAsync(client._uri, timeout.Token);
        return client;
    }

    /// <summary>The default hello every test starts from: the current protocol version.</summary>
    public Task HelloAsync(string? clientBuild = "test-client") => SendAsync(new EdgeHello
    {
        ProtocolVersion = EdgeProtocolVersion.Current, ClientBuild = clientBuild
    });

    public Task SendAsync<T>(T message) => _socket.SendAsync(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, EdgeJson.Options)),
        WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Sends one raw pre-built frame — for frames the helpers would not produce,
    /// like an oversized body or a hand-written envelope.</summary>
    public Task SendRawAsync(string text) => _socket.SendAsync(
        Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Receives one JSON frame. Returns null when the server closed instead.</summary>
    public async Task<JsonElement?> ReceiveAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            using var frame = new MemoryStream();
            while (true)
            {
                var received = await _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), cancellation.Token);
                if (received.MessageType == WebSocketMessageType.Close) return null;
                frame.Write(_buffer, 0, received.Count);
                if (received.EndOfMessage)
                    return JsonDocument.Parse(Encoding.UTF8.GetString(frame.ToArray())).RootElement.Clone();
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No frame arrived within {timeout}.");
        }
    }

    /// <summary>Waits for a frame of the given type, keeping any earlier frames as
    /// diagnostics in the thrown timeout.</summary>
    public async Task<JsonElement> ExpectAsync(string type, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var seen = new List<string>();
        while (DateTime.UtcNow < deadline)
        {
            var frame = await ReceiveAsync(deadline - DateTime.UtcNow);
            if (frame is null) throw new TimeoutException(
                $"The gateway closed while waiting for '{type}' (saw: {string.Join(", ", seen)}).");
            if (frame.Value.GetProperty("type").GetString() == type) return frame.Value;
            seen.Add(frame.Value.GetProperty("type").GetString() ?? "?");
        }
        throw new TimeoutException($"Frame '{type}' never arrived (saw: {string.Join(", ", seen)}).");
    }

    /// <summary>Waits for the server to close and reports the close status it chose; text
    /// frames sent on the way out are drained, not buffered.</summary>
    public async Task<WebSocketCloseStatus> WaitForCloseAsync(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(Math.Max(100, (deadline - DateTime.UtcNow).TotalMilliseconds)));
            WebSocketReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The gateway never closed the connection.");
            }
            if (received.MessageType == WebSocketMessageType.Close)
                return received.CloseStatus ?? WebSocketCloseStatus.Empty;
        }
    }

    public void Dispose() => _socket.Dispose();
}
