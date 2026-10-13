using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.LoadTool;

/// <summary>
/// One simulated streamer client over the real WS surface: hello token auth,
/// welcome handshake, snapshot reception, ACK sending at a per-client cadence
/// (fast / normal / slow+lag), and random disconnect+reconnect cycles.
/// A fresh <see cref="ClientWebSocket"/> is created per cycle so reconnects
/// exercise the full handshake again. Snapshots are played back through a
/// <see cref="FramePlayer"/> (frame-sync P1): stale frames drop, and a gap
/// triggers one <c>resync</c> pull (frame-sync P2).
/// </summary>
public sealed class ClientSimulation
{
    private readonly string _baseWsUrl;
    private readonly string _sessionId;
    private readonly string _clientId;
    private readonly string? _token;
    private readonly string _profile;
    private readonly Random _random;
    private readonly int _disconnectEverySeconds;
    private readonly int _reconnectBackoffMs;

    public long SnapshotsReceived => Interlocked.Read(ref _snapshots);
    public long AcksSent => Interlocked.Read(ref _acks);
    public long Disconnects => Interlocked.Read(ref _disconnects);
    public long ResyncsSent => Interlocked.Read(ref _resyncs);
    public long LastAppliedFrameId => _player.LastAppliedFrameId;
    public int LastLagTicks => Volatile.Read(ref _lastLag);

    private readonly FramePlayer _player = new();

    private long _snapshots, _acks, _disconnects, _resyncs, _lastSnapshotTick;
    private int _lastLag;

    public ClientSimulation(string baseWsUrl, string sessionId, string clientId, string? token, string profile,
        int disconnectEverySeconds, int reconnectBackoffMs, int seed)
    {
        _baseWsUrl = baseWsUrl;
        _sessionId = sessionId;
        _clientId = clientId;
        _token = token;
        _profile = profile;
        _disconnectEverySeconds = disconnectEverySeconds;
        _reconnectBackoffMs = reconnectBackoffMs;
        _random = new Random(seed);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(_baseWsUrl), cancellationToken).ConfigureAwait(false);
                await SendHelloAsync(ws, cancellationToken).ConfigureAwait(false);

                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // ClientWebSocket forbids overlapping sends: hello, periodic
                // acks and gap-fill resyncs all serialize on one gate per
                // connection cycle (repo idiom: join both loops before the
                // gate goes away).
                using var sendGate = new SemaphoreSlim(1, 1);
                var firstAckDelay = _profile switch
                {
                    "fast" => 250,
                    "slow" => 5000,
                    _ => 1000
                };
                var ackTask = Task.Run(() => AckLoopAsync(ws, sendGate, connectionCts.Token, firstAckDelay), connectionCts.Token);
                var receiveTask = Task.Run(() => ReceiveLoopAsync(ws, sendGate, connectionCts.Token), connectionCts.Token);

                var aliveFor = _disconnectEverySeconds > 0
                    ? TimeSpan.FromSeconds(Math.Max(1,
                        _disconnectEverySeconds + _random.Next(-Math.Max(1, _disconnectEverySeconds / 4), _disconnectEverySeconds / 4 + 1)))
                    : Timeout.InfiniteTimeSpan;

                await Task.WhenAny(ackTask, receiveTask, Task.Delay(aliveFor, connectionCts.Token)).ConfigureAwait(false);
                connectionCts.Cancel();

                try
                {
                    await Task.WhenAll(ackTask, receiveTask).ConfigureAwait(false);
                }
                catch
                {
                    // Cancelled or mid-run drop: the cycle is over either way.
                }

                Interlocked.Increment(ref _disconnects);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Handshake failure or mid-run drop: back off and reconnect.
            }
            finally
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "load done", CancellationToken.None); } catch { }
                }
                ws.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(_reconnectBackoffMs + _random.Next(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendHelloAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var hello = new ClientHello(
            ProtocolTypes.Hello, _sessionId, _clientId, _token,
            ProtocolVersion: Chorus.Protocol.ProtocolVersionPolicy.Current);
        await SendTextAsync(ws, JsonSerializer.SerializeToUtf8Bytes(hello, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task AckLoopAsync(ClientWebSocket ws, SemaphoreSlim sendGate, CancellationToken cancellationToken, int firstDelayMs)
    {
        await Task.Delay(firstDelayMs + _random.Next(250), cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            // fast ≈ realtime, normal ≈ a few ticks behind, slow ≈ a dozen behind.
            var lag = _profile switch
            {
                "fast" => _random.Next(0, 2),
                "slow" => _random.Next(2, 14),
                _ => _random.Next(0, 4)
            };

            var tick = Math.Max(0, Volatile.Read(ref _lastSnapshotTick) - lag);
            Volatile.Write(ref _lastLag, lag);

            var ack = new ClientAck(ProtocolTypes.Ack, _sessionId, _clientId, tick,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await SendTextAsyncLocked(ws, sendGate, JsonSerializer.SerializeToUtf8Bytes(ack, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _acks);

            var period = _profile switch
            {
                "fast" => 250,
                "slow" => 5000,
                _ => 1000
            };
            await Task.Delay(period + _random.Next(period / 2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (text.Contains("\"type\":\"" + ProtocolTypes.Snapshot + "\"", StringComparison.Ordinal))
            {
                var frameId = ReadSnapshotTick(text);
                var decision = _player.Accept(frameId);
                if (decision.Apply)
                {
                    Volatile.Write(ref _lastSnapshotTick, frameId);
                    Interlocked.Increment(ref _snapshots);
                }

                // Frame-sync P2 gap-fill: a jump over more than one frame means
                // the missing frames must be pulled; one resync per gap.
                if (decision.GapFrom >= 0)
                {
                    await SendResyncAsync(ws, sendGate, decision.GapFrom, cts.Token).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task SendResyncAsync(ClientWebSocket ws, SemaphoreSlim sendGate, long fromFrameId, CancellationToken cancellationToken)
    {
        var resync = new ClientResync(ProtocolTypes.Resync, _sessionId, _clientId, fromFrameId);
        await SendTextAsyncLocked(ws, sendGate, JsonSerializer.SerializeToUtf8Bytes(resync, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _resyncs);
    }

    private static long ReadSnapshotTick(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("tickId", out var tick) && tick.TryGetInt64(out var value) ? value : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static async Task SendTextAsync(ClientWebSocket ws, byte[] payload, CancellationToken cancellationToken)
    {
        await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send serialized on the per-connection gate so the ack loop and the
    /// resync path can never interleave frames on the socket.
    /// </summary>
    private static async Task SendTextAsyncLocked(ClientWebSocket ws, SemaphoreSlim sendGate, byte[] payload, CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendTextAsync(ws, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }
}
