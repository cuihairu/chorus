using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.PlatformGateway;

/// <summary>
/// The bilibili open-live module: event verification (access-key signature), event
/// mapping (session-bound), and — when the full bridge credential set is present — the
/// open-live ingress bridge hosted service. Capability row reuses the engine-side
/// declaration (<see cref="PlatformCapabilities.Bilibili"/>) instead of restating it.
/// </summary>
public sealed class BilibiliPlatformModule : IPlatformModule
{
    public string Name => PlatformNames.Bilibili;

    public PlatformModuleCapabilities Capabilities { get; } = new(
        PlatformModuleSurface.EventIngest,
        PlatformCapabilities.Bilibili.SupportedEventKinds.ToHashSet());

    public void ValidateConfiguration(IConfiguration configuration) => PlatformModuleConfiguration.RequireKeys(configuration, Name,
        "Ingress:Bilibili:AccessKeyId",
        "Ingress:Bilibili:AccessKeySecret",
        "Ingress:Bilibili:SessionId");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new BilibiliPlatformEventVerifier(
            accessKeyId: configuration["Ingress:Bilibili:AccessKeyId"],
            accessKeySecret: configuration["Ingress:Bilibili:AccessKeySecret"]));
        services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<BilibiliPlatformEventVerifier>());
        services.AddSingleton<IPlatformEventMapper>(_ => new BilibiliPlatformEventMapper(
            sessionId: configuration["Ingress:Bilibili:SessionId"]));
        // The bridge is part of this module but mounts only on the complete credential set
        // (IsConfigured also demands AppId/RoomOwnerAuthCode/IngressUrl) — same rule as the
        // production gateway line: a verifier without the long-lived bridge still ingests
        // webhook pushes.
        if (BilibiliIngressBridgeHostedService.IsConfigured(configuration))
        {
            services.AddHostedService(BilibiliIngressBridgeHostedService.Create);
        }
    }
}
