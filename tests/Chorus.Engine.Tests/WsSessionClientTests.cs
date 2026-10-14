using System.Net.WebSockets;
using System.Text;
using Chorus.Engine.Session;
using Chorus.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// WsSessionClient 传输层：编码快路径、sessionId 过滤、以及发送失败/取消
/// 的静默吞掉语义（帧同步链路上客户端掉线不能让广播线程挂掉）。
/// </summary>
public sealed class WsSessionClientTests
{
    private static SessionSnapshot SnapshotFor(string sessionId, long tickId)
        => new(sessionId, tickId, 0, new { v = 1 });

    private static void AssertBody(byte[] expected, byte[] actual)
    {
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
    }

    private sealed class StubWebSocket : WebSocket
    {
        private WebSocketState _state;
        private Exception? _sendThrows;

        public StubWebSocket(WebSocketState state = WebSocketState.Open)
        {
            _state = state;
        }

        public void Arm(Exception ex) => _sendThrows = ex;

        public List<byte[]> Sent { get; } = new();

        public override WebSocketState State => _state;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort() => throw new NotSupportedException();

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

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
        {
            var ex = _sendThrows;
            if (ex is not null)
            {
                throw ex;
            }

            var copy = new byte[buffer.Count];
            Buffer.BlockCopy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            Sent.Add(copy);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SendSnapshot_EncodesAndSends()
    {
        var ws = new StubWebSocket();
        var client = new WsSessionClient("c1", "s1", ws, NullLogger.Instance);
        var snapshot = SnapshotFor("s1", tickId: 3);

        await client.SendSnapshotAsync(snapshot, CancellationToken.None);

        Assert.Single(ws.Sent);
        Assert.Equal(
            WireSnapshot.Encode(snapshot).EncodedJson,
            ws.Sent[0]);
    }

    [Fact]
    public async Task SendSnapshot_SkipsWhenSessionIdDiffers()
    {
        var ws = new StubWebSocket();
        var client = new WsSessionClient("c1", "s1", ws, NullLogger.Instance);

        await client.SendSnapshotAsync(SnapshotFor("other", tickId: 3), CancellationToken.None);

        Assert.Empty(ws.Sent);
    }

    [Fact]
    public async Task SendSnapshot_SkipsWhenSocketNotOpen()
    {
        var ws = new StubWebSocket(WebSocketState.Closed);
        var client = new WsSessionClient("c1", "s1", ws, NullLogger.Instance);

        await client.SendSnapshotAsync(SnapshotFor("s1", tickId: 3), CancellationToken.None);

        Assert.Empty(ws.Sent);
    }

    [Fact]
    public async Task SendSnapshot_SwallowsCancel()
    {
        var ws = new StubWebSocket();
        ws.Arm(new OperationCanceledException());
        var client = new WsSessionClient("c1", "s1", ws, NullLogger.Instance);

        await client.SendSnapshotAsync(SnapshotFor("s1", tickId: 3), CancellationToken.None);
    }

    [Fact]
    public async Task SendSnapshot_SwallowsTransportError()
    {
        var ws = new StubWebSocket();
        ws.Arm(new IOException("socket died"));
        var client = new WsSessionClient("c1", "s1", ws, NullLogger.Instance);

        await client.SendSnapshotAsync(SnapshotFor("s1", tickId: 3), CancellationToken.None);
    }
}
