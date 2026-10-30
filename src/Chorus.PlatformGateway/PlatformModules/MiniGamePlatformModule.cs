using Chorus.Adapters.Platforms;
using Chorus.Protocol.Models;
using Chorus.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.PlatformGateway;

/// <summary>
/// The mini-game module: the douyin mini-game server API surface (code2session login
/// credential verification, the only mini-game platform whose endpoint shape is
/// verified against official documentation — see MiniGamePlatformCapabilities).
/// This module has no event ingest, so it contributes no event-capability row; the
/// identity binder and rank upload stay library-side until their redesign hosts land.
/// The other mini-game platforms (kuaishou) stay unwired until their endpoints are
/// verified — no guessed credentials, no fabricated capability.
/// </summary>
public sealed class MiniGamePlatformModule : IPlatformModule
{
    public const string ModuleName = "minigame";

    public string Name => ModuleName;

    public PlatformModuleCapabilities Capabilities { get; } = new(
        PlatformModuleSurface.Login,
        new HashSet<AudienceEventKind>());

    public void ValidateConfiguration(IConfiguration configuration) => PlatformModuleConfiguration.RequireKeys(configuration, Name,
        "Ingress:MiniGame:Douyin:AppId",
        "Ingress:MiniGame:Douyin:AppSecret");

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new DouyinMiniGameOptions(
            AppId: configuration["Ingress:MiniGame:Douyin:AppId"]!,
            AppSecret: configuration["Ingress:MiniGame:Douyin:AppSecret"]!,
            ApiBaseUrl: configuration["Ingress:MiniGame:Douyin:ApiBaseUrl"]));
        services.AddSingleton(sp => new DouyinMiniGameAuthClient(
            sp.GetRequiredService<DouyinMiniGameOptions>(),
            new HttpMessageInvoker(new SocketsHttpHandler())));
    }
}
