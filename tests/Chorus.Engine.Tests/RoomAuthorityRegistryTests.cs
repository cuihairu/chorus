using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Room 权威/Epoch 注册表金测（todo §18 Epoch 项，契约 = 审计 §11.1 七保证中的
/// 1/2/3/4/7）：同一 Room 同一时刻只有一个有效权威写入者；两阶段接管
/// （Begin 冻结旧写者 → 新 Worker 恢复 → Complete 换主加 Epoch）；
/// 迁移期间无人可写（新 Worker 未完成恢复不接权威输入）；
/// 过期路由可识别（epoch 对不上即过期）；恢复失败回滚不伪装接管成功。
/// 事件恢复策略（保证 5）与快照校验（保证 6，已由 SnapshotStore 落地）不在本表。
/// </summary>
public sealed class RoomAuthorityRegistryTests
{
    private static RoomAuthorityRegistry NewRegistry(string roomId = "room-1", string workerId = "worker-a")
    {
        var registry = new RoomAuthorityRegistry();
        registry.TryRegister(roomId, workerId);
        return registry;
    }

    [Fact]
    public void TryRegister_NewRoom_StartsAtEpochOneStable()
    {
        var registry = new RoomAuthorityRegistry();

        Assert.True(registry.TryRegister("room-1", "worker-a"));

        var authority = registry.TryGet("room-1")!;
        Assert.Equal(1, authority.Epoch);
        Assert.Equal("worker-a", authority.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, authority.State);
    }

    [Fact]
    public void TryRegister_DuplicateRoom_FailsVisibleWithoutOverwrite()
    {
        var registry = NewRegistry();

        // 不隐式换主：重复注册 = 冲突，false 且旧权威不变。
        Assert.False(registry.TryRegister("room-1", "worker-b"));
        Assert.Equal("worker-a", registry.TryGet("room-1")!.WorkerId);
    }

    [Fact]
    public void TryGet_UnknownRoom_ReturnsNull()
    {
        Assert.Null(new RoomAuthorityRegistry().TryGet("ghost"));
    }

    [Fact]
    public void IsAuthoritative_OnlyOwnerHoldsAuthority()
    {
        var registry = NewRegistry();

        // 保证 1：同一 Room 同一时刻只有一个有效权威写入者。
        Assert.True(registry.IsAuthoritative("room-1", "worker-a"));
        Assert.False(registry.IsAuthoritative("room-1", "worker-b"));
    }

    [Fact]
    public void IsAuthoritative_UnknownRoom_FailsClosed()
    {
        var registry = new RoomAuthorityRegistry();

        Assert.False(registry.IsAuthoritative("ghost", "worker-a"));
    }

    [Fact]
    public void BeginTakeover_FreezesOldWriter()
    {
        var registry = NewRegistry();

        Assert.True(registry.TryBeginTakeover("room-1", expectedEpoch: 1));

        // 保证 2：旧 Worker 停止权威写入的明确机制——冻结后旧写者不再权威。
        Assert.False(registry.IsAuthoritative("room-1", "worker-a"));
        Assert.Equal(RoomAuthorityState.Migrating, registry.TryGet("room-1")!.State);
    }

    [Fact]
    public void BeginTakeover_SameEpochRetry_IsIdempotent()
    {
        var registry = NewRegistry();
        registry.TryBeginTakeover("room-1", expectedEpoch: 1);

        // 同一接管重试（如消息重发）：幂等成功，不报冲突。
        Assert.True(registry.TryBeginTakeover("room-1", expectedEpoch: 1));
        Assert.Equal(RoomAuthorityState.Migrating, registry.TryGet("room-1")!.State);
    }

    [Fact]
    public void BeginTakeover_StaleEpoch_Rejected()
    {
        var registry = NewRegistry(workerId: "worker-a");
        registry.TryRegister("room-1", "worker-a");

        // 拿旧 epoch 发起接管 = 过期请求：fail-visible 拒绝，状态不变。
        Assert.False(registry.TryBeginTakeover("room-1", expectedEpoch: 2));
        Assert.Equal(RoomAuthorityState.Stable, registry.TryGet("room-1")!.State);
    }

    [Fact]
    public void BeginTakeover_UnknownRoom_Rejected()
    {
        var registry = new RoomAuthorityRegistry();

        Assert.False(registry.TryBeginTakeover("ghost", expectedEpoch: 1));
    }

