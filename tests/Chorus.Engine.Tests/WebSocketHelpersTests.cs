using System.Net.WebSockets;
using Chorus.Gateway.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// Receive-side size cap: an oversized text frame must throw
/// <see cref="WsMessageTooLargeException"/> carrying the configured limit,
/// so callers can log/close with the number that was exceeded.
/// </summary>
public sealed class WebSocketHelpersTests
{
    [Fact]
    public async Task ReceiveText_OverCap_ThrowsWithMaxBytes()
    {
        var ws = new StubSocket(segmentSize: 16 * 1024);

        var ex = await Assert.ThrowsAsync<WsMessageTooLargeException>(
            async () => await WebSocketHelpers.ReceiveTextAsync(ws, CancellationToken.None, 64));

        Assert.Equal(64, ex.MaxBytes);
    }

    private sealed class StubSocket(int segmentSize) : WebSocket
    {
        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort() => throw new NotSupportedException();

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(segmentSize, WebSocketMessageType.Text, true));

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
