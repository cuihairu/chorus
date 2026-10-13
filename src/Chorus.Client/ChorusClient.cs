using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.Client;

/// <summary>
/// Options for <see cref="ChorusClient.ConnectAsync"/>.
/// </summary>
public sealed record ChorusClientOptions(
    string WsUrl,
    string SessionId,
    string ClientId,
    string? Token = null,
    /// <summary>Ack every Nth applied frame; the wire caps acks per connection
    /// (Faces:StreamerWs:AckPerMinute, default 600/min), so "1" can hit the cap
    /// at fast tick rates.</summary>
    int AckEveryFrames = 2,
    /// <summary>Minimum interval between resync pulls; the wire rate-limits them
    /// (Faces:StreamerWs:ResyncPerMinute, default 30/min) and the engine cools
    /// per client (Session:ResyncRequestCooldownMs, default 1000ms).</summary>
    TimeSpan ResyncCooldown = default,
    /// <summary>Budget for the hello/welcome handshake.</summary>
    TimeSpan HandshakeTimeout = default
)
{
    public static ChorusClientOptions Default(string wsUrl, string sessionId, string clientId, string? token = null)
        => new(wsUrl, sessionId, clientId, token)
        {
            ResyncCooldown = TimeSpan.FromSeconds(2),
            HandshakeTimeout = TimeSpan.FromSeconds(10),
        };
}

