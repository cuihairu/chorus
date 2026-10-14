namespace Chorus.Engine.Matchmaking;

/// <summary>
/// Default bot participant factory（审计 §6 BotFactory）。机器人不是虚构的主播账号，
/// 也不是伪造的 WebSocket 客户端——只是参与者层的游戏实体：ParticipantId 与 BotId
/// 都是进程内生成的确定性序号（规则 2：两者独立）。
/// </summary>
public sealed class DefaultBotParticipantFactory : IBotParticipantFactory
{
    private long _nextId;

    public IReadOnlyList<MatchParticipant> Create(int count, string requestId, long nowMs)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "机器人数量不能为负");
        }

        List<MatchParticipant> bots = new(count);
        for (var i = 0; i < count; i++)
        {
            var botId = $"bot-{Interlocked.Increment(ref _nextId):D6}";
            bots.Add(new MatchParticipant($"{botId}-p", IsBot: true, PlayerId: null, BotId: botId));
        }

        return bots;
    }
}

/// <summary>
/// 机器人动作（审计 §6.1 规则 4/5）：纯数据记录——不携带委托、回调或任何状态引用，
/// 策略层只能产出动作（规则 3），由统一输入处理流程应用。
/// </summary>
/// <param name="BotId">发出动作的机器人。</param>
/// <param name="ParticipantId">该机器人的参与者身份。</param>
/// <param name="TargetTick">规则 5：动作的显式目标 Tick。</param>
/// <param name="Sequence">bot 内单调序号（幂等锚点，bot 间独立）。</param>
/// <param name="ActionType">动作类型（如 <c>vote</c>）。</param>
/// <param name="Payload">动作参数（JSON，可空）。</param>
public sealed record BotAction(
    string BotId,
    string ParticipantId,
    long TargetTick,
    long Sequence,
    string ActionType,
    string? Payload);

/// <summary>
/// 统一输入队列的机器人侧入口（审计 §6.1 规则 4 + §7 输入流水线 Sequence 段）：
/// 机器人动作与平台事件、主播操作进入同一条确定性输入流。
///
/// <list type="bullet">
/// <item>确定性总序：TargetTick 升序、同 Tick 按 BotId 字典序、再按 bot 内 Sequence
/// ——排序只依赖动作字段，与提交顺序无关。</item>
/// <item>幂等：同 (BotId, Sequence) 是重放，fail-visible 拒绝且计入 DuplicateCount
/// （防策略重试导致的双发）。</item>
/// <item>有界：容量是硬上限，满载 fail-visible 拒绝并计数——策略层据此等待或放弃，
/// 动作绝不静默蒸发。</item>
/// </list>
/// </summary>
public sealed class BotActionQueue
{
    private readonly object _gate = new();
    private readonly List<BotAction> _pending = [];
    private readonly HashSet<(string BotId, long Sequence)> _seen = [];
    private readonly int _capacity;
    private long _rejectedFullCount;
    private long _duplicateCount;

    public BotActionQueue(int capacity = 4_096)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "队列容量必须为正");
        }

        _capacity = capacity;
    }

    /// <summary>当前排队动作数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>满载拒绝数（可观测）。</summary>
    public long RejectedFullCount
    {
        get
        {
            lock (_gate)
            {
                return _rejectedFullCount;
            }
        }
    }

    /// <summary>重放抑制数（可观测）。</summary>
    public long DuplicateCount
    {
        get
        {
            lock (_gate)
            {
                return _duplicateCount;
            }
        }
    }

    /// <summary>
    /// Submits an action. False = duplicate (BotId, Sequence) replay or the
    /// queue is full - both counted, neither silent.
    /// </summary>
    public bool TrySubmit(BotAction action)
    {
        if (string.IsNullOrWhiteSpace(action.BotId))
        {
            throw new ArgumentException("bot id 不能为空", nameof(BotAction.BotId));
        }

        if (string.IsNullOrWhiteSpace(action.ParticipantId))
        {
            throw new ArgumentException("participant id 不能为空", nameof(BotAction.ParticipantId));
        }

        if (string.IsNullOrWhiteSpace(action.ActionType))
        {
            throw new ArgumentException("action type 不能为空", nameof(BotAction.ActionType));
        }

        if (action.TargetTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action.TargetTick, "目标 Tick 不能为负");
        }

        lock (_gate)
        {
            if (_pending.Count >= _capacity)
            {
                _rejectedFullCount++;
                return false;
            }

            if (!_seen.Add((action.BotId, action.Sequence))
                || _pending.Any(a => a.BotId == action.BotId && a.Sequence == action.Sequence))
            {
                _duplicateCount++;
                return false;
            }

            _pending.Add(action);
            return true;
        }
    }

    /// <summary>
    /// Drains all pending actions in deterministic order
    /// (TargetTick, BotId, Sequence).
    /// </summary>
    public IReadOnlyList<BotAction> Drain()
    {
        lock (_gate)
        {
            var drained = Order(_pending);
            _pending.Clear();
            return drained;
        }
    }

    /// <summary>
    /// Drains only actions whose target tick is due (<c>&lt;= targetTick</c>),
    /// in deterministic order; later actions stay queued.
    /// </summary>
    public IReadOnlyList<BotAction> DrainUpTo(long targetTick)
    {
        lock (_gate)
        {
            var due = _pending.Where(a => a.TargetTick <= targetTick).ToList();
            var ordered = Order(due);
            foreach (var action in due)
            {
                _pending.Remove(action);
            }

            return ordered;
        }
    }

    private static List<BotAction> Order(IEnumerable<BotAction> actions) =>
        actions
            .OrderBy(a => a.TargetTick)
            .ThenBy(a => a.BotId, StringComparer.Ordinal)
            .ThenBy(a => a.Sequence)
            .ToList();
}
