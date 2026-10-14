using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>
/// 排行榜作用域（审计 §9.1 的四个存储型榜单；PlatformLeaderboard 是外置平台原生榜
/// 适配器，见 <see cref="PlatformLeaderboardRegistry"/>，不在此存储）。周期榜把周期
/// 作为 scope key 的一部分（如 <c>day:2026-10-14</c>），跨周期换 key 即自然重置。
/// </summary>
public enum LeaderboardScope
{
    Room = 0,
    Match = 1,
    Period = 2,
    Global = 3,
}

/// <summary>榜单条目；<see cref="Rank"/> 采用竞赛排名——并列分数共享名次（1,2,2,4）。</summary>
public sealed record RankEntry(string ParticipantId, long Score, int Rank);

/// <summary>
/// 排行榜核心（审计 §9「排行榜必须是正式架构模块」+ §9.2/§9.3 规则）：
///
/// <list type="bullet">
/// <item>替换式投影（UpsertScore）：房间/单局榜来自 Room/Match 权威状态——同参与者再投影
/// 是替换不是累加（天然幂等）；本 API 只暴露给引擎侧，客户端不能自报分（§9.2）。</item>
/// <item>幂等增量（AddDelta）：全局/周期榜由 Data Worker 异步聚合；同 sourceId
/// （如 DataCommand.CommandId）重放绝不重复加分（§9.2），未知参与者从 0 起算，
/// 负增量允许用于纠错。</item>
/// <item>确定性并列：分数降序、同分按 participantId 字典序（查询结果不抖动），
/// 并列共享竞赛名次。</item>
/// <item>最终成绩冻结（Freeze）：冻结即封榜——之后一切写入 fail-visible 拒绝，
/// 查询返回冻结时刻名次（单局结算冻结的落地）。</item>
/// <item>查询与更新分离（§9.3）：Query 返回独立快照；实时分不必逐笔同步全局榜——
/// 延迟聚合由 Data Worker 调用方控制。</item>
/// </list>
/// sourceId 台账是榜内第二道防线；命令级幂等由 <see cref="DataCommandIdempotencyTracker"/>
/// 在上游负责。生产接线属后续项。
/// </summary>
public sealed class LeaderboardStore
{
    private sealed class Board
    {
        public readonly ConcurrentDictionary<string, long> Scores = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, byte> AppliedSources = new(StringComparer.Ordinal);
        public volatile IReadOnlyList<RankEntry>? Frozen;
    }

    private readonly ConcurrentDictionary<string, Board> _boards = new(StringComparer.Ordinal);

    /// <summary>
    /// Projects an authoritative score for a participant (replace semantics).
    /// Returns false when the board is frozen (fail-visible, no silent drop).
    /// </summary>
    public bool UpsertScore(LeaderboardScope scope, string scopeId, string participantId, long score)
    {
        ValidateScopeId(scopeId);
        ValidateParticipantId(participantId);

        var board = BoardFor(scope, scopeId);
        if (board.Frozen is not null)
        {
            return false;
        }

        board.Scores[participantId] = score;
        return true;
    }

    /// <summary>
    /// Applies an idempotent score delta. True = newly applied; false = the
    /// sourceId was already counted (duplicate events never double-count) or
    /// the board is frozen (fail-visible). Unknown participants start from 0.
    /// </summary>
    public bool AddDelta(LeaderboardScope scope, string scopeId, string participantId, long delta, string sourceId)
    {
        ValidateScopeId(scopeId);
        ValidateParticipantId(participantId);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("source id 不能为空（幂等锚点）", nameof(sourceId));
        }

        var board = BoardFor(scope, scopeId);
        if (board.Frozen is not null)
        {
            return false;
        }

        // 先占幂等锚点再改分：并发同源重放恰好一个生效。
        if (!board.AppliedSources.TryAdd(sourceId, 0))
        {
            return false;
        }

        board.Scores.AddOrUpdate(participantId, delta, (_, current) => current + delta);
        return true;
    }

    /// <summary>
    /// Freezes the board at its current standings. Idempotent: the first
    /// snapshot wins; re-freezing neither throws nor changes the visible result.
    /// </summary>
    public void Freeze(LeaderboardScope scope, string scopeId)
    {
        ValidateScopeId(scopeId);

        var board = BoardFor(scope, scopeId);
        board.Frozen ??= Rank(board.Scores);
    }

    /// <summary>True when the board is frozen (final standings).</summary>
    public bool IsFrozen(LeaderboardScope scope, string scopeId)
    {
        ValidateScopeId(scopeId);
        return _boards.TryGetValue(Key(scope, scopeId), out var board) && board.Frozen is not null;
    }

    /// <summary>
    /// Ranked entries ordered by score descending (ties by participant id
    /// ascending, competition ranking). Returns an independent snapshot;
    /// frozen boards always return the frozen standings.
    /// </summary>
    public IReadOnlyList<RankEntry> Query(LeaderboardScope scope, string scopeId, int? top = null)
    {
        ValidateScopeId(scopeId);

        if (!_boards.TryGetValue(Key(scope, scopeId), out var board))
        {
            return [];
        }

        var entries = board.Frozen ?? Rank(board.Scores);
        return top is null || entries.Count <= top.Value ? entries : entries.Take(top.Value).ToArray();
    }

    private static IReadOnlyList<RankEntry> Rank(ConcurrentDictionary<string, long> scores)
    {
        var ordered = scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        List<RankEntry> entries = new(ordered.Count);
        var rank = 0;
        long? previous = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var (participantId, score) = ordered[i];
            // 竞赛排名：与前一并列同号，否则名次 = 1-based 序号（1,2,2,4）。
            rank = score == previous ? rank : i + 1;
            previous = score;
            entries.Add(new RankEntry(participantId, score, rank));
        }

        return entries;
    }

    private Board BoardFor(LeaderboardScope scope, string scopeId)
    {
        return _boards.GetOrAdd(Key(scope, scopeId), _ => new Board());
    }

    private static string Key(LeaderboardScope scope, string scopeId) => $"{(int)scope}:{scopeId}";

    private static void ValidateScopeId(string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new ArgumentException("scope id 不能为空", nameof(scopeId));
        }
    }

    private static void ValidateParticipantId(string? participantId)
    {
        if (string.IsNullOrWhiteSpace(participantId))
        {
            throw new ArgumentException("participant id 不能为空", nameof(participantId));
        }
    }
}
