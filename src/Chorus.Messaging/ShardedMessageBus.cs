namespace Chorus.Messaging;

/// <summary>
/// Publish-side routing over sharded eng-to-gw channels (todo §10 blocker ②):
/// unlike <see cref="FanOutMessageBus"/> (which copies every envelope to every
/// child), each envelope goes to exactly one shard picked by a stable hash of
/// its session id. A session therefore always travels the same socket, so
/// per-session ordering and the synchronous PublishAsync semantics the tick
/// body relies on are untouched — only the write contention across sessions
/// is split. Subscribe is publish-only by design; the engine receives commands
/// on its own gw-to-eng server bus.
/// </summary>
public sealed class ShardedMessageBus : IMessageBus
{
    private readonly IMessageBus[] _shards;

    public ShardedMessageBus(IEnumerable<IMessageBus> shards)
    {
        _shards = shards.ToArray();
        if (_shards.Length == 0)
        {
            throw new ArgumentException("At least one shard bus is required.", nameof(shards));
        }
    }

    /// <summary>Stable shard pick: FNV-1a over the session id's characters,
    /// reduced modulo the shard count. Deliberately not
    /// <see cref="string.GetHashCode"/> — that hash is randomised per process,
    /// which would reshuffle sessions between restarts. Characters are hashed
    /// directly (no UTF-8 encoding round-trip) because the tick body calls
    /// this per snapshot and only stability plus spread are contracted here.</summary>
    public static int ShardIndex(string sessionId, int shardCount)
    {
        if (shardCount <= 1 || string.IsNullOrEmpty(sessionId))
        {
            // EnvelopeFactory rejects empty session ids, so this only guards
            // degenerate inputs; channel-level frames like heartbeats hash by
            // their "__heartbeat" sentinel and land on one stable shard.
            return 0;
        }

        var hash = 2166136261u;
        foreach (var ch in sessionId)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return (int)(hash % (uint)shardCount);
    }

    public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
        => _shards[ShardIndex(envelope.SessionId, _shards.Length)].PublishAsync(envelope, cancellationToken);

    public IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ShardedMessageBus is publish-only; receive on the gw-to-eng server bus.");

    public ValueTask DisposeAsync()
    {
        foreach (var shard in _shards)
        {
            shard.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return ValueTask.CompletedTask;
    }
}
