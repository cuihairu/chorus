using Chorus.Data;
using Chorus.DevTools;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway.Core.Leaderboard;
using Chorus.Gateway.Core.Metrics;
using Chorus.Messaging.Ipc;
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
        ResyncRequestCooldownMs: cfg.GetValue("Session:ResyncRequestCooldownMs", 1000),
        MaxResyncReplayTicks: cfg.GetValue("Session:MaxResyncReplayTicks", 128),
        CloseClientlessIdleRooms: cfg.GetValue("Session:CloseClientlessIdleRooms", false)
    );
});
// Register the concrete type (not just IHostedService): the /metrics and
// /sessions endpoints inject EngineIpcService directly. AddHostedService alone
// only registers it under IHostedService, which made those endpoints 500.
builder.Services.AddSingleton<EngineIpcService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineIpcService>());

var processCpu = new ProcessCpuGauge();

// DevTools (IDE-EXT): an IDE-facing push channel (dev.log / dev.metrics over
// the IPC socket framing) for IDE attach. Default off, and refused in the
// Production environment — the same discipline as Ingress:AllowAll; naming it
// there is a misconfiguration, fail fast. The bare --devtools flag answers
// from the raw args (the command-line provider silently drops a valueless
// switch — nothing lands in configuration); the valued forms
// (--devtools=true, DevTools:Enabled=true) ride the config keys.
if (args.Contains("--devtools", StringComparer.Ordinal)
    || builder.Configuration.GetValue("DevTools:Enabled", false)
    || builder.Configuration.GetValue("devtools", false))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "DevTools is a development surface and is refused in the Production environment.");
    }
    builder.Services.AddChorusDevTools(options =>
    {
        // The IPC section's own keys drive the channel endpoints, so one
        // configuration carries both planes; the sampler composes the same
        // gauge the /metrics face samples — one caliber for both faces.
        options.Transport = builder.Configuration.GetValue("Ipc:Transport", IpcTransport.Auto);
        options.UnixSocketDir = builder.Configuration.GetValue("Ipc:UnixSocketDir", "/tmp/chorus");
        options.PipePrefix = builder.Configuration.GetValue("Ipc:NamedPipePrefix", "chorus");
        options.MetricsSampler = () => DevMetricsSnapshot.SelfProcess(processCpu.Sample());
    });
}

var app = builder.Build();

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
            engine.StartFailures, engine.IdleEvictions, engine.CapacityEvictions, engine.CapacityRejections)),
    ChorusMetricsText.ContentTypeValue));

app.Run();
