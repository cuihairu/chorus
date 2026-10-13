using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;

namespace Chorus.Engine.Tests;

/// <summary>
/// Engine-side fan-out over per-gateway eng-to-gw channels (stage-2 transport
/// half): one worker publishes every engine message to every configured
/// gateway; a dead gateway must not block or lose the others' delivery, and
/// the failure surfaces to the caller instead of being swallowed here.
/// </summary>
public sealed class FanOutMessageBusTests
{
    private static Envelope Env() =>
        EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, "s-fanout", new { id = 1 });

    [Fact]
    public async Task Publish_ReachesEveryChild()
    {
        await using var a = new InProcMessageBus();
        await using var b = new InProcMessageBus();
        await using var fan = new FanOutMessageBus(new IMessageBus[] { a, b });

        var seenA = new List<Envelope>();
        var seenB = new List<Envelope>();
        var pumpA = PumpAsync(a, seenA);
        var pumpB = PumpAsync(b, seenB);

        await fan.PublishAsync(Env(), CancellationToken.None);

        await WaitUntilAsync(() => seenA.Count >= 1 && seenB.Count >= 1);
        Assert.Equal("s-fanout", seenA[0].SessionId);
        Assert.Equal("s-fanout", seenB[0].SessionId);
    }

    [Fact]
    public async Task OneChildFails_OthersStillReceive_FailureSurfaces()
    {
        await using var good = new InProcMessageBus();
        await using var fan = new FanOutMessageBus(new IMessageBus[] { new ThrowingBus(), good });

        var seen = new List<Envelope>();
        _ = PumpAsync(good, seen);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            async () => await fan.PublishAsync(Env(), CancellationToken.None));
        Assert.Single(ex.InnerExceptions);

        await WaitUntilAsync(() => seen.Count >= 1);
        Assert.Equal("s-fanout", seen[0].SessionId);
    }

    [Fact]
    public async Task Subscribe_IsNotSupported()
    {
        await using var fan = new FanOutMessageBus(new IMessageBus[] { new InProcMessageBus() });
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in fan.SubscribeAsync())
            {
            }
        });
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
            => throw new InvalidOperationException("child down");

        public async IAsyncEnumerable<Envelope> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
