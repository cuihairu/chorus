using System.Collections.Concurrent;

namespace Chorus.Adapters.Platforms;

public enum DedupEntryState
{
    /// <summary>Atomically claimed; the event is being published. Not yet a durable "seen" mark.</summary>
    Reserved,

    /// <summary>Publish succeeded; the event id is remembered until the TTL expires.</summary>
    Delivered,
}

/// <summary>
/// Exactly-once marker for ingress events (review P0-7: 去重原子化).
///
/// The old implementation did a check-then-set <c>TryMark</c> on IMemoryCache:
/// two concurrent requests with the same event id could both pass. This class
/// replaces it with an atomic reserve/commit/rollback protocol:
/// <list type="bullet">
/// <item><see cref="TryReserve"/> claims the id atomically (ConcurrentDictionary
/// TryAdd + token); exactly one concurrent caller wins.</item>
/// <item><see cref="Commit"/> upgrades the claim to Delivered after the IPC
/// publish succeeded - duplicates stay suppressed.</item>
/// <item><see cref="Rollback"/> releases the claim when publishing FAILED, so a
/// delivery failure is never misremembered as "already seen" (四态投递中的
/// Failed 必须可重试，而不是被判为重复).</item>
/// </list>
/// Entries expire after the TTL; a background-free sweep runs amortized on
/// reserve/commit. A capacity safety valve evicts the soonest-to-expire entry
/// when the table stays full after sweeping, trading one id's dedup window for
/// bounded memory.
/// </summary>
public sealed class EventDeduplicator
{
    public readonly record struct DedupLease(string Key, long Token)
    {
        /// <summary>False for blank platform/event ids, which are accepted without marking.</summary>
        public bool Valid => !string.IsNullOrEmpty(Key);
    }

    private sealed record Entry(DedupEntryState State, long Token, long ExpiresAtMs);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly int _sweepIntervalMs;
    private long _token;
    private long _lastSweepMs;

    public EventDeduplicator(TimeSpan ttl, int capacity = 200_000, int sweepIntervalMs = 30_000)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl));
        }

        _ttl = ttl;
        _capacity = capacity;
        _sweepIntervalMs = sweepIntervalMs;
    }

    public bool TryReserve(string platform, string eventId, out DedupLease lease)
    {
        lease = default;
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(eventId))
        {
            // Same contract as the old TryMark: unverifiable ids pass through unmarked.
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        MaybeSweep(now);

        var key = ToKey(platform, eventId);
        var token = Interlocked.Increment(ref _token);
        var fresh = new Entry(DedupEntryState.Reserved, token, now + (long)_ttl.TotalMilliseconds);

        if (_entries.TryAdd(key, fresh))
        {
            lease = new DedupLease(key, token);
            return true;
        }

        // Key exists: accept only if the standing entry has already expired,
        // replaced atomically so a concurrent reserver still sees exactly one winner.
        if (_entries.TryGetValue(key, out var existing) && existing.ExpiresAtMs <= now
            && _entries.TryUpdate(key, fresh, existing))
        {
            lease = new DedupLease(key, token);
            return true;
        }

        return false;
    }

    /// <summary>Publish succeeded: keep the id remembered (Reserved → Delivered, TTL refreshed).</summary>
    public void Commit(DedupLease lease)
    {
        if (!lease.Valid)
        {
            return;
        }

        if (_entries.TryGetValue(lease.Key, out var current) && current.Token == lease.Token)
        {
            var delivered = new Entry(
                DedupEntryState.Delivered,
                lease.Token,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)_ttl.TotalMilliseconds);
            _entries.TryUpdate(lease.Key, delivered, current);
        }
    }

    /// <summary>
    /// Publish failed: release the reservation so a platform retry is treated
    /// as a fresh delivery, not a duplicate.
    /// </summary>
    public void Rollback(DedupLease lease)
    {
        if (!lease.Valid)
        {
            return;
        }

        if (_entries.TryGetValue(lease.Key, out var current)
            && current.Token == lease.Token
            && current.State == DedupEntryState.Reserved)
        {
            ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(lease.Key, current));
        }
    }

    private static string ToKey(string platform, string eventId) => $"{platform}:{eventId}";

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
        var soonestEntry = default(Entry);
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

        // Capacity valve: still full after sweeping -> evict the entry whose dedup
        // window ends soonest; bounded memory wins over one id's remaining window.
        if (_entries.Count >= _capacity && soonestKey != null)
        {
            // soonestKey and soonestEntry are only ever assigned together in the
            // sweep above, so a non-null key implies a non-null entry.
            ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(soonestKey, soonestEntry!));
        }
    }
}
