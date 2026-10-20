using System.Runtime.CompilerServices;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;

namespace Chorus.Engine.Tests;

/// <summary>
/// Gateway-side merge of K eng-to-gw shard sockets (todo §10 blocker ②):
/// each shard keeps its own reader, all of them funnel into one consumer
/// stream, so the gateway's single-consumer dispatch semantics stay intact
/// while the engine side gains parallel sockets.
/// </summary>
public sealed class FanInMessageBusTests
{
    private static Envelope Env(string sessionId, string sourceEventId) =>
        EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, sessionId, new { id = 1 }, sourceEventId);

    [Fact]
    public async Task Subscribe_MergesAllChildStreams()
    {
        await using var childA = new InProcMessageBus();
        await using var childB = new InProcMessageBus();
        await using var childC = new InProcMessageBus();
        await using var fanIn = new FanInMessageBus([childA, childB, childC]);

        var seen = new List<Envelope>();
        var consume = Task.Run(async () =>
        {
            await foreach (var env in fanIn.SubscribeAsync(CancellationToken.None))
            {
                lock (seen)
                {
                    seen.Add(env);
                }
            }
        });

        for (var i = 0; i < 10; i++)
        {
            await childA.PublishAsync(Env($"sa-{i}", $"a{i}"), CancellationToken.None);
            await childB.PublishAsync(Env($"sb-{i}", $"b{i}"), CancellationToken.None);
            await childC.PublishAsync(Env($"sc-{i}", $"c{i}"), CancellationToken.None);
        }

        await WaitUntilAsync(() =>
        {
            lock (seen)
            {
                return seen.Count == 30;
            }
        });

        lock (seen)
        {
            Assert.Equal(30, seen.Count);
            Assert.Equal(Enumerable.Range(0, 10).SelectMany(i => new[] { $"a{i}", $"b{i}", $"c{i}" }).ToHashSet(),
                seen.Select(e => e.SourceEventId!).ToHashSet());
        }

        // 结束消费不挂死：children 完成后流终止。
        await childA.DisposeAsync();
        await childB.DisposeAsync();
        await childC.DisposeAsync();
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PerChildOrder_Preserved()
    {
        await using var childA = new InProcMessageBus();
        await using var childB = new InProcMessageBus();
        await using var fanIn = new FanInMessageBus([childA, childB]);

        var seen = new List<Envelope>();
        var consume = Task.Run(async () =>
        {
            await foreach (var env in fanIn.SubscribeAsync(CancellationToken.None))
            {
                lock (seen)
                {
                    seen.Add(env);
                }
            }
        });

        // 两个分片交错写入：合并流允许 a/b 交错，但各自内部顺序不得颠倒。
        for (var i = 0; i < 20; i++)
        {
            await childA.PublishAsync(Env("sa", $"a{i:D2}"), CancellationToken.None);
            await childB.PublishAsync(Env("sb", $"b{i:D2}"), CancellationToken.None);
        }

        await WaitUntilAsync(() =>
        {
            lock (seen)
            {
                return seen.Count == 40;
            }
        });

        await childA.DisposeAsync();
        await childB.DisposeAsync();
        await consume.WaitAsync(TimeSpan.FromSeconds(5));

        lock (seen)
        {
            var aSeq = seen.Where(e => e.SourceEventId!.StartsWith("a", StringComparison.Ordinal)).Select(e => e.SourceEventId).ToList();
            var bSeq = seen.Where(e => e.SourceEventId!.StartsWith("b", StringComparison.Ordinal)).Select(e => e.SourceEventId).ToList();
            Assert.Equal(20, aSeq.Count);
            Assert.Equal(20, bSeq.Count);
            Assert.Equal(aSeq.OrderBy(id => id, StringComparer.Ordinal), aSeq);
            Assert.Equal(bSeq.OrderBy(id => id, StringComparer.Ordinal), bSeq);
        }
    }

    [Fact]
    public async Task StreamEnds_WhenAllChildrenComplete()
    {
        await using var childA = new InProcMessageBus();
        await using var childB = new InProcMessageBus();
        await using var fanIn = new FanInMessageBus([childA, childB]);

        await childA.PublishAsync(Env("sa", "a0"), CancellationToken.None);
        await childB.PublishAsync(Env("sb", "b0"), CancellationToken.None);
        await childA.DisposeAsync();
        await childB.DisposeAsync();

        var count = 0;
        await foreach (var _ in fanIn.SubscribeAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Publish_IsNotSupported()
    {
        await using var fanIn = new FanInMessageBus([new InProcMessageBus()]);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await fanIn.PublishAsync(Env("s", "m"), CancellationToken.None));
    }

    [Fact]
    public void Constructor_NoChildren_Throws()
    {
        // 空上游集合构造期即拒绝：订阅一个没有源的流只会静默空转。
        Assert.Throws<ArgumentException>(() => new FanInMessageBus(Array.Empty<IMessageBus>()));
    }

    [Fact]
    public async Task Cancellation_StopsConsumer()
    {
        await using var child = new InProcMessageBus();
        await using var fanIn = new FanInMessageBus([child]);

        using var cts = new CancellationTokenSource();
        var consuming = false;
        var consume = Task.Run(async () =>
        {
            await foreach (var _ in fanIn.SubscribeAsync(cts.Token))
            {
                consuming = true;
                cts.Cancel();
            }
        });

        await child.PublishAsync(Env("s", "m0"), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await consume.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(consuming);
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
}
