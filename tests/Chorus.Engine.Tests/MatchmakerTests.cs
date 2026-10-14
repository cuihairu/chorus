using Chorus.Engine.Matchmaking;

namespace Chorus.Engine.Tests;

/// <summary>
/// Matchmaker 金测（todo §16 首项，契约 = 审计 §6 匹配流程 + §6.1 规则 1）：
/// 匹配策略（人数/模式/等待时间）驱动；真人凑满最小人数即开局（先到先排队 FIFO）；
/// 等待超时按配置填充机器人（规则 1：立即填充 vs 等真人，由策略决定）；
/// 重复排队 fail-visible；开局后参与者不再回到队列；机器人填充委托工厂并校验产出
/// （独立 ParticipantId/BotId，规则 2 由 Matchmaker 侧强制）。
/// </summary>
public sealed class MatchmakerTests
{
    private const long StartMs = 1_000_000;

    private static MatchmakingPolicy NewPolicy(
        int min = 2,
        int max = 4,
        MatchmakingFillMode fillMode = MatchmakingFillMode.WaitForRealPlayers,
        long? waitTimeoutMs = 10_000) =>
        new("vote-battle", min, max, fillMode, waitTimeoutMs);

    private static Matchmaker NewMatchmaker(MatchmakingPolicy? policy = null, List<string>? factoryCalls = null) =>
        new(
            policy ?? NewPolicy(),
            new FakeBotFactory(factoryCalls));

    private sealed class FakeBotFactory(List<string>? calls) : IBotParticipantFactory
    {
        public IReadOnlyList<MatchParticipant> Create(int count, string requestId, long nowMs)
        {
            calls?.Add(requestId);
            return Enumerable.Range(0, count)
                .Select(i => new MatchParticipant($"bot-{Guid.NewGuid():N}", IsBot: true, PlayerId: null, BotId: $"bot-def-{i}"))
                .ToArray();
        }
    }

    [Fact]
    public void Join_BelowMin_StaysWaiting()
    {
        var matchmaker = NewMatchmaker();

        matchmaker.Join("req-1", "streamer-a", StartMs);

        var result = matchmaker.TryFormMatch(StartMs + 1_000);
        Assert.False(result.Started);
        Assert.Empty(result.Participants);
    }

    [Fact]
    public void TryFormMatch_ReachesMin_StartsWithRealOnly()
    {
        var matchmaker = NewMatchmaker();
        matchmaker.Join("req-1", "streamer-a", StartMs);
        matchmaker.Join("req-2", "streamer-b", StartMs + 1);

        var result = matchmaker.TryFormMatch(StartMs + 2);

        Assert.True(result.Started);
        Assert.False(result.FilledWithBots);
        Assert.Equal(2, result.Participants.Count);
        Assert.All(result.Participants, p => Assert.False(p.IsBot));
    }

    [Fact]
    public void TryFormMatch_AboveMax_TakesFirstInJoinOrder()
    {
        var matchmaker = NewMatchmaker(NewPolicy(min: 2, max: 3));
        matchmaker.Join("req-1", "s-a", StartMs);
        matchmaker.Join("req-2", "s-b", StartMs + 1);
        matchmaker.Join("req-3", "s-c", StartMs + 2);
        matchmaker.Join("req-4", "s-d", StartMs + 3);

        var result = matchmaker.TryFormMatch(StartMs + 4);

        // 队列 FIFO：先到先入局，超额者留在队列等下一局。
        Assert.Equal(new[] { "s-a", "s-b", "s-c" }, result.Participants.Select(p => p.PlayerId));
        Assert.Equal(1, matchmaker.PendingCount);
    }

    [Fact]
    public void TryFormMatch_Timeout_FillsShortfallWithBots()
    {
        var calls = new List<string>();
        var matchmaker = NewMatchmaker(NewPolicy(min: 3, max: 4, waitTimeoutMs: 5_000), calls);
        matchmaker.Join("req-1", "streamer-a", StartMs);

        var result = matchmaker.TryFormMatch(StartMs + 5_000);

        // 规则 1：等待超时后按配置填充机器人补齐到最小人数。
        Assert.True(result.Started);
        Assert.True(result.FilledWithBots);
        Assert.Equal(3, result.Participants.Count);
        Assert.Single(result.Participants.Where(p => !p.IsBot));
        Assert.Equal(2, result.Participants.Count(p => p.IsBot));
        Assert.All(result.Participants.Where(p => p.IsBot), p =>
        {
            Assert.NotNull(p.BotId);
            Assert.True(p.ParticipantId != p.BotId);
        });
        Assert.Equal(["req-1"], calls);
    }

    [Fact]
    public void TryFormMatch_TimeoutNotReached_StillWaits()
    {
        var matchmaker = NewMatchmaker(NewPolicy(min: 3, waitTimeoutMs: 5_000));
        matchmaker.Join("req-1", "streamer-a", StartMs);

        var result = matchmaker.TryFormMatch(StartMs + 4_999);

        Assert.False(result.Started);
        Assert.Equal(1, matchmaker.PendingCount);
    }

    [Fact]
    public void TryFormMatch_TimeoutButMinRealReached_NoFill()
    {
        var matchmaker = NewMatchmaker(NewPolicy(min: 2, waitTimeoutMs: 5_000));
        matchmaker.Join("req-1", "s-a", StartMs);
        matchmaker.Join("req-2", "s-b", StartMs + 1);

        var result = matchmaker.TryFormMatch(StartMs + 9_999);

        // 真人已凑满最小人数：超时与否都不需要机器人。
        Assert.True(result.Started);
        Assert.False(result.FilledWithBots);
    }

