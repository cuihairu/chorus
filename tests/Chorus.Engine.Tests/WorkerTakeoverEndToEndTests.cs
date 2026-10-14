using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Worker 故障接管端到端测试（todo §18）：控制面故障接管编排跨两表联动——
/// WorkerRegistry 健康观察（心跳过期仅从健康集排除，摘除是显式决策）驱动接管决策，
/// RoomAuthorityRegistry 两阶段接管落地权威切换。覆盖：旧写者被冻结、换主 Epoch+1、
/// 接管中止回滚（不伪装接管成功）、重复/迟到接管 fail-visible、换主后旧 epoch 路由被拒、
/// 故障恢复的旧写者不再自动获得权威。时间由测试注入，不读墙钟；进程内无控制器接线
/// （生产接线随阶段三多 worker，运行期数据门控），本表验收两表编排契约。
/// </summary>
public sealed class WorkerTakeoverEndToEndTests
{
    private const long StartMs = 1_000_000;
    private const int StaleAfterMs = 30_000;

    private static WorkerRegistry NewWorkerRegistry() => new(StaleAfterMs);

    private static RoomAuthorityRegistry NewAuthorityRegistry(string roomId = "room-1", string workerId = "worker-a")
    {
        var registry = new RoomAuthorityRegistry();
        registry.TryRegister(roomId, workerId);
        return registry;
    }

    [Fact]
    public void StaleWorker_TakeoverFreezesOldWriterThenTransfersWithEpochBump()
    {
        var workers = NewWorkerRegistry();
        var authority = NewAuthorityRegistry();
        workers.Register("worker-a", nowMs: StartMs);
        workers.Register("worker-b", nowMs: StartMs);

        // worker-a 心跳过期：被健康集排除——控制面观察到故障（仅观察，不自动摘除）。
        Assert.DoesNotContain(workers.Healthy(StartMs + 40_000), w => w.WorkerId == "worker-a");

        // 阶段一：冻结旧写者——旧 Worker 停止权威写入的明确机制。
        Assert.True(authority.TryBeginTakeover("room-1", expectedEpoch: 1));
        Assert.False(authority.IsAuthoritative("room-1", "worker-a"));

        // 迁移期间人人无权威：新 Worker 未完成恢复前不接权威输入。
        Assert.False(authority.IsAuthoritative("room-1", "worker-b"));

        // 阶段二：新 Worker 恢复完成——换主且 Epoch+1，旧写者彻底出局。
        Assert.True(authority.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b"));

        var record = authority.TryGet("room-1")!;
        Assert.Equal(2, record.Epoch);
        Assert.Equal("worker-b", record.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, record.State);
        Assert.True(authority.IsAuthoritative("room-1", "worker-b"));
        Assert.False(authority.IsAuthoritative("room-1", "worker-a"));
    }

    [Fact]
    public void FailedRestore_AbortTakeoverRollsBackToOldWriterWithoutFakingSuccess()
    {
        var authority = NewAuthorityRegistry();
        authority.TryBeginTakeover("room-1", expectedEpoch: 1);

        // 新 Worker 恢复失败：中止回滚，绝不伪装成接管成功。
        Assert.True(authority.TryAbortTakeover("room-1", expectedEpoch: 1));

        var record = authority.TryGet("room-1")!;
        Assert.Equal(1, record.Epoch);
        Assert.Equal("worker-a", record.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, record.State);
        Assert.True(authority.IsAuthoritative("room-1", "worker-a"));

        // 回滚不动 epoch：旧写者的旧路由仍然有效。
        Assert.True(authority.IsRouteCurrent("room-1", epoch: 1));
    }

    [Fact]
    public void AbortedTakeover_CanRestartAtSameEpoch()
    {
        var authority = NewAuthorityRegistry();
        authority.TryBeginTakeover("room-1", expectedEpoch: 1);
        authority.TryAbortTakeover("room-1", expectedEpoch: 1);

        // 回滚清除了 Migrating：同一 epoch 可再次发起接管，权威最终正常换主。
        Assert.True(authority.TryBeginTakeover("room-1", expectedEpoch: 1));
        Assert.True(authority.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-c"));

        var record = authority.TryGet("room-1")!;
        Assert.Equal(2, record.Epoch);
        Assert.Equal("worker-c", record.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, record.State);
    }

    [Fact]
    public void CompletedTakeover_DuplicateTakeoverAttemptsFailVisible()
    {
        var authority = NewAuthorityRegistry();
        authority.TryBeginTakeover("room-1", expectedEpoch: 1);
        authority.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b");

        // 迟到的旧 epoch 重复接管：Begin 与 Complete 均被拒，权威不动。
        Assert.False(authority.TryBeginTakeover("room-1", expectedEpoch: 1));
        Assert.False(authority.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-c"));

        // 重复完成（同 epoch 二次 Complete）：房间已 Stable，非两阶段夺权被拒。
        Assert.False(authority.TryCompleteTakeover("room-1", expectedEpoch: 2, newWorkerId: "worker-c"));

        var record = authority.TryGet("room-1")!;
        Assert.Equal(2, record.Epoch);
        Assert.Equal("worker-b", record.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, record.State);
    }

    [Fact]
    public void CompletedTakeover_LateRouteWithOldEpochIsRejected()
    {
        var authority = NewAuthorityRegistry();
        var epochBeforeTakeover = authority.TryGet("room-1")!.Epoch;

        // 接管前：该 epoch 路由是当前版本。
        Assert.True(authority.IsRouteCurrent("room-1", epochBeforeTakeover));

        authority.TryBeginTakeover("room-1", expectedEpoch: 1);

        // 迁移期间 epoch 未推进：旧路由仍是当前版本，但权威检查已拒绝任何写入
        // （含旧写者）——冻结靠状态判定，路由版本只在换主时推进。
        Assert.True(authority.IsRouteCurrent("room-1", epochBeforeTakeover));
        Assert.False(authority.IsAuthoritative("room-1", "worker-a"));

        authority.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b");

        // 换主后：旧 epoch 路由一律过期被拒；新 epoch 路由生效。
        Assert.False(authority.IsRouteCurrent("room-1", epochBeforeTakeover));
        Assert.True(authority.IsRouteCurrent("room-1", epochBeforeTakeover + 1));
        Assert.False(authority.IsRouteCurrent("room-1", epochBeforeTakeover + 2));
    }

    [Fact]
    public void RecoveredOldWorker_HealthyAgainButNeverRegainsAuthority()
    {
        var workers = NewWorkerRegistry();
        var authority = NewAuthorityRegistry();
        workers.Register("worker-a", nowMs: StartMs);

        // 故障：心跳过期，健康集排除。
        Assert.DoesNotContain(workers.Healthy(StartMs + 40_000), w => w.WorkerId == "worker-a");

        // 控制面接管换主。
        Assert.True(authority.TryBeginTakeover("room-1", expectedEpoch: 1));
        Assert.True(authority.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b"));

        // 旧写者恢复上线：重新回到健康集，但权威不自动回授——不隐式换主。
        Assert.True(workers.Heartbeat("worker-a", StartMs + 50_000));
        Assert.True(workers.IsHealthy("worker-a", nowMs: StartMs + 51_000));
        Assert.Contains(workers.Healthy(StartMs + 51_000), w => w.WorkerId == "worker-a");

        Assert.False(authority.IsAuthoritative("room-1", "worker-a"));
        Assert.False(authority.TryRegister("room-1", "worker-a"));
        Assert.Equal("worker-b", authority.TryGet("room-1")!.WorkerId);
    }
}
