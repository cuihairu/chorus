namespace Chorus.Engine.Matchmaking;

/// <summary>规则 1（审计 §6.1）的配置半边：单人时立即填充机器人，或等待真人。</summary>
public enum MatchmakingFillMode
{
    /// <summary>等待真人，直到凑满最小人数或等待超时（超时后填充机器人补齐）。</summary>
    WaitForRealPlayers = 0,

    /// <summary>有人排队即立即填充机器人开局（不等待）。</summary>
    FillWithBotsImmediately = 1,
}

/// <summary>
/// 匹配策略（审计 §6：人数、模式、等待时间）。等待真人模式必须给出等待上限，
/// 否则队列会永远卡住——这是策略构造期就拒绝的契约违规。
/// </summary>
public sealed record MatchmakingPolicy
{
    public MatchmakingPolicy(
        string mode,
        int minParticipants,
        int maxParticipants,
        MatchmakingFillMode fillMode,
        long? waitTimeoutMs)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            throw new ArgumentException("mode 不能为空", nameof(mode));
        }

        if (minParticipants < 1)
        {
            throw new ArgumentException("最小参与人数至少为 1", nameof(minParticipants));
        }

        if (maxParticipants < minParticipants)
        {
            throw new ArgumentException("最大参与人数不能小于最小参与人数", nameof(maxParticipants));
        }

        if (fillMode == MatchmakingFillMode.WaitForRealPlayers
            && (waitTimeoutMs is null || waitTimeoutMs < 1))
        {
            throw new ArgumentException("等待真人模式必须给出等待上限（毫秒）", nameof(waitTimeoutMs));
        }

        Mode = mode;
        MinParticipants = minParticipants;
        MaxParticipants = maxParticipants;
        FillMode = fillMode;
        WaitTimeoutMs = waitTimeoutMs;
    }

    /// <summary>游戏模式标识（策略按模式区分）。</summary>
    public string Mode { get; init; }

    public int MinParticipants { get; init; }

    public int MaxParticipants { get; init; }

    public MatchmakingFillMode FillMode { get; init; }

    /// <summary>等待上限（毫秒）；仅等待真人模式使用。</summary>
    public long? WaitTimeoutMs { get; init; }
}

/// <summary>
/// 比赛参与者（审计 §4：必须明确区分真人与机器人，并记录参与者身份）。
/// 规则 2：机器人拥有独立 ParticipantId 与 BotId——由 Matchmaker 校验工厂产出强制执行。
/// </summary>
/// <param name="ParticipantId">参与者统一身份（真人与机器人同一命名空间，不重复）。</param>
/// <param name="IsBot">是否机器人。</param>
/// <param name="PlayerId">真人玩家身份；机器人为 null。</param>
/// <param name="BotId">机器人身份；真人为 null。</param>
public sealed record MatchParticipant(
    string ParticipantId,
    bool IsBot,
    string? PlayerId,
    string? BotId);

/// <summary>一次组局尝试的结果；未开局时 Participants 为空。</summary>
public sealed record MatchmakingResult(
    bool Started,
    bool FilledWithBots,
    IReadOnlyList<MatchParticipant> Participants)
{
    /// <summary>共享的「未开局」结果（无副作用，可复用）。</summary>
    public static readonly MatchmakingResult NotStarted = new(false, false, []);
}

/// <summary>
/// 机器人参与者工厂契约（审计 §6 BotFactory）。产出会被 Matchmaker 校验：
/// 数量、独立 ParticipantId、非空 BotId（规则 2）。
/// </summary>
public interface IBotParticipantFactory
{
    /// <summary>Creates <paramref name="count"/> bot participants for a forming match.</summary>
    IReadOnlyList<MatchParticipant> Create(int count, string requestId, long nowMs);
}

/// <summary>
/// Matchmaker（审计 §6 匹配流程的策略与参与者分配段）：
///
/// <list type="bullet">
/// <item>真人凑满最小人数即开局；参与者在 FIFO 队列中先到先得，一局最多
/// <c>MaxParticipants</c> 人，超额者留在队列等下一局。</item>
/// <item>规则 1：凑不满时按配置行事——等真人（超时后机器人补齐到最小人数）或
/// 有人即立即填充开局。</item>
/// <item>重复排队 fail-visible 拒绝；开局消耗队列，同一批真人不会被二次组局。</item>
/// <item>工厂产出校验：数量不足、缺独立 ParticipantId/BotId 一律
/// <see cref="InvalidOperationException"/>，不拿残缺参与者组局。</item>
/// </list>
/// 时间全部由调用方传入（nowMs），不读墙钟。参与者集合 → Match/Room 的创建与绑定
/// 是调用方职责（见审计 §6 ParticipantSet 之后的流程），不在本类。
/// </summary>
public sealed class Matchmaker
{
    private sealed record Pending(string RequestId, string PlayerId, long JoinedAtMs);

