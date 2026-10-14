using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Room 生命周期状态机金测（todo §14 生命周期状态机项，契约 = 总体架构审计 §5.1 五条约束）：
/// 非法跃迁 fail-visible、Destroyed 只能经 Closing 到达（销毁有明确路径）、
/// Sleeping 非终态（休眠≠丢弃权威状态）、迁移必经 Recovering（保留状态与待处理事件）、
/// 容量淘汰与生命周期分离（注册表无驱逐→Destroyed 的通道）。
/// </summary>
public sealed class RoomLifecycleTests
{
    [Fact]
    public void Created_StartWaiting_GoesWaiting()
    {
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Created, RoomLifecycleAction.StartWaiting, out var to));
        Assert.Equal(RoomLifecycleState.Waiting, to);
    }

    [Fact]
    public void Waiting_Activate_GoesActive()
    {
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Waiting, RoomLifecycleAction.Activate, out var to));
        Assert.Equal(RoomLifecycleState.Active, to);
    }

    [Fact]
    public void Active_GoIdle_GoesIdle()
    {
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Active, RoomLifecycleAction.GoIdle, out var to));
        Assert.Equal(RoomLifecycleState.Idle, to);
    }

    [Fact]
    public void Idle_Sleep_GoesSleeping_AndSleeping_IsNotTerminal()
    {
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Idle, RoomLifecycleAction.Sleep, out var to));
        Assert.Equal(RoomLifecycleState.Sleeping, to);

        // 约束③：休眠不等于权威状态丢弃——休眠房间可恢复（走 Recovering，不直接回 Active）。
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Sleeping, RoomLifecycleAction.Recovered, out _));
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Sleeping, RoomLifecycleAction.Resume, out var resumeTo));
        Assert.Equal(RoomLifecycleState.Recovering, resumeTo);
        Assert.False(RoomLifecycleTransitions.IsTerminal(RoomLifecycleState.Sleeping));
    }

    [Fact]
    public void Migrating_MustPassThroughRecovering()
    {
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Active, RoomLifecycleAction.BeginMigration, out var to));
        Assert.Equal(RoomLifecycleState.Migrating, to);

        // 约束④：迁移必须保留权威状态与待处理事件，恢复完成后才回到 Active。
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Migrating, RoomLifecycleAction.Recovered, out _));
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Migrating, RoomLifecycleAction.BeginRecovery, out var recovering));
        Assert.Equal(RoomLifecycleState.Recovering, recovering);
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Recovering, RoomLifecycleAction.Recovered, out var back));
        Assert.Equal(RoomLifecycleState.Active, back);
    }

    [Fact]
    public void Migrating_CanAbortedByClosing()
    {
        // 迁移中止（源端失败/业务决定）也必须有收敛路径，不能卡在 Migrating。
        Assert.True(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Migrating, RoomLifecycleAction.Close, out var to));
        Assert.Equal(RoomLifecycleState.Closing, to);
    }

    [Fact]
    public void IllegalTransition_ReturnsFalse()
    {
        // 观众清空必须先 GoIdle，不能直接从 Active 休眠。
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Active, RoomLifecycleAction.Sleep, out _));
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Sleeping, RoomLifecycleAction.Activate, out _));
    }

    [Fact]
    public void Destroyed_IsTerminal()
    {
        Assert.True(RoomLifecycleTransitions.IsTerminal(RoomLifecycleState.Destroyed));
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Destroyed, RoomLifecycleAction.Destroyed, out _));
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Destroyed, RoomLifecycleAction.Activate, out _));
    }

    [Fact]
    public void Destroyed_OnlyReachableViaClosing()
    {
        // 约束⑤：销毁房间必须有明确业务条件与持久化策略——只有一条销毁路径
        // （Closing → Destroyed），任何状态都不能一步直接销毁。
        var actions = Enum.GetValues<RoomLifecycleAction>();
        foreach (var from in Enum.GetValues<RoomLifecycleState>())
        {
            foreach (var action in actions)
            {
                if (RoomLifecycleTransitions.CanTransition(from, action, out var to)
                    && to == RoomLifecycleState.Destroyed)
                {
                    Assert.True(from == RoomLifecycleState.Closing, $"{from} --{action}--> Destroyed 绕过 Closing");
                }
            }
        }

        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Created, RoomLifecycleAction.Destroyed, out _));
    }

    [Fact]
    public void EveryState_CanClose()
    {
        // 每个非终态都能进 Closing（或本身已是 Closing），确保任何生命周期节点都有收敛路径。
        foreach (var state in Enum.GetValues<RoomLifecycleState>())
        {
            if (RoomLifecycleTransitions.IsTerminal(state) || state == RoomLifecycleState.Closing)
            {
                continue;
            }

            Assert.True(RoomLifecycleTransitions.CanTransition(state, RoomLifecycleAction.Close, out _), $"{state} 不能 Close");
        }
    }

    [Fact]
    public void Created_NoStreamer_CannotBeDestroyed()
    {
        // 约束①：没有主播不等于 Room 可以销毁——Created（从未开播）也只能走 Closing，
        // 不能一步销毁。
        Assert.False(RoomLifecycleTransitions.CanTransition(RoomLifecycleState.Created, RoomLifecycleAction.Destroyed, out var to));
    }

    [Fact]
    public void Registry_TryCreate_NewRoom_StartsInCreated()
    {
        var registry = new RoomLifecycleRegistry();

        var ok = registry.TryCreate("room-1", out var state);

        Assert.True(ok);
        Assert.Equal(RoomLifecycleState.Created, state);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Registry_TryCreate_Duplicate_FailsFailVisible()
    {
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);

        var ok = registry.TryCreate("room-1", out var state);

        Assert.False(ok);
        Assert.Equal(RoomLifecycleState.Created, state);
    }

    [Fact]
    public void Registry_TryApply_ValidTransition_UpdatesState()
    {
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);

        var ok = registry.TryApply("room-1", RoomLifecycleAction.StartWaiting, out var state);

        Assert.True(ok);
        Assert.Equal(RoomLifecycleState.Waiting, state);
    }

    [Fact]
    public void Registry_TryApply_IllegalTransition_FailsFailVisible()
    {
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);

        var ok = registry.TryApply("room-1", RoomLifecycleAction.Activate, out var state);

        // 状态不变，非法跃迁对调用方可见。
        Assert.False(ok);
        Assert.Equal(RoomLifecycleState.Created, state);
        Assert.Equal(RoomLifecycleState.Created, registry.TryGet("room-1")!.State);
    }

    [Fact]
    public void Registry_TryApply_UnknownRoom_FailsFailVisible()
    {
        var registry = new RoomLifecycleRegistry();

        var ok = registry.TryApply("no-such-room", RoomLifecycleAction.StartWaiting, out var state);

        Assert.False(ok);
        Assert.Equal(RoomLifecycleState.Created, state);
    }

    [Fact]
    public void Registry_TryGet_UnknownRoom_ReturnsNull()
    {
        var registry = new RoomLifecycleRegistry();

        Assert.Null(registry.TryGet("no-such-room"));
    }

    [Fact]
    public void Registry_Snapshot_ListsRoomStates()
    {
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);
        registry.TryCreate("room-2", out _);
        registry.TryApply("room-2", RoomLifecycleAction.StartWaiting, out _);
        registry.TryApply("room-2", RoomLifecycleAction.Activate, out _);

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, r => r.RoomId == "room-1" && r.State == RoomLifecycleState.Created);
        Assert.Contains(snapshot, r => r.RoomId == "room-2" && r.State == RoomLifecycleState.Active);
    }

    [Fact]
    public void Registry_CountByState_SumsToRoomCount()
    {
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);
        registry.TryCreate("room-2", out _);
        registry.TryApply("room-2", RoomLifecycleAction.StartWaiting, out _);

        var counts = registry.CountByState();

        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts[RoomLifecycleState.Created]);
        Assert.Equal(1, counts[RoomLifecycleState.Waiting]);
        Assert.Equal(2, counts.Values.Sum());
    }

    [Fact]
    public void Registry_ConcurrentApply_StaysInLegalState()
    {
        // 并发推进同一房间：任意时刻状态都必须是可到达的合法状态，不能撕裂。
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);

        Parallel.For(0, 64, i => registry.TryApply("room-1", RoomLifecycleAction.StartWaiting, out _));

        Assert.Equal(RoomLifecycleState.Waiting, registry.TryGet("room-1")!.State);
    }

    [Fact]
    public void Registry_TryForget_NonTerminal_FailsFailVisible()
    {
        // 约束②：SessionRegistry 的容量淘汰不能等同业务房间销毁——
        // 非终态房间不能从生命周期表移除（无绕过 Closing 的出口）。
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);
        registry.TryApply("room-1", RoomLifecycleAction.StartWaiting, out _);

        var ok = registry.TryForget("room-1");

        Assert.False(ok);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Registry_TryForget_DestroyedRoom_RemovesTerminalRecord()
    {
        var registry = new RoomLifecycleRegistry();
        registry.TryCreate("room-1", out _);
        Assert.True(registry.TryApply("room-1", RoomLifecycleAction.Close, out _));
        Assert.True(registry.TryApply("room-1", RoomLifecycleAction.Destroyed, out _));

        Assert.True(registry.TryForget("room-1"));
        Assert.Equal(0, registry.Count);
        Assert.Null(registry.TryGet("room-1"));
    }

    [Fact]
    public void Registry_TryForget_UnknownRoom_FailsFailVisible()
    {
        var registry = new RoomLifecycleRegistry();

        Assert.False(registry.TryForget("no-such-room"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Registry_TryCreate_InvalidRoomId_Throws(string? roomId)
    {
        var registry = new RoomLifecycleRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.TryCreate(roomId!, out _));
    }
}
