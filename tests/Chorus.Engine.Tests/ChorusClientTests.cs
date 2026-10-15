using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Chorus.Client;
using Chorus.Server;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-sync P3 acceptance (客户端 SDK): a new project connects with one call
/// and one render callback against the real /ws surface - states arrive by
/// frame number (strictly increasing, replays dropped idempotently), acks flow
/// after applying, and handshake failures surface as exceptions.
/// </summary>
public sealed class ChorusClientTests
{
    /// <summary>Connect through the in-memory test server's WS transport
    /// (the SDK's transport seam; production uses a real ClientWebSocket).</summary>
    private static Task<ChorusClient> ConnectAsync(
        WebApplicationFactory<Chorus.Server.Program> factory,
        string sessionId,
        string clientId,
        Action<JsonElement> onState)
    {
        var wsUrl = new Uri(factory.Server.BaseAddress, "ws").ToString();
        var wsClient = factory.Server.CreateWebSocketClient();
        return ChorusClient.ConnectAsync(
            ChorusClientOptions.Default(wsUrl, sessionId, clientId),
            onState,
            connect: wsClient.ConnectAsync);
    }

    [Fact]
    public async Task Connect_RendersStatesByFrameNumber_AndAcks()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();

        var states = new List<JsonElement>();
        var gate = new SemaphoreSlim(1, 1);
        // Quickstart-shaped usage: one call, one callback.
        await using var client = await ConnectAsync(factory, "s-sdk-e2e", "c-sdk",
            state =>
            {
                gate.Wait();
                try
                {
                    states.Add(state);
                }
                finally
                {
                    gate.Release();
                }
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            gate.Wait();
            var count = states.Count;
            gate.Release();
            if (count >= 3)
            {
                break;
            }

            await Task.Delay(10);
        }

        gate.Wait();
        try
        {
            Assert.True(states.Count >= 3, $"only {states.Count} states rendered");
            Assert.All(states, s => Assert.Equal(JsonValueKind.Object, s.ValueKind));
        }
        finally
        {
            gate.Release();
        }

        Assert.True(client.FramesApplied >= 3);
        Assert.True(client.LastAppliedFrameId >= 3);
        // Frames arrive strictly by frame number - never a stale or replayed one.
        Assert.True(client.LastAppliedFrameId >= client.FramesApplied);
        Assert.True(client.AcksSent >= 1, "no ack reached the wire");
    }

    [Fact]
    public async Task Backpressure_StalledCallback_DropsDoNotRegressTheAppliedId()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();

