using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Worker 注册与健康管理金测（todo §14 Worker 注册项，契约 = 总体架构审计 §5.2 Worker 分配）：
/// 心跳窗口健康判定、过期不自动移除（健康检测是观察，摘除是显式控制决策）、
/// worker 集合可直接喂给 RoomAffinity.Assign（阶段三代码半边，接线等运行期数据门控）。
/// </summary>
public sealed class WorkerRegistryTests
{
    private const long StartMs = 1_000_000;

    private static WorkerRegistry NewRegistry(int staleAfterMs = 30_000)
        => new(staleAfterMs);

    [Fact]
    public void Register_NewWorker_ShowsInSnapshot()
    {
        var registry = NewRegistry();

        registry.Register("worker-a", roles: "all", nowMs: StartMs);

        Assert.Equal(1, registry.Count);
        Assert.Contains(registry.Snapshot(), w => w.WorkerId == "worker-a" && w.Roles == "all");
    }

    [Fact]
    public void Register_SameWorkerTwice_ResetsHeartbeatWithoutCountChange()
    {
        var registry = NewRegistry();
        registry.Register("worker-a", nowMs: StartMs);

        registry.Register("worker-a", roles: "platform", nowMs: StartMs + 10_000);

        // 重注册 = 幂等 upsert（worker 重启场景），心跳重置、角色覆盖、计数不变。
        Assert.Equal(1, registry.Count);
        var worker = registry.Snapshot().Single(w => w.WorkerId == "worker-a");
        Assert.Equal(StartMs + 10_000, worker.LastHeartbeatMs);
        Assert.Equal("platform", worker.Roles);
    }

    [Fact]
    public void Heartbeat_UnknownWorker_FailsFailVisible()
    {
        var registry = NewRegistry();

        Assert.False(registry.Heartbeat("ghost", StartMs));
    }

    [Fact]
    public void Heartbeat_ExtendsHealthyWindow()
    {
        var registry = NewRegistry(staleAfterMs: 30_000);
        registry.Register("worker-a", nowMs: StartMs);

        // 30s 窗口内先报 20s 后心跳，49s 时依然健康（最后一跳在 20s）。
        Assert.True(registry.Heartbeat("worker-a", StartMs + 20_000));

        Assert.True(registry.IsHealthy("worker-a", nowMs: StartMs + 49_000));
        // 恰好满窗口按过期处理（fail-closed）。
        Assert.False(registry.IsHealthy("worker-a", nowMs: StartMs + 50_000));
    }

    [Fact]
    public void Healthy_ExcludesStaleWorkers_ButKeepsThemInSnapshot()
    {
        var registry = NewRegistry(staleAfterMs: 30_000);
        registry.Register("worker-a", nowMs: StartMs);
        registry.Register("worker-b", nowMs: StartMs);

        var healthy = registry.Healthy(nowMs: StartMs + 40_000);

        // worker-a 过期被健康集排除，但不自动移除——摘除是显式控制决策。
        Assert.Empty(healthy);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Remove_GracefulShutdown_RemovesFromAll()
    {
        var registry = NewRegistry();
        registry.Register("worker-a", nowMs: StartMs);

        Assert.True(registry.Remove("worker-a"));
        Assert.Equal(0, registry.Count);
        Assert.False(registry.Heartbeat("worker-a", StartMs + 1));
    }

    [Fact]
    public void Remove_UnknownWorker_FailsFailVisible()
    {
        var registry = NewRegistry();

        Assert.False(registry.Remove("ghost"));
    }

    [Fact]
    public void SortedWorkerIds_FeedsRoomAffinity()
    {
        var registry = NewRegistry();
        registry.Register("worker-b", nowMs: StartMs);
        registry.Register("worker-a", nowMs: StartMs);
        registry.Register("worker-c", nowMs: StartMs);

        var assigned = RoomAffinity.Assign("session-42", registry.SortedWorkerIds());

        Assert.Contains(assigned, registry.SortedWorkerIds());
    }

    [Fact]
    public void SortedWorkerIds_Empty_AllowsNull()
    {
        var registry = NewRegistry();

        Assert.Null(RoomAffinity.Assign("session-42", registry.SortedWorkerIds()));
    }

    [Fact]
    public void Snapshot_IsSortedById()
    {
        var registry = NewRegistry();
        registry.Register("worker-c", nowMs: StartMs);
        registry.Register("worker-a", nowMs: StartMs);

        var ids = registry.Snapshot().Select(w => w.WorkerId).ToArray();

        Assert.Equal(new[] { "worker-a", "worker-c" }, ids);
    }

    [Fact]
    public void ConcurrentHeartbeats_ThreadSafe()
    {
        var registry = NewRegistry();
        registry.Register("worker-a", nowMs: StartMs);

        Parallel.For(0, 64, i => registry.Heartbeat("worker-a", StartMs + i));

        // 心跳取最新值（向后单调），不会倒退。
        Assert.Equal(StartMs + 63, registry.Snapshot().Single(w => w.WorkerId == "worker-a").LastHeartbeatMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Register_InvalidWorkerId_Throws(string? workerId)
    {
        var registry = NewRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.Register(workerId!, nowMs: StartMs));
    }
}
