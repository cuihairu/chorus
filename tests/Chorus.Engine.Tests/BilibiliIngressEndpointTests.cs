using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Golden tests for the Bilibili ingress pipeline over the real HTTP endpoint
/// (todo §12 P1 acceptance): valid open-live signature accepted, tampered
/// body rejected, duplicate msg_id deduplicated, unknown event types do not
/// crash. Field layout per blivedm models/open_live.py (✅ 原文核实).
/// </summary>
public sealed class BilibiliIngressEndpointTests : IClassFixture<BilibiliIngressEndpointTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:Bilibili:AccessKeyId", "e2e-key");
            builder.UseSetting("Ingress:Bilibili:AccessKeySecret", "e2e-secret");
            builder.UseSetting("Ingress:Bilibili:SessionId", "s-bili");
            builder.UseSetting("Ingress:DedupTtlSeconds", "300");
        }
    }

    private const string DanmuBody =
        """{"cmd":"DANMU_MSG","msg_id":"m-1","data":{"open_id":"ou-1","uname":"viewer","msg":"hello"}}""";

    private readonly Factory _factory;

    public BilibiliIngressEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    private static HttpResponseMessage PostBilibili(HttpClient client, string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        headers ??= SignedHeaders(body);
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

    private static IReadOnlyDictionary<string, string> SignedHeaders(string body, long? timestampSeconds = null, string keyId = "e2e-key")
        => BilibiliSigner.BuildHeaders(
            body,
            keyId,
            "e2e-secret",
            timestampSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Ingress_CorrectSignature_Accepts()
    {
        var client = _factory.CreateClient();

        using var response = PostBilibili(client, DanmuBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("s-bili", payload.GetProperty("sessionId").GetString());
        Assert.Equal("m-1", payload.GetProperty("eventId").GetString());
    }

    [Fact]
    public void Ingress_TamperedBody_Rejected()
    {
        var client = _factory.CreateClient();
        var headers = SignedHeaders(DanmuBody);
        var tampered = DanmuBody.Replace("hello", "attacker");

        using var response = PostBilibili(client, tampered, headers);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingress_DuplicateEventId_Deduplicated()
    {
        var client = _factory.CreateClient();
        const string body =
            """{"cmd":"DANMU_MSG","msg_id":"m-dup","data":{"open_id":"ou-2","msg":"hi"}}""";

        using var first = PostBilibili(client, body);
        using var second = PostBilibili(client, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var payload = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("duplicated").GetBoolean());
    }

    [Fact]
    public async Task Ingress_UnknownEventType_DoesNotCrash()
    {
        var client = _factory.CreateClient();
        const string body = """{"cmd":"LIVE_START","msg_id":"m-unknown","data":{}}""";

        using var response = PostBilibili(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Ingress_MissingSignature_Rejected()
    {
        var client = _factory.CreateClient();

        using var response = PostBilibili(client, DanmuBody, headers: new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Ingress_StaleTimestamp_Rejected()
    {
        var client = _factory.CreateClient();
        var headers = SignedHeaders(DanmuBody, timestampSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 601);

        using var response = PostBilibili(client, DanmuBody, headers);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Ingress_WrongAccessKeyId_Rejected()
    {
        var client = _factory.CreateClient();
        var headers = SignedHeaders(DanmuBody, keyId: "someone-elses-key");

        using var response = PostBilibili(client, DanmuBody, headers);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
