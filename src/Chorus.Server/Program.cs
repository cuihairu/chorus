using Chorus.Engine.Abstractions;
using Chorus.Engine.Routing;
using Chorus.Engine.Session;
using Chorus.Gateway.Core.Metrics;
using Chorus.Server;
using Chorus.SampleGame;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Gateway.Core.Actors;
using Chorus.Gateway.Core.Faces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Session").Get<SessionConfig>() ?? new SessionConfig());
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MatchRegistry>();

// Tell seam (todo §3.5): the ingress face delivers events through the shared
// ISessionActorRouter; the monolith resolves actors in-process.
builder.Services.AddSingleton<ISessionActorRouter>(sp => new InProcSessionActorRouter(sp.GetRequiredService<SessionManager>()));
builder.Services.AddSingleton(sp => new PlatformIngressFace(
    sp.GetRequiredService<IPlatformEventVerifier>(),
    sp.GetServices<IPlatformEventMapper>(),
    sp.GetRequiredService<IAudienceFactionRule>(),
    sp.GetRequiredService<EventDeduplicator>(),
    sp.GetRequiredService<IngressFaceGuard>(),
    sp.GetRequiredService<FaceCounters>(),
    sp.GetRequiredService<ISessionActorRouter>(),
    sp.GetRequiredService<MatchRegistry>()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(IngressFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IngressFaceGuard>();
builder.Services.AddSingleton<FaceCounters>();
builder.Services.AddSingleton(StreamerWsFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(sp => new MonolithSessionRouter(
        sp.GetRequiredService<SessionManager>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Chorus.StreamerWs")));
builder.Services.AddSingleton(sp => new StreamerWsFace(
    sp.GetRequiredService<StreamerWsFaceOptions>(),
    sp.GetRequiredService<MonolithSessionRouter>(),
    sp.GetRequiredService<FaceCounters>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Chorus.StreamerWs")));
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

app.MapGet("/", () => new { name = "Chorus", ok = true });
app.MapGet("/health", () => Results.Ok());
app.MapGet("/sessions", (SessionManager sessions) => sessions.GetDiagnostics());
app.MapGet("/matches", (MatchRegistry matches) => matches.GetDiagnostics());
app.MapGet("/faces", (FaceCounters faces) => faces);
// Single-process topology has no IPC hop; publish failures stay 0 here and
// are real counters on the Gateway/EngineHost surfaces.
app.MapGet("/metrics", (SessionManager sessions, FaceCounters faces) => Results.Text(
    ChorusMetricsText.RenderServer(sessions.GetDiagnostics(), faces, ipcPublishFailures: 0),
    ChorusMetricsText.ContentTypeValue));

// platform-ingress listener face (docs/design/process-topology.md §4): shared
// with the gateway via PlatformIngressFace; this host tells in-process actors.
app.MapPost("/ingress/{platform}", (string platform, HttpContext context, PlatformIngressFace face) => face.HandleAsync(platform, context));

// streamer-ws listener face: hello token auth (fail-closed), message body
// cap, ACK rate limit (docs/design/process-topology.md §4). Shared with the
// gateway via StreamerWsFace; this host resolves sessions in-process.
app.Map("/ws", async context =>
{
    await context.RequestServices.GetRequiredService<StreamerWsFace>().HandleAsync(context);
});

app.Run();

static IReadOnlyDictionary<string, string> BuildFactionMap(IConfigurationSection section)
{
    return section.GetChildren()
        .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
        .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
}
