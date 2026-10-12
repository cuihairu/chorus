using System.Threading.Channels;

namespace Chorus.Engine.Session;

/// <summary>
/// Review stage-2 (权威 Tick 去阻塞发送): wraps a real client with a
/// latest-wins outbound slot so a slow consumer can never block the tick
/// loop. The capacity-1 channel uses DropOldest: while the pump is stuck
/// sending snapshot N to a lagging transport, newer snapshots replace the
/// queued one. The client therefore always catches up to the newest state on
/// the next successful send (plus a forced resync if it acks too late).
/// </summary>
public sealed class OutboundClientQueue : IAsyncDisposable
{
    private readonly ISessionClient _inner;
    private readonly Channel<SessionSnapshot> _slot = Channel.CreateBounded<SessionSnapshot>(
        new BoundedChannelOptions(capacity: 1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _dropped;
    private long _sent;

    public OutboundClientQueue(ISessionClient inner)
    {
        _inner = inner;
        ClientId = inner.ClientId;
        _pump = PumpAsync();
    }

    public string ClientId { get; }

    /// <summary>Snapshots replaced before the transport picked them up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Enqueue-only: completes synchronously, never awaits the transport.</summary>
    public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_slot.Writer.TryWrite(snapshot))
        {
            Interlocked.Increment(ref _dropped);
        }

        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var snapshot in _slot.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _inner.SendSnapshotAsync(snapshot, _cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A broken transport must not take the pump (or the session) down;
            // further writes collapse into the slot, memory stays bounded.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _slot.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
