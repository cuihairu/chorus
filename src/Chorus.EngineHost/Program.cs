using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.SampleGame;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new SessionConfig(
        TickDurationMs: cfg.GetValue("Session:TickDurationMs", 100),
        InputDelayTicks: cfg.GetValue("Session:InputDelayTicks", 2),
        SnapshotEveryTicks: cfg.GetValue("Session:SnapshotEveryTicks", 1),
        MaxEventsPerTick: cfg.GetValue("Session:MaxEventsPerTick", 200),
        MaxLagTicks: cfg.GetValue("Session:MaxLagTicks", 50),
        ResyncCooldownMs: cfg.GetValue("Session:ResyncCooldownMs", 2000)
    );
});
builder.Services.AddHostedService<EngineIpcService>();

var app = builder.Build();

app.MapGet("/", () => new { name = "Chorus.EngineHost", ok = true });
app.MapGet("/health", () => Results.Ok());

app.Run();
