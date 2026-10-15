using System.Net.WebSockets;
using System.Threading.Channels;
using Chorus.Gateway.Core;
using Chorus.Protocol.Messages;

namespace Chorus.Gateway;

/// <summary>
/// Review stage-2 (去阻塞发送): snapshots enqueue into a latest-wins slot and
/// a per-connection pump performs the actual WS send, so one slow streamer
/// client can no longer stall the IPC dispatch loop (and with it every other
/// session). While the pump is stuck on snapshot N, newer snapshots replace
/// the queued one.
/// </summary>
public sealed class GatewayClientConnection : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<ServerSnapshot> _outbound = Channel.CreateBounded<ServerSnapshot>(
        new BoundedChannelOptions(capacity: 1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly CancellationTokenSource _cts = new();
    private int _pumpStarted;
    private Task? _pump;
    private int _disposed;
    private long _dropped;

    public GatewayClientConnection(string sessionId, string clientId, WebSocket ws)
    {
        SessionId = sessionId;
        ClientId = clientId;
        _ws = ws;
    }

    public string SessionId { get; }
    public string ClientId { get; }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    /// <summary>Snapshots replaced before the transport picked them up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public ValueTask SendSnapshotAsync(ServerSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_outbound.Writer.TryWrite(snapshot))
        {
            Interlocked.Increment(ref _dropped);
        }

        StartPump();
        return ValueTask.CompletedTask;
    }

    private void StartPump()
    {
        if (Interlocked.CompareExchange(ref _pumpStarted, 1, 0) == 0)
        {
            _pump = PumpAsync();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var snapshot in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!IsOpen)
                {
                    return;
                }

                await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await WebSocketHelpers.SendJsonAsync(_ws, snapshot, _cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Closed/aborted transport: remaining snapshots collapse into the
            // slot; memory stays bounded.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outbound.Writer.TryComplete();
        _cts.Cancel();
        if (_pump != null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _cts.Dispose();
        _sendLock.Dispose();
    }
}
