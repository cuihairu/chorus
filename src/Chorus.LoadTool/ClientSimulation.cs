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
/// exercise the full handshake again.
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
    public int LastLagTicks => Volatile.Read(ref _lastLag);

    private long _snapshots, _acks, _disconnects, _lastSnapshotTick;
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
                var firstAckDelay = _profile switch
                {
                    "fast" => 250,
                    "slow" => 5000,
                    _ => 1000
                };
                var ackTask = Task.Run(() => AckLoopAsync(ws, connectionCts.Token, firstAckDelay), connectionCts.Token);
                var receiveTask = Task.Run(() => ReceiveLoopAsync(ws, connectionCts.Token), connectionCts.Token);

                var aliveFor = _disconnectEverySeconds > 0
                    ? TimeSpan.FromSeconds(Math.Max(1,
                        _disconnectEverySeconds + _random.Next(-Math.Max(1, _disconnectEverySeconds / 4), _disconnectEverySeconds / 4 + 1)))
                    : Timeout.InfiniteTimeSpan;

                await Task.WhenAny(ackTask, receiveTask, Task.Delay(aliveFor, connectionCts.Token)).ConfigureAwait(false);
                connectionCts.Cancel();

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
        var hello = new ClientHello(ProtocolTypes.Hello, _sessionId, _clientId, _token);
        await SendTextAsync(ws, JsonSerializer.SerializeToUtf8Bytes(hello, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task AckLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken, int firstDelayMs)
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
            await SendTextAsync(ws, JsonSerializer.SerializeToUtf8Bytes(ack, ProtocolJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
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

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
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
                Volatile.Write(ref _lastSnapshotTick, ReadSnapshotTick(text));
                Interlocked.Increment(ref _snapshots);
            }
        }
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
}