/// <summary>
/// Minimal client SDK entry (frame-sync P3 acceptance): one connect call, one
/// render callback. The client sends hello, adopts the welcome's authoritative
/// timing, renders every applied frame through the callback in frame order
/// (frame number = tickId, replayed/stale frames dropped idempotently), acks
/// after applying, and pulls a gap-fill window with resync when frames were
/// lost. Callbacks run on the receive loop - do not block them.
/// </summary>
public sealed class ChorusClient : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly ChorusClientOptions _options;
    private readonly Action<JsonElement> _onState;
    private readonly CancellationTokenSource _loopCts;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly FramePlayer _player = new();
    private readonly Task _loop;

    private long _framesApplied, _acksSent, _resyncsSent, _appliedSinceAck;
    private long _lastResyncMs = -1;

    private ChorusClient(WebSocket ws, ChorusClientOptions options, Action<JsonElement> onState, CancellationTokenSource loopCts)
    {
        _ws = ws;
        _options = options;
        _onState = onState;
        _loopCts = loopCts;
        _loop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
        Completion = _loop;
    }

    /// <summary>Connects, performs the hello/welcome handshake and starts the
    /// receive loop. Throws when the connection or handshake fails.
    /// <paramref name="connect"/> is a transport seam for tests (default: a
    /// real <see cref="ClientWebSocket"/>).</summary>
    public static Task<ChorusClient> ConnectAsync(
        ChorusClientOptions options,
        Action<JsonElement> onState,
        CancellationToken cancellationToken = default)
        => ConnectAsync(options, onState, connect: null, cancellationToken);

    public static async Task<ChorusClient> ConnectAsync(
        ChorusClientOptions options,
        Action<JsonElement> onState,
        Func<Uri, CancellationToken, Task<WebSocket>>? connect,
        CancellationToken cancellationToken = default)
    {
        connect ??= DefaultConnectAsync;
        var ws = await connect(new Uri(options.WsUrl), cancellationToken).ConfigureAwait(false);
        try
        {

            var hello = new ClientHello(
                ProtocolTypes.Hello, options.SessionId, options.ClientId, options.Token,
                ProtocolVersion: ProtocolVersionPolicy.Current);
            await SendTextAsync(ws, JsonSerializer.SerializeToUtf8Bytes(hello, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.HandshakeTimeout > TimeSpan.Zero)
            {
                handshakeCts.CancelAfter(options.HandshakeTimeout);
            }

            await ReadWelcomeAsync(ws, handshakeCts.Token).ConfigureAwait(false);

            var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return new ChorusClient(ws, options, onState, loopCts);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>Completes when the connection closes (server close, transport
    /// error or <see cref="DisposeAsync"/>). Faults on unexpected loop errors.</summary>
    public Task Completion { get; }

    /// <summary>Last frame id handed to the renderer (-1 before the first frame).</summary>
    public long LastAppliedFrameId => _player.LastAppliedFrameId;

    public long FramesApplied => Interlocked.Read(ref _framesApplied);
    public long AcksSent => Interlocked.Read(ref _acksSent);
    public long ResyncsSent => Interlocked.Read(ref _resyncsSent);

    private static async Task<WebSocket> DefaultConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static async Task ReadWelcomeAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var text = await ReceiveTextAsync(ws, buffer, cancellationToken).ConfigureAwait(false);
            if (text.Contains("\"type\":\"" + ProtocolTypes.Welcome + "\"", StringComparison.Ordinal))
            {
                return;
            }

            // A hot session's snapshots can outrun the welcome (documented WS
            // face ordering); they are older frames - skip them.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        while (_ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string text;
            try
            {
                text = await ReceiveTextAsync(_ws, buffer, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue; // idle read timeout - keep waiting for frames
            }
            catch (WebSocketException)
            {
                return; // transport closed
            }

            if (!text.Contains("\"type\":\"" + ProtocolTypes.Snapshot + "\"", StringComparison.Ordinal))
            {
                continue; // pong or future server message
            }

            HandleSnapshot(text, cancellationToken);
        }
    }

    private void HandleSnapshot(string text, CancellationToken cancellationToken)
    {
        long frameId;
        bool forced;
        JsonElement state;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            frameId = root.GetProperty("tickId").GetInt64();
            forced = root.TryGetProperty("forced", out var f) && f.ValueKind == JsonValueKind.True;
            // Clone: the caller owns the document and disposes it here.
            state = root.GetProperty("state").Clone();
        }
        catch (JsonException)
        {
            return; // malformed frame - the next snapshot realigns
        }

        var decision = _player.Accept(frameId);
        if (!decision.Apply)
        {
            return; // stale or replayed frame: idempotent drop
        }

        Interlocked.Increment(ref _framesApplied);
        _onState(state);

        // A forced snapshot IS the alignment result - never chase it with a pull.
        if (decision.GapFrom >= 0 && !forced && ShouldSendResync())
        {
            _ = SendResyncAsync(decision.GapFrom, cancellationToken);
        }

        var ackNow = forced
            || _options.AckEveryFrames <= 1
            || Interlocked.Increment(ref _appliedSinceAck) >= _options.AckEveryFrames;
        if (ackNow)
        {
            Interlocked.Exchange(ref _appliedSinceAck, 0);
            _ = SendAckAsync(frameId, cancellationToken);
        }
    }

    private bool ShouldSendResync()
    {
        var cooldownMs = (long)_options.ResyncCooldown.TotalMilliseconds;
        if (cooldownMs <= 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = Interlocked.Read(ref _lastResyncMs);
        if (last >= 0 && now - last < cooldownMs)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastResyncMs, now, last) == last;
    }

    private async Task SendResyncAsync(long fromFrameId, CancellationToken cancellationToken)
    {
        try
        {
            var resync = new ClientResync(ProtocolTypes.Resync, _options.SessionId, _options.ClientId, fromFrameId);
            await SendTextLockedAsync(JsonSerializer.SerializeToUtf8Bytes(resync, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _resyncsSent);
        }
        catch
        {
            // Best effort: a failed pull is retried on the next detected gap.
        }
    }

    private async Task SendAckAsync(long frameId, CancellationToken cancellationToken)
    {
        try
        {
            var ack = new ClientAck(ProtocolTypes.Ack, _options.SessionId, _options.ClientId, frameId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await SendTextLockedAsync(JsonSerializer.SerializeToUtf8Bytes(ack, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _acksSent);
        }
        catch
        {
            // A lost ack only makes the server see more lag - the next one catches up.
        }
    }

    private async Task SendTextLockedAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendTextAsync(_ws, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task SendTextAsync(WebSocket ws, byte[] payload, CancellationToken cancellationToken)
    {
        await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, byte[] buffer, CancellationToken cancellationToken)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new WebSocketException("connection closed by server");
        }

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
        }

        _sendGate.Dispose();
        _loopCts.Dispose();
        _ws.Dispose();
    }
}