    [Fact]
    public void DuringMigration_NobodyIsAuthoritative()
    {
        var registry = NewRegistry();
        registry.TryBeginTakeover("room-1", expectedEpoch: 1);

        // 保证 3：新 Worker 不能在状态恢复完成前接收权威输入——迁移期间人人无权威。
        Assert.False(registry.IsAuthoritative("room-1", "worker-a"));
        Assert.False(registry.IsAuthoritative("room-1", "worker-b"));
    }

    [Fact]
    public void CompleteTakeover_TransfersAuthorityWithEpochBump()
    {
        var registry = NewRegistry();
        registry.TryBeginTakeover("room-1", expectedEpoch: 1);

        Assert.True(registry.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b"));

        var authority = registry.TryGet("room-1")!;
        Assert.Equal(2, authority.Epoch);
        Assert.Equal("worker-b", authority.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, authority.State);

        // 保证 1（接管后）：只有新写者权威，旧写者彻底出局。
        Assert.True(registry.IsAuthoritative("room-1", "worker-b"));
        Assert.False(registry.IsAuthoritative("room-1", "worker-a"));
    }

    [Fact]
    public void CompleteTakeover_MakesOldRoutesStale()
    {
        var registry = NewRegistry();
        registry.TryBeginTakeover("room-1", expectedEpoch: 1);
        registry.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b");

        // 保证 4：路由版本必须能识别过期路由——旧 epoch 的路由一律过期。
        Assert.False(registry.IsRouteCurrent("room-1", epoch: 1));
        Assert.True(registry.IsRouteCurrent("room-1", epoch: 2));
    }

    [Fact]
    public void CompleteTakeover_WithoutBegin_Rejected()
    {
        var registry = NewRegistry();

        // 跳过冻结直接换主 = 单方面夺权：必须走两阶段，拒绝。
        Assert.False(registry.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b"));
        Assert.Equal("worker-a", registry.TryGet("room-1")!.WorkerId);
        Assert.Equal(1, registry.TryGet("room-1")!.Epoch);
    }

    [Fact]
    public void CompleteTakeover_StaleEpoch_Rejected()
    {
        var registry = NewRegistry();
        registry.TryBeginTakeover("room-1", expectedEpoch: 1);
        registry.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-b");

        // 竞态：拿 epoch 1 的旧接管请求迟到了——epoch 已到 2，拒绝。
        Assert.False(registry.TryCompleteTakeover("room-1", expectedEpoch: 1, newWorkerId: "worker-c"));
        Assert.Equal("worker-b", registry.TryGet("room-1")!.WorkerId);
    }

    [Fact]
    public void AbortTakeover_RestoresOldWriterWithoutEpochBump()
    {
        var registry = NewRegistry();
        registry.TryBeginTakeover("room-1", expectedEpoch: 1);

        // 保证 7：恢复失败不能伪装成接管成功——中止回到旧写者，epoch 不动。
        Assert.True(registry.TryAbortTakeover("room-1", expectedEpoch: 1));

        var authority = registry.TryGet("room-1")!;
        Assert.Equal(1, authority.Epoch);
        Assert.Equal("worker-a", authority.WorkerId);
        Assert.Equal(RoomAuthorityState.Stable, authority.State);
        Assert.True(registry.IsAuthoritative("room-1", "worker-a"));
    }

    [Fact]
    public void AbortTakeover_WithoutBegin_Rejected()
    {
        var registry = NewRegistry();

        Assert.False(registry.TryAbortTakeover("room-1", expectedEpoch: 1));
        Assert.Equal(RoomAuthorityState.Stable, registry.TryGet("room-1")!.State);
    }

    [Fact]
    public void TryForget_RemovesRoom()
    {
        var registry = NewRegistry();

        Assert.True(registry.TryForget("room-1"));
        Assert.Null(registry.TryGet("room-1"));
        Assert.False(registry.IsAuthoritative("room-1", "worker-a"));
        Assert.False(registry.TryForget("room-1"));
    }

    [Fact]
    public void ConcurrentRegister_ExactlyOneWins()
    {
        var registry = new RoomAuthorityRegistry();
        var winners = 0;

        Parallel.For(0, 16, _ =>
        {
            if (registry.TryRegister("room-1", "worker-a"))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TryRegister_BlankIds_Throw(string? id)
    {
        var registry = new RoomAuthorityRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.TryRegister(id!, "worker-a"));
        Assert.ThrowsAny<ArgumentException>(() => registry.TryRegister("room-1", id!));
    }
}