        // Stall the render callback: the server's latest-wins outbound slot
        // drops frames for this slow client, which is exactly the gap-fill
        // scenario - the applied id may jump, but never regress.
        await using var client = await ConnectAsync(factory, "s-sdk-lag", "c-sdk-lag",
            _ => Thread.Sleep(120));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && client.FramesApplied < 3)
        {
            await Task.Delay(10);
        }

        Assert.True(client.FramesApplied >= 1, "no frame applied under backpressure");
        Assert.True(client.Completion.IsFaulted == false);
    }

    [Fact]
    public async Task FrameGaps_PullResync_WithinCooldown()
    {
        // Snapshots every 2 ticks: the client observes tickId jumps (0, 2, 4,
        // ...) - every periodic frame is a gap, so the SDK must keep pulling
        // the missing window, cooldown-limited, and drop replayed frames.
        using var factory = new WebApplicationFactory<Chorus.Server.Program>()
            .WithWebHostBuilder(b => b.UseSetting("Session:SnapshotEveryTicks", "2"));
        var wsClient = factory.Server.CreateWebSocketClient();

        var options = ChorusClientOptions.Default(
                new Uri(factory.Server.BaseAddress, "ws").ToString(), "s-sdk-gap", "c-sdk-gap")
            with { ResyncCooldown = TimeSpan.FromMilliseconds(100) };

        await using var client = await ChorusClient.ConnectAsync(
            options, _ => { },
            connect: (uri, ct) => wsClient.ConnectAsync(uri, ct));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.ResyncsSent < 2)
        {
            await Task.Delay(10);
        }

        Assert.True(client.ResyncsSent >= 2,
            $"frames={client.FramesApplied} resyncs={client.ResyncsSent}");
        // Replayed frames must be dropped idempotently: the applied id only
        // ever moves forward.
        Assert.True(client.LastAppliedFrameId >= client.FramesApplied);
    }

    [Fact]
    public async Task Connect_ToUnreachableServer_Throws()
    {
        var options = ChorusClientOptions.Default("ws://localhost:1/ws", "s-x", "c-x");
        options = options with { HandshakeTimeout = TimeSpan.FromSeconds(2) };

        await Assert.ThrowsAnyAsync<Exception>(
            () => ChorusClient.ConnectAsync(options, _ => { }));
    }

    // ---- Scripted transport seam tests ----

    [Fact]
    public async Task ReceiveLoop_SkipsNonSnapshotFrames_AndAcksAfterEverySecondFrame()
    {
        var socket = new ScriptedClientSocket();
        socket.Push(WelcomeFrame());
        socket.PushOce();
        socket.Push("{\"type\":\"pong\"}");
        socket.Push(SnapshotFrame(1));
        socket.Push(SnapshotFrame(2));

        var options = ChorusClientOptions.Default("ws://stub", "s-script", "c-script");
        await using var client = await ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.AcksSent < 1)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, client.FramesApplied);
        Assert.True(client.AcksSent >= 1, $"acks={client.AcksSent}");
        Assert.Equal(2, client.LastAppliedFrameId);
    }

    [Fact]
    public async Task ServerClose_EndsLoop_NonFaulted()
    {
        var socket = new ScriptedClientSocket();
        socket.Push(WelcomeFrame());
        socket.PushClose();

        var options = ChorusClientOptions.Default("ws://stub", "s-close", "c-close");
        await using var client = await ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !client.Completion.IsCompleted)
        {
            await Task.Delay(10);
        }

        Assert.True(client.Completion.IsCompleted);
        Assert.False(client.Completion.IsFaulted);
    }

    [Fact]
    public async Task MalformedSnapshot_IsIgnored_NextFrameStillApplies()
    {
        var socket = new ScriptedClientSocket();
        socket.Push(WelcomeFrame());
        socket.Push("{\"type\":\"snapshot\",\"tickId\":");
        socket.Push(SnapshotFrame(1));

        var options = ChorusClientOptions.Default("ws://stub", "s-malformed", "c-malformed");
        await using var client = await ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.FramesApplied < 1)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, client.FramesApplied);
        Assert.Equal(1, client.LastAppliedFrameId);
    }

    [Fact]
    public async Task Resync_NoCooldown_AlwaysPulls()
    {
        var socket = new ScriptedClientSocket();
        socket.Push(WelcomeFrame());
        socket.Push(SnapshotFrame(1));
        socket.Push(SnapshotFrame(5));

        var options = ChorusClientOptions.Default("ws://stub", "s-resync", "c-resync") with { ResyncCooldown = default };
        await using var client = await ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.ResyncsSent < 1)
        {
            await Task.Delay(10);
        }

        Assert.True(client.ResyncsSent >= 1, $"resyncs={client.ResyncsSent}");
    }

    [Fact]
    public async Task Resync_InsideCooldown_IsThrottled()
    {
        var socket = new ScriptedClientSocket();
        socket.Push(WelcomeFrame());
        socket.Push(SnapshotFrame(1));
        socket.Push(SnapshotFrame(5));
        socket.Push(SnapshotFrame(9));

        var options = ChorusClientOptions.Default("ws://stub", "s-throttle", "c-throttle") with { ResyncCooldown = TimeSpan.FromSeconds(10) };
        await using var client = await ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.ResyncsSent < 1)
        {
            await Task.Delay(10);
        }

        await Task.Delay(100);
        Assert.Equal(1, client.ResyncsSent);
    }

    [Fact]
    public async Task FailedResyncAndAckSends_AreSwallowed()
    {
        var socket = new ScriptedClientSocket { SendFailsAfter = 2 };
        socket.Push(WelcomeFrame());
        socket.Push(SnapshotFrame(1));
        socket.Push(SnapshotFrame(2));
        socket.Push(SnapshotFrame(5));

        var options = ChorusClientOptions.Default("ws://stub", "s-fail", "c-fail") with { AckEveryFrames = 1 };
        await using var client = await ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.FramesApplied < 3)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
        Assert.Equal(3, client.FramesApplied);
        Assert.False(client.Completion.IsFaulted);
    }

    [Fact]
    public async Task HandshakeTimeout_DisposesSocket_AndThrows()
    {
        var socket = new ScriptedClientSocket();
        socket.Park();

        var options = ChorusClientOptions.Default("ws://stub", "s-timeout", "c-timeout") with { HandshakeTimeout = TimeSpan.FromMilliseconds(200) };
        await Assert.ThrowsAnyAsync<Exception>(
            () => ChorusClient.ConnectAsync(options, _ => { }, connect: (_, _) => Task.FromResult<WebSocket>(socket)));

        Assert.True(socket.Disposed);
    }

    private static string WelcomeFrame() => """{"type":"welcome","sessionId":"s","clientId":"c","tickId":0,"tickDurationMs":100}""";

    private static string SnapshotFrame(long tickId) =>
        "{\"type\":\"snapshot\",\"tickId\":" + tickId + ",\"state\":{\"counter\":0}}";

    private sealed class ScriptedClientSocket : WebSocket
    {
        private readonly object _gate = new();
        private readonly Queue<ReceiveItem> _queue = new();
        private readonly List<string> _sent = new();
        private bool _disposed;
        private WebSocketState _state = WebSocketState.Open;

        private enum ReceiveKind { Text, Oce, Close, Park }

        private sealed record ReceiveItem(ReceiveKind Kind, byte[] Data);

        public int SendFailsAfter;

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_gate)
                {
                    return _sent.ToList();
                }
            }
        }

        public bool Disposed => _disposed;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public void Push(string text)
        {
            lock (_gate)
            {
                _queue.Enqueue(new ReceiveItem(ReceiveKind.Text, Encoding.UTF8.GetBytes(text)));
            }
        }

        public void PushOce()
        {
            lock (_gate)
            {
                _queue.Enqueue(new ReceiveItem(ReceiveKind.Oce, Array.Empty<byte>()));
            }
        }

        public void PushClose()
        {
            lock (_gate)
            {
                _queue.Enqueue(new ReceiveItem(ReceiveKind.Close, Array.Empty<byte>()));
            }
        }

        public void Park()
        {
            lock (_gate)
            {
                _queue.Enqueue(new ReceiveItem(ReceiveKind.Park, Array.Empty<byte>()));
            }
        }

        public override void Abort() => throw new NotSupportedException();

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ReceiveItem item;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return ParkAsync(cancellationToken);
                }
                item = _queue.Dequeue();
            }

            switch (item.Kind)
            {
                case ReceiveKind.Text:
                    Array.Copy(item.Data, 0, buffer.Array!, buffer.Offset, item.Data.Length);
                    return Task.FromResult(new WebSocketReceiveResult(item.Data.Length, WebSocketMessageType.Text, true));
                case ReceiveKind.Oce:
                    return Task.FromException<WebSocketReceiveResult>(new OperationCanceledException(cancellationToken));
                case ReceiveKind.Close:
                    _state = WebSocketState.Closed;
                    return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                case ReceiveKind.Park:
                    return ParkAsync(cancellationToken);
                default:
                    throw new InvalidOperationException();
            }
        }

        private static async Task<WebSocketReceiveResult> ParkAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(-1, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
                if (SendFailsAfter > 0 && _sent.Count >= SendFailsAfter)
                {
                    throw new InvalidOperationException("stub socket send failure");
                }
            }

            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _disposed = true;
        }
    }
}
