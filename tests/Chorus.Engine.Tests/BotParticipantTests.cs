using Chorus.Engine.Matchmaking;

namespace Chorus.Engine.Tests;

/// <summary>
/// Bot 工厂 + Bot Participant + 统一输入队列金测（todo §16 第二项，契约 = 审计 §6.1 规则 2/4/5 +
/// 「机器人不是虚构的主播账号，也不是伪造的 WebSocket 客户端」）：
/// 工厂产出独立 ParticipantId 与 BotId（规则 2）；
/// 动作带显式目标 Tick 与确定性排序（规则 5）、进入统一输入队列（规则 4）、队列有界满载 fail-visible；
/// 动作只是数据，不携带任何改状态的能力（规则 3 的队列半边）。
/// </summary>
public sealed class BotParticipantTests
{
    private const long StartMs = 1_000_000;

    private static BotAction NewAction(
        string botId = "bot-1",
        string participantId = "p-bot-1",
        long targetTick = 10,
        long sequence = 1,
        string actionType = "vote",
        string? payload = """{"choice":1}""") =>
        new(botId, participantId, targetTick, sequence, actionType, payload);

    // ---- 工厂 ----

    [Fact]
    public void Factory_ProducesDistinctParticipantIds()
    {
        var factory = new DefaultBotParticipantFactory();

        var bots = factory.Create(5, "req-1", StartMs);

        Assert.Equal(5, bots.Select(p => p.ParticipantId).Distinct().Count());
    }

    [Fact]
    public void Factory_ProducesIndependentBotAndParticipantIds()
    {
        var factory = new DefaultBotParticipantFactory();

        var bots = factory.Create(4, "req-1", StartMs);

        // 规则 2：机器人拥有独立 ParticipantId 与 BotId，两者都非空且互不相同。
        Assert.All(bots, p =>
        {
            Assert.True(p.IsBot);
            Assert.Null(p.PlayerId);
            Assert.False(string.IsNullOrWhiteSpace(p.BotId));
            Assert.NotEqual(p.BotId, p.ParticipantId);
        });
        Assert.Equal(4, bots.Select(p => p.BotId).Distinct().Count());
    }

    [Fact]
    public void Factory_IdGenerationIsMonotonicAcrossCalls()
    {
        var factory = new DefaultBotParticipantFactory();
        var first = factory.Create(3, "req-1", StartMs);
        var second = factory.Create(3, "req-2", StartMs + 1);

        // 同一工厂生命周期内 id 不复用：两次产出无交集。
        Assert.Empty(first.Select(p => p.ParticipantId).Intersect(second.Select(p => p.ParticipantId)));
    }

    [Fact]
    public void Factory_ZeroCount_ReturnsEmpty()
    {
        var factory = new DefaultBotParticipantFactory();

        Assert.Empty(factory.Create(0, "req-1", StartMs));
    }

    [Fact]
    public void Factory_NegativeCount_Throws()
    {
        var factory = new DefaultBotParticipantFactory();

        Assert.ThrowsAny<ArgumentException>(() => factory.Create(-1, "req-1", StartMs));
    }

    // ---- 统一输入队列 ----

    [Fact]
    public void TrySubmit_ValidAction_Accepted()
    {
        var queue = new BotActionQueue();

        Assert.True(queue.TrySubmit(NewAction()));
        Assert.Equal(1, queue.Count);
    }

    [Theory]
    [InlineData("BotId", " ", "p-bot-1", "vote")]
    [InlineData("ParticipantId", "bot-1", " ", "vote")]
    [InlineData("ActionType", "bot-1", "p-bot-1", " ")]
    public void TrySubmit_BlankField_Throws(string field, string botId, string participantId, string actionType)
    {
        var queue = new BotActionQueue();

        var args = Assert.ThrowsAny<ArgumentException>(() =>
            queue.TrySubmit(NewAction(botId, participantId, actionType: actionType)));
        Assert.Equal(field, args.ParamName);
    }

    [Fact]
    public void TrySubmit_NegativeTargetTick_Throws()
    {
        var queue = new BotActionQueue();

        Assert.ThrowsAny<ArgumentException>(() => queue.TrySubmit(NewAction(targetTick: -1)));
    }

