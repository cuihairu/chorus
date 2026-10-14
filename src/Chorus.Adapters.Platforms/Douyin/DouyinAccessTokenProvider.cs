using System.Text;
using System.Text.Json;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// 抖音小游戏 access_token 获取与缓存（getAccessToken，官方口径 2026-10-14 核实：
/// POST /mgplatform/api/apps/v2/token，grant_type=client_credential，data.expires_in
/// 秒；有效期 2 小时、重复获取会把旧 token 压到 5 分钟——官方建议按小时刷新）。
/// token 是小游戏级全局凭据：进程内单例缓存、SemaphoreSlim 单飞避免并发重复
/// 获取压短旧 token。err_no != 0 抛 InvalidOperationException（fail-visible：
/// 调用方决定重试或降级，绝不静默回落旧 token）。
/// </summary>
public sealed class DouyinAccessTokenProvider
{
    /// <summary>到期前提前刷新的余量（官方建议小时级刷新，2h TTL）。</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly DouyinMiniGameOptions _options;
    private readonly HttpMessageInvoker _http;
    private readonly SemaphoreSlim _flight = new(1, 1);
    private readonly object _cacheLock = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public DouyinAccessTokenProvider(DouyinMiniGameOptions options, HttpMessageInvoker http)
    {
        _options = options;
        _http = http;
    }

    /// <summary>缓存的 access_token；缺失或临近过期则单飞重取。</summary>
    public async Task<string> GetAsync(CancellationToken ct)
    {
        await _flight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_cacheLock)
            {
                if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                {
                    return _token;
                }
            }

            var (token, expiresAt) = await FetchAsync(ct).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _token = token;
                _expiresAt = expiresAt;
            }

            return token;
        }
        finally
        {
            _flight.Release();
        }
    }

    /// <summary>主动失效（rank upload 28001008 token 过期时调用，强制下次重取）。</summary>
    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _token = null;
            _expiresAt = DateTimeOffset.MinValue;
        }
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> FetchAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            appid = _options.AppId,
            secret = _options.AppSecret,
            grant_type = "client_credential",
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl}/mgplatform/api/apps/v2/token")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 缺 err_no 视为错误（fail-closed，不猜成功）。
        var errNo = root.TryGetProperty("err_no", out var errEl) && errEl.ValueKind == JsonValueKind.Number
            ? errEl.GetInt64()
            : -1L;
        if (errNo != 0)
        {
            var tips = root.TryGetProperty("err_tips", out var tipsEl) && tipsEl.ValueKind == JsonValueKind.String
                ? tipsEl.GetString()
                : null;
            throw new InvalidOperationException($"douyin getAccessToken failed, err_no={errNo} err_tips={tips}");
        }

        var token = root.GetProperty("data").GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("douyin getAccessToken returned empty access_token");
        var expiresIn = root.GetProperty("data").TryGetProperty("expires_in", out var expEl)
                        && expEl.ValueKind == JsonValueKind.Number
            ? expEl.GetInt64()
            : 7200;
        return (token, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiresIn) - RefreshMargin);
    }
}
