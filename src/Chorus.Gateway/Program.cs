using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Gateway;
using Chorus.Gateway.Core.Actors;
using Chorus.Gateway.Core.Faces;
using Chorus.Gateway.Core.Metrics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Role packaging for the three-process topology (docs/design/process-topology.md
// §4): one gateway process hosts both listener roles by default; platform /
// streamer run it as platform-gw / streamer-gw with the same shared faces.
var gatewayRoles = GatewayRoles.Parse(builder.Configuration["Gateway:Roles"]);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(IngressFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IngressFaceGuard>();
builder.Services.AddSingleton<FaceCounters>();
builder.Services.AddSingleton(StreamerWsFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(sp => new IpcSessionRouter(
    sp.GetRequiredService<GatewayIpcService>(),
    sp.GetRequiredService<FaceCounters>()));

// Tell seam (todo §3.5): the ingress face delivers events through the shared
// ISessionActorRouter; this host routes Tells over the engine IPC.
builder.Services.AddSingleton<ISessionActorRouter>(sp => new IpcSessionActorRouter(sp.GetRequiredService<GatewayIpcService>()));
builder.Services.AddSingleton(sp => new PlatformIngressFace(
    sp.GetRequiredService<IPlatformEventVerifier>(),
    sp.GetServices<IPlatformEventMapper>(),
    sp.GetRequiredService<IAudienceFactionRule>(),
    sp.GetRequiredService<EventDeduplicator>(),
    sp.GetRequiredService<IngressFaceGuard>(),
    sp.GetRequiredService<FaceCounters>(),
    sp.GetRequiredService<ISessionActorRouter>()));
builder.Services.AddSingleton(sp => new StreamerWsFace(
    sp.GetRequiredService<StreamerWsFaceOptions>(),
    sp.GetRequiredService<IpcSessionRouter>(),
    sp.GetRequiredService<FaceCounters>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Chorus.Gateway.StreamerWs")));
// Ingress:AllowAll (default false) keeps the unsigned dev verifier registered;
// without it the router fails closed for platforms without a real verifier.
if (builder.Configuration.GetValue("Ingress:AllowAll", false))
{
    builder.Services.AddSingleton<AllowAllPlatformEventVerifier>();
}
builder.Services.AddSingleton(sp =>
    new DouyinPlatformEventVerifier(
        secret: sp.GetRequiredService<IConfiguration>()["Ingress:Douyin:AppSecret"],
        signatureHeader: sp.GetRequiredService<IConfiguration>()["Ingress:Douyin:SignatureHeader"]));
builder.Services.AddSingleton(sp =>
    new KuaishouPlatformEventVerifier(
        verifyToken: sp.GetRequiredService<IConfiguration>()["Ingress:Kuaishou:VerifyToken"],
        signatureHeader: sp.GetRequiredService<IConfiguration>()["Ingress:Kuaishou:SignatureHeader"]));
builder.Services.AddSingleton(sp =>
    new BilibiliPlatformEventVerifier(
        accessKeyId: sp.GetRequiredService<IConfiguration>()["Ingress:Bilibili:AccessKeyId"],
        accessKeySecret: sp.GetRequiredService<IConfiguration>()["Ingress:Bilibili:AccessKeySecret"]));
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<DouyinPlatformEventVerifier>());
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<KuaishouPlatformEventVerifier>());
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<BilibiliPlatformEventVerifier>());
builder.Services.AddSingleton<PlatformEventVerifierRouter>(sp => new PlatformEventVerifierRouter(
    sp.GetServices<IPlatformNamedEventVerifier>(),
    fallback: sp.GetService<AllowAllPlatformEventVerifier>()));
builder.Services.AddSingleton<IPlatformEventVerifier>(sp => sp.GetRequiredService<PlatformEventVerifierRouter>());
builder.Services.AddSingleton<IPlatformEventMapper, TestPlatformEventMapper>();
builder.Services.AddSingleton<PlatformCapabilityRegistry>(_ =>
{
    var registry = new PlatformCapabilityRegistry();
    registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);
    registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);
    registry.Register(PlatformNames.Kuaishou, PlatformCapabilities.Kuaishou);
    return registry;
});
builder.Services.AddSingleton<IPlatformEventMapper, DouyinPlatformEventMapper>();
builder.Services.AddSingleton<IPlatformEventMapper>(sp => new KuaishouPlatformEventMapper(
    messageEncryptKey: sp.GetRequiredService<IConfiguration>()["Ingress:Kuaishou:MsgEncryptKey"]));
