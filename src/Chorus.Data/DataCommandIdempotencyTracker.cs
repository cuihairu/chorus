using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>
/// Idempotency ledger for data commands (audit §8.3「数据命令必须有幂等语义」).
///
/// The protocol mirrors <c>EventDeduplicator</c>'s reserve/commit/rollback
/// discipline - check-then-set is what lets a redelivered command double-apply:
/// <list type="bullet">
/// <item><see cref="TryBegin"/> claims the CommandId atomically (TryAdd + token);
/// under redelivery exactly one caller wins and duplicates are suppressed.</item>
/// <item><see cref="Commit"/> upgrades the claim after the write succeeded, so
/// later redeliveries stay suppressed.</item>
/// <item><see cref="Rollback"/> releases the claim when the write FAILED, so a
/// retry is processed as fresh work - a failure is never misremembered as
/// "already applied" (which would silently lose the record).</item>
/// </list>
/// Entries expire after the TTL (bounded suppression window), swept amortized on
/// begin/commit without a background thread. A capacity safety valve evicts the
/// soonest-to-expire entry when the table stays full after sweeping: bounded
/// memory wins over one command's remaining suppression window.
/// </summary>
public sealed class DataCommandIdempotencyTracker
{
    public enum EntryState
    {
        /// <summary>Claimed and being applied; not yet a durable "applied" mark.</summary>
        Reserved = 0,

        /// <summary>Write succeeded; the CommandId is remembered until the TTL expires.</summary>
        Committed = 1,
    }

    /// <summary>Claim handle; a failed <see cref="TryBegin"/> yields the default (invalid) lease.</summary>
    public readonly record struct Lease(string CommandId, long Token)
    {
        /// <summary>False for the default lease, i.e. no claim was taken.</summary>
        public bool Valid => !string.IsNullOrEmpty(CommandId);
    }

    private sealed record Entry(EntryState State, long Token, long ExpiresAtMs);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly int _sweepIntervalMs;
    private long _token;
    private long _lastSweepMs;

    public DataCommandIdempotencyTracker(
        TimeSpan ttl,
        int capacity = 200_000,
        int sweepIntervalMs = 30_000)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ttl = ttl;
        _capacity = capacity;
        _sweepIntervalMs = sweepIntervalMs;
    }

    /// <summary>Number of tracked commands (claimed or committed).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Claims the command for exclusive processing. False means "do not apply":
    /// either the CommandId is blank (unverifiable, no idempotency possible) or
    /// another caller already holds/committed the claim.
    /// </summary>
    public bool TryBegin(DataCommand command, long nowMs, out Lease lease)
    {
        lease = default;
        if (string.IsNullOrWhiteSpace(command?.CommandId))
        {
            // No key means no idempotency: refuse rather than apply untracked.
            return false;
        }

        MaybeSweep(nowMs);

        var key = command.CommandId;
        var token = Interlocked.Increment(ref _token);
        var fresh = new Entry(EntryState.Reserved, token, nowMs + (long)_ttl.TotalMilliseconds);

        if (_entries.TryAdd(key, fresh))
        {
            lease = new Lease(key, token);
            return true;
        }

        // Key exists: take it over only if the standing claim already expired,
        // replaced atomically so a concurrent begin still sees exactly one winner.
        if (_entries.TryGetValue(key, out var existing) && existing.ExpiresAtMs <= nowMs
            && _entries.TryUpdate(key, fresh, existing))
        {
            lease = new Lease(key, token);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Write succeeded: keep the CommandId suppressed. The suppression window
    /// keeps the expiry taken at <see cref="TryBegin"/> - the claim age is what
    /// bounds a very slow run, and no wall clock is consulted here, so the
    /// ledger stays deterministic under replay.
    /// </summary>
    public void Commit(Lease lease)
    {
        if (!lease.Valid)
        {
            return;
        }

        if (_entries.TryGetValue(lease.CommandId, out var current) && current.Token == lease.Token)
        {
            var committed = new Entry(EntryState.Committed, lease.Token, current.ExpiresAtMs);
            _entries.TryUpdate(lease.CommandId, committed, current);
        }
    }

    /// <summary>
    /// Write failed: release the claim so the producer's retry is processed as
    /// fresh work instead of being mistaken for a duplicate.
    /// </summary>
    public void Rollback(Lease lease)
    {
        if (!lease.Valid)
        {
            return;
        }

        if (_entries.TryGetValue(lease.CommandId, out var current)
            && current.Token == lease.Token
            && current.State == EntryState.Reserved)
        {
            ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(lease.CommandId, current));
        }
    }

    /// <summary>True when the CommandId is present in the table (claimed or committed).</summary>
    public bool IsClaimed(string commandId) =>
        !string.IsNullOrWhiteSpace(commandId) && _entries.ContainsKey(commandId);

    /// <summary>True when the CommandId is present and not yet expired at <paramref name="nowMs"/>.</summary>
    public bool IsClaimedAt(string commandId, long nowMs) =>
        _entries.TryGetValue(commandId ?? string.Empty, out var entry) && entry.ExpiresAtMs > nowMs;

    private void MaybeSweep(long nowMs)
    {
        var last = Interlocked.Read(ref _lastSweepMs);
        if (nowMs - last < _sweepIntervalMs && _entries.Count < _capacity)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepMs, nowMs, last) != last && _entries.Count < _capacity)
        {
            return;
        }

        // One linear pass: drop expired entries and remember the soonest-to-expire
        // survivor in case the capacity valve needs a victim.
        string? soonestKey = null;
        var soonestExpiry = long.MaxValue;
        Entry? soonestEntry = null;
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAtMs <= nowMs)
            {
                ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(kv);
            }
            else if (kv.Value.ExpiresAtMs < soonestExpiry)
            {
                soonestExpiry = kv.Value.ExpiresAtMs;
                soonestKey = kv.Key;
                soonestEntry = kv.Value;
            }
        }

        // Capacity valve: still full after sweeping -> evict the claim whose
        // suppression window ends soonest; bounded memory wins over one command.
        if (_entries.Count >= _capacity && soonestKey != null && soonestEntry != null)
        {
            ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(soonestKey, soonestEntry));
        }
    }
}
