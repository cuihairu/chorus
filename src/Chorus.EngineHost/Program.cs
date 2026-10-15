using Chorus.Data;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway.Core.Leaderboard;
using Chorus.Gateway.Core.Metrics;
using Chorus.SampleGame;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
// Leaderboard (audit §9): split mode keeps the store engine-side with the
// sessions it scores; the gateway holds no board and exposes no endpoint.
builder.Services.AddSingleton<LeaderboardStore>();
builder.Services.AddSingleton<ISessionScoreSink, LeaderboardScoreSink>();
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
        MaxSnapshotBytes: cfg.GetValue("Session:MaxSnapshotBytes", 1_048_576),
        FrameHistoryBytes: cfg.GetValue("Session:FrameHistoryBytes", 8_388_608),
        ResyncRequestCooldownMs: cfg.GetValue("Session:ResyncRequestCooldownMs", 1000)
    );
});
// Register the concrete type (not just IHostedService): the /metrics and
// /sessions endpoints inject EngineIpcService directly. AddHostedService alone
// only registers it under IHostedService, which made those endpoints 500.
builder.Services.AddSingleton<EngineIpcService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineIpcService>());

var app = builder.Build();

var processCpu = new ProcessCpuGauge();

app.MapGet("/", () => new { name = "Chorus.EngineHost", ok = true });
app.MapGet("/health", () => Results.Ok());
// Session/client diagnostics (per-client lag/ack detail the /metrics aggregates
// cannot give). The engine is the authority for session state in split mode, so
// exposing it here restores parity with the monolith's /sessions.
app.MapGet("/sessions", (EngineIpcService engine) => engine.GetDiagnostics());
app.MapLeaderboard();
app.MapGet("/metrics", (EngineIpcService engine) => Results.Text(
    ChorusMetricsText.RenderEngine(
        engine.GetDiagnostics(),
        engine.PublishFailures,
        processCpu.Sample(),
        registry: new Chorus.Gateway.Core.Metrics.SessionRegistryCounters(
            engine.StartFailures, engine.IdleEvictions, engine.CapacityEvictions)),
    ChorusMetricsText.ContentTypeValue));

app.Run();
