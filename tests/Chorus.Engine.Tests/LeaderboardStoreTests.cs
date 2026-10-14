using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 排行榜核心金测（todo §15 排行榜项，契约 = 审计 §9/§9.2/§9.3）：
/// 替换式实时分（Room/Match 权威状态直接投影，客户端不能自报分——本 API 只暴露给引擎侧）、
/// 幂等增量（重复 sourceId 不重复加分，全局/周期榜由 Data Worker 异步聚合）、
/// 并列分数共享名次且同分确定性排序（participantId 字典序）、
/// 最终成绩冻结（赛后查询不变）、查询与更新分离（Query 返回独立快照）。
/// </summary>
public sealed class LeaderboardStoreTests
{
    private static readonly (string Id, long Score)[] Sample =
    {
        ("p-c", 30),
        ("p-a", 30),
        ("p-b", 50),
        ("p-d", 10),
    };

    private static LeaderboardStore NewBoard()
    {
        var store = new LeaderboardStore();
        foreach (var (id, score) in Sample)
        {
            store.UpsertScore(LeaderboardScope.Room, "room-1", id, score);
        }

        return store;
    }

    [Fact]
    public void UpsertScore_NewParticipant_AppearsInQuery()
    {
        var store = NewBoard();

        var top = store.Query(LeaderboardScope.Room, "room-1")[0];

        Assert.Equal("p-b", top.ParticipantId);
        Assert.Equal(50, top.Score);
        Assert.Equal(1, top.Rank);
    }

    [Fact]
    public void UpsertScore_IsReplace_NotAccumulate()
    {
        var store = NewBoard();

        // 房间/单局榜来自权威状态投影：同参与者再投影 = 替换，不是累加（幂等）。
        store.UpsertScore(LeaderboardScope.Room, "room-1", "p-b", 20);

        Assert.Equal(20, store.Query(LeaderboardScope.Room, "room-1").Single(e => e.ParticipantId == "p-b").Score);
    }

    [Fact]
    public void Query_OrdersByScoreDesc_TiesByIdAscending()
    {
        var store = NewBoard();

        // 分数降序；并列时按 participantId 字典序（确定性，不抖动）。
        Assert.Equal(new[] { "p-b", "p-a", "p-c", "p-d" }, store.Query(LeaderboardScope.Room, "room-1").Select(e => e.ParticipantId));
    }

