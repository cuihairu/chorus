using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Split-mode Tell acceptance (todo §3.5, shared seam): the gateway's
/// <c>IpcSessionActorRouter</c> publishes audience events over the engine IPC.
/// With no engine listening the Tell surfaces as Unavailable and the shared
/// ingress face answers 503 with the dedup claim rolled back — the same
/// retry posture the monolith's lane-full Rejected gets.
/// </summary>
public sealed class GatewayIngressTellTests
{
    public sealed class EngineDownFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-tell-{Guid.NewGuid():N}");
        }
    }

    [Fact]
    public async Task Ingress_WithEngineDown_TellUnavailable_Returns503()
    {
        using var factory = new EngineDownFactory();
        var client = factory.CreateClient();

        var first = await PostEventAsync(client);
        var retry = await PostEventAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry);
    }

    private static async Task<HttpStatusCode> PostEventAsync(HttpClient client)
    {
        var response = await client.PostAsync("/ingress/test", new StringContent(
            "{\"sessionId\":\"s-tell\",\"eventId\":\"ev-tell-1\",\"userId\":\"u1\",\"kind\":\"comment\",\"text\":\"hi\"}",
            Encoding.UTF8,
            "application/json"));
        return response.StatusCode;
    }
}
