using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.PlatformGateway;

/// <summary>
/// The douyin live-event module: signature verification (app secret; the signature
/// header has an adapter-side default) and the placeholder-schema event mapper.
/// Capability row reuses the engine-side declaration
/// (<see cref="PlatformCapabilities.Douyin"/>).
/// </summary>
public sealed class DouyinPlatformModule : IPlatformModule
{
    public string Name => PlatformNames.Douyin;

    public PlatformModuleCapabilities Capabilities { get; } = new(
        PlatformModuleSurface.EventIngest,
        PlatformCapabilities.Douyin.SupportedEventKinds.ToHashSet(),
        // The envelope below is the placeholder contract agreed for integration testing,
        // not the official Douyin schema (see DouyinPlatformEventMapper's STATUS note):
        // signature header and field names await joint debugging. Not production ready.
        ProductionReady: false);

    public void ValidateConfiguration(IConfiguration configuration) => PlatformModuleConfiguration.RequireKeys(configuration, Name,
        "Ingress:Douyin:AppSecret");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new DouyinPlatformEventVerifier(
            secret: configuration["Ingress:Douyin:AppSecret"],
            appId: configuration["Ingress:Douyin:AppId"],
            signatureHeader: configuration["Ingress:Douyin:SignatureHeader"]));
        services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<DouyinPlatformEventVerifier>());
        services.AddSingleton<IPlatformEventMapper, DouyinPlatformEventMapper>();
    }
}
