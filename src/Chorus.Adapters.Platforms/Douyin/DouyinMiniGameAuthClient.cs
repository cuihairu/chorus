using System.Text.Json;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// 抖音小游戏登录凭证校验（code2Session，官方口径 2026-10-14 核实：
/// GET /mgplatform/api/apps/jscode2session?appid&amp;secret&amp;code|anonymous_code，
/// code/anonymous_code 单次有效、至少传一个，error==0 成功）。
/// session_key 属敏感凭据：只放进结果对象、绝不写日志、绝不下发客户端
/// （官方安全要求：泄露可能导致小游戏被下架）。
/// </summary>
public sealed class DouyinMiniGameAuthClient
{
    private readonly DouyinMiniGameOptions _options;
    private readonly HttpMessageInvoker _http;

    public DouyinMiniGameAuthClient(DouyinMiniGameOptions options, HttpMessageInvoker http)
    {
        _options = options;
        _http = http;
    }

    /// <summary>
    /// 用 tt.login 返回的 code（非匿名）或 anonymous_code（匿名）换取会话。
    /// 两者都为空直接抛 ArgumentException（契约违反，不发请求）。
    /// error != 0（40014 缺参 / 40015 appid 错 / 40017 secret 错 /
    /// 40018 code 错 / 40019 匿名 code 错 / -1 系统错）返回失败结果不抛：
    /// 平台错误码是正常业务面，由调用方决定重试或提示。
    /// </summary>
    public async Task<DouyinCode2SessionResult> Code2SessionAsync(
        string? code, string? anonymousCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(anonymousCode))
        {
            throw new ArgumentException("code 与 anonymous_code 至少传一个（官方契约）");
        }

        var query = $"appid={Uri.EscapeDataString(_options.AppId)}"
                    + $"&secret={Uri.EscapeDataString(_options.AppSecret)}";
        if (!string.IsNullOrWhiteSpace(code))
        {
            query += $"&code={Uri.EscapeDataString(code)}";
        }

        if (!string.IsNullOrWhiteSpace(anonymousCode))
        {
            query += $"&anonymous_code={Uri.EscapeDataString(anonymousCode)}";
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_options.BaseUrl}/mgplatform/api/apps/jscode2session?{query}");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 缺 error 视为错误（fail-closed，不猜成功）。
        var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Number
            ? errEl.GetInt64()
            : -1L;
        if (error != 0)
        {
            var message = ReadString(root, "errmsg") ?? ReadString(root, "message");
            return DouyinCode2SessionResult.Fail(error, message);
        }

        var openid = ReadString(root, "openid");
        var anonymousOpenid = ReadString(root, "anonymous_openid");
        if (string.IsNullOrEmpty(openid) && string.IsNullOrEmpty(anonymousOpenid))
        {
            // error==0 但两个身份都没有 = 畸形成功响应，按失败处理。
            return DouyinCode2SessionResult.Fail(-1, "code2session returned neither openid nor anonymous_openid");
        }

        return DouyinCode2SessionResult.Ok(new DouyinSession(
            Openid: openid ?? string.Empty,
            SessionKey: ReadString(root, "session_key"),
            Unionid: ReadString(root, "unionid"),
            AnonymousOpenid: anonymousOpenid));
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>code2Session 成功结果。SessionKey/Unionid 属敏感字段：不下发客户端、不进日志。</summary>
public sealed record DouyinSession(
    string Openid,
    string? SessionKey,
    string? Unionid,
    string? AnonymousOpenid);

/// <summary>code2Session 调用结果：Success=false 时 ErrorCode/ErrorMessage 携带平台错误码。</summary>
public sealed record DouyinCode2SessionResult(
    bool Success,
    long ErrorCode,
    string? ErrorMessage,
    DouyinSession? Session)
{
    public static DouyinCode2SessionResult Ok(DouyinSession session) =>
        new(true, 0, null, session);

    public static DouyinCode2SessionResult Fail(long errorCode, string? errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

/// <summary>
/// 抖音 openid → Chorus 用户标识的确定性映射（登录/身份映射半边）：
/// <c>douyin:{openid}</c>。无状态、可重放、跨进程一致；openid 在同一小游戏内
/// 稳定（unionid 才跨小游戏），需要跨小游戏身份归并时由上层用
/// <see cref="DouyinSession.Unionid"/> 另行决策。
/// </summary>
public static class DouyinIdentity
{
    public const string Prefix = "douyin:";

    public static string ToChorusUserId(string openid)
    {
        if (string.IsNullOrWhiteSpace(openid))
        {
            throw new ArgumentException("openid 不能为空", nameof(openid));
        }

        return Prefix + openid;
    }
}