    private readonly MatchmakingPolicy _policy;
    private readonly IBotParticipantFactory _botFactory;
    private readonly object _gate = new();
    private readonly List<Pending> _queue = [];

    public Matchmaker(MatchmakingPolicy policy, IBotParticipantFactory botFactory)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _botFactory = botFactory ?? throw new ArgumentNullException(nameof(botFactory));
    }

    /// <summary>当前排队等待的真人数量。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// Joins the queue. False when the player is already queued (fail-visible,
    /// 不隐式去重也不重复占位).
    /// </summary>
    public bool Join(string requestId, string playerId, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("request id 不能为空", nameof(requestId));
        }

        if (string.IsNullOrWhiteSpace(playerId))
        {
            throw new ArgumentException("player id 不能为空", nameof(playerId));
        }

        lock (_gate)
        {
            if (_queue.Any(p => p.PlayerId == playerId))
            {
                return false;
            }

            _queue.Add(new Pending(requestId, playerId, nowMs));
            return true;
        }
    }

    /// <summary>Leaves the queue. False when the player is not queued.</summary>
    public bool Leave(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            throw new ArgumentException("player id 不能为空", nameof(playerId));
        }

        lock (_gate)
        {
            var removed = _queue.RemoveAll(p => p.PlayerId == playerId);
            return removed > 0;
        }
    }

    /// <summary>
    /// Attempts to form a match: real players FIFO up to the policy max; below
    /// the minimum the fill mode decides (wait, or bots filling up to the
    /// minimum after the wait timeout / immediately).
    /// </summary>
    public MatchmakingResult TryFormMatch(long nowMs)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return MatchmakingResult.NotStarted;
            }

            // FIFO 快照：开局时先到先得，最多取 MaxParticipants。
            var reals = _queue.Take(Math.Min(_queue.Count, _policy.MaxParticipants)).ToList();
            var oldestJoinedAt = reals[0].JoinedAtMs;

            if (reals.Count >= _policy.MinParticipants)
            {
                _queue.RemoveRange(0, reals.Count);
                return new MatchmakingResult(
                    Started: true,
                    FilledWithBots: false,
                    Participants: reals.Select(ToRealParticipant).ToArray());
            }

            var waitedTooLong = _policy.FillMode == MatchmakingFillMode.WaitForRealPlayers
                && _policy.WaitTimeoutMs is { } timeout
                && nowMs - oldestJoinedAt >= timeout;
            var fillNow = _policy.FillMode == MatchmakingFillMode.FillWithBotsImmediately || waitedTooLong;
            if (!fillNow)
            {
                return MatchmakingResult.NotStarted;
            }

            var needed = _policy.MinParticipants - reals.Count;
            var requestId = reals[0].RequestId;
            var bots = _botFactory.Create(needed, requestId, nowMs);
            ValidateBots(bots, needed, reals);

            _queue.RemoveRange(0, reals.Count);
            var participants = reals.Select(ToRealParticipant).Concat(bots).ToArray();
            return new MatchmakingResult(Started: true, FilledWithBots: true, Participants: participants);
        }
    }

    private static MatchParticipant ToRealParticipant(Pending pending) =>
        new(pending.PlayerId, IsBot: false, PlayerId: pending.PlayerId, BotId: null);

    /// <summary>规则 2 在组局边界的强制：工厂产出数量与身份不合规即拒绝。</summary>
    private static void ValidateBots(IReadOnlyList<MatchParticipant> bots, int expectedCount, List<Pending> reals)
    {
        if (bots.Count != expectedCount)
        {
            throw new InvalidOperationException($"机器人工厂产出 {bots.Count} 个参与者，期望 {expectedCount} 个");
        }

        var seenIds = new HashSet<string>(reals.Select(p => p.PlayerId), StringComparer.Ordinal);
        foreach (var bot in bots)
        {
            if (!bot.IsBot || string.IsNullOrWhiteSpace(bot.ParticipantId) || string.IsNullOrWhiteSpace(bot.BotId))
            {
                throw new InvalidOperationException("机器人参与者缺少独立 ParticipantId/BotId（规则 2）");
            }

            if (!seenIds.Add(bot.ParticipantId))
            {
                throw new InvalidOperationException("机器人参与者 ParticipantId 重复（规则 2）");
            }
        }
    }
}
