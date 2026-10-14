namespace Chorus.Engine.Routing;

/// <summary>Lifecycle states of a business Room (todo §14 control-plane item, contract =
/// 总体架构审计 §5.1 生命周期约束).</summary>
public enum RoomLifecycleState
{
    /// <summary>房间已登记，等待主播接入（从未开播）。</summary>
    Created = 0,

    /// <summary>主播已接入，等待首个观众/游戏开始。</summary>
    Waiting = 1,

    /// <summary>有观众且游戏运行中。</summary>
    Active = 2,

    /// <summary>无活跃观众（主播可能仍在），权威状态保留。</summary>
    Idle = 3,

    /// <summary>已休眠，权威状态保留（持久化门控属 §15），无计算。</summary>
    Sleeping = 4,

    /// <summary>迁往另一 Worker，权威状态与待处理事件必须保留。</summary>
    Migrating = 5,

    /// <summary>恢复中（从快照/事件补齐），未就绪不投递。</summary>
    Recovering = 6,

    /// <summary>关闭中（排空连接/落盘快照）。</summary>
    Closing = 7,

    /// <summary>已销毁（终态，只能从 Closing 到达）。</summary>
    Destroyed = 8,
}

/// <summary>Actions that move a Room between lifecycle states.</summary>
public enum RoomLifecycleAction
{
    /// <summary>主播接入/创建完成。</summary>
    StartWaiting = 0,

    /// <summary>首个观众/游戏开始。</summary>
    Activate = 1,

    /// <summary>观众清空。</summary>
    GoIdle = 2,

    /// <summary>空闲超时。</summary>
    Sleep = 3,

    /// <summary>唤醒（从休眠恢复，先经 Recovering）。</summary>
    Resume = 4,

    /// <summary>开始迁移到另一 Worker。</summary>
    BeginMigration = 5,

    /// <summary>迁移完成，目标 Worker 开始恢复。</summary>
    BeginRecovery = 6,

    /// <summary>恢复完成，可以投递。</summary>
    Recovered = 7,

    /// <summary>业务条件满足，开始关闭。</summary>
    Close = 8,

    /// <summary>关闭完成，销毁房间。</summary>
    Destroyed = 9,
}

/// <summary>
/// Pure lifecycle transition table (todo §14 Room 生命周期状态机). Illegal
/// transitions fail visibly (caller decides what to do), matching the audit
/// §5.1 constraints: no streamer ≠ destroyable (Created only reaches
/// Closing), capacity eviction ≠ destruction (this table is the only path to
/// Destroyed, and it runs through Closing; <see cref="RoomLifecycleRegistry"/>
/// exposes no eviction API), sleep ≠ state discard (Sleeping is not terminal
/// and resumes through Recovering), migration preserves state and pending
/// events (Migrating must pass through Recovering), destruction requires an
/// explicit business condition (Destroyed is only reachable from Closing).
/// </summary>
public static class RoomLifecycleTransitions
{
    private static readonly Dictionary<(RoomLifecycleState From, RoomLifecycleAction Action), RoomLifecycleState> Table =
        new()
        {
            [(RoomLifecycleState.Created, RoomLifecycleAction.StartWaiting)] = RoomLifecycleState.Waiting,
            [(RoomLifecycleState.Created, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Waiting, RoomLifecycleAction.Activate)] = RoomLifecycleState.Active,
            [(RoomLifecycleState.Waiting, RoomLifecycleAction.GoIdle)] = RoomLifecycleState.Idle,
            [(RoomLifecycleState.Waiting, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Active, RoomLifecycleAction.GoIdle)] = RoomLifecycleState.Idle,
            [(RoomLifecycleState.Active, RoomLifecycleAction.BeginMigration)] = RoomLifecycleState.Migrating,
            [(RoomLifecycleState.Active, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Idle, RoomLifecycleAction.Activate)] = RoomLifecycleState.Active,
            [(RoomLifecycleState.Idle, RoomLifecycleAction.Sleep)] = RoomLifecycleState.Sleeping,
            [(RoomLifecycleState.Idle, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Sleeping, RoomLifecycleAction.Resume)] = RoomLifecycleState.Recovering,
            [(RoomLifecycleState.Sleeping, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Migrating, RoomLifecycleAction.BeginRecovery)] = RoomLifecycleState.Recovering,
            [(RoomLifecycleState.Migrating, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Recovering, RoomLifecycleAction.Recovered)] = RoomLifecycleState.Active,
            [(RoomLifecycleState.Recovering, RoomLifecycleAction.Close)] = RoomLifecycleState.Closing,

            [(RoomLifecycleState.Closing, RoomLifecycleAction.Destroyed)] = RoomLifecycleState.Destroyed,
        };

