using System.Net;
using System.Net.Http;
using System.Text;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Adapters.Platforms.MiniGame;

namespace Chorus.Engine.Tests;

/// <summary>
/// 小游戏登录换 session 金测（todo §17 登录/身份映射）：官方口径配置驱动、
/// 字段名可配（不猜平台字段）、凭据/端点缺失一律 fail-closed、平台成功标志
/// 不匹配即失败、数值型成功标志按原文比较（快手 result = 1）。
/// </summary>
public sealed class MiniGameLoginExchangeTests
{
    private const int MaxBodyBytes = 16 * 1024;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
            => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Ok(string body, int? lengthOverride = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        if (lengthOverride is not null)
        {
            content.Headers.ContentLength = lengthOverride.Value;
        }

        response.Content = content;
        return response;
    }

    private static HttpMiniGameLoginExchange Exchange(
        MiniGameLoginExchangeConfig config,
        StubHandler handler)
        => new(config, new HttpClient(handler));

    private static HttpMiniGameLoginExchange KuaishouExchange(StubHandler handler)
        => Exchange(MiniGameLoginExchangeConfig.KuaishouMiniProgram("app-1", "secret-1"), handler);

    private static StubHandler Responding(HttpResponseMessage response)
        => new((_, _) => response);

    [Fact]
    public async Task Exchange_KuaishouOfficialShape_ReturnsAccountAndSessionKey()
    {
        var exchange = KuaishouExchange(Responding(Ok("{\"result\":1,\"session_key\":\"sk-1\",\"open_id\":\"open-1\"}")));

        var session = await exchange.ExchangeAsync("code-1");

        Assert.NotNull(session);
        Assert.Equal(PlatformNames.Kuaishou, session.Platform);
        Assert.Equal("open-1", session.PlatformAccountId);
        Assert.Equal("sk-1", session.SessionKey);
    }

    [Fact]
    public async Task Exchange_PostsFormWithVerifiedFieldNames()
    {
        var handler = Responding(Ok("{\"result\":1,\"open_id\":\"open-1\"}"));
        var exchange = KuaishouExchange(handler);

        await exchange.ExchangeAsync("code-1");

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://open.kuaishou.com/oauth2/mp/code2session", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("js_code=code-1", handler.LastBody!);
        Assert.Contains("app_id=app-1", handler.LastBody!);
        Assert.Contains("app_secret=secret-1", handler.LastBody!);
    }

    [Theory]
    [InlineData("{\"result\":0,\"open_id\":\"open-1\"}")]
    [InlineData("{\"result\":2,\"open_id\":\"open-1\"}")]
    public async Task Exchange_PlatformSuccessFlagMismatch_ReturnsNull(string body)
    {
        var exchange = KuaishouExchange(Responding(Ok(body)));

        Assert.Null(await exchange.ExchangeAsync("code-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"session_key\":\"sk-1\"}")]
    [InlineData("{\"result\":1}")]
    [InlineData("{\"result\":1,\"open_id\":\"\"}")]
    public async Task Exchange_MissingAccountId_ReturnsNull(string body)
    {
        var exchange = KuaishouExchange(Responding(Ok(body)));

        Assert.Null(await exchange.ExchangeAsync("code-1"));
    }

    [Fact]
    public async Task Exchange_NoResultFieldConfigured_SkipsSuccessFlagGate()
    {
        // 平台未公开成功标志字段时不猜：配置不带 ResultField 即不做标志门控。
        var config = MiniGameLoginExchangeConfig.KuaishouMiniProgram("app-1", "secret-1") with
        {
            ResultField = null,
            ResultSuccessValue = null,
        };
        var exchange = Exchange(config, Responding(Ok("{\"session_key\":\"sk-1\",\"open_id\":\"open-1\"}")));

        var session = await exchange.ExchangeAsync("code-1");

        Assert.NotNull(session);
        Assert.Equal("open-1", session.PlatformAccountId);
    }

    [Fact]
    public async Task Exchange_SessionKeyAbsent_LeavesSessionKeyNull()
    {
        var exchange = KuaishouExchange(Responding(Ok("{\"result\":1,\"open_id\":\"open-1\"}")));

        var session = await exchange.ExchangeAsync("code-1");

        Assert.NotNull(session);
        Assert.Null(session.SessionKey);
    }

    [Fact]
    public async Task Exchange_MissingEndpoint_FailsClosedWithoutCall()
    {
        var handler = Responding(Ok("{}"));
        var config = MiniGameLoginExchangeConfig.KuaishouMiniProgram("app-1", "secret-1") with { EndpointUrl = null };
        var exchange = Exchange(config, handler);

        Assert.Null(await exchange.ExchangeAsync("code-1"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Exchange_MissingCredentials_FailsClosedWithoutCall()
    {
        var handler = Responding(Ok("{}"));
        var config = MiniGameLoginExchangeConfig.KuaishouMiniProgram("app-1", "secret-1") with { AppSecret = null };
        var exchange = Exchange(config, handler);

        Assert.Null(await exchange.ExchangeAsync("code-1"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Exchange_BlankLoginCode_ReturnsNullWithoutCall()
    {
        var handler = Responding(Ok("{}"));
        var exchange = KuaishouExchange(handler);

        Assert.Null(await exchange.ExchangeAsync("  "));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Exchange_PlatformErrorStatus_ReturnsNull(HttpStatusCode status)
    {
        var exchange = KuaishouExchange(Responding(new HttpResponseMessage(status)));

        Assert.Null(await exchange.ExchangeAsync("code-1"));
    }

    [Fact]
    public async Task Exchange_NetworkFailure_ReturnsNull()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("down"));
        var exchange = KuaishouExchange(handler);

        Assert.Null(await exchange.ExchangeAsync("code-1"));
    }

    [Fact]
    public async Task Exchange_MalformedJson_ReturnsNull()
    {
        var exchange = KuaishouExchange(Responding(Ok("{not json")));

        Assert.Null(await exchange.ExchangeAsync("code-1"));
    }

    [Fact]
    public async Task Exchange_OverSizedBody_ReturnsNull()
    {
        // 声明长度超过上限：不读取也不解析（拒绝超大入站体）。
        Assert.Null(await KuaishouExchange(
            Responding(Ok("{\"result\":1,\"open_id\":\"open-1\"}", lengthOverride: MaxBodyBytes + 1)))
            .ExchangeAsync("code-1"));
    }

    [Fact]
    public void Exchange_NullConfig_Throws()
        => Assert.Throws<ArgumentNullException>(() => new HttpMiniGameLoginExchange(null!, new HttpClient()));

    [Fact]
    public void Exchange_NullHttp_Throws()
        => Assert.Throws<ArgumentNullException>(() => new HttpMiniGameLoginExchange(
            MiniGameLoginExchangeConfig.KuaishouMiniProgram("app-1", "secret-1"), http: null));

    [Fact]
    public void KuaishouConfig_UsesVerifiedPublicShape()
    {
        var config = MiniGameLoginExchangeConfig.KuaishouMiniProgram("app-1", "secret-1");

        // 官方已核实的端点与字段名（小程序 code2Session）。
        Assert.Equal("https://open.kuaishou.com/oauth2/mp/code2session", config.EndpointUrl);
        Assert.Equal("js_code", config.CodeFormField);
        Assert.Equal("app_id", config.AppIdFormField);
        Assert.Equal("app_secret", config.AppSecretFormField);
        Assert.Equal("result", config.ResultField);
        Assert.Equal("1", config.ResultSuccessValue);
        Assert.Equal("open_id", config.AccountIdField);
        Assert.Equal("session_key", config.SessionKeyField);
    }
}