    [Fact]
    public void Policy_FillWithBotsImmediately_FillsRightAway()
    {
        var matchmaker = NewMatchmaker(NewPolicy(min: 3, fillMode: MatchmakingFillMode.FillWithBotsImmediately));
        matchmaker.Join("req-1", "streamer-a", StartMs);

        var result = matchmaker.TryFormMatch(StartMs + 1);

        Assert.True(result.Started);
        Assert.True(result.FilledWithBots);
        Assert.Single(result.Participants.Where(p => !p.IsBot));
    }

    [Fact]
    public void Join_DuplicatePlayerId_FailsVisible()
    {
        var matchmaker = NewMatchmaker();
        matchmaker.Join("req-1", "streamer-a", StartMs);

        Assert.False(matchmaker.Join("req-2", "streamer-a", StartMs + 1));
        Assert.Equal(1, matchmaker.PendingCount);
    }

    [Fact]
    public void Leave_RemovesPending()
    {
        var matchmaker = NewMatchmaker();
        matchmaker.Join("req-1", "streamer-a", StartMs);
        matchmaker.Join("req-2", "streamer-b", StartMs + 1);

        Assert.True(matchmaker.Leave("streamer-a"));
        Assert.Equal(1, matchmaker.PendingCount);
        Assert.False(matchmaker.Leave("streamer-a"));
    }

    [Fact]
    public void StartedParticipants_DoNotRequeue()
    {
        var matchmaker = NewMatchmaker();
        matchmaker.Join("req-1", "streamer-a", StartMs);
        matchmaker.Join("req-2", "streamer-b", StartMs + 1);
        matchmaker.TryFormMatch(StartMs + 2);

        // 开局消耗队列：不会把同一批真人再次组局。
        var result = matchmaker.TryFormMatch(StartMs + 3);
        Assert.False(result.Started);
        Assert.Equal(0, matchmaker.PendingCount);
    }

    [Fact]
    public void ExcessPlayers_WaitForNextMatch()
    {
        var matchmaker = NewMatchmaker(NewPolicy(min: 2, max: 3));
        matchmaker.Join("req-1", "s-a", StartMs);
        matchmaker.Join("req-2", "s-b", StartMs + 1);
        matchmaker.Join("req-3", "s-c", StartMs + 2);
        matchmaker.Join("req-4", "s-d", StartMs + 3);
        matchmaker.TryFormMatch(StartMs + 4);

        // 剩下的 s-d 超时后走机器人填充开局。
        var result = matchmaker.TryFormMatch(StartMs + 20_000);
        Assert.True(result.Started);
        Assert.True(result.FilledWithBots);
        Assert.Equal("s-d", result.Participants.Single(p => !p.IsBot).PlayerId);
    }

    [Fact]
    public void FactoryOutput_IsValidated()
    {
        var badFactory = new DelegatingFactory(count =>
        {
            // 违反规则 2：机器人缺独立 BotId。
            return Enumerable.Range(0, count)
                .Select(_ => new MatchParticipant($"bot-{Guid.NewGuid():N}", IsBot: true, PlayerId: null, BotId: null))
                .ToArray();
        });
        var matchmaker = new Matchmaker(NewPolicy(min: 2), badFactory);
        matchmaker.Join("req-1", "streamer-a", StartMs);

        // 工厂产出不合规 = fail-visible 抛错，不拿残缺参与者组局。
        Assert.Throws<InvalidOperationException>(() => matchmaker.TryFormMatch(StartMs + 10_000));
    }

    [Fact]
    public void FactoryOutput_WrongCount_IsRejected()
    {
        var badFactory = new DelegatingFactory(_ => []);
        var matchmaker = new Matchmaker(NewPolicy(min: 2), badFactory);
        matchmaker.Join("req-1", "streamer-a", StartMs);

        Assert.Throws<InvalidOperationException>(() => matchmaker.TryFormMatch(StartMs + 10_000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Join_BlankPlayerId_Throws(string? playerId)
    {
        var matchmaker = NewMatchmaker();

        Assert.ThrowsAny<ArgumentException>(() => matchmaker.Join("req-1", playerId!, StartMs));
    }

    [Fact]
    public void Policy_InvalidBounds_Throw()
    {
        Assert.Throws<ArgumentException>(() => NewPolicy(min: 0));
        Assert.Throws<ArgumentException>(() => NewPolicy(min: 4, max: 2));
    }

    [Fact]
    public void Policy_WaitMode_RequiresTimeout()
    {
        // 规则 1 的另一半：等真人模式必须有等待上限，否则永远卡住。
        Assert.Throws<ArgumentException>(() => NewPolicy(fillMode: MatchmakingFillMode.WaitForRealPlayers, waitTimeoutMs: null));
    }

    [Fact]
    public void ConcurrentJoin_AllDistinctPlayersQueued()
    {
        var matchmaker = NewMatchmaker(NewPolicy(min: 2, max: 64));

        Parallel.For(0, 32, i => matchmaker.Join($"req-{i}", $"s-{i}", StartMs + i));

        Assert.Equal(32, matchmaker.PendingCount);
    }

    private sealed class DelegatingFactory(Func<int, IReadOnlyList<MatchParticipant>> create) : IBotParticipantFactory
    {
        public IReadOnlyList<MatchParticipant> Create(int count, string requestId, long nowMs) => create(count);
    }
}
