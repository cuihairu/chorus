using System.Net;
using System.Text;
using Chorus.Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Role packaging for the three-process topology (design §4, todo 进程拓扑):
/// Gateway:Roles picks which listener roles one gateway process hosts — all
/// (default, unchanged), platform (platform-gw) or streamer (streamer-gw).
/// The faces stay shared; the gate only omits the other role's endpoint, so
/// the deployment shape becomes config, not a code fork.
///
/// Mapping is asserted with payloads that fail before the Tell (unmapped 400
/// / non-WS 400), so no engine is needed: mapped endpoints answer 400, omitted
/// ones 404.
/// </summary>
public sealed class GatewayRolesTests
{
    public sealed class PlatformOnlyFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-roles-{Guid.NewGuid():N}");
            builder.UseSetting("Gateway:Roles", "platform");
        }
    }

    public sealed class StreamerOnlyFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-roles-{Guid.NewGuid():N}");
            builder.UseSetting("Gateway:Roles", "streamer");
        }
    }

    public sealed class DefaultFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-roles-{Guid.NewGuid():N}");
        }
    }

    [Fact]
    public async Task PlatformRole_MapsIngress_OmitsWs()
    {
        using var factory = new PlatformOnlyFactory();
        var client = factory.CreateClient();

        var ingress = await PostUnmappedAsync(client);
        var ws = await client.GetAsync("/ws");

        // Mapped: the face answers unmapped_event itself (400), no engine needed.
        Assert.Equal(HttpStatusCode.BadRequest, ingress);
        Assert.Equal(HttpStatusCode.NotFound, ws.StatusCode);
    }

    [Fact]
    public async Task StreamerRole_MapsWs_OmitsIngress()
    {
        using var factory = new StreamerOnlyFactory();
        var client = factory.CreateClient();

        var ingress = await PostUnmappedAsync(client);
        var ws = await client.GetAsync("/ws");

        Assert.Equal(HttpStatusCode.NotFound, ingress);
        // Mapped: the face rejects the non-WebSocket GET itself (400).
        Assert.Equal(HttpStatusCode.BadRequest, ws.StatusCode);
    }

    [Fact]
    public async Task DefaultRole_MapsBothListeners()
    {
        using var factory = new DefaultFactory();
        var client = factory.CreateClient();

        var ingress = await PostUnmappedAsync(client);
        var ws = await client.GetAsync("/ws");

        Assert.Equal(HttpStatusCode.BadRequest, ingress);
        Assert.Equal(HttpStatusCode.BadRequest, ws.StatusCode);
    }

    [Fact]
    public void Parse_BlankOrUnknown()
    {
        Assert.Equal(GatewayRoleSet.All, GatewayRoles.Parse(null));
        Assert.Equal(GatewayRoleSet.All, GatewayRoles.Parse(""));
        Assert.Equal(GatewayRoleSet.All, GatewayRoles.Parse(" ALL "));
        Assert.Equal(GatewayRoleSet.Platform, GatewayRoles.Parse("Platform"));
        Assert.Equal(GatewayRoleSet.Streamer, GatewayRoles.Parse("streamer"));
        Assert.Throws<InvalidOperationException>(() => GatewayRoles.Parse("both"));
    }

    private static async Task<HttpStatusCode> PostUnmappedAsync(HttpClient client)
    {
        // No sessionId: TestPlatformEventMapper returns null, the face answers
        // 400 unmapped_event before any Tell (no engine dependency).
        var response = await client.PostAsync("/ingress/test", new StringContent(
            "{}",
            Encoding.UTF8,
            "application/json"));
        return response.StatusCode;
    }
}