builder.Services.AddSingleton<IPlatformEventMapper>(sp => new BilibiliPlatformEventMapper(
    sessionId: sp.GetRequiredService<IConfiguration>()["Ingress:Bilibili:SessionId"]));
builder.Services.AddSingleton<IAudienceFactionRule>(_ =>
    new KeywordGiftAudienceFactionRule(
        commentKeywords: BuildFactionMap(builder.Configuration.GetSection("Ingress:FactionRules:CommentKeywords")),
        giftIds: BuildFactionMap(builder.Configuration.GetSection("Ingress:FactionRules:GiftIds"))));
builder.Services.AddSingleton(sp =>
{
    var ttlSeconds = sp.GetRequiredService<IConfiguration>().GetValue("Ingress:DedupTtlSeconds", 600);
    return new EventDeduplicator(TimeSpan.FromSeconds(ttlSeconds));
});

builder.Services.AddSingleton<GatewayIpcService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayIpcService>());

// B站 open-live 桥（todo §12 P1）：凭据/会话绑定/转发目标齐全才启动，
// 把 open-live wss 事件签名转发进 /ingress/bilibili 的共享管线。
if (BilibiliIngressBridgeHostedService.IsConfigured(builder.Configuration))
{
    builder.Services.AddHostedService(sp => BilibiliIngressBridgeHostedService.Create(sp));
}

var app = builder.Build();
app.UseWebSockets();

// Loud startup warnings for the two dev-only switches (docs/reference/config.md).
if (builder.Configuration.GetValue("Ingress:AllowAll", false))
{
    app.Logger.LogError(
        "Ingress:AllowAll is ENABLED: platform callbacks are accepted WITHOUT signature verification. Dev/demo only - never enable in production.");
}

var streamerWsFaces = app.Services.GetRequiredService<StreamerWsFaceOptions>();
if (streamerWsFaces.AllowAnonymous)
{
    app.Logger.LogWarning(
        "Faces:StreamerWs:AllowAnonymous is ENABLED: streamer WS connections are accepted without a token. Dev/demo only.");
}

if (streamerWsFaces.Tokens.Count == 0 && !streamerWsFaces.AllowAnonymous)
{
    app.Logger.LogError(
        "No streamer WS tokens configured (Faces:StreamerWs:AuthTokens) and anonymous access is off: all WS connections will be rejected. Configure tokens or enable AllowAnonymous for local dev.");
}

app.MapGet("/", () => new { name = "Chorus.Gateway", ok = true });
app.MapGet("/health", () => Results.Ok());
app.MapGet("/faces", (FaceCounters faces) => faces);
app.MapGet("/metrics", (FaceCounters faces, GatewayIpcService ipc) => Results.Text(
    ChorusMetricsText.RenderGateway(faces, ipc.PublishFailures, ipc.EngineSilenceMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
    ChorusMetricsText.ContentTypeValue));

// Readiness probe (docs/reference/deployment.md "split-mode readiness probe"):
// 200 only while EngineHost messages (heartbeats/welcomes/snapshots) arrive
// within Ipc:ReadinessMaxSilenceMs; 503 - and no traffic - otherwise.
app.MapGet("/ready", (GatewayIpcService ipc) =>
{
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var ready = ipc.IsEngineReady(nowMs);
    return ready
        ? Results.Ok(new { ready = true, engineSilenceMs = ipc.EngineSilenceMs(nowMs) })
        : Results.Json(new { ready = false, engineSilenceMs = ipc.EngineSilenceMs(nowMs) }, statusCode: 503);
});

// The two listener endpoints are gated by Gateway:Roles; host-level surface
// (health/faces/metrics/ready) stays regardless of the packaging.
if (gatewayRoles is GatewayRoleSet.All or GatewayRoleSet.Platform)
{
    // platform-ingress listener face (docs/design/process-topology.md §4): shared
    // with the monolith via PlatformIngressFace; this host tells over engine IPC.
    app.MapPost("/ingress/{platform}", (string platform, HttpContext context, PlatformIngressFace face) => face.HandleAsync(platform, context));
}

if (gatewayRoles is GatewayRoleSet.All or GatewayRoleSet.Streamer)
{
    // streamer-ws listener face: hello token auth (fail-closed), message body
    // cap, ACK rate limit (docs/design/process-topology.md §4). Shared with the
    // monolith via StreamerWsFace; this host routes clients over the engine IPC.
    app.Map("/ws", async context =>
    {
        await context.RequestServices.GetRequiredService<StreamerWsFace>().HandleAsync(context);
    });
}

app.Run();

static IReadOnlyDictionary<string, string> BuildFactionMap(IConfigurationSection section)
{
    return section.GetChildren()
        .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
        .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
}

