namespace Chorus.GameServer;

/// <summary>
/// The recovery concurrency budget (FIX-PERF-08): a host runs at most a set number of
/// recoveries' heavy work — the plan load and frame replay that dominate a mass takeover's
/// memory — at once, while the rest wait in a bounded queue. Past the queue a recovery is
/// refused explicitly: the caller sees <see cref="RecoveryQueueFullException"/> (mapped to
/// ResourceExhausted), the room stays Recovering for the next sweep, and the host is never
/// left to OOM by an unbounded burst. An invalid bound fails closed — a mistyped value is a
/// configuration error, not a silently unlimited host.
/// <para>The gate bounds concurrent recovery <em>work</em>, not the room count: the active-room
/// admission budget (FIX-PERF-07) still governs how many rooms the host runs, and a refused
/// recovery reserves no room slot at all.</para>
/// </summary>
internal sealed class RecoveryConcurrencyGate
{
    private readonly SemaphoreSlim _slots;
    private readonly int _maxQueued;

    /// <summary>Recoveries whose heavy work may run at the same instant.</summary>
    public int MaxConcurrent { get; }

    /// <summary>Recoveries allowed to wait for a slot before a new one is refused.</summary>
    public int MaxQueued => _maxQueued;

    private int _waiting;

    public RecoveryConcurrencyGate(int maxConcurrent, int maxQueued)
    {
        if (maxConcurrent <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent),
                "The recovery concurrency must be a positive integer.");
        if (maxQueued < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueued),
                "The recovery queue bound must be a non-negative integer.");
        MaxConcurrent = maxConcurrent;
        _maxQueued = maxQueued;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>
    /// Acquires a slot for one recovery's heavy work. A free slot is taken without queueing;
    /// otherwise the caller joins the bounded queue and a waiter past the bound is refused
    /// with <see cref="RecoveryQueueFullException"/> — never silently accepted.
    /// </summary>
    public async ValueTask<RecoveryLease> EnterAsync(CancellationToken cancellationToken)
    {
        // Fast path: a free slot takes it without queueing.
        if (_slots.Wait(0)) return new RecoveryLease(_slots);
        // No slot: join the bounded queue — a waiter past the bound is refused explicitly.
        var queued = Interlocked.Increment(ref _waiting);
        if (queued > _maxQueued)
        {
            Interlocked.Decrement(ref _waiting);
            throw new RecoveryQueueFullException(
                $"The recovery queue is full ({_maxQueued} waiting); this recovery is refused until a slot frees.");
        }
        try
        {
            await _slots.WaitAsync(cancellationToken);
            return new RecoveryLease(_slots);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>A granted slot; disposing it returns the slot to the pool.</summary>
    public readonly struct RecoveryLease(SemaphoreSlim slots) : IDisposable
    {
        private readonly SemaphoreSlim _slots = slots;
        public void Dispose() => _slots.Release();
    }
}

/// <summary>Refused because the host's bounded recovery queue was already full (FIX-PERF-08).</summary>
public sealed class RecoveryQueueFullException(string message) : Exception(message);
