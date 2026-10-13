using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;
using Chorus.Client;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Bilibili adapter e2e (todo §12 P1 acceptance): a signed open-live DANMU_MSG
/// posted to the real /ingress/bilibili flows through the shared face into
/// the tick pipeline, and a real client over /ws sees it land in a snapshot.
/// </summary>
public sealed class BilibiliEndToEndTests : IClassFixture<BilibiliEndToEndTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:Bilibili:AccessKeyId", "e2e-key");
            builder.UseSetting("Ingress:Bilibili:AccessKeySecret", "e2e-secret");
            builder.UseSetting("Ingress:Bilibili:SessionId", "s-bili-e2e");
        }
    }

    private readonly Factory _factory;

    public BilibiliEndToEndTests(Factory factory)
    {
        _factory = factory;
    }

    private static HttpResponseMessage PostSigned(HttpClient client, string body)
    {
        var headers = BilibiliSigner.BuildHeaders(
            body, "e2e-key", "e2e-secret", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/bilibili")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Danmaku_Ingress_ReachesClientSnapshot()
    {
        using var factory = _factory;
        var http = factory.CreateClient();
        var wsClient = factory.Server.CreateWebSocketClient();

        var states = new List<JsonElement>();
        var gate = new SemaphoreSlim(1, 1);
        await using var client = await ChorusClient.ConnectAsync(
            ChorusClientOptions.Default(
                new Uri(factory.Server.BaseAddress, "ws").ToString(), "s-bili-e2e", "c-bili"),
            state =>
            {
                gate.Wait();
                try
                {
                    states.Add(state);
                }
                finally
                {
                    gate.Release();
                }
            },
            connect: wsClient.ConnectAsync);

        // 等会话在 /ws 侧就绪（先连客户端再发事件，避免 face 503 回滚）。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            gate.Wait();
            var count = states.Count;
            gate.Release();
            if (count >= 1)
            {
                break;
            }

            await Task.Delay(10);
        }

        const string danmu =
            """{"cmd":"DANMU_MSG","msg_id":"e2e-danmaku-1","data":{"open_id":"ou-e2e","uname":"viewer","msg":"hello-chorus"}}""";
        using var response = PostSigned(http, danmu);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 弹幕 → AudienceEvent → tick → SampleGameModule totals.comments → 快照。
        var sawComment = false;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 8000 && !sawComment)
        {
            gate.Wait();
            try
            {
                sawComment = states.Any(s =>
                    s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("totals", out var totals)
                    && totals.TryGetProperty("comments", out var comments)
                    && comments.GetInt64() >= 1);
            }
            finally
            {
                gate.Release();
            }

            await Task.Delay(20);
        }

        Assert.True(sawComment, "danmaku never reached a client snapshot");
    }
}
