using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chorus.DevTools;

public static class DevToolsServiceCollectionExtensions
{
    /// <summary>Registers the devtools channel: the log-forwarding provider
    /// and the push service, sharing one DropOldest bounded queue. Nothing here
    /// turns the channel on by itself — the host gates the registration behind
    /// its own explicit flag and refuses it in Production (the Ingress:AllowAll
    /// discipline), so a default host never binds the socket.</summary>
    public static IServiceCollection AddChorusDevTools(this IServiceCollection services,
        Action<DevToolsOptions>? configure = null)
    {
        var options = new DevToolsOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(_ => Channel.CreateBounded<DevLogEntry>(
            new BoundedChannelOptions(Math.Max(1, options.LogQueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            }));
        services.AddSingleton<DevToolsLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<DevToolsLoggerProvider>());
        // The concrete type (not AddHostedService alone): tests and future
        // endpoints inject DevToolsService directly — the same trap
        // EngineIpcService's registration documents.
        services.AddSingleton<DevToolsService>();
        services.AddHostedService(sp => sp.GetRequiredService<DevToolsService>());
        return services;
    }
}
