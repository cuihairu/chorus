using System.Collections.Concurrent;

namespace Chorus.Engine.Routing;

/// <summary>Room 权威状态：Stable（有唯一写者）/ Migrating（两阶段接管进行中，无人可写）。</summary>
public enum RoomAuthorityState
{
    Stable = 0,
    Migrating = 1,
}

/// <summary>Room 的权威记录：当前持有者、Epoch（路由版本）、状态。</summary>
public sealed record RoomAuthority(
    string RoomId,
    string WorkerId,
    long Epoch,
    RoomAuthorityState State);

/// <summary>
/// Room 权威与 Epoch 注册表（审计 §11「同一 Room 同一时刻只有一个有效权威写入者」）：
/// 两阶段接管——Begin 冻结旧写者（旧写者停止的明确机制），新 Worker 完成恢复后
/// Complete 换主并将 Epoch 加一，恢复失败 Abort 回滚（不伪装接管成功）。
///
/// <list type="bullet">
/// <item>唯一权威：只有 Stable 状态下的持有者是权威写入者；Migrating 期间人人无权威
/// （新 Worker 未完成恢复不接权威输入）。</item>
/// <item>过期路由识别：Epoch 对不上即过期——旧 epoch 的接管/路由请求 fail-visible 拒绝
/// （竞态迟到的旧接管不会得逞）。</item>
/// <item>接管必须两阶段：未 Begin 直接 Complete（单方面夺权）被拒。</item>
/// <item>重复注册 fail-visible，不隐式换主；注册无驱逐（终态清理走 TryForget）。</item>
/// </list>
/// 本表是控制面的权威决策点；与快照（SnapshotStore 的版本单调 + 完整性校验）、
/// 事件恢复策略分别验收（§11.2）。生产接线随阶段三多 worker 落地（等运行期门控）。
/// </summary>
public sealed class RoomAuthorityRegistry
{
    private readonly ConcurrentDictionary<string, RoomAuthority> _rooms = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the first authoritative owner of a room (epoch 1, stable).
    /// False when the room is already registered - ownership is never silently
    /// reassigned.
    /// </summary>
    public bool TryRegister(string roomId, string workerId)
    {
        ValidateRoomId(roomId);
        ValidateWorkerId(workerId);

        return _rooms.TryAdd(roomId, new RoomAuthority(roomId, workerId, Epoch: 1, RoomAuthorityState.Stable));
    }

    /// <summary>Current authority record, or null for an unknown room.</summary>
    public RoomAuthority? TryGet(string? roomId) =>
        !string.IsNullOrWhiteSpace(roomId) && _rooms.TryGetValue(roomId, out var authority) ? authority : null;

    /// <summary>
    /// Guarantees 1/2/3: only the stable owner is authoritative; during a
    /// takeover nobody is (including the incoming worker).
    /// </summary>
    public bool IsAuthoritative(string roomId, string workerId)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(workerId))
        {
            return false;
        }

        return _rooms.TryGetValue(roomId, out var authority)
            && authority.State == RoomAuthorityState.Stable
            && authority.WorkerId == workerId;
    }

    /// <summary>Guarantee 4: a route with this epoch is still current.</summary>
    public bool IsRouteCurrent(string roomId, long epoch)
    {
        if (string.IsNullOrWhiteSpace(roomId) || epoch < 1)
        {
            return false;
        }

        return _rooms.TryGetValue(roomId, out var authority) && authority.Epoch == epoch;
    }

    /// <summary>
    /// Phase 1: freezes the current writer (old worker stops being
    /// authoritative). Idempotent for a takeover already in flight at the same
    /// epoch; stale epochs are rejected.
    /// </summary>
    public bool TryBeginTakeover(string roomId, long expectedEpoch)
    {
        ValidateRoomId(roomId);
        if (expectedEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedEpoch), expectedEpoch, "epoch 从 1 开始");
        }

        return Mutate(roomId, authority =>
        {
            if (authority.Epoch != expectedEpoch)
            {
                return null; // 过期请求：fail-visible。
            }

            return authority.State == RoomAuthorityState.Migrating
                ? authority // 幂等重试。
                : authority with { State = RoomAuthorityState.Migrating };
        });
    }

    /// <summary>
    /// Phase 2: the new worker finished restoring - transfer authority and bump
    /// the epoch. Requires an in-flight takeover at the expected epoch; the old
    /// owner loses authority permanently for this room.
    /// </summary>
    public bool TryCompleteTakeover(string roomId, long expectedEpoch, string newWorkerId)
    {
        ValidateRoomId(roomId);
        ValidateWorkerId(newWorkerId);
        if (expectedEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedEpoch), expectedEpoch, "epoch 从 1 开始");
        }

        return Mutate(roomId, authority =>
        {
            if (authority.Epoch != expectedEpoch || authority.State != RoomAuthorityState.Migrating)
            {
                return null;
            }

            return authority with
            {
                WorkerId = newWorkerId,
                Epoch = authority.Epoch + 1,
                State = RoomAuthorityState.Stable,
            };
        });
    }

    /// <summary>
    /// Guarantee 7: a failed restore must not fake a successful takeover - abort
    /// returns the room to the previous owner at the same epoch.
    /// </summary>
    public bool TryAbortTakeover(string roomId, long expectedEpoch)
    {
        ValidateRoomId(roomId);
        if (expectedEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedEpoch), expectedEpoch, "epoch 从 1 开始");
        }

        return Mutate(roomId, authority =>
        {
            if (authority.Epoch != expectedEpoch || authority.State != RoomAuthorityState.Migrating)
            {
                return null;
            }

            return authority with { State = RoomAuthorityState.Stable };
        });
    }

    /// <summary>Removes a room (terminal cleanup). False when unknown.</summary>
    public bool TryForget(string roomId)
    {
        ValidateRoomId(roomId);
        return _rooms.TryRemove(roomId, out _);
    }

    /// <summary>CAS-style mutation; the mutator returns the new record or null to reject.</summary>
    private bool Mutate(string roomId, Func<RoomAuthority, RoomAuthority?> mutator)
    {
        while (true)
        {
            if (!_rooms.TryGetValue(roomId, out var current))
            {
                return false;
            }

            var next = mutator(current);
            if (next is null)
            {
                return false;
            }

            if (ReferenceEquals(next, current) || _rooms.TryUpdate(roomId, next, current))
            {
                return true;
            }
        }
    }

    private static void ValidateRoomId(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("room id 不能为空", nameof(roomId));
        }
    }

    private static void ValidateWorkerId(string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("worker id 不能为空", nameof(workerId));
        }
    }
}
