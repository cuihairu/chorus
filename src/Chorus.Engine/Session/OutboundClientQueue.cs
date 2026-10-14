using System.Threading.Channels;

namespace Chorus.Engine.Session;

/// <summary>
/// Review stage-2 (权威 Tick 去阻塞发送): wraps a real client with a
/// latest-wins outbound slot so a slow consumer can never block the tick
/// loop. While the pump is stuck sending snapshot N to a lagging transport,
/// newer snapshots replace the queued one (audit B3: every replacement is
/// counted on <see cref="Dropped"/>). The client therefore always catches up
/// to the newest state on the next successful send (plus a forced resync if
/// it acks too late).
///
/// Frame-sync P2 adds a direct-send path for gap-fill replay: replayed
/// frames must all arrive in order, so they bypass the latest-wins slot and
/// go straight to the transport, serialized with the pump by a semaphore.
/// </summary>
public sealed class OutboundClientQueue : IAsyncDisposable
{
    private readonly ISessionClient _inner;
    private readonly Channel<WireSnapshot> _slot;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _dropped;
    private long _sent;
    private long _replayed;

    public OutboundClientQueue(ISessionClient inner)
    {
        _inner = inner;
        ClientId = inner.ClientId;

        // FullMode.Wait (not DropOldest) keeps TryWrite honest: false means
        // the slot is occupied, so the eviction in SendSnapshotAsync is an
        // explicit, countable act. DropOldest admits the new item and
        // silently discards the queued one - identical delivery, but no hook
        // to count it (BoundedChannelOptions has no public dropped-callback),
        // which is how the Dropped counter ended up as dead code.
        _slot = Channel.CreateBounded<WireSnapshot>(new BoundedChannelOptions(capacity: 1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _pump = PumpAsync();
    }

    public string ClientId { get; }

    /// <summary>Snapshots replaced before the transport picked them up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Frames delivered through the direct replay path (frame-sync P2).</summary>
    public long Replayed => Interlocked.Read(ref _replayed);

    /// <summary>Enqueue-only: completes synchronously, never awaits the transport.</summary>
    public ValueTask SendSnapshotAsync(WireSnapshot wire, CancellationToken cancellationToken)
    {
        if (!_slot.Writer.TryWrite(wire))
        {
            // Slot occupied - latest-wins: evict the stale queued snapshot
            // ourselves (it was about to be replaced anyway) and count that
            // eviction. If the pump drained the slot in between, the read
            // misses and no drop happened. If the retry write loses the freed
            // slot to another writer, OUR snapshot is the dropped one.
            if (_slot.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _dropped);
            }

            if (!_slot.Writer.TryWrite(wire))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Direct ordered delivery for gap-fill replay (frame-sync P2): bypasses
    /// the latest-wins slot so every replayed frame reaches the client in
    /// order. Serialized with the pump per frame, and whole bursts are
    /// serialized against each other by <see cref="_replayGate"/> so two
    /// overlapping resync answers cannot interleave frames on the transport.
    /// Transport errors are swallowed - a broken client must not take the
    /// session down; the client's next resync re-pulls the window.
    /// </summary>
    public async ValueTask SendReplayAsync(IReadOnlyList<WireSnapshot> frames, CancellationToken cancellationToken)
    {
        if (frames.Count == 0)
        {
            return;
        }

        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var wire in frames)
            {
                await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _inner.SendSnapshotAsync(wire, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _replayed);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Broken transport: stop the replay burst, the client resyncs.
                    return;
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled between frames (or waiting for the send gate): the
            // caller discarded this task, so nothing may escape.
        }
        catch (Exception)
        {
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var wire in _slot.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _sendGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await _inner.SendSnapshotAsync(wire, _cts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _sent);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                    // A broken transport must not take the pump (or the session) down;
                    // further writes collapse into the slot, memory stays bounded.
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
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

        _sendGate.Dispose();
        _replayGate.Dispose();
        _cts.Dispose();
    }
}
