using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Golden tests for the Douyin ingress pipeline over the real HTTP endpoint
/// (official-sample style payloads: valid signature accepted, tampered
/// rejected, duplicate event ids deduplicated, unknown event types do not
/// crash). Signature/fields follow the placeholder contract documented in
/// docs/reference/platform-douyin.md.
/// </summary>
public sealed class DouyinIngressEndpointTests : IClassFixture<DouyinIngressEndpointTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:Douyin:AppSecret", "e2e-secret");
            builder.UseSetting("Ingress:DedupTtlSeconds", "300");
        }
    }

    private readonly Factory _factory;

    public DouyinIngressEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    private static string Sign(string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("e2e-secret"), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpResponseMessage PostDouyin(HttpClient client, string body, string? signature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/douyin")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(DouyinPlatformEventVerifier.DefaultSignatureHeader, signature ?? Sign(body));
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    // Every accepted payload carries the signature-covered freshness timestamp (review M5).
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Ingress_CorrectSignature_Accepts()
    {
        var client = _factory.CreateClient();
        var body = $$"""{"event":"comment","event_id":"e2e-ok-1","timestamp":{{NowMs}},"session_id":"s-e2e-1","user_id":"u1","content":"hi"}""";

        using var response = PostDouyin(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("s-e2e-1", payload.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void Ingress_TamperedBody_Rejected()
    {
        var client = _factory.CreateClient();
        var body = """{"event":"comment","event_id":"e2e-tamper-1","session_id":"s-e2e-2","user_id":"u1","content":"hi"}""";
        var signature = Sign(body);
        var tampered = body.Replace("u1", "attacker");

        using var response = PostDouyin(client, tampered, signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingress_DuplicateEventId_Deduplicated()
    {
        var client = _factory.CreateClient();
        var body = $$"""{"event":"like","event_id":"e2e-dup-1","timestamp":{{NowMs}},"session_id":"s-e2e-3","user_id":"u1"}""";

        using var first = PostDouyin(client, body);
        using var second = PostDouyin(client, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var payload = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("duplicated").GetBoolean());
    }

    [Fact]
    public async Task Ingress_UnknownEventType_DoesNotCrash()
    {
        var client = _factory.CreateClient();
        var body = $$"""{"event":"share","event_id":"e2e-unknown-1","timestamp":{{NowMs}},"session_id":"s-e2e-4","user_id":"u1"}""";

        using var response = PostDouyin(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Ingress_SignedNonJsonBody_FailClosedUnauthorized()
    {
        // Freshness runs after the signature and parses the body: a validly signed
        // payload with no parseable timestamp is refused (401) before any mapping.
        var client = _factory.CreateClient();

        using var response = PostDouyin(client, "not-json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Ingress_SignedPayloadMissingSession_Unmapped()
    {
        // The mapper's 400 unmapped path stays reachable past the freshness bound:
        // a fresh, validly signed payload without session_id is still unmapped.
        var client = _factory.CreateClient();
        var body = $$"""{"event":"comment","event_id":"e2e-unmapped-1","timestamp":{{NowMs}},"user_id":"u1"}""";

        using var response = PostDouyin(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Ingress_MissingSignature_Rejected()
    {
        var client = _factory.CreateClient();
        var body = """{"event":"comment","event_id":"e2e-nosig-1","session_id":"s-e2e-5","user_id":"u1"}""";

        using var response = PostDouyin(client, body, signature: " ");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
