using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Acceptance for the latest-wins slot's observability (audit B3 /
/// frame-sync-best-practices §可观测性, "待导出"): a replacement that
/// evicts a queued snapshot must be counted on <see cref="OutboundClientQueue.Dropped"/>.
/// The counter used to be dead code - the slot is capacity 1 with
/// <c>BoundedChannelFullMode.DropOldest</c>, so <c>TryWrite</c> returns true
/// while silently evicting the older item and never failed; only a completed
/// writer returned false. Real drops were therefore invisible.
/// </summary>
public sealed class OutboundClientQueueTests
{
    [Fact]
    public async Task DropOldestEviction_IsCountedOnDropped()
    {
        var client = new BlockingClient();
        await using var queue = new OutboundClientQueue(client);

        await queue.SendSnapshotAsync(Wire(1), CancellationToken.None);

        // The pump has picked up wire 1 and is stuck sending it, so the slot
        // is free: wire 2 lands in it, wire 3 and wire 4 have nowhere to go.
        await client.Entered.Task;
        await queue.SendSnapshotAsync(Wire(2), CancellationToken.None);
        await queue.SendSnapshotAsync(Wire(3), CancellationToken.None);
        await queue.SendSnapshotAsync(Wire(4), CancellationToken.None);

        // wire 3 evicts wire 2, wire 4 evicts wire 3: exactly two replacements,
        // each counted. The old counter saw none of them.
        Assert.Equal(2, queue.Dropped);
    }

    [Fact]
    public async Task ClientId_PassesThrough()
    {
        await using var queue = new OutboundClientQueue(new BlockingClient());

        Assert.Equal("c-block", queue.ClientId);
    }

    [Fact]
    public async Task Replay_DeliversAllFramesInOrder()
    {
        var client = new RecordingClient();
        await using var queue = new OutboundClientQueue(client);

        await queue.SendReplayAsync([Wire(1), Wire(2), Wire(3)], CancellationToken.None);

        Assert.Equal(3, queue.Replayed);
        lock (client.Ticks)
        {
            Assert.Equal([1L, 2L, 3L], client.Ticks);
        }
    }

    [Fact]
    public async Task Replay_EmptyFrames_IsNoOp()
    {
        var client = new RecordingClient();
        await using var queue = new OutboundClientQueue(client);

        await queue.SendReplayAsync(Array.Empty<WireSnapshot>(), CancellationToken.None);

        Assert.Equal(0, queue.Replayed);
    }

    [Fact]
    public async Task Replay_TransportError_StopsBurstSilently()
    {
        var client = new RecordingClient { ThrowOnNext = new IOException("socket died") };
        await using var queue = new OutboundClientQueue(client);

        // 第 1 帧就失败：整段静默中止（掉线客户端靠下一次 resync 追回），不逃逸。
        await queue.SendReplayAsync([Wire(1), Wire(2), Wire(3)], CancellationToken.None);

        Assert.Equal(0, queue.Replayed);
        lock (client.Ticks)
        {
            Assert.Empty(client.Ticks);
        }
    }

    [Fact]
    public async Task Replay_CancelledBeforeGate_IsSilent()
    {
        var client = new RecordingClient();
        await using var queue = new OutboundClientQueue(client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await queue.SendReplayAsync([Wire(1)], cts.Token);

        Assert.Equal(0, queue.Replayed);
    }

    [Fact]
    public async Task Pump_TransportError_KeepsPumping()
    {
        var client = new RecordingClient { ThrowOnNext = new IOException("socket died") };
        await using var queue = new OutboundClientQueue(client);

        // 等 pump 真正进入 wire 1 的发送（client 报告 Entered）再投 wire 2：
        // 保证武装的异常被 in-flight 的 wire 1 消费，而不是被 latest-wins
        // 挤出的那一帧随机消费。
        await queue.SendSnapshotAsync(Wire(1), CancellationToken.None);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.SendSnapshotAsync(Wire(2), CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && Volatile.Read(ref client.SentCount) < 1)
        {
            await Task.Delay(10);
        }

        Assert.True(queue.Sent >= 1, $"pump died on transport error (sent={queue.Sent})");
        lock (client.Ticks)
        {
            Assert.Equal([2L], client.Ticks);
        }
    }

    private static WireSnapshot Wire(long tickId) => new(
        new SessionSnapshot("s-queue", tickId, 0, new { }),
        "[]u="u8.ToArray());

    private sealed class BlockingClient : ISessionClient
    {
        public string ClientId => "c-block";

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    /// <summary>记录已投递 tick；可单发武装一次异常以模拟传输故障。</summary>
    private sealed class RecordingClient : ISessionClient
    {
        private Exception? _throwOnNext;

        public string ClientId => "c-rec";

        public List<long> Ticks { get; } = new();

        public int SentCount;

        /// <summary>每次发送都被调用前置位：测试用来确定 pump 已进入本次发送。</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? ThrowOnNext
        {
            get => _throwOnNext;
            set => _throwOnNext = value;
        }

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            var ex = Interlocked.Exchange(ref _throwOnNext, null);
            if (ex is not null)
            {
                throw ex;
            }

            lock (Ticks)
            {
                Ticks.Add(snapshot.TickId);
            }

            Interlocked.Increment(ref SentCount);
            return ValueTask.CompletedTask;
        }
    }
}
