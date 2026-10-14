using System.Net;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// 抖音小游戏 OpenAPI 的按路径分发 JSON 桩（HttpMessageInvoker 缝，离线金测用）：
/// 记录全部请求（方法/URL/body/headers），按 path 出 body——Sequence 先到先出、
/// Sticky 恒定、默认 err_no:-1。B 站 FakeOpenLiveHttp 同款模式。
/// </summary>
internal sealed class DouyinJsonApi : HttpMessageInvoker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<string>> _sequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sticky = new(StringComparer.Ordinal);

    public List<(string Method, string Path, string Uri, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = new();

    public DouyinJsonApi() : base(new HttpClientHandler())
    {
    }

    /// <summary>恒定返回体（token 等幂等端点）。</summary>
    public DouyinJsonApi Sticky(string path, string body)
    {
        _sticky[path] = body;
        return this;
    }

    /// <summary>按调用顺序返回（28001008 → 成功等重试路径）。</summary>
    public DouyinJsonApi Sequence(string path, params string[] bodies)
    {
        _sequence[path] = new Queue<string>(bodies);
        return this;
    }

    public List<(string Method, string Path, string Uri, string Body, IReadOnlyDictionary<string, string> Headers)> Snapshot()
    {
        lock (_gate)
        {
            return Requests.ToList();
        }
    }

    public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var headers = request.Headers.ToDictionary(
            h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content?.Headers is { } contentHeaders)
        {
            foreach (var h in contentHeaders)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
        }

        lock (_gate)
        {
            Requests.Add((
                request.Method.Method,
                path,
                request.RequestUri.AbsoluteUri,
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty,
                headers));

            string body;
            if (_sequence.TryGetValue(path, out var seq) && seq.Count > 0)
            {
                body = seq.Dequeue();
            }
            else if (_sticky.TryGetValue(path, out var sticky))
            {
                body = sticky;
            }
            else
            {
                body = """{"err_no":-1}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>
/// 抖音小游戏服务端 OpenAPI 金测（官方口径 2026-10-14 核实，见
/// docs/reference/platform-douyin.md）：access_token 获取/缓存/单飞/失效、
/// code2Session 成功与平台错误码、openid → Chorus 用户标识确定性映射。
/// secret 只出现在请求里，绝不进错误消息与日志。
/// </summary>
public sealed class DouyinMiniGameClientTests
{
    private const string AppId = "tt-test-appid";
    private const string Secret = "test-secret";

    private const string TokenPath = "/mgplatform/api/apps/v2/token";
    private const string Code2SessionPath = "/mgplatform/api/apps/jscode2session";

    private static DouyinMiniGameOptions Options() => new(AppId, Secret, "http://test");

    private static string TokenJson(string token = "tok-1", long expiresIn = 7200) =>
        "{\"err_no\":0,\"err_tips\":\"success\",\"data\":{\"access_token\":\"" + token
        + "\",\"expires_in\":" + expiresIn + "}}";

    // ---- getAccessToken ----

    [Fact]
    public async Task GetAccessToken_PostsExpectedBody_AndCaches()
    {
        var api = new DouyinJsonApi().Sticky(TokenPath, TokenJson());
        var provider = new DouyinAccessTokenProvider(Options(), api);

        var first = await provider.GetAsync(CancellationToken.None);
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.Equal("tok-1", first);
        Assert.Equal("tok-1", second);
        var sent = Assert.Single(api.Snapshot());
        Assert.Equal("POST", sent.Method);
        Assert.Equal(TokenPath, sent.Path);
        // 官方契约：appid + secret + grant_type=client_credential（JSON body）。
        Assert.Contains("\"appid\":\"tt-test-appid\"", sent.Body);
        Assert.Contains("\"secret\":\"test-secret\"", sent.Body);
        Assert.Contains("\"grant_type\":\"client_credential\"", sent.Body);
    }

    [Fact]
    public async Task GetAccessToken_TokenShorterThanRefreshMargin_IsRefetched()
    {
        // 官方建议小时级刷新（2h TTL、重复获取压短旧 token 到 5 分钟）：
        // 短于余量的 token 视为已过期，绝不复用。
        var api = new DouyinJsonApi().Sequence(
            TokenPath, TokenJson("tok-1", expiresIn: 1), TokenJson("tok-2"));
        var provider = new DouyinAccessTokenProvider(Options(), api);

        Assert.Equal("tok-1", await provider.GetAsync(CancellationToken.None));
        Assert.Equal("tok-2", await provider.GetAsync(CancellationToken.None));
        Assert.Equal(2, api.Snapshot().Count);
    }

    [Fact]
    public async Task GetAccessToken_Invalidate_DropsCache()
    {
        var api = new DouyinJsonApi().Sequence(TokenPath, TokenJson("tok-1"), TokenJson("tok-2"));
        var provider = new DouyinAccessTokenProvider(Options(), api);

        Assert.Equal("tok-1", await provider.GetAsync(CancellationToken.None));
        provider.Invalidate();
        Assert.Equal("tok-2", await provider.GetAsync(CancellationToken.None));
        Assert.Equal(2, api.Snapshot().Count);
    }

    [Fact]
    public async Task GetAccessToken_ErrorCode_ThrowsFailVisible_WithoutSecretInMessage()
    {
        var api = new DouyinJsonApi().Sticky(TokenPath, """{"err_no":40017,"err_tips":"secret error"}""");
        var provider = new DouyinAccessTokenProvider(Options(), api);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAsync(CancellationToken.None));

        Assert.Contains("40017", ex.Message);
        // secret 绝不进错误消息（凭据纪律）。
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public async Task GetAccessToken_MalformedResponse_MissingErrNo_FailsClosed()
    {
        // 缺 err_no 不猜成功：两次调用都失败，绝不缓存出假 token。
        var api = new DouyinJsonApi().Sticky(TokenPath, """{"unexpected":true}""");
        var provider = new DouyinAccessTokenProvider(Options(), api);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAsync(CancellationToken.None));
        Assert.Equal(2, api.Snapshot().Count);
    }

    // ---- code2Session ----

    [Fact]
    public async Task Code2Session_Success_ParsesIdentity_AndSendsQueryContract()
    {
        var api = new DouyinJsonApi().Sticky(
            Code2SessionPath,
            """{"error":0,"openid":"ou-123","session_key":"sk-1","unionid":"un-9"}""");
        var client = new DouyinMiniGameAuthClient(Options(), api);

        var result = await client.Code2SessionAsync("code-1", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ErrorCode);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("ou-123", result.Session!.Openid);
        Assert.Equal("sk-1", result.Session.SessionKey);
        Assert.Equal("un-9", result.Session.Unionid);
        Assert.Null(result.Session.AnonymousOpenid);

        var sent = Assert.Single(api.Snapshot());
        Assert.Equal("GET", sent.Method);
        Assert.Equal(Code2SessionPath, sent.Path);
        Assert.Contains("appid=tt-test-appid", sent.Uri);
        Assert.Contains("secret=test-secret", sent.Uri);
        Assert.Contains("code=code-1", sent.Uri);
        Assert.DoesNotContain("anonymous_code", sent.Uri);
    }

    [Fact]
    public async Task Code2Session_AnonymousOnly_SendsAnonymousCode()
    {
        var api = new DouyinJsonApi().Sticky(
            Code2SessionPath, """{"error":0,"anonymous_openid":"anon-7"}""");
        var client = new DouyinMiniGameAuthClient(Options(), api);

        var result = await client.Code2SessionAsync(null, "anon-code-1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Session!.Openid);
        Assert.Equal("anon-7", result.Session.AnonymousOpenid);
        var sent = Assert.Single(api.Snapshot());
        Assert.Contains("anonymous_code=anon-code-1", sent.Uri);
        Assert.DoesNotContain("&code=", sent.Uri);
    }

    [Fact]
    public async Task Code2Session_PlatformError_ReturnsErrorCode_DoesNotThrow()
    {
        var api = new DouyinJsonApi().Sticky(
            Code2SessionPath, """{"error":40018,"errmsg":"code error"}""");
        var client = new DouyinMiniGameAuthClient(Options(), api);

        var result = await client.Code2SessionAsync("bad-code", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(40018, result.ErrorCode);
        Assert.Equal("code error", result.ErrorMessage);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task Code2Session_MalformedSuccess_NoIdentity_TreatedAsFailure()
    {
        // error==0 但 openid/anonymous_openid 都没有 = 畸形成功响应，fail-closed。
        var api = new DouyinJsonApi().Sticky(Code2SessionPath, """{"error":0}""");
        var client = new DouyinMiniGameAuthClient(Options(), api);

        var result = await client.Code2SessionAsync("code-1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("neither openid nor anonymous_openid", result.ErrorMessage);
    }

    [Fact]
    public async Task Code2Session_BothCodesMissing_ThrowsWithoutRequest()
    {
        var api = new DouyinJsonApi();
        var client = new DouyinMiniGameAuthClient(Options(), api);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Code2SessionAsync(null, null, CancellationToken.None));

        Assert.Empty(api.Snapshot());
    }

    // ---- 身份映射 ----

    [Fact]
    public void Identity_ToChorusUserId_IsDeterministic()
    {
        Assert.Equal("douyin:ou-123", DouyinIdentity.ToChorusUserId("ou-123"));
        Assert.Equal(DouyinIdentity.ToChorusUserId("ou-123"), DouyinIdentity.ToChorusUserId("ou-123"));
        Assert.Throws<ArgumentException>(() => DouyinIdentity.ToChorusUserId(""));
        Assert.Throws<ArgumentException>(() => DouyinIdentity.ToChorusUserId(" "));
    }
}
