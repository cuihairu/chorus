using System.Net;
using System.Net.Http.Json;
using System.Text;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Ingress:AllowAll switch (review P0-4 acceptance: without a configured
/// verifier/secret, external ingress must not accept unverified events).
/// Default is fail-closed; the unsigned dev verifier only exists when the
/// switch is explicitly enabled.
/// </summary>
public sealed class IngressAllowAllEndpointTests
{
    public sealed class LockedFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "false");
        }
    }

    public sealed class AllowAllFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
        }
    }

    private static HttpResponseMessage PostTestEvent(WebApplicationFactory<Chorus.Server.Program> factory)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingress/test")
        {
            Content = new StringContent(
                """{"sessionId":"s-allowall","eventId":"ev-1","userId":"u1","kind":"comment","text":"hi"}""",
                Encoding.UTF8,
                "application/json"),
        };
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    [Fact]
    public void DefaultLocked_UnverifiedCallbackRejected()
    {
        using var factory = new LockedFactory();

        using var response = PostTestEvent(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void ExplicitAllowAll_AcceptsUnsignedCallback()
    {
        using var factory = new AllowAllFactory();

        using var response = PostTestEvent(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().GetAwaiter().GetResult();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("s-allowall", payload.GetProperty("sessionId").GetString());
    }
}
