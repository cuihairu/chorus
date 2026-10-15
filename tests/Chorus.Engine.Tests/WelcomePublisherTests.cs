using System.Collections.Concurrent;
using Chorus.EngineHost;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.Engine.Tests;

/// <summary>
/// Welcome delivery off the engine's receive loop: a stalled eng-&gt;gw bus
/// must stall welcome delivery (counted as failures) and never the event path.
/// </summary>
public sealed class WelcomePublisherTests
{
    private static Envelope Welcome(int n) =>
        EnvelopeFactory.Create(IpcMessageTypes.EngineWelcome, $"s-{n}", new ServerWelcome(
            Type: ProtocolTypes.Welcome, SessionId: $"s-{n}", ClientId: $"c-{n}",
            TickDurationMs: 10, InputDelayTicks: 0, SnapshotEveryTicks: 1, ServerTimeMs: 1));

    [Fact]
    public async Task QueuedWelcomes_DeliverInOrder()
    {
        var seen = new ConcurrentQueue<int>();
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Envelope, CancellationToken, Task> publish = (env, _) =>
        {
            var n = int.Parse(env.SessionId.AsSpan()[2..]);
            seen.Enqueue(n);
            if (n == 2)
            {
                done.TrySetResult(n);
            }

            return Task.CompletedTask;
        };
        var publisher = new WelcomePublisher(publish, onFailure: (_, _) => Assert.Fail("no failure expected"));

        await publisher.StartAsync(CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(publisher.TryEnqueue(Welcome(i)));
        }

        Assert.Equal(2, await done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 0, 1, 2 }, seen.ToArray());
        Assert.Equal(0, publisher.Failed);
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task QueueFull_DropsWelcomes_WithoutBlockingTheCaller()
    {
        // The bus never drains, so the reader blocks on the first envelope
        // while the queue holds the rest. Fill the queue before the publisher
        // starts so the first drop is decided by the queue limit alone, then
        // push more: at most one of them can be accepted (the slot the reader
        // frees while blocked), so the rest must drop without the caller ever
        // awaiting.
        var stalled = new TaskCompletionSource<bool>();
        var publisher = new WelcomePublisher(
            (_, _) => stalled.Task,
            capacity: 2,
            onFailure: (_, ex) => Assert.IsType<InvalidOperationException>(ex));

        Assert.True(publisher.TryEnqueue(Welcome(0)));
        Assert.True(publisher.TryEnqueue(Welcome(1)));
        Assert.False(publisher.TryEnqueue(Welcome(2)));
        Assert.Equal(1, publisher.Failed);

        await publisher.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        var dropped = 0;
        for (var i = 3; i < 103; i++)
        {
            if (!publisher.TryEnqueue(Welcome(i)))
            {
                dropped++;
            }
        }

        Assert.True(dropped >= 99);
        Assert.Equal(1 + dropped, publisher.Failed);

        stalled.SetResult(true);
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task PublishFailure_IsReported_AndPublisherKeepsGoing()
    {
        var attempts = new ConcurrentQueue<int>();
        var delivered = new SemaphoreSlim(0);
        Func<Envelope, CancellationToken, Task> publish = (env, _) =>
        {
            var n = int.Parse(env.SessionId.AsSpan()[2..]);
            attempts.Enqueue(n);
            if (n == 0)
            {
                throw new InvalidOperationException("bus down");
            }

            delivered.Release();
            return Task.CompletedTask;
        };
        var publisher = new WelcomePublisher(
            publish,
            capacity: 4,
            onFailure: (_, ex) => Assert.IsType<InvalidOperationException>(ex));

        await publisher.StartAsync(CancellationToken.None);
        Assert.True(publisher.TryEnqueue(Welcome(0)));
        Assert.True(publisher.TryEnqueue(Welcome(1)));

        // The first envelope throws, the second still gets delivered.
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 0, 1 }, attempts.ToArray());
        Assert.Equal(1, publisher.Failed);
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task CancelledToken_CompletesThePublisherTask()
    {
        var publisher = new WelcomePublisher((_, _) => Task.Delay(1_000));

        using var cts = new CancellationTokenSource();
        await publisher.StartAsync(cts.Token);
        Assert.True(publisher.TryEnqueue(Welcome(0)));
        cts.Cancel();

        var dispose = publisher.DisposeAsync().AsTask();
        Assert.True(await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))) == dispose);
    }
}
