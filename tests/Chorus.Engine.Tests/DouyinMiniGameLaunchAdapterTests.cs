using System.Net;
using System.Text;
using Chorus.Platform;

namespace Chorus.Engine.Tests;

/// <summary>
/// code2Session HTTP 桩（HttpMessageInvoker 缝）：按 path 回放预设 (status, body)，
/// 记录全部请求 URI 供断言。500/503 等非 2xx 走 <see cref="Respond"/>，2xx 走 <see cref="Ok"/>。
/// </summary>
internal sealed class DouyinLaunchHttp : HttpMessageInvoker
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);
    public List<string> RequestUris { get; } = new();

    public DouyinLaunchHttp() : base(new HttpClientHandler())
    {
    }

    public DouyinLaunchHttp Ok(string body) => Respond("/mgplatform/api/apps/jscode2session",
        (HttpStatusCode.OK, body));

    public DouyinLaunchHttp Respond(string path, (HttpStatusCode Status, string Body) response)
    {
        _responses[path] = response;
        return this;
    }

    public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (RequestUris)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
        }
        var path = request.RequestUri.AbsolutePath;
        var (status, body) = _responses.TryGetValue(path, out var response)
            ? response
            : (HttpStatusCode.OK, """{"error":-1}""");
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// 抖音小游戏 launch 适配器金测（官方 code2Session 口径 2026-10-14 核实，见
/// docs/zh/reference/platform-douyin.md §2.2/§2.5）：code/anonymous_code 编码、
/// 身份与 canonical live key 推导、app-mismatch 本地拒绝、平台错误码与
/// fail-closed 细节。secret/credential 绝不出现在错误消息里。
/// </summary>
public sealed class DouyinMiniGameLaunchAdapterTests
{
    private const string AppId = "tt-test-appid";
    private const string Secret = "test-secret";

    private static string OkJson(string? openid = "open-1", string? anonymousOpenid = null) =>
        "{\"error\":0,\"openid\":\"" + (openid ?? "") + "\",\"session_key\":\"sk-secret\""
        + (anonymousOpenid is null ? "" : ",\"anonymous_openid\":\"" + anonymousOpenid + "\"") + "}";

    private static PlatformLaunchRequest Request(string credential = "code-1", string appId = AppId) =>
        new(appId, credential, "attempt-1");

    [Fact]
    public async Task Code2Session_Success_VerifiesLaunch()
    {
        var http = new DouyinLaunchHttp().Ok(OkJson(openid: "open-1"));
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var verified = await adapter.VerifyLaunchAsync(Request("code-1"));

        Assert.Equal("douyin", verified.Platform);
        Assert.Equal(AppId, verified.AppId);
        Assert.Equal("open-1", verified.OpenId);
        Assert.Equal($"douyin-live:{AppId}:open-1", verified.CanonicalLiveSessionKey);
        Assert.Equal("attempt-1", verified.LoginAttemptId);
        var uri = Assert.Single(http.RequestUris);
        Assert.StartsWith("http://test/mgplatform/api/apps/jscode2session?", uri);
        Assert.Contains($"appid={Uri.EscapeDataString(AppId)}", uri);
        Assert.Contains($"secret={Uri.EscapeDataString(Secret)}", uri);
        Assert.Contains("code=code-1", uri);
        Assert.DoesNotContain("anonymous_code", uri);
    }

    [Fact]
    public async Task AnonymousCredential_UsesAnonymousCode()
    {
        var http = new DouyinLaunchHttp().Ok(OkJson(openid: null, anonymousOpenid: "anon-open-1"));
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var verified = await adapter.VerifyLaunchAsync(Request("anonymous:anon-code-1"));

        Assert.Equal("anonymous:anon-open-1", verified.OpenId);
        Assert.Equal($"douyin-live:{AppId}:anonymous:anon-open-1", verified.CanonicalLiveSessionKey);
        var uri = Assert.Single(http.RequestUris);
        Assert.Contains("anonymous_code=anon-code-1", uri);
        Assert.DoesNotContain("&code=", uri);
    }

    [Fact]
    public async Task AnonymousPrefixWithoutCode_IsMalformed()
    {
        var http = new DouyinLaunchHttp();
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => adapter.VerifyLaunchAsync(Request("anonymous:")));
        Assert.Equal("malformed-credential", ex.ErrorCode);
        Assert.Empty(http.RequestUris);
    }

    [Fact]
    public async Task AppMismatch_RejectsBeforeHttp()
    {
        var http = new DouyinLaunchHttp().Ok(OkJson());
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => adapter.VerifyLaunchAsync(Request(appId: "tt-other-app")));
        Assert.Equal("app-mismatch", ex.ErrorCode);
        Assert.Empty(http.RequestUris);
    }

    [Fact]
    public async Task PlatformError_RejectsWithStableCode()
    {
        var http = new DouyinLaunchHttp().Ok("""{"error":40018,"errmsg":"invalid code"}""");
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => adapter.VerifyLaunchAsync(Request("super-secret-code")));
        Assert.Equal("platform-rejected", ex.ErrorCode);
        Assert.Contains("40018", ex.Message);
        Assert.DoesNotContain("super-secret-code", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public async Task SuccessWithoutIdentity_IsRejected()
    {
        var http = new DouyinLaunchHttp().Ok("""{"error":0}""");
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => adapter.VerifyLaunchAsync(Request()));
        Assert.Equal("platform-response-invalid", ex.ErrorCode);
    }

    [Fact]
    public async Task HttpError_IsUnreachable()
    {
        var http = new DouyinLaunchHttp().Respond(
            "/mgplatform/api/apps/jscode2session", (HttpStatusCode.InternalServerError, "boom"));
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => adapter.VerifyLaunchAsync(Request()));
        Assert.Equal("platform-unreachable", ex.ErrorCode);
    }

    [Fact]
    public async Task BlankCredential_IsMalformed()
    {
        var http = new DouyinLaunchHttp();
        var adapter = new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test");

        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => adapter.VerifyLaunchAsync(Request("")));
        Assert.Equal("malformed-credential", ex.ErrorCode);
        Assert.Empty(http.RequestUris);
    }

    [Fact]
    public void BlankSecret_RefusesToConstruct()
    {
        Assert.Throws<InvalidOperationException>(
            () => new DouyinMiniGameLaunchAdapter(AppId, "", new DouyinLaunchHttp(), "http://test"));
        Assert.Throws<InvalidOperationException>(
            () => new DouyinMiniGameLaunchAdapter("", Secret, new DouyinLaunchHttp(), "http://test"));
    }

    [Fact]
    public async Task Gateway_TakesRealAdapter_WithoutDevFlag()
    {
        // The real adapter declares ProductionReady, so the gateway serves it with the
        // default (non-development) gating; the fail-closed stub stays rejected alongside.
        var http = new DouyinLaunchHttp().Ok(OkJson());
        var gateway = new RedesignPlatformGateway(
            [new DouyinMiniGameLaunchAdapter(AppId, Secret, http, "http://test")],
            allowNotProductionReady: false);

        var verified = await gateway.VerifyLaunchAsync("douyin", Request());

        Assert.Equal("open-1", verified.OpenId);

        var stubGateway = new RedesignPlatformGateway([new DouyinLaunchAdapter()], allowNotProductionReady: false);
        var ex = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => stubGateway.VerifyLaunchAsync("douyin", Request()));
        Assert.Equal("adapter-not-production-ready", ex.ErrorCode);
    }
}
