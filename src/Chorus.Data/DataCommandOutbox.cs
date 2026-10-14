using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>Result of offering a command to the outbox.</summary>
public enum DataOutboxOutcome
{
    /// <summary>Queued.</summary>
    Accepted = 0,

    /// <summary>
    /// Queue full and the tier forbids dropping. Fail-visible: the producer must
    /// retry or fail the operation - the write is NOT applied and NOT lost
    /// silently (audit §8.3).
    /// </summary>
    RejectedFull = 1,

    /// <summary>
    /// Queue full and the tier is 普通统计: dropped on purpose. The drop is
    /// counted so it stays observable, and the number is rebuildable (audit §8.1).
    /// </summary>
    Dropped = 2,

    /// <summary>Contract violation (missing id/type/room/payload): refused, never queued.</summary>
    Invalid = 3,
}

/// <summary>A queued command plus its enqueue time (audit §8.3「数据处理延迟必须可观测」).</summary>
public sealed record DataCommandEnvelope(DataCommand Command, long EnqueuedAtMs)
{
    /// <summary>Queueing latency so far; the processing side reports it as a metric.</summary>
    public long AgeMs(long nowMs) => nowMs - EnqueuedAtMs;
}

/// <summary>
/// Bounded hand-off queue between the game loop and the Data Worker
/// (audit §8.3「队列容量必须有上限」).
///
/// <list type="bullet">
/// <item>Capacity is a hard limit: admission is counted, so backpressure reaches
/// the producer instead of growing memory without bound.</item>
/// <item>When full, the reliability tier decides (§8.1): 普通统计 is dropped with
/// a counter, every other tier is rejected fail-visible. Nothing is discarded
/// quietly.</item>
/// <item>FIFO per queue: tiers that carry business order (game record, critical
/// economy) rely on it for retry ordering (§8.3).</item>
/// </list>
/// This queue is a hand-off buffer, not durability: for 关键经济 the producer must
/// record the command in a business ledger before treating acceptance as final.
/// </summary>
public sealed class DataCommandOutbox
{
    private readonly ConcurrentQueue<DataCommandEnvelope> _queue = new();
    private readonly int _capacity;
    private int _admitted;
    private long _enqueuedCount;
    private long _rejectedFullCount;
    private long _droppedCount;
    private long _invalidCount;

    public DataCommandOutbox(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>Commands currently queued.</summary>
    public int Count => Volatile.Read(ref _admitted);

    /// <summary>Capacity ceiling.</summary>
    public int Capacity => _capacity;

    public long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);

    public long RejectedFullCount => Interlocked.Read(ref _rejectedFullCount);

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public long InvalidCount => Interlocked.Read(ref _invalidCount);

    /// <summary>
    /// Offers a command to the queue. The outcome is always explicit - callers
    /// must branch on RejectedFull/Invalid rather than assume success.
    /// </summary>
    public DataOutboxOutcome Enqueue(DataCommand? command, long nowMs)
    {
        if (!DataCommands.IsValid(command))
        {
            Interlocked.Increment(ref _invalidCount);
            return DataOutboxOutcome.Invalid;
        }

        // Counted admission: the slot is taken before the command becomes visible,
        // so concurrent producers can never oversell the queue.
        if (Interlocked.Increment(ref _admitted) > _capacity)
        {
            Interlocked.Decrement(ref _admitted);

            if (DataReliabilityPolicy.MayDropOnBackpressure(command!.Level))
            {
                Interlocked.Increment(ref _droppedCount);
                return DataOutboxOutcome.Dropped;
            }

            Interlocked.Increment(ref _rejectedFullCount);
            return DataOutboxOutcome.RejectedFull;
        }

        _queue.Enqueue(new DataCommandEnvelope(command!, nowMs));
        Interlocked.Increment(ref _enqueuedCount);
        return DataOutboxOutcome.Accepted;
    }

    /// <summary>Dequeues the oldest command (FIFO).</summary>
    public bool TryDequeue(out DataCommandEnvelope? envelope)
    {
        if (_queue.TryDequeue(out var found) && found is not null)
        {
            Interlocked.Decrement(ref _admitted);
            envelope = found;
            return true;
        }

        envelope = null;
        return false;
    }
}
