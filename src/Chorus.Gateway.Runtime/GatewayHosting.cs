using Chorus.Gateway.Core.Faces;
using Chorus.Gateway.Core.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.Gateway;

/// <summary>Transport and diagnostics shared by the independently deployed gateway hosts.</summary>
public static class GatewayHosting
{
    public static IServiceCollection AddGatewayRuntime(this IServiceCollection services)
    {
        services.AddSingleton<FaceCounters>();
        services.AddSingleton<GatewayIpcService>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayIpcService>());
        return services;
    }

    public static void MapGatewayDiagnostics(this WebApplication app, string serviceName)
    {
        app.MapGet("/", () => new { name = serviceName, ok = true });
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/faces", (FaceCounters faces) => faces);
        app.MapGet("/metrics", (FaceCounters faces, GatewayIpcService ipc) => Results.Text(
            ChorusMetricsText.RenderGateway(faces, ipc.PublishFailures, ipc.EngineSilenceMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
            ChorusMetricsText.ContentTypeValue));
        app.MapGet("/ready", (GatewayIpcService ipc) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var body = new { ready = ipc.IsEngineReady(now), engineSilenceMs = ipc.EngineSilenceMs(now) };
            return Results.Json(body, statusCode: body.ready ? 200 : 503);
        });
    }
}