    [Fact]
    public void Query_TiesShareCompetitionRank()
    {
        var store = NewBoard();

        // 并列分数共享名次（竞赛排名：1,2,2,4），名次不再按并列内顺序加一。
        var ranks = store.Query(LeaderboardScope.Room, "room-1").ToDictionary(e => e.ParticipantId, e => e.Rank);

        Assert.Equal(1, ranks["p-b"]);
        Assert.Equal(2, ranks["p-a"]);
        Assert.Equal(2, ranks["p-c"]);
        Assert.Equal(4, ranks["p-d"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void UpsertScore_BlankScopeOrParticipant_Throws(string blank)
    {
        var store = new LeaderboardStore();

        Assert.ThrowsAny<ArgumentException>(() => store.UpsertScore(LeaderboardScope.Room, blank, "p-1", 10));
        Assert.ThrowsAny<ArgumentException>(() => store.UpsertScore(LeaderboardScope.Room, "room-1", blank, 10));
    }

    [Fact]
    public void UpsertScore_ReplaySameScore_NoRankingChange()
    {
        var store = NewBoard();
        var before = store.Query(LeaderboardScope.Room, "room-1");

        store.UpsertScore(LeaderboardScope.Room, "room-1", "p-a", 30);

        Assert.Equal(before, store.Query(LeaderboardScope.Room, "room-1"));
    }

    [Fact]
    public void AddDelta_AccumulatesForParticipant()
    {
        var store = new LeaderboardStore();

        // 全局/周期榜走异步聚合：增量累加（来自已去重的命令）。
        store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-1");
        store.AddDelta(LeaderboardScope.Global, "all", "p-1", 7, "cmd-2");

        Assert.Equal(12, store.Query(LeaderboardScope.Global, "all").Single(e => e.ParticipantId == "p-1").Score);
    }

    [Fact]
    public void AddDelta_UnknownParticipant_StartsFromZero()
    {
        var store = new LeaderboardStore();

        Assert.True(store.AddDelta(LeaderboardScope.Global, "all", "p-new", 5, "cmd-1"));
        Assert.Equal(5, store.Query(LeaderboardScope.Global, "all").Single().Score);
    }

    [Fact]
    public void AddDelta_DuplicateSourceId_NotDoubleCounted()
    {
        var store = new LeaderboardStore();
        Assert.True(store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-1"));

        // §9.2「重复事件不能重复增加积分」：同 sourceId 重放被拒，分数不变。
        Assert.False(store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-1"));
        Assert.Equal(5, store.Query(LeaderboardScope.Global, "all").Single().Score);
    }

    [Fact]
    public void AddDelta_DifferentSourceIds_BothCounted()
    {
        var store = new LeaderboardStore();

        store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-1");
        store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-2");

        Assert.Equal(10, store.Query(LeaderboardScope.Global, "all").Single().Score);
    }

    [Fact]
    public void AddDelta_NegativeDelta_CorrectsScore()
    {
        var store = new LeaderboardStore();
        store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-1");

        // 负增量用于纠错（审计允许业务定义），仓储不悄悄夹到 0。
        store.AddDelta(LeaderboardScope.Global, "all", "p-1", -3, "cmd-3");

        Assert.Equal(2, store.Query(LeaderboardScope.Global, "all").Single().Score);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddDelta_BlankSourceId_Throws(string blank)
    {
        var store = new LeaderboardStore();

        // 无 sourceId 无法保证幂等：fail-visible 拒绝。
        Assert.ThrowsAny<ArgumentException>(() => store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, blank));
    }

    [Fact]
    public void Scopes_AreIsolated()
    {
        var store = NewBoard();

        // 房间榜与全局榜是不同作用域：同 id 互不可见（查询与更新分离的一部分）。
        store.UpsertScore(LeaderboardScope.Global, "room-1", "p-x", 1);

        Assert.DoesNotContain(store.Query(LeaderboardScope.Room, "room-1"), e => e.ParticipantId == "p-x");
        Assert.Single(store.Query(LeaderboardScope.Global, "room-1"));
    }

    [Fact]
    public void Query_UnknownScope_ReturnsEmpty()
    {
        var store = new LeaderboardStore();

        Assert.Empty(store.Query(LeaderboardScope.Room, "ghost"));
    }

    [Fact]
    public void Query_TopN_ReturnsLeadingEntries()
    {
        var store = NewBoard();

        var top2 = store.Query(LeaderboardScope.Room, "room-1", top: 2);

        Assert.Equal(new[] { "p-b", "p-a" }, top2.Select(e => e.ParticipantId));
    }

    [Fact]
    public void Freeze_LocksStandings()
    {
        var store = NewBoard();
        // Match 与 Room 作用域隔离：先把同一份成绩投影到单局榜，再在结算时冻结。
        foreach (var (id, score) in Sample)
        {
            store.UpsertScore(LeaderboardScope.Match, "match-9", id, score);
        }

        store.Freeze(LeaderboardScope.Match, "match-9");

        Assert.True(store.IsFrozen(LeaderboardScope.Match, "match-9"));
        Assert.Equal(4, store.Query(LeaderboardScope.Match, "match-9").Count);
    }

    [Fact]
    public void Freeze_PostFreezeWrites_RejectedVisible()
    {
        var store = new LeaderboardStore();
        store.UpsertScore(LeaderboardScope.Match, "match-9", "p-1", 10);
        store.Freeze(LeaderboardScope.Match, "match-9");

        Assert.False(store.UpsertScore(LeaderboardScope.Match, "match-9", "p-1", 99));
        Assert.False(store.AddDelta(LeaderboardScope.Match, "match-9", "p-1", 99, "cmd-9"));

        Assert.Equal(10, store.Query(LeaderboardScope.Match, "match-9").Single().Score);
    }

    [Fact]
    public void Freeze_Refreeze_Idempotent()
    {
        var store = NewBoard();
        store.Freeze(LeaderboardScope.Room, "room-1");

        // 重复冻结 = 幂等：不抛错、名次不变。
        store.Freeze(LeaderboardScope.Room, "room-1");

        Assert.Equal(4, store.Query(LeaderboardScope.Room, "room-1").Count);
    }

    [Fact]
    public void ConcurrentSameSourceDelta_AppliedExactlyOnce()
    {
        var store = new LeaderboardStore();
        var applied = 0;

        Parallel.For(0, 32, _ =>
        {
            if (store.AddDelta(LeaderboardScope.Global, "all", "p-1", 5, "cmd-race"))
            {
                Interlocked.Increment(ref applied);
            }
        });

        // 并发同源重放恰好生效一次：不多加不少加。
        Assert.Equal(1, applied);
        Assert.Equal(5, store.Query(LeaderboardScope.Global, "all").Single().Score);
    }

    [Fact]
    public void ConcurrentDifferentSources_AllApplied()
    {
        var store = new LeaderboardStore();

        Parallel.For(0, 32, i => store.AddDelta(LeaderboardScope.Global, "all", "p-1", 1, $"cmd-{i}"));

        Assert.Equal(32, store.Query(LeaderboardScope.Global, "all").Single().Score);
    }

    [Fact]
    public void PeriodBoards_ResolveByScopeKey()
    {
        var store = new LeaderboardStore();

        // 周期榜：周期是 scope key 的一部分（如 day:2026-10-14）——跨周期自然重置，无需清零。
        store.AddDelta(LeaderboardScope.Period, "day:2026-10-14", "p-1", 5, "cmd-1");
        store.AddDelta(LeaderboardScope.Period, "day:2026-10-15", "p-1", 3, "cmd-2");

        Assert.Equal(5, store.Query(LeaderboardScope.Period, "day:2026-10-14").Single().Score);
        Assert.Equal(3, store.Query(LeaderboardScope.Period, "day:2026-10-15").Single().Score);
    }
}