    /// <summary>The next state for a legal transition, or false for an illegal one.</summary>
    public static bool CanTransition(RoomLifecycleState from, RoomLifecycleAction action, out RoomLifecycleState to)
        => Table.TryGetValue((from, action), out to);

    /// <summary>Whether the state is terminal (Destroyed — no outgoing transitions).</summary>
    public static bool IsTerminal(RoomLifecycleState state)
        => state == RoomLifecycleState.Destroyed;

    /// <summary>The actions currently legal from a state (for callers building menus/APIs).</summary>
    public static IReadOnlySet<RoomLifecycleAction> AllowedActions(RoomLifecycleState from)
    {
        var result = new HashSet<RoomLifecycleAction>();
        foreach (var entry in Table)
        {
            if (entry.Key.From == from)
            {
                result.Add(entry.Key.Action);
            }
        }

        return result;
    }
}

/// <summary>One room's current lifecycle position.</summary>
/// <param name="RoomId">Room id (same id as the SessionRegistry directory).</param>
/// <param name="State">Current lifecycle state.</param>
public sealed record RoomLifecycleEntry(string RoomId, RoomLifecycleState State);

/// <summary>
/// Per-room lifecycle state holder (todo §14 控制面). Single-writer semantics
/// per room: all transitions apply through the registry so concurrent callers
/// can never tear a state. Rooms enter via <see cref="TryCreate"/> (Created) and
/// leave only via Closing → Destroyed, matching the audit §5.1 constraints —
/// there is deliberately no eviction API: <c>SessionRegistry</c>'s capacity
/// eviction stays a cache concern and never implies business destruction.
/// <see cref="TryForget"/> removes only terminal records so the table stays
/// bounded. In-memory, process-local: persistence lands with §15.
/// </summary>
public sealed class RoomLifecycleRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RoomLifecycleState> _states = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    /// <summary>Register a room in Created state; fails visibly on duplicate.</summary>
    public bool TryCreate(string roomId, out RoomLifecycleState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        lock (_gate)
        {
            if (_states.ContainsKey(roomId))
            {
                state = RoomLifecycleState.Created;
                return false;
            }

            _states[roomId] = RoomLifecycleState.Created;
            state = RoomLifecycleState.Created;
            return true;
        }
    }

    /// <summary>The room's current state, or null when unknown.</summary>
    public RoomLifecycleEntry? TryGet(string roomId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(roomId, out var state) ? new RoomLifecycleEntry(roomId, state) : null;
        }
    }

    /// <summary>Apply a transition; illegal transitions fail visibly and change nothing.</summary>
    public bool TryApply(string roomId, RoomLifecycleAction action, out RoomLifecycleState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        lock (_gate)
        {
            if (!_states.TryGetValue(roomId, out var from))
            {
                state = RoomLifecycleState.Created;
                return false;
            }

            if (!RoomLifecycleTransitions.CanTransition(from, action, out var to))
            {
                state = from;
                return false;
            }

            _states[roomId] = to;
            state = to;
            return true;
        }
    }

    /// <summary>Remove a terminal (Destroyed) record; non-terminal rooms cannot be removed.</summary>
    public bool TryForget(string roomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        lock (_gate)
        {
            if (_states.TryGetValue(roomId, out var state) && RoomLifecycleTransitions.IsTerminal(state))
            {
                return _states.Remove(roomId);
            }

            return false;
        }
    }

    /// <summary>A consistent view of all room states, for diagnostics.</summary>
    public IReadOnlyList<RoomLifecycleEntry> Snapshot()
    {
        lock (_gate)
        {
            return _states.Select(pair => new RoomLifecycleEntry(pair.Key, pair.Value)).ToArray();
        }
    }

    /// <summary>Rooms per lifecycle state, for metrics/dashboards.</summary>
    public IReadOnlyDictionary<RoomLifecycleState, int> CountByState()
    {
        lock (_gate)
        {
            return _states.Values
                .GroupBy(state => state)
                .ToDictionary(group => group.Key, group => group.Count());
        }
    }
}
