namespace Chorus.StreamerGateway;

/// <summary>
/// The per-connection downlink queue. Snapshots re-baseline: a newer snapshot replaces an
/// unsent older one (counted) and supersedes every queued delta — the client would discard
/// them anyway against the new state version. Deltas are chained (each carries the base
/// version it applies to) so they are never dropped from the middle: once the ordered queue
/// is full, later deltas are refused wholesale and the producer is told to re-baseline with a
/// snapshot instead. Sticky entries — a round result is the receipt the client must see — are
/// never dropped. A single pump task drains everything in order, so the socket sees one
/// outstanding send at a time.
/// </summary>
public sealed class EdgeOutbox : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(string Json, bool Sticky)> _ordered;
    private readonly int _capacity;
    private readonly SemaphoreSlim _signal = new(0);
    private string? _pendingSnapshot;
    private bool _needsSnapshot;
    private long _supersededSnapshots;
    private long _supersededDeltas;
    private long _refusedDeltas;

    public EdgeOutbox(int capacity = 256)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ordered = new Queue<(string, bool)>(capacity);
    }

    /// <summary>True when deltas were refused or superseded and only a fresh snapshot can put
    /// the client back on the chain — the fan-out side polls this to re-baseline.</summary>
    public bool NeedsSnapshot { get { lock (_gate) return _needsSnapshot; } }

    public long SupersededSnapshots { get { lock (_gate) return _supersededSnapshots; } }
    public long SupersededDeltas { get { lock (_gate) return _supersededDeltas; } }
    public long RefusedDeltas { get { lock (_gate) return _refusedDeltas; } }

    public void EnqueueSnapshot(string json)
    {
        lock (_gate)
        {
            if (_pendingSnapshot is not null) _supersededSnapshots++;
            _pendingSnapshot = json;
            // The snapshot re-baselines the client: every chained delta ahead of it is void,
            // and an outstanding re-baseline request is answered.
            while (_ordered.Count > 0 && !_ordered.Peek().Sticky)
            {
                _ordered.Dequeue();
                _supersededDeltas++;
            }
            _needsSnapshot = false;
        }
        _signal.Release();
    }

    /// <summary>Queues a chained message. Returns false when the ordered queue is full: the
    /// message was refused, the producer must re-baseline with a snapshot.</summary>
    public bool TryEnqueueOrdered(string json, bool sticky = false)
    {
        lock (_gate)
        {
            if (!sticky && _ordered.Count >= _capacity)
            {
                _refusedDeltas++;
                _needsSnapshot = true;
                return false;
            }
            _ordered.Enqueue((json, sticky));
        }
        _signal.Release();
        return true;
    }

    /// <summary>Drains the outbox in order — snapshot slot first, then the ordered queue —
    /// sending one frame at a time through the single writer.</summary>
    public async Task RunAsync(Func<string, CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken);
            string? next;
            lock (_gate)
            {
                if (_pendingSnapshot is not null)
                {
                    next = _pendingSnapshot;
                    _pendingSnapshot = null;
                }
                else if (_ordered.Count > 0) next = _ordered.Dequeue().Json;
                else next = null;
            }
            if (next is not null) await send(next, cancellationToken);
        }
    }

    public void Dispose() => _signal.Dispose();
}
