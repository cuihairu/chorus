using Chorus.Engine.Matchmaking;

namespace Chorus.Engine.Tests;

/// <summary>
/// 机器人策略配置与动作金测（todo §16 第三项，契约 = 审计 §6.1 规则 3/5/6）：
/// 策略只观察只读世界视图、只能产出 BotAction（规则 3：不直接改权威状态）；
/// 动作带显式目标 Tick 与 bot 内单调序号（规则 5）；
/// 策略状态可导出恢复或重建（规则 6：迁移时恢复必要状态 / 重新初始化可重建策略）；
/// 策略异常只影响该 bot，不拖垮运行时。
/// </summary>
public sealed class BotStrategyTests
{
    private static ScriptedAction Script(long tick, string type = "vote") => new(tick, type, null);

    private static BotWorldView View(long tick) => new(tick, tick + 1, new Dictionary<string, string>());

    // ---- 配置 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Config_BlankStrategyId_Throws(string? strategyId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new BotStrategyConfig(strategyId!));
    }

    [Fact]
    public void Config_NegativeCooldown_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new BotStrategyConfig("vote-bot", cooldownTicks: -1));
    }

    [Fact]
    public void Config_BlankParameterKey_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new BotStrategyConfig("vote-bot", parameters: new Dictionary<string, string> { [" "] = "1" }));
    }

    [Fact]
    public void Config_ParametersAreAccessible()
    {
        var config = new BotStrategyConfig(
            "vote-bot",
            parameters: new Dictionary<string, string> { ["weight"] = "3", ["label"] = "hostile" });

        Assert.Equal("3", config.GetString("weight"));
        Assert.Equal("hostile", config.GetString("label"));
        Assert.Null(config.GetString("missing"));
        Assert.Equal(3, config.GetInt("weight"));
        Assert.Equal(0, config.GetInt("missing"));
    }

    [Fact]
    public void WorldView_IsReadOnlyData()
    {
        var facts = new Dictionary<string, string> { ["room"] = "room-1" };
        var view = new BotWorldView(42, 43, facts);

        // 规则 3 的视图半边：策略拿到的只是不可变数据快照。
        Assert.Equal(42, view.CurrentTick);
        Assert.Equal(43, view.NextActionTick);
        Assert.Equal("room-1", view.RoomFacts["room"]);
    }

    // ---- 脚本策略 ----

    [Fact]
    public void ScriptedStrategy_EmitsActionsWhenTickDue()
    {
        var strategy = new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(10), Script(20)]);

        // tick 未到：不产出。
        Assert.Empty(strategy.Decide(View(9)));
        // tick 到：产出目标 tick 的动作（规则 5：显式 TargetTick）。
        var due = strategy.Decide(View(10));
        Assert.Single(due);
        Assert.Equal(10, due[0].TargetTick);
        Assert.Equal("bot-1", due[0].BotId);
        Assert.Equal("p-bot-1", due[0].ParticipantId);
    }

    [Fact]
    public void ScriptedStrategy_DoesNotReEmitSpentActions()
    {
        var strategy = new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(10), Script(20)]);

        strategy.Decide(View(10));

        // 已产出的动作不再重复（游标推进）。
        Assert.Empty(strategy.Decide(View(15)));
        Assert.Single(strategy.Decide(View(20)));
    }

    [Fact]
    public void ScriptedStrategy_SequencesAreMonotonicPerBot()
    {
        var strategy = new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(1), Script(2), Script(3)]);

        var all = strategy.Decide(View(3));

        // bot 内序号单调（BotActionQueue 的幂等锚点）。
        Assert.Equal(new[] { 1L, 2L, 3L }, all.Select(a => a.Sequence).ToArray());
    }

    [Fact]
    public void ScriptedStrategy_ExportRestore_RoundTripsCursor()
    {
        var strategy = new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(1), Script(2), Script(3)]);
        strategy.Decide(View(1));

        // 规则 6（可恢复半边）：导出游标 → 新实例恢复 → 剩余脚本一致。
        var state = strategy.ExportState();
        var restored = new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(1), Script(2), Script(3)]);
        restored.RestoreState(state);

        Assert.Empty(restored.Decide(View(1)));
        Assert.Equal(2, restored.Decide(View(3)).Count);
    }

    [Fact]
    public void ScriptedStrategy_RestoreNull_Reinitializes()
    {
        var strategy = new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(1), Script(2)]);
        strategy.Decide(View(2));

        // 规则 6（可重建半边）：Restore(null) = 从头重新初始化（可重建策略状态的迁移语义）。
        strategy.RestoreState(null);

        Assert.Equal(2, strategy.Decide(View(2)).Count);
    }

    // ---- 运行时 ----

    [Fact]
    public void Runtime_TickRunsStrategiesAndSubmitsToQueue()
    {
        var queue = new BotActionQueue();
        var runtime = new BotRuntime(queue);
        runtime.Register("p-bot-1", new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(5)]));

        var submitted = runtime.Tick(View(5));

        // 策略产出 → 统一输入队列（规则 4：与平台事件/主播操作同一条输入流）。
        Assert.Equal(1, submitted);
        Assert.Equal(1, queue.Count);
        var action = queue.DrainUpTo(5).Single();
        Assert.Equal("p-bot-1", action.ParticipantId);
    }

    [Fact]
    public void Runtime_NoStrategies_SubmitsNothing()
    {
        var runtime = new BotRuntime(new BotActionQueue());

        Assert.Equal(0, runtime.Tick(View(10)));
    }

    [Fact]
    public void Runtime_StrategyFailure_IsolatedAndCounted()
    {
        var queue = new BotActionQueue();
        var runtime = new BotRuntime(queue);
        runtime.Register("p-bad", new ThrowingStrategy());
        runtime.Register("p-bot-1", new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(5)]));

        // 单个策略崩溃只影响该 bot：计数可观测，其他策略照常产出。
        var submitted = runtime.Tick(View(5));

        Assert.Equal(1, submitted);
        Assert.Equal(1, runtime.StrategyErrorsCount);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Runtime_Register_ReplacesStrategy()
    {
        var queue = new BotActionQueue();
        var runtime = new BotRuntime(queue);
        runtime.Register("p-bot-1", new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(5)]));
        runtime.Register("p-bot-1", new ScriptedBotStrategy(
            new BotStrategyConfig("scripted2"), "bot-1", "p-bot-1", [Script(6), Script(7)]));

        runtime.Tick(View(5));

        Assert.Empty(queue.DrainUpTo(5));
        Assert.Equal(2, runtime.Tick(View(7)));
    }

    [Fact]
    public void Runtime_Unregister_StopsStrategy()
    {
        var queue = new BotActionQueue();
        var runtime = new BotRuntime(queue);
        runtime.Register("p-bot-1", new ScriptedBotStrategy(
            new BotStrategyConfig("scripted"), "bot-1", "p-bot-1", [Script(5)]));

        Assert.True(runtime.Unregister("p-bot-1"));
        Assert.False(runtime.Unregister("p-bot-1"));
        Assert.Equal(0, runtime.Tick(View(5)));
    }

    [Fact]
    public void Runtime_MultipleBots_AllRun()
    {
        var queue = new BotActionQueue();
        var runtime = new BotRuntime(queue);
        runtime.Register("p-bot-1", new ScriptedBotStrategy(
            new BotStrategyConfig("a"), "bot-1", "p-bot-1", [Script(5)]));
        runtime.Register("p-bot-2", new ScriptedBotStrategy(
            new BotStrategyConfig("b"), "bot-2", "p-bot-2", [Script(5)]));

        Assert.Equal(2, runtime.Tick(View(5)));
        Assert.Equal(2, queue.Count);
    }

    private sealed class ThrowingStrategy : IBotStrategy
    {
        public string StrategyId => "throwing";

        public IReadOnlyList<BotAction> Decide(BotWorldView view) => throw new InvalidOperationException("boom");

        public string? ExportState() => null;

        public void RestoreState(string? state)
        {
        }
    }
}
