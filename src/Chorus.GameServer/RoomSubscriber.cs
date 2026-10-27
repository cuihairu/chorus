using Chorus.Redesign.Rpc;

namespace Chorus.GameServer;

/// <summary>
/// One subscriber's bounded view of a running room (architecture 8.3). The actor offers
/// committed messages without ever waiting on this queue — the room keeps ticking no matter
/// how slow the reader is. An overflowing queue drops its backlog; the actor notices and
/// re-baselines that subscriber with a fresh snapshot of the committed head instead of a
/// discontinuous delta. The frozen round result is never dropped: a terminal message
/// clears the backlog and lands.
/// </summary>
internal sealed class RoomSubscriber : IDisposable
{
    private const int Capacity = 64;
    private readonly object _gate = new();
    private readonly Queue<SyncMessage> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;

    /// <summary>Set when the queue overflowed; the actor's next publish re-baselines here.</summary>
    internal bool NeedsSnapshot { get; set; }

    internal void Offer(SyncMessage message)
    {
        lock (_gate)
        {
            if (_pending.Count >= Capacity)
            {
                _pending.Clear();
                NeedsSnapshot = true;
                if (message.KindCase is SyncMessage.KindOneofCase.RoundResult
                    or SyncMessage.KindOneofCase.ResyncRequired)
                    _pending.Enqueue(message); // terminal messages are never dropped
                else
                    return; // this delta was discontinuous the moment its predecessors dropped
            }
            else
            {
                _pending.Enqueue(message);
            }
        }
        _signal.Release();
    }

    /// <summary>Waits for the next committed message; null once the room finished and drained.</summary>
    internal async Task<SyncMessage?> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken);
            lock (_gate)
            {
                if (_pending.Count > 0) return _pending.Dequeue();
                if (_completed) return null;
            }
        }
    }

    /// <summary>Ends the stream after every queued message drained.</summary>
    internal void Complete()
    {
        lock (_gate) _completed = true;
        _signal.Release();
    }

    public void Dispose() => _signal.Dispose();
}
