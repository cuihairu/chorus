using System.Runtime.CompilerServices;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;

namespace Chorus.Engine.Tests;

/// <summary>
/// Engine-side per-session sharding over one eng-to-gw channel (todo §10
/// blocker ②): one session always lands on one shard bus so per-session order
/// is untouched, while different sessions stop queueing behind a single
/// socket's write lock. Publish stays synchronous (delivery semantics
/// unchanged); only the contention surface shrinks.
/// </summary>
public sealed class ShardedMessageBusTests
{
    private static Envelope Env(string sessionId) =>
        EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, sessionId, new { id = 1 });

    [Fact]
    public void ShardIndex_IsStableAndInRange()
    {
        // Stable across runs: routing must not reshuffle sessions between
        // restarts, and every shard must be reachable.
        Assert.Equal(ShardedMessageBus.ShardIndex("load-s1", 4), ShardedMessageBus.ShardIndex("load-s1", 4));
        var seen = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            seen.Add(ShardedMessageBus.ShardIndex($"load-s{i}", 3));
            Assert.InRange(ShardedMessageBus.ShardIndex($"load-s{i}", 3), 0, 2);
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(0, ShardedMessageBus.ShardIndex("", 3));
    }

    [Fact]
    public async Task Publish_RoutesEachEnvelopeToExactlyOneShard()
    {
        await using var busA = new InProcMessageBus();
        await using var busB = new InProcMessageBus();
        await using var busC = new InProcMessageBus();
        await using var sharded = new ShardedMessageBus([busA, busB, busC]);

        var seen = new List<Envelope>();
        _ = PumpAsync(busA, seen);
        _ = PumpAsync(busB, seen);
        _ = PumpAsync(busC, seen);

        for (var i = 0; i < 60; i++)
        {
            await sharded.PublishAsync(Env($"load-s{i}"), CancellationToken.None);
        }

        await WaitUntilAsync(() => seen.Count == 60);
        Assert.Equal(60, seen.Count);
    }

    [Fact]
    public async Task SameSession_StaysOnOneShard()
    {
        await using var busA = new InProcMessageBus();
        await using var busB = new InProcMessageBus();
        await using var sharded = new ShardedMessageBus([busA, busB]);

        var seenOnA = new List<Envelope>();
        var seenOnB = new List<Envelope>();
        _ = PumpAsync(busA, seenOnA);
        _ = PumpAsync(busB, seenOnB);

        // The tick body publishes snapshots one session at a time: one session's
        // frames must never split across shards (that would reorder them).
        for (var i = 0; i < 5; i++)
        {
            await sharded.PublishAsync(Env("s-1"), CancellationToken.None);
        }

        await WaitUntilAsync(() => seenOnA.Count + seenOnB.Count == 5);

        Assert.Equal(5, seenOnA.Count + seenOnB.Count);
        // 全部 5 帧落在同一片：亲和性成立（0+5 或 5+0）。
        Assert.True(seenOnA.Count == 5 || seenOnB.Count == 5);
    }

    [Fact]
    public async Task DifferentSessions_SpreadOverShards()
    {
        await using var busA = new InProcMessageBus();
        await using var busB = new InProcMessageBus();
        await using var sharded = new ShardedMessageBus([busA, busB]);

        var seenOnA = new List<Envelope>();
        var seenOnB = new List<Envelope>();
        _ = PumpAsync(busA, seenOnA);
        _ = PumpAsync(busB, seenOnB);

        for (var i = 0; i < 20; i++)
        {
            await sharded.PublishAsync(Env($"load-s{i}"), CancellationToken.None);
        }

        await WaitUntilAsync(() => seenOnA.Count + seenOnB.Count == 20);
        // Both shards carry traffic: the whole point is splitting the
        // per-session write queues instead of one serial socket.
        Assert.True(seenOnA.Count > 0 && seenOnB.Count > 0);
        Assert.Equal(20, seenOnA.Count + seenOnB.Count);
    }

    [Fact]
    public async Task ConcurrentPublishes_AreAllDelivered()
    {
        await using var busA = new InProcMessageBus();
        await using var busB = new InProcMessageBus();
        await using var sharded = new ShardedMessageBus([busA, busB]);

        var seen = new List<Envelope>();
        _ = PumpAsync(busA, seen);
        _ = PumpAsync(busB, seen);

        const int producers = 32;
        const int perProducer = 20;
        var tasks = Enumerable.Range(0, producers)
            .Select(async producer =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    await sharded.PublishAsync(Env($"load-s{i % 20}"), CancellationToken.None);
                }
            })
            .ToArray();
        await Task.WhenAll(tasks);

        const int total = producers * perProducer;

        await WaitUntilAsync(() => seen.Count == total);
        Assert.Equal(total, seen.Count);
    }

    [Fact]
    public async Task ShardPublishFailure_SurfacesToCaller()
    {
        await using var good = new InProcMessageBus();
        await using var sharded = new ShardedMessageBus(new IMessageBus[] { new ThrowingBus(), good });

        // 路由确定：用公开的 ShardIndex 挑一个落在坏片(0)与好片(1)的 session。
        var downSession = "s-down-0";
        var upSession = "s-up-1";
        for (var i = 0; ; i++)
        {
            if (ShardedMessageBus.ShardIndex($"s-down-{i}", 2) == 0)
            {
                downSession = $"s-down-{i}";
            }

            if (ShardedMessageBus.ShardIndex($"s-up-{i}", 2) == 1)
            {
                upSession = $"s-up-{i}";
            }

            if (downSession != "s-down-0" && upSession != "s-up-1")
            {
                break;
            }
        }

        var seenOnGood = new List<Envelope>();
        _ = PumpAsync(good, seenOnGood);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sharded.PublishAsync(Env(downSession), CancellationToken.None));
        Assert.Equal("shard down", ex.Message);

        await sharded.PublishAsync(Env(upSession), CancellationToken.None);
        await WaitUntilAsync(() => seenOnGood.Count == 1);
    }

    [Fact]
    public async Task Subscribe_IsNotSupported()
    {
        await using var sharded = new ShardedMessageBus([new InProcMessageBus()]);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in sharded.SubscribeAsync())
            {
            }
        });
    }

    [Fact]
    public void Constructor_NoShards_Throws()
    {
        // 空分片集合构造期即拒绝：没有下游的发布会静默丢失。
        Assert.Throws<ArgumentException>(() => new ShardedMessageBus(Array.Empty<IMessageBus>()));
    }

    private static async Task PumpAsync(IMessageBus bus, List<Envelope> into)
    {
        await foreach (var env in bus.SubscribeAsync(CancellationToken.None))
        {
            lock (into)
            {
                into.Add(env);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "condition never became true");
            await Task.Delay(10);
        }
    }

    private sealed class ThrowingBus : IMessageBus
    {
        public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
            => throw new InvalidOperationException("shard down");

        public async IAsyncEnumerable<Envelope> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
