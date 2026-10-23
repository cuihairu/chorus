using System.Net;
using Chorus.Adapters.Abstractions;
using Chorus.Gateway.Core.Faces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.Engine.Tests;

public sealed class IndependentGatewayHostTests
{
    private static void Configure(IWebHostBuilder builder) => builder
        .UseSetting("Ipc:Transport", "UnixDomainSocket")
        .UseSetting("Ipc:UnixSocketDir", "/tmp/chorus-independent-" + Guid.NewGuid().ToString("N"))
        .UseSetting("Ingress:AllowAll", "true");

    [Fact]
    public async Task StreamerHostOwnsOnlyWebSocketFaceEvenWithPlatformRoleSetting()
    {
        using var factory = new WebApplicationFactory<Chorus.StreamerGateway.Program>()
            .WithWebHostBuilder(b => { Configure(b); b.UseSetting("Gateway:Roles", "platform"); });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/ws")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/ingress/test", new StringContent("{}"))).StatusCode);
        Assert.NotNull(factory.Services.GetService<StreamerWsFace>());
        Assert.Null(factory.Services.GetService<PlatformIngressFace>());
        Assert.Null(factory.Services.GetService<IPlatformEventVerifier>());
        Assert.Contains("Chorus.StreamerGateway", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task PlatformHostOwnsOnlyIngressFaceEvenWithStreamerRoleSetting()
    {
        using var factory = new WebApplicationFactory<Chorus.PlatformGateway.Program>()
            .WithWebHostBuilder(b => { Configure(b); b.UseSetting("Gateway:Roles", "streamer"); });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ws")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/ingress/test", new StringContent("{}"))).StatusCode);
        Assert.NotNull(factory.Services.GetService<PlatformIngressFace>());
        Assert.Null(factory.Services.GetService<StreamerWsFace>());
        Assert.Contains("Chorus.PlatformGateway", await client.GetStringAsync("/"));
    }
}
