using System.Text;
using System.Text.Json;
using Chorus.Data;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// 抖音小游戏赛事排名上报（选手排名上报，官方口径 2026-10-14 核实：
/// POST /mgplatform/api/apps/competition/rank/upload，header access-token，
/// body app_id/competition_id/schedule_id/rank_status/ranks[]，err_no==0 成功，
/// QPS &lt; 10；28001008 = token 过期）。
///
/// 契约适配（诚实边界）：官方 API 是「赛事赛段最终名次」上报，而
/// <see cref="IPlatformLeaderboardAdapter.TrySubmit"/> 是逐条
/// (scope, participant, score) 同步契约。本适配器为每 scope 维护替换式
/// 投影台账，名次按 <see cref="Chorus.Data"/> 的 LeaderboardStore 同语义
/// 计算（分数降序 + participantId 字典序 + 竞赛名次 1,2,2,4），一条一报。
/// 赛段冻结时的批量上报需要 LeaderboardScope.Query 快照整批喂入——属生产
/// 接线决策，本适配器不提供投机批量 API。
///
/// 同步 <c>bool</c> 契约内的 HTTP 走 sync-over-async（ASP.NET Core 无 sync
/// context，无死锁面）。平台拒绝与传输异常都走 false + Rejections 计数
/// （fail-visible，调用方降级到本地榜），不拖垮上游。
/// </summary>
public sealed class DouyinRankUploadAdapter : IPlatformLeaderboardAdapter
{
    /// <summary>每 scope 跟踪的参与人数上限；超限拒报（fail-visible）。</summary>
    public const int DefaultMaxParticipantsPerScope = 500;

    /// <summary>官方错误码：access-token 过期。</summary>
    public const long ErrNoAccessTokenExpired = 28001008;

    private readonly DouyinMiniGameOptions _options;
    private readonly DouyinAccessTokenProvider _tokens;
    private readonly HttpMessageInvoker _http;
    private readonly string _competitionId;
    private readonly string _scheduleId;
    private readonly int _rankStatus;
    private readonly int _maxParticipantsPerScope;

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, long>> _boards = new(StringComparer.Ordinal);

    /// <summary>成功上报数（err_no == 0）。</summary>
    public long Uploads;

    /// <summary>被拒数（err_no != 0 或超容量）。</summary>
    public long Rejections;

    public DouyinRankUploadAdapter(
        DouyinMiniGameOptions options,
        string competitionId,
        string scheduleId,
        int rankStatus = 1,
        int maxParticipantsPerScope = DefaultMaxParticipantsPerScope,
        HttpMessageInvoker? http = null)
    {
        if (string.IsNullOrWhiteSpace(competitionId))
        {
            throw new ArgumentException("competitionId 不能为空", nameof(competitionId));
        }

        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            throw new ArgumentException("scheduleId 不能为空", nameof(scheduleId));
        }

        if (maxParticipantsPerScope < 1)
        {
            throw new ArgumentException("容量必须 >= 1", nameof(maxParticipantsPerScope));
        }

        _options = options;
        _competitionId = competitionId;
        _scheduleId = scheduleId;
        _rankStatus = rankStatus;
        _maxParticipantsPerScope = maxParticipantsPerScope;
        _http = http ?? new HttpMessageInvoker(new HttpClientHandler());
        _tokens = new DouyinAccessTokenProvider(_options, _http);
    }

    public string Platform => PlatformNames.Douyin;

    /// <summary>
    /// 提交一条 (scope, participant, score)：替换式更新台账、算出竞赛名次、
    /// 单条上报。平台拒绝 / 超容量 / 传输异常 → false（Rejections 计数）。
    /// 空白 scope / participant 是契约违反，抛 ArgumentException。
    /// </summary>
    public bool TrySubmit(string scopeId, string participantId, long score)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new ArgumentException("scope id 不能为空", nameof(scopeId));
        }

        if (string.IsNullOrWhiteSpace(participantId))
        {
            throw new ArgumentException("participant id 不能为空", nameof(participantId));
        }

        long rank;
        lock (_gate)
        {
            if (!_boards.TryGetValue(scopeId, out var board))
            {
                board = new Dictionary<string, long>(StringComparer.Ordinal);
                _boards[scopeId] = board;
            }

            var isNew = !board.ContainsKey(participantId);
            board[participantId] = score;

            // 新参与者挤爆容量：名次不再精确，拒报而非报一个错名次。
            if (isNew && board.Count > _maxParticipantsPerScope)
            {
                Interlocked.Increment(ref Rejections);
                return false;
            }

            rank = ComputeRank(board, participantId, score);
        }

        try
        {
            return UploadCore(rank, participantId, score);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 平台拒绝与传输异常同口径：false + 计数，调用方降级本地榜。
            Interlocked.Increment(ref Rejections);
            return false;
        }
    }

    /// <summary>
    /// 竞赛名次 = 1 + 分数严格更高的条目数（并列共享名次 1,2,2,4），与
    /// LeaderboardStore 的确定性并列语义一致。同分不推名次——同分条目谁先谁后
    /// 只影响展示排序，不影响名次本身。
    /// </summary>
    private static long ComputeRank(Dictionary<string, long> board, string participantId, long score)
    {
        var better = 0;
        foreach (var (id, value) in board)
        {
            if (id == participantId)
            {
                continue;
            }

            if (value > score)
            {
                better++;
            }
        }

        return better + 1;
    }

    private bool UploadCore(long rank, string participantId, long score)
    {
        var body = JsonSerializer.Serialize(new
        {
            app_id = _options.AppId,
            competition_id = _competitionId,
            schedule_id = _scheduleId,
            rank_status = _rankStatus,
            ranks = new[]
            {
                new
                {
                    open_id = participantId,
                    // 无独立昵称来源时回退 participantId（平台展示名，非敏感）。
                    nickname = participantId,
                    player_source = 1, // 1 = 抖音用户（官方枚举）
                    rank,
                    score,
                },
            },
        });

        var retried = false;
        while (true)
        {
            var token = _tokens.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.BaseUrl}/mgplatform/api/apps/competition/rank/upload")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("access-token", token);

            using var response = _http.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 缺 err_no 视为错误（fail-closed）。
            var errNo = root.TryGetProperty("err_no", out var errEl) && errEl.ValueKind == JsonValueKind.Number
                ? errEl.GetInt64()
                : -1L;
            if (errNo == 0)
            {
                Interlocked.Increment(ref Uploads);
                return true;
            }

            // token 过期：失效重取重试一次；其余错误不重试（平台幂等语义未载明，
            // 盲重放可能重复计名次——fail-visible 拒绝，由调用方决定）。
            if (errNo == ErrNoAccessTokenExpired && !retried)
            {
                _tokens.Invalidate();
                retried = true;
                continue;
            }

            Interlocked.Increment(ref Rejections);
            return false;
        }
    }
}