    [Fact]
    public void TrySubmit_DuplicateSequence_RejectedVisible()
    {
        var queue = new BotActionQueue();
        queue.TrySubmit(NewAction(sequence: 7));

        // 同一 bot 的同一 Sequence 是重放：fail-visible 拒绝，不重复入队。
        Assert.False(queue.TrySubmit(NewAction(sequence: 7, actionType: "move")));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void TrySubmit_SameSequenceFromDifferentBots_Accepted()
    {
        var queue = new BotActionQueue();

        // Sequence 是 bot 内序：不同 bot 的同一序号互不冲突。
        Assert.True(queue.TrySubmit(NewAction(botId: "bot-1", participantId: "p-bot-1", sequence: 7)));
        Assert.True(queue.TrySubmit(NewAction(botId: "bot-2", participantId: "p-bot-2", sequence: 7)));
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Drain_OrdersByTargetTickThenSequence()
    {
        var queue = new BotActionQueue();
        queue.TrySubmit(NewAction(botId: "bot-a", participantId: "p-bot-a", targetTick: 12, sequence: 1));
        queue.TrySubmit(NewAction(botId: "bot-b", participantId: "p-bot-b", targetTick: 10, sequence: 2));
        queue.TrySubmit(NewAction(botId: "bot-c", participantId: "p-bot-c", targetTick: 10, sequence: 1));

        // 规则 5：显式目标 Tick + 确定性总序（TargetTick 升序、同 Tick 按 bot 字典序、再按 bot 内序号）。
        var drained = queue.Drain();
        Assert.Equal(new[] { ("p-bot-b", 2L), ("p-bot-c", 1L), ("p-bot-a", 1L) },
            drained.Select(a => (a.ParticipantId, a.Sequence)));
    }

    [Fact]
    public void Drain_IsDeterministicRegardlessOfSubmissionOrder()
    {
        var actions = new[]
        {
            NewAction(botId: "bot-b", participantId: "p-bot-b", targetTick: 10, sequence: 2),
            NewAction(botId: "bot-a", participantId: "p-bot-a", targetTick: 10, sequence: 1),
            NewAction(botId: "bot-a", participantId: "p-bot-a", targetTick: 10, sequence: 3),
        };

        var first = new BotActionQueue();
        var second = new BotActionQueue();
        foreach (var action in actions)
        {
            first.TrySubmit(action);
        }

        foreach (var action in actions.Reverse())
        {
            second.TrySubmit(action);
        }

        // 排序规则只依赖动作字段：乱序提交得到同一总序（确定性入队，§7 Sequence 段）。
        Assert.Equal(
            first.Drain().Select(a => (a.BotId, a.TargetTick, a.Sequence)),
            second.Drain().Select(a => (a.BotId, a.TargetTick, a.Sequence)));
    }

    [Fact]
    public void DrainUpTo_ReturnsOnlyActionsDueByTick()
    {
        var queue = new BotActionQueue();
        queue.TrySubmit(NewAction(botId: "bot-a", participantId: "p-bot-a", targetTick: 9, sequence: 1));
        queue.TrySubmit(NewAction(botId: "bot-b", participantId: "p-bot-b", targetTick: 10, sequence: 1));
        queue.TrySubmit(NewAction(botId: "bot-c", participantId: "p-bot-c", targetTick: 11, sequence: 1));

        var due = queue.DrainUpTo(targetTick: 10);

        Assert.Equal(new[] { "p-bot-a", "p-bot-b" }, due.Select(a => a.ParticipantId));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Drain_Empty_ReturnsEmpty()
    {
        var queue = new BotActionQueue();

        Assert.Empty(queue.Drain());
    }

    [Fact]
    public void Drain_RemovesDrainedActions()
    {
        var queue = new BotActionQueue();
        queue.TrySubmit(NewAction());

        Assert.Single(queue.Drain());
        Assert.Equal(0, queue.Count);
        // 已提交的 Sequence 不能复用（防重放）：清空后同 Sequence 仍被拒。
        Assert.False(queue.TrySubmit(NewAction()));
        Assert.Equal(1, queue.DuplicateCount);
    }

    [Fact]
    public void TrySubmit_FullQueue_RejectedVisible()
    {
        var queue = new BotActionQueue(capacity: 2);
        queue.TrySubmit(NewAction(targetTick: 1, sequence: 1));
        queue.TrySubmit(NewAction(targetTick: 2, sequence: 2));

        // 统一输入队列有界：满载 fail-visible 拒绝（策略层决定等待/放弃），不静默丢弃。
        Assert.False(queue.TrySubmit(NewAction(targetTick: 3, sequence: 3)));
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.RejectedFullCount);
    }

    [Fact]
    public void Actions_ArePureData()
    {
        var action = NewAction();

        // 规则 3（队列半边）：动作是纯数据记录，不携带委托/回调，策略只能产出动作、
        // 无法借队列直接改权威状态。
        Assert.Equal("bot-1", action.BotId);
        Assert.Equal("p-bot-1", action.ParticipantId);
        Assert.Equal(10, action.TargetTick);
        Assert.Equal(1, action.Sequence);
        Assert.Equal("vote", action.ActionType);
        Assert.Equal("""{"choice":1}""", action.Payload);
    }

    [Fact]
    public void ConcurrentSubmit_AllDistinctAccepted()
    {
        var queue = new BotActionQueue(capacity: 64);

        Parallel.For(0, 32, i => queue.TrySubmit(NewAction(targetTick: i, sequence: i + 1)));

        Assert.Equal(32, queue.Count);
        Assert.Equal(32, queue.Drain().Count);
    }

    [Fact]
    public void InvalidCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BotActionQueue(capacity: 0));
    }
}
