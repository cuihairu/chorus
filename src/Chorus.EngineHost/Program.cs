using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway.Core.Metrics;
using Chorus.SampleGame;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new SessionConfig(
        TickDurationMs: cfg.GetValue("Session:TickDurationMs", 66),
        InputDelayTicks: cfg.GetValue("Session:InputDelayTicks", 2),
        SnapshotEveryTicks: cfg.GetValue("Session:SnapshotEveryTicks", 1),
        MaxEventsPerTick: cfg.GetValue("Session:MaxEventsPerTick", 200),
        MaxLagTicks: cfg.GetValue("Session:MaxLagTicks", 50),
        ResyncCooldownMs: cfg.GetValue("Session:ResyncCooldownMs", 2000),
        MaxPendingEvents: cfg.GetValue("Session:MaxPendingEvents", 8000),
        EventEnqueueMaxWaitMs: cfg.GetValue("Session:EventEnqueueMaxWaitMs", 50),
        TickTimeoutMs: cfg.GetValue("Session:TickTimeoutMs", 250),
        MaxSessions: cfg.GetValue("Session:MaxSessions", 200),
        IdleEvictAfterMs: cfg.GetValue("Session:IdleEvictAfterMs", 600_000),
        MaxSnapshotBytes: cfg.GetValue("Session:MaxSnapshotBytes", 1_048_576)
    );
});
builder.Services.AddHostedService<EngineIpcService>();

var app = builder.Build();

var processCpu = new ProcessCpuGauge();

app.MapGet("/", () => new { name = "Chorus.EngineHost", ok = true });
app.MapGet("/health", () => Results.Ok());
app.MapGet("/metrics", (EngineIpcService engine) => Results.Text(
    ChorusMetricsText.RenderEngine(engine.GetDiagnostics(), processCpu.Sample()),
    ChorusMetricsText.ContentTypeValue));

app.Run();
