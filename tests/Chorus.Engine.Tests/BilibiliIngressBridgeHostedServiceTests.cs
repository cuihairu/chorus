using System.Net;
using Chorus.Adapters.Platforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// 覆盖 open-live 桥的宿主集成面：六件套配置门禁（IsConfigured）、
/// Start/Stop 对 client 的委派、以及 Create 的配置装配。
/// </summary>
public sealed class BilibiliIngressBridgeHostedServiceTests
{
    private static readonly Dictionary<string, string?> CompleteSettings = new()
    {
        ["Ingress:Bilibili:AccessKeyId"] = BilibiliOpenLiveClientTestsKeyId,
        ["Ingress:Bilibili:AccessKeySecret"] = "secret",
        ["Ingress:Bilibili:RoomOwnerAuthCode"] = "auth-code",
        ["Ingress:Bilibili:SessionId"] = "s-1",
        ["Ingress:Bilibili:IngressUrl"] = "http://test/ingress/bilibili",
        ["Ingress:Bilibili:AppId"] = "1001",
    };

    private const string BilibiliOpenLiveClientTestsKeyId = "key-id";

    private static IConfiguration ConfigWithout(string key)
    {
        var settings = new Dictionary<string, string?>(CompleteSettings);
        settings[key] = null;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static IConfiguration ConfigWith(string key, string? value)
    {
        var settings = new Dictionary<string, string?>(CompleteSettings) { [key] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void IsConfigured_True_WhenAllPiecesPresent()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(CompleteSettings).Build();

        Assert.True(BilibiliIngressBridgeHostedService.IsConfigured(configuration));
    }

    [Theory]
    [InlineData("Ingress:Bilibili:AccessKeyId")]
    [InlineData("Ingress:Bilibili:AccessKeySecret")]
    [InlineData("Ingress:Bilibili:RoomOwnerAuthCode")]
    [InlineData("Ingress:Bilibili:SessionId")]
    [InlineData("Ingress:Bilibili:IngressUrl")]
    [InlineData("Ingress:Bilibili:AppId")]
    public void IsConfigured_False_WhenAnyPieceMissing(string key)
    {
        Assert.False(BilibiliIngressBridgeHostedService.IsConfigured(ConfigWithout(key)));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData(" ")]
    public void IsConfigured_False_WhenAppIdNotNumeric(string appId)
    {
        Assert.False(BilibiliIngressBridgeHostedService.IsConfigured(
            ConfigWith("Ingress:Bilibili:AppId", appId)));
    }

    [Fact]
    public async Task StartAsync_DelegatesToClient_StopAsyncCompletesLoop()
    {
        // app_start 恒 code!=0：client 循环持续重试、永不触达 connect，
        // 可观测面就是 stub 上不断增长的 app_start 请求。
        var http = new RejectingStartHttp();
        var client = new BilibiliOpenLiveClient(
            new BilibiliOpenLiveOptions(
                AccessKeyId: "key-id",
                AccessKeySecret: "secret",
                AppId: 1001,
                RoomOwnerAuthCode: "auth-code",
                IngressUrl: "http://test/ingress/bilibili",
                StartUrl: "http://test/app_start",
                HeartbeatUrl: "http://test/app/heartbeat",
                EndUrl: "http://test/app/end",
                ReconnectMinDelay: TimeSpan.FromMilliseconds(10),
                ReconnectMaxDelay: TimeSpan.FromMilliseconds(50)),
            http,
            connect: (_, _) => throw new InvalidOperationException("unreachable"));
        var bridge = new BilibiliIngressBridgeHostedService(
            client, NullLogger<BilibiliIngressBridgeHostedService>.Instance);

        await bridge.StartAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && Volatile.Read(ref http.AppStartRequests) < 1)
        {
            await Task.Delay(10);
        }

        Assert.True(Volatile.Read(ref http.AppStartRequests) >= 1, "client loop never started");

        await bridge.StopAsync(CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref client.AppStartCalls));
    }

    [Fact]
    public void Create_WiresBridge_FromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(CompleteSettings).Build();
        var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .BuildServiceProvider();

        var bridge = BilibiliIngressBridgeHostedService.Create(provider);

        Assert.NotNull(bridge);
    }

    [Fact]
    public void Create_WithoutConfiguration_Throws()
    {
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => BilibiliIngressBridgeHostedService.Create(provider));
    }

    /// <summary>app_start 恒定失败（code!=0）的最小桩，只计数不解析。</summary>
    private sealed class RejectingStartHttp : HttpMessageInvoker
    {
        public int AppStartRequests;

        public RejectingStartHttp()
            : base(new HttpClientHandler())
        {
        }

        public override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AppStartRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"code":-101}"""),
            });
        }
    }
}
