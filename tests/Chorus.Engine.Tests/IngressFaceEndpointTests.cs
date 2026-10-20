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
/// Rate-limit and body-cap verification for the platform-ingress listener face
/// (user-requested gate for the listener separation). Uses its own factory with
/// a tiny rate limit and body cap.
/// </summary>
public sealed class IngressFaceEndpointTests
{
    private static Factory CreateFactory()
    {
        var factory = new Factory();
        return factory;
    }

    public sealed class Factory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
            builder.UseSetting("Ingress:Douyin:AppSecret", "face-secret");
            builder.UseSetting("Faces:PlatformIngress:RateLimitPerMinute", "3");
            builder.UseSetting("Faces:PlatformIngress:MaxBodyBytes", "256");
            builder.UseSetting("Faces:PlatformIngress:TimeoutMs", "5000");
        }
    }

    private static string Sign(string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("face-secret"), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpResponseMessage PostDouyin(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/douyin")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(DouyinPlatformEventVerifier.DefaultSignatureHeader, Sign(body));
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    private static HttpResponseMessage PostTest(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/test")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    [Fact]
    public void Ingress_RateLimit_RejectsWith429()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var body = """{"event":"like","event_id":"face-rl-1","session_id":"s-face-1","user_id":"u1"}""";

        var codes = new[]
        {
            PostDouyin(client, body).StatusCode,
            PostDouyin(client, body).StatusCode,
            PostDouyin(client, body).StatusCode,
            PostDouyin(client, body).StatusCode,
        };

        Assert.Equal(new[]
        {
            HttpStatusCode.OK,
            HttpStatusCode.OK,
            HttpStatusCode.OK,
            HttpStatusCode.TooManyRequests,
        }, codes);
    }

    [Fact]
    public void Ingress_OversizedBody_RejectsWith413()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var body = "{\"event\":\"like\",\"event_id\":\"face-big-1\",\"session_id\":\"s-face-2\",\"user_id\":\"u1\",\"pad\":\"" + new string('x', 512) + "\"}";

        using var response = PostDouyin(client, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Ingress_RateLimitCounter_ExposedViaFacesEndpoint()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        PostDouyin(client, """{"event":"like","event_id":"face-ctr-1","session_id":"s-face-3","user_id":"u1"}""");
        PostDouyin(client, """{"event":"like","event_id":"face-ctr-2","session_id":"s-face-3","user_id":"u1"}""");
        PostDouyin(client, """{"event":"like","event_id":"face-ctr-3","session_id":"s-face-3","user_id":"u1"}""");
        PostDouyin(client, """{"event":"like","event_id":"face-ctr-4","session_id":"s-face-3","user_id":"u1"}"""); // 429

        using var response = await client.GetAsync("/faces");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, payload.GetProperty("ingress").GetProperty("accepted").GetInt64());
        Assert.Equal(1, payload.GetProperty("ingress").GetProperty("rateLimited").GetInt64());
    }

    [Fact]
    public async Task Ingress_TestPlatform_UsesFallbackAndIngests()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var response = PostTest(client, """{"sessionId":"s-face-4","eventId":"e-face-1","userId":"u1","kind":"Like"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
    }
}
