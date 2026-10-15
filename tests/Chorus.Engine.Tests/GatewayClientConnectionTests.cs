using System.Net.WebSockets;
using Chorus.Gateway;
using Chorus.Protocol.Messages;

namespace Chorus.Engine.Tests;

/// <summary>
/// 去阻塞发送的 per-connection pump：latest-wins 槽 + 后台 pump，慢/断开的
/// streamer 连接不得拖住 IPC 派发循环。传输异常必须安静终结 pump，已关闭的
/// 连接再投快照计为 dropped 而不是假成功。
/// </summary>
public sealed class GatewayClientConnectionTests
{
    private static ServerSnapshot Snapshot(long tickId) => new(
        "snapshot", "s1", tickId, 0,
        System.Text.Json.JsonSerializer.SerializeToElement(new { v = tickId }));

    [Fact]
    public async Task SendSnapshot_PumpsOnce_AndReportsZeroDropped()
    {
        var ws = new RecordingSocket();
        await using var conn = new GatewayClientConnection("s1", "c1", ws);

        await conn.SendSnapshotAsync(Snapshot(1), CancellationToken.None);
        await WaitForAsync(() => ws.Sent.Count == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(0, conn.Dropped);
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task SendSnapshot_AfterDispose_CountsDropped()
    {
        var ws = new RecordingSocket();
        await using var conn = new GatewayClientConnection("s1", "c1", ws);
        await conn.DisposeAsync();

        await conn.SendSnapshotAsync(Snapshot(1), CancellationToken.None);

        // 写入槽已 complete：不得假成功，必须计入 dropped。
        Assert.Equal(1, conn.Dropped);
        Assert.Equal(0, ws.Sent.Count);
    }

    [Fact]
    public async Task DoubleDisposeIsNoOp()
    {
        // 与各 MessageBus 相同的仓内契约：DisposeAsync 幂等，二次调用不得抛。
        var ws = new RecordingSocket();
        var conn = new GatewayClientConnection("s1", "c1", ws);

        await conn.DisposeAsync();
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Pump_SendThrows_FailsQuietly_AndStaysDead()
    {
        // 传输中途断开：pump 吞掉异常退出，不把异常抛回 fire-and-forget 调用方。
        var ws = new RecordingSocket(new InvalidOperationException("connection reset"));
        await using var conn = new GatewayClientConnection("s1", "c1", ws);

        await conn.SendSnapshotAsync(Snapshot(1), CancellationToken.None);
        await WaitForAsync(() => ws.SendAttempts == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(0, ws.Sent.Count);
        Assert.Equal(0, conn.Dropped); // TryWrite 成功入槽，断开不算 dropped。

        await conn.DisposeAsync();
        await conn.SendSnapshotAsync(Snapshot(2), CancellationToken.None);

        Assert.Equal(1, conn.Dropped); // 泵已死 + 槽已完结：只可能计 dropped。
        Assert.Equal(0, ws.Sent.Count);
    }

    [Fact]
    public async Task Pump_ClosedSocket_StopsBeforeSend()
    {
        var ws = new RecordingSocket();
        ws.Close();
        await using var conn = new GatewayClientConnection("s1", "c1", ws);

        await conn.SendSnapshotAsync(Snapshot(1), CancellationToken.None);
        await conn.DisposeAsync(); // DisposeAsync 等待 pump 退出：此后断言无竞态。

        Assert.Equal(0, ws.SendAttempts);
        Assert.Equal(0, ws.Sent.Count);
    }

    [Fact]
    public async Task Dispose_CancelsPumpWaitingOnChannel_SwallowingOce()
    {
        // Pump sitting on the empty channel when the connection closes: the
        // cancellation must be swallowed, not escaped as an unobserved fault.
        var ws = new RecordingSocket();
        await using var conn = new GatewayClientConnection("s1", "c1", ws);

        await conn.SendSnapshotAsync(Snapshot(1), CancellationToken.None);
        await WaitForAsync(() => ws.Sent.Count == 1, TimeSpan.FromSeconds(5));

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_AwaitingFaultingPump_SwallowingPumpException()
    {
        // Transport dies mid-send: dispose must join a faulted pump and not
        // surface the transport exception to the caller.
        var ws = new RecordingSocket(new WebSocketException("connection reset"));
        await using var conn = new GatewayClientConnection("s1", "c1", ws);

        await conn.SendSnapshotAsync(Snapshot(1), CancellationToken.None);
        await WaitForAsync(() => ws.SendAttempts == 1, TimeSpan.FromSeconds(5));

        await conn.DisposeAsync();

        Assert.Equal(0, ws.Sent.Count);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            await Task.Delay(10);
        }
        if (!condition())
        {
            throw new TimeoutException("condition not met");
        }
    }

    private sealed class RecordingSocket : WebSocket
    {
        private readonly Exception? _sendThrows;
        private WebSocketState _state = WebSocketState.Open;
        private int _attempts;
        private readonly object _gate = new();

        public RecordingSocket(Exception? sendThrows = null)
        {
            _sendThrows = sendThrows;
        }

        public List<byte[]> Sent { get; } = new();

        public int SendAttempts
        {
            get
            {
                lock (_gate)
                {
                    return _attempts;
                }
            }
        }

        public void Close()
        {
            _state = WebSocketState.Closed;
        }

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
            lock (_gate)
            {
                _attempts++;
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
    }
}
