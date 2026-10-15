using System.Text.Json;
using Chorus.Adapters.Platforms;
using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 抖音赛事排名上报适配器金测（官方口径 2026-10-14 核实，见
/// docs/reference/platform-douyin.md）：payload 形状（app_id/competition_id/
/// schedule_id/rank_status/ranks[] + access-token header）、竞赛名次语义
/// （1,2,2,4，与 LeaderboardStore 同序）、替换式投影重报重排、平台错误码
/// fail-visible、token 过期 28001008 失效重取重试一次、容量上限拒报。
/// </summary>
public sealed class DouyinRankUploadAdapterTests
{
    private const string AppId = "tt-test-appid";
    private const string CompetitionId = "comp-1";
    private const string ScheduleId = "sch-7";
    private const string TokenPath = "/mgplatform/api/apps/v2/token";
    private const string UploadPath = "/mgplatform/api/apps/competition/rank/upload";

    private static string TokenJson(string token = "tok-1") =>
        "{\"err_no\":0,\"data\":{\"access_token\":\"" + token + "\",\"expires_in\":7200}}";

    private static string UploadOk() => """{"err_no":0,"err_msg":"","log_id":"log-1"}""";

    private static DouyinRankUploadAdapter Adapter(
        DouyinJsonApi api, int max = DouyinRankUploadAdapter.DefaultMaxParticipantsPerScope, int rankStatus = 1)
    {
        api.Sticky(TokenPath, TokenJson());
        api.Sticky(UploadPath, UploadOk());
        return new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"),
            CompetitionId, ScheduleId, rankStatus, max, api);
    }

    private static JsonElement LastUploadBody(DouyinJsonApi api)
    {
        var upload = api.Snapshot().Single(r => r.Path == UploadPath);
        return JsonDocument.Parse(upload.Body).RootElement.Clone();
    }

    [Fact]
    public void Platform_IsDouyin_AndRegistryResolvesIt()
    {
        var adapter = Adapter(new DouyinJsonApi());
        Assert.Equal(PlatformNames.Douyin, adapter.Platform);

        var registry = new PlatformLeaderboardRegistry();
        registry.Register(adapter);
        Assert.True(registry.Supports("douyin"));
        Assert.Same(adapter, registry.TryGet("DOUYIN")); // OrdinalIgnoreCase
    }

    [Fact]
    public void TrySubmit_UploadsSingleEntry_WithOfficialPayloadShape()
    {
        var api = new DouyinJsonApi();
        var adapter = Adapter(api);

        Assert.True(adapter.TrySubmit("room-1", "ou-A", 100));
        Assert.Equal(1, adapter.Uploads);
        Assert.Equal(0, adapter.Rejections);

        var body = LastUploadBody(api);
        Assert.Equal(AppId, body.GetProperty("app_id").GetString());
        Assert.Equal(CompetitionId, body.GetProperty("competition_id").GetString());
        Assert.Equal(ScheduleId, body.GetProperty("schedule_id").GetString());
        Assert.Equal(1, body.GetProperty("rank_status").GetInt32());

        var entry = body.GetProperty("ranks")[0];
        Assert.Equal("ou-A", entry.GetProperty("open_id").GetString());
        Assert.Equal("ou-A", entry.GetProperty("nickname").GetString());
        Assert.Equal(1, entry.GetProperty("player_source").GetInt32());
        Assert.Equal(1, entry.GetProperty("rank").GetInt64());
        Assert.Equal(100, entry.GetProperty("score").GetInt64());

        var upload = api.Snapshot().Single(r => r.Path == UploadPath);
        Assert.Equal("POST", upload.Method);
        Assert.Equal("tok-1", upload.Headers["access-token"]);
        Assert.Contains("application/json", upload.Headers["Content-Type"]);
    }

    [Fact]
    public void TrySubmit_CompetitionRankSemantics_1_2_2_4()
    {
        var api = new DouyinJsonApi();
        var adapter = Adapter(api);

        Assert.True(adapter.TrySubmit("s", "A", 100)); // rank 1
        Assert.True(adapter.TrySubmit("s", "B", 90));  // rank 2
        Assert.True(adapter.TrySubmit("s", "C", 90));  // 与 B 并列 → rank 2（竞赛名次）
        Assert.True(adapter.TrySubmit("s", "D", 80));  // rank 4

        var ranks = api.Snapshot()
            .Where(r => r.Path == UploadPath)
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("ranks")[0].GetProperty("rank").GetInt64())
            .ToArray();
        Assert.Equal(new long[] { 1, 2, 2, 4 }, ranks);
    }

    [Fact]
    public void TrySubmit_Resubmit_ReplacesScore_AndReordersRanks()
    {
        // 替换式投影（UpsertScore 同语义）：A 从 100 改 80 后，C=85 排在 A 前。
        var api = new DouyinJsonApi();
        var adapter = Adapter(api);

        Assert.True(adapter.TrySubmit("s", "A", 100));
        Assert.True(adapter.TrySubmit("s", "B", 90));
        Assert.True(adapter.TrySubmit("s", "A", 80));
        Assert.True(adapter.TrySubmit("s", "C", 85)); // 台账 {A:80,B:90,C:85} → 仅 B 严格更高 → rank 2

        var ranks = api.Snapshot()
            .Where(r => r.Path == UploadPath)
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("ranks")[0].GetProperty("rank").GetInt64())
            .ToArray();
        Assert.Equal(new long[] { 1, 2, 2, 2 }, ranks);
    }

    [Fact]
    public void TrySubmit_PlatformError_ReturnsFalse_Counted()
    {
        var api = new DouyinJsonApi();
        api.Sticky(TokenPath, TokenJson());
        api.Sticky(UploadPath, """{"err_no":28001007,"err_msg":"parameter error"}""");
        var adapter = new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"),
            CompetitionId, ScheduleId, http: api);

        Assert.False(adapter.TrySubmit("s", "ou-A", 100));
        Assert.Equal(0, adapter.Uploads);
        Assert.Equal(1, adapter.Rejections);
    }

    [Fact]
    public void TrySubmit_MalformedResponse_ReturnsFalse_Counted()
    {
        var api = new DouyinJsonApi();
        api.Sticky(TokenPath, TokenJson());
        api.Sticky(UploadPath, "not-json");
        var adapter = new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"),
            CompetitionId, ScheduleId, http: api);

        Assert.False(adapter.TrySubmit("s", "ou-A", 100));
        Assert.Equal(0, adapter.Uploads);
        Assert.Equal(1, adapter.Rejections);
    }

    [Fact]
    public void TrySubmit_ExpiredToken_Invalidates_AndRetriesOnce()
    {
        // 28001008 = token 过期：失效重取、重试恰好一次。
        var api = new DouyinJsonApi();
        api.Sequence(TokenPath, TokenJson("tok-old"), TokenJson("tok-new"));
        api.Sequence(UploadPath,
            """{"err_no":28001008,"err_msg":"access-token expired"}""",
            UploadOk());
        var adapter = new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"),
            CompetitionId, ScheduleId, http: api);

        Assert.True(adapter.TrySubmit("s", "ou-A", 100));
        Assert.Equal(1, adapter.Uploads);
        Assert.Equal(0, adapter.Rejections);

        var tokens = api.Snapshot().Where(r => r.Path == TokenPath).ToList();
        Assert.Equal(2, tokens.Count);
        var retry = api.Snapshot().Last(r => r.Path == UploadPath);
        Assert.Equal("tok-new", retry.Headers["access-token"]);
    }

    [Fact]
    public void TrySubmit_BeyondCapacity_RejectsWithoutUpload()
    {
        // 新参与者挤爆容量：名次不再精确 → 拒报（fail-visible），已跟踪者不受影响。
        var api = new DouyinJsonApi();
        var adapter = Adapter(api, max: 2);

        Assert.True(adapter.TrySubmit("s", "A", 100));
        Assert.True(adapter.TrySubmit("s", "B", 90));
        Assert.False(adapter.TrySubmit("s", "C", 80));
        Assert.True(adapter.TrySubmit("s", "A", 85)); // 已跟踪者重报仍可

        var uploads = api.Snapshot().Where(r => r.Path == UploadPath).ToList();
        Assert.Equal(3, uploads.Count);
        Assert.Equal(1, adapter.Rejections);
    }

    [Fact]
    public void TrySubmit_BlankIds_Throw()
    {
        var adapter = Adapter(new DouyinJsonApi());

        Assert.Throws<ArgumentException>(() => adapter.TrySubmit("", "ou-A", 1));
        Assert.Throws<ArgumentException>(() => adapter.TrySubmit("s", " ", 1));
    }

    [Fact]
    public void Constructor_BlankCompetitionOrSchedule_Throws()
    {
        var api = new DouyinJsonApi();
        Assert.Throws<ArgumentException>(() => new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"), "", ScheduleId, http: api));
        Assert.Throws<ArgumentException>(() => new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"), CompetitionId, "", http: api));
        Assert.Throws<ArgumentException>(() => new DouyinRankUploadAdapter(
            new DouyinMiniGameOptions(AppId, "test-secret", "http://test"), CompetitionId, ScheduleId, 1, 0, api));
    }
}
