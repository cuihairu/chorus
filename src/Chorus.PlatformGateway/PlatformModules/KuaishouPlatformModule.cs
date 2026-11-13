using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.PlatformGateway;

/// <summary>
/// The kuaishou live-event module: token verification and message decryption mapping
/// (pushes arrive encrypted, so the encrypt key is a working requirement, not an
/// option). Capability row reuses the engine-side declaration
/// (<see cref="PlatformCapabilities.Kuaishou"/>).
/// </summary>
public sealed class KuaishouPlatformModule : IPlatformModule
{
    public string Name => PlatformNames.Kuaishou;

    public PlatformModuleCapabilities Capabilities { get; } = new(
        PlatformModuleSurface.EventIngest,
        PlatformCapabilities.Kuaishou.SupportedEventKinds.ToHashSet(),
        // Token verification and message decryption follow the documented push scheme, but
        // the live event schema has not survived joint debugging against real callbacks.
        // Not production ready until that round lands.
        ProductionReady: false);

    public void ValidateConfiguration(IConfiguration configuration) => PlatformModuleConfiguration.RequireKeys(configuration, Name,
        "Ingress:Kuaishou:VerifyToken",
        "Ingress:Kuaishou:MsgEncryptKey");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new KuaishouPlatformEventVerifier(
            verifyToken: configuration["Ingress:Kuaishou:VerifyToken"],
            appId: configuration["Ingress:Kuaishou:AppId"],
            signatureHeader: configuration["Ingress:Kuaishou:SignatureHeader"]));
        services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<KuaishouPlatformEventVerifier>());
        services.AddSingleton<IPlatformEventMapper>(_ => new KuaishouPlatformEventMapper(
            messageEncryptKey: configuration["Ingress:Kuaishou:MsgEncryptKey"]));
    }
}
