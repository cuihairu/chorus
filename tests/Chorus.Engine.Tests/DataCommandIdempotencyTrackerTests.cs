using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// DataCommand 幂等追踪金测（todo §15 首项，契约 = 审计 §8.3「数据命令必须有幂等语义」）：
/// 协议沿用 EventDeduplicator 的 reserve/commit/rollback 原子纪律——
/// 首个调用者独占、处理失败回滚后允许重试（失败绝不被误记为「已处理」）、TTL 过期后可重处理。
/// </summary>
public sealed class DataCommandIdempotencyTrackerTests
{
    private const long StartMs = 1_000_000;

    private static DataCommand NewCommand(string id = "cmd-1") => new(
        CommandId: id,
        CommandType: "player.score.submit",
        RoomId: "room-1",
        Level: DataReliabilityLevel.GameRecord,
        Payload: "{}");

    [Fact]
    public void TryBegin_FirstCommand_ClaimsLease()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));

        Assert.True(tracker.TryBegin(NewCommand(), StartMs, out var lease));
        Assert.True(lease.Valid);
        Assert.True(tracker.IsClaimed("cmd-1"));
    }

    [Fact]
    public void TryBegin_DuplicateCommandId_SecondCallerLoses()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));
        tracker.TryBegin(NewCommand(), StartMs, out _);

        // 同 CommandId 并发处理恰好一个赢，重复命令被抑制（fail-visible 返回 false）。
        Assert.False(tracker.TryBegin(NewCommand(), StartMs + 1, out var lease));
        Assert.False(lease.Valid);
    }

    [Fact]
    public void Commit_AfterProcessing_RemembersCommandAsSeen()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));
        tracker.TryBegin(NewCommand(), StartMs, out var lease);

        tracker.Commit(lease);

        // 已提交 = 已处理：同 CommandId 重投递被抑制（幂等语义的核心）。
        Assert.False(tracker.TryBegin(NewCommand(), StartMs + 1, out _));
        Assert.True(tracker.IsClaimed("cmd-1"));
    }

    [Fact]
    public void Rollback_AfterFailure_AllowsRetry()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));
        tracker.TryBegin(NewCommand(), StartMs, out var lease);

        tracker.Rollback(lease);

        // 处理失败回滚释放占位——失败绝不被误记为「已处理」，生产者可安全重试。
        Assert.False(tracker.IsClaimed("cmd-1"));
        Assert.True(tracker.TryBegin(NewCommand(), StartMs + 1, out var retryLease));
        Assert.True(retryLease.Valid);
    }

    [Fact]
    public void TryBegin_AfterTtlExpiry_AllowsReprocessing()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromMilliseconds(1_000));
        tracker.TryBegin(NewCommand(), StartMs, out var lease);
        tracker.Commit(lease);

        // TTL 过期后记录释放：极晚到的重复投递不再无限期抑制（与 EventDeduplicator 同语义）。
        Assert.False(tracker.IsClaimedAt("cmd-1", StartMs + 2_000));
        Assert.True(tracker.TryBegin(NewCommand(), StartMs + 2_000, out _));
    }

    [Fact]
    public void TryBegin_BlankCommandId_ReturnsInvalidLeaseWithoutClaim()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));

        // 无 CommandId 无法幂等：不占位不抑制，由上层校验 fail-visible。
        Assert.False(tracker.TryBegin(NewCommand(id: " "), StartMs, out var lease));
        Assert.False(lease.Valid);
    }

    [Fact]
    public void CommitOrRollback_InvalidLease_IsNoOp()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));

        var blankLease = default(DataCommandIdempotencyTracker.Lease);
        tracker.Commit(blankLease);
        tracker.Rollback(blankLease);

        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void ConcurrentSameCommandId_ExactlyOneWinner()
    {
        var tracker = new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60));
        var wins = 0;

        Parallel.For(0, 32, i =>
        {
            if (tracker.TryBegin(NewCommand(), StartMs, out _))
                Interlocked.Increment(ref wins);
        });

        Assert.Equal(1, wins);
    }

    [Fact]
    public void CapacityExceeded_EvictsOldestExpiringEntry()
    {
        var tracker = new DataCommandIdempotencyTracker(
            TimeSpan.FromSeconds(60), capacity: 2, sweepIntervalMs: 1);

        tracker.TryBegin(NewCommand("cmd-a"), StartMs, out var a);
        tracker.Commit(a);
        tracker.TryBegin(NewCommand("cmd-b"), StartMs + 1, out var b);
        tracker.Commit(b);

        // 第三个命令触发安全阀：驱逐最早过期的条目（cmd-a），保证容量有界不失控。
        Assert.True(tracker.TryBegin(NewCommand("cmd-c"), StartMs + 2, out _));
        Assert.Equal(2, tracker.Count);

        // 被驱逐的 cmd-a 可重新处理（可观测的容量换稳定性折衷，同 EventDeduplicator 安全阀语义）。
        Assert.True(tracker.TryBegin(NewCommand("cmd-a"), StartMs + 3, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveTtl_Throws(long seconds)
    {
        // TTL 非正数会让每条记录立即过期（抑制窗口归零），构造期即拒绝。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveCapacity_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DataCommandIdempotencyTracker(TimeSpan.FromSeconds(60), capacity: capacity));
    }
}
