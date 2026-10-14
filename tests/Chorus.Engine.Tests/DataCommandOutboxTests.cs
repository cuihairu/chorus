using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 有界 DataCommand outbox 金测（todo §15 首项，契约 = 审计 §8.3）：
/// 队列容量必须有上限；满载时按可靠性分级分派——普通统计可丢弃（计数可观测、可重建），
/// 其余级别 RejectedFull fail-visible 绝不静默丢弃；非法命令拒绝不吞。
/// </summary>
public sealed class DataCommandOutboxTests
{
    private const long StartMs = 1_000_000;

    private static DataCommand NewCommand(
        string id = "cmd-1",
        DataReliabilityLevel level = DataReliabilityLevel.GameRecord) => new(
        CommandId: id,
        CommandType: "player.score.submit",
        RoomId: "room-1",
        Level: level,
        Payload: "{}");

    [Fact]
    public void Enqueue_UndefinedLevel_RejectedVisible()
    {
        var outbox = new DataCommandOutbox(capacity: 4);

        // 分级空洞比非法字段更危险（会被按默认策略投递），必须连同命令一起拒收。
        var outcome = outbox.Enqueue(NewCommand(level: (DataReliabilityLevel)99), StartMs);

        Assert.Equal(DataOutboxOutcome.Invalid, outcome);
        Assert.Equal(1, outbox.InvalidCount);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public void Enqueue_UnderCapacity_IsAcceptedAndCounted()
    {
        var outbox = new DataCommandOutbox(capacity: 4);

        var outcome = outbox.Enqueue(NewCommand(), StartMs);

        Assert.Equal(DataOutboxOutcome.Accepted, outcome);
        Assert.Equal(1, outbox.Count);
        Assert.Equal(1, outbox.EnqueuedCount);
    }

    [Fact]
    public void Enqueue_FullQueue_CriticalData_RejectedNotDropped()
    {
        var outbox = new DataCommandOutbox(capacity: 1);
        outbox.Enqueue(NewCommand("cmd-a", DataReliabilityLevel.CriticalEconomy), StartMs);

        // §8.3：关键数据不能在失败时静默丢弃——满载时 fail-visible 拒绝，由生产者决定重试。
        var outcome = outbox.Enqueue(NewCommand("cmd-b", DataReliabilityLevel.CriticalEconomy), StartMs + 1);

        Assert.Equal(DataOutboxOutcome.RejectedFull, outcome);
        Assert.Equal(1, outbox.Count);
        Assert.Equal(1, outbox.RejectedFullCount);
        Assert.Equal(0, outbox.DroppedCount);
    }

    [Fact]
    public void Enqueue_FullQueue_Statistical_DroppedAndCounted()
    {
        var outbox = new DataCommandOutbox(capacity: 1);
        outbox.Enqueue(NewCommand("cmd-a", DataReliabilityLevel.Statistical), StartMs);

        // §8.1：普通统计可丢弃并重建——但丢弃必须计数可观测，不是静默蒸发。
        var outcome = outbox.Enqueue(NewCommand("cmd-b", DataReliabilityLevel.Statistical), StartMs + 1);

        Assert.Equal(DataOutboxOutcome.Dropped, outcome);
        Assert.Equal(1, outbox.Count);
        Assert.Equal(1, outbox.DroppedCount);
    }

    [Fact]
    public void Enqueue_FullQueue_GameRecordAndProfile_RejectedNotDropped()
    {
        foreach (var level in new[] { DataReliabilityLevel.GameRecord, DataReliabilityLevel.PlayerProfile })
        {
            var outbox = new DataCommandOutbox(capacity: 1);
            outbox.Enqueue(NewCommand("cmd-a", level), StartMs);

            var outcome = outbox.Enqueue(NewCommand("cmd-b", level), StartMs + 1);

            Assert.Equal(DataOutboxOutcome.RejectedFull, outcome);
        }
    }

    [Fact]
    public void Enqueue_InvalidCommand_RejectedVisible()
    {
        var outbox = new DataCommandOutbox(capacity: 4);

        // 空 CommandId 属非法命令：fail-visible 拒绝，绝不入队也不计入丢弃。
        var outcome = outbox.Enqueue(NewCommand(id: " "), StartMs);

        Assert.Equal(DataOutboxOutcome.Invalid, outcome);
        Assert.Equal(0, outbox.Count);
        Assert.Equal(1, outbox.InvalidCount);
    }

    [Fact]
    public void TryDequeue_PreservesFifoOrder()
    {
        var outbox = new DataCommandOutbox(capacity: 4);
        outbox.Enqueue(NewCommand("cmd-a"), StartMs);
        outbox.Enqueue(NewCommand("cmd-b"), StartMs + 1);

        // 同级别保序：先入先出，GameRecord/CriticalEconomy 的顺序语义依赖此约束。
        Assert.True(outbox.TryDequeue(out var first));
        Assert.Equal("cmd-a", first!.Command.CommandId);
        Assert.True(outbox.TryDequeue(out var second));
        Assert.Equal("cmd-b", second!.Command.CommandId);
        Assert.False(outbox.TryDequeue(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Envelope_CarriesEnqueueTimestampForLatencyObservability()
    {
        var outbox = new DataCommandOutbox(capacity: 4);
        outbox.Enqueue(NewCommand(), StartMs);

        // §8.3：数据处理延迟必须可观测——envelope 携带入队时间戳，处理侧可算排队延迟。
        outbox.TryDequeue(out var envelope);

        Assert.Equal(StartMs, envelope!.EnqueuedAtMs);
    }

    [Fact]
    public void ConcurrentEnqueue_RespectsCapacityExactly()
    {
        var outbox = new DataCommandOutbox(capacity: 8);
        var accepted = 0;

        Parallel.For(0, 64, i =>
        {
            if (outbox.Enqueue(NewCommand($"cmd-{i}"), StartMs) == DataOutboxOutcome.Accepted)
                Interlocked.Increment(ref accepted);
        });

        // 容量是硬上限：并发下既不超卖也不少收。
        Assert.Equal(8, accepted);
        Assert.Equal(8, outbox.Count);
        Assert.Equal(8, outbox.EnqueuedCount);
    }

    [Fact]
    public void InvalidCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DataCommandOutbox(capacity: 0));
    }
}
