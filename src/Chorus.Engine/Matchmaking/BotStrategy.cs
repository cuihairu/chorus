namespace Chorus.Engine.Matchmaking;

/// <summary>
/// 机器人策略配置（审计 §6.1 规则 3 的配置半边）：声明式参数，不含任何可执行状态。
/// </summary>
public sealed record BotStrategyConfig
{
    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>();

    public BotStrategyConfig(
        string strategyId,
        int cooldownTicks = 0,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            throw new ArgumentException("strategy id 不能为空", nameof(StrategyId));
        }

        if (cooldownTicks < 0)
        {
            throw new ArgumentException("cooldown ticks 不能为负", nameof(CooldownTicks));
        }

        if (parameters is not null && parameters.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("参数键不能为空", nameof(Parameters));
        }

        StrategyId = strategyId;
        CooldownTicks = cooldownTicks;
        Parameters = parameters ?? EmptyParameters;
    }

    /// <summary>策略类型标识（如 <c>scripted</c>）。</summary>
    public string StrategyId { get; init; }

    /// <summary>两次动作之间的最小间隔 Tick（0 = 不限）。</summary>
    public int CooldownTicks { get; init; }

    /// <summary>策略私有参数（键值对）。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; }

    /// <summary>字符串参数；缺失返回 null。</summary>
    public string? GetString(string key) => Parameters.TryGetValue(key, out var value) ? value : null;

    /// <summary>整数参数；缺失或不可解析返回 0。</summary>
    public int GetInt(string key) => int.TryParse(GetString(key), out var value) ? value : 0;
}

/// <summary>
/// 机器人的只读世界视图（规则 3）：策略只能看到不可变数据快照——当前 Tick、
/// 下一可用动作 Tick、房间事实——没有任何可变引用，无法直接改权威状态。
/// </summary>
public sealed record BotWorldView(
    long CurrentTick,
    long NextActionTick,
    IReadOnlyDictionary<string, string> RoomFacts);

/// <summary>
/// 机器人策略契约（审计 §6 BotRuntime）：观察只读视图、产出
/// <see cref="BotAction"/>（规则 3/5）；状态可导出恢复，或声明可重建
/// （<see cref="ExportState"/> 返回 null，迁移时从头初始化——规则 6）。
/// </summary>
public interface IBotStrategy
{
    string StrategyId { get; }

    /// <summary>Produces the actions due at <paramref name="view"/>'s tick.</summary>
    IReadOnlyList<BotAction> Decide(BotWorldView view);

    /// <summary>Serialized strategy state; null means the strategy is rebuildable.</summary>
    string? ExportState();

    /// <summary>Restores exported state; null reinitializes from scratch.</summary>
    void RestoreState(string? state);
}

/// <summary>脚本条目：目标 Tick + 动作类型 + 可选参数。</summary>
public sealed record ScriptedAction(long TargetTick, string ActionType, string? Payload);

/// <summary>
/// 脚本策略（参考实现）：按脚本顺序在目标 Tick 到达时逐条产出动作，
/// bot 内序号单调递增（队列幂等锚点）。状态 = 已消费游标，可导出恢复（规则 6），
/// Restore(null) 即从头重放（可重建语义）。
/// </summary>
public sealed class ScriptedBotStrategy : IBotStrategy
{
    private readonly IReadOnlyList<ScriptedAction> _script;
    private readonly string _botId;
    private readonly string _participantId;
    private int _cursor;

    public ScriptedBotStrategy(
        BotStrategyConfig config,
        string botId,
        string participantId,
        IReadOnlyList<ScriptedAction> script)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (string.IsNullOrWhiteSpace(botId) || string.IsNullOrWhiteSpace(participantId))
        {
            throw new ArgumentException("bot/participant id 不能为空");
        }

        _script = script ?? throw new ArgumentNullException(nameof(script));
        _botId = botId;
        _participantId = participantId;
    }

    public string StrategyId => "scripted";

    public IReadOnlyList<BotAction> Decide(BotWorldView view)
    {
        List<BotAction> actions = [];
        while (_cursor < _script.Count && _script[_cursor].TargetTick <= view.CurrentTick)
        {
            var entry = _script[_cursor];
            _cursor++;
            // 序号 = 1 起的产出序（bot 内单调，BotActionQueue 据此防重放）。
            actions.Add(new BotAction(_botId, _participantId, entry.TargetTick, _cursor, entry.ActionType, entry.Payload));
        }

        return actions;
    }

    /// <summary>导出已消费游标。</summary>
    public string? ExportState() => _cursor.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void RestoreState(string? state)
    {
        if (state is null)
        {
            // 规则 6 可重建半边：从头重新初始化。
            _cursor = 0;
            return;
        }

        if (!int.TryParse(state, System.Globalization.CultureInfo.InvariantCulture, out var cursor) || cursor < 0 || cursor > _script.Count)
        {
            throw new ArgumentException("无效的脚本策略状态", nameof(state));
        }

        _cursor = cursor;
    }
}

/// <summary>
/// Bot 运行时（审计 §6 BotRuntime → ActionQueue）：按 Tick 驱动全部策略，
/// 把产出的动作提交进统一输入队列（规则 4）。单个策略崩溃只影响该 bot
/// （跳过并计数，不拖垮运行时）；注册按参与者幂等替换。
/// </summary>
public sealed class BotRuntime
{
    private readonly BotActionQueue _queue;
    private readonly Dictionary<string, IBotStrategy> _strategies = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _strategyErrors;

    public BotRuntime(BotActionQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    /// <summary>策略异常/非法产出计数（可观测）。</summary>
    public long StrategyErrorsCount => Interlocked.Read(ref _strategyErrors);

    /// <summary>Registers (or replaces) the strategy of a bot participant.</summary>
    public void Register(string participantId, IBotStrategy strategy)
    {
        if (string.IsNullOrWhiteSpace(participantId))
        {
            throw new ArgumentException("participant id 不能为空", nameof(participantId));
        }

        if (strategy is null)
        {
            throw new ArgumentNullException(nameof(strategy));
        }

        lock (_gate)
        {
            _strategies[participantId] = strategy;
        }
    }

    /// <summary>Removes a strategy. False when the participant has none.</summary>
    public bool Unregister(string participantId)
    {
        if (string.IsNullOrWhiteSpace(participantId))
        {
            throw new ArgumentException("participant id 不能为空", nameof(participantId));
        }

        lock (_gate)
        {
            return _strategies.Remove(participantId);
        }
    }

    /// <summary>
    /// Runs one tick: every strategy observes the view and its due actions are
    /// submitted to the unified queue. Returns the number submitted.
    /// </summary>
    public int Tick(BotWorldView view)
    {
        if (view is null)
        {
            throw new ArgumentNullException(nameof(view));
        }

        IBotStrategy[] strategies;
        lock (_gate)
        {
            strategies = [.. _strategies.Values];
        }

        var submitted = 0;
        foreach (var strategy in strategies)
        {
            IReadOnlyList<BotAction>? actions;
            try
            {
                actions = strategy.Decide(view);
            }
            catch
            {
                Interlocked.Increment(ref _strategyErrors);
                continue;
            }

            if (actions is null)
            {
                Interlocked.Increment(ref _strategyErrors);
                continue;
            }

            foreach (var action in actions)
            {
                try
                {
                    if (_queue.TrySubmit(action))
                    {
                        submitted++;
                    }
                }
                catch
                {
                    // 非法动作（缺身份等）只影响该 bot。
                    Interlocked.Increment(ref _strategyErrors);
                    break;
                }
            }
        }

        return submitted;
    }
}
