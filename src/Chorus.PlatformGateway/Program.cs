using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Gateway.Core.Actors;
using Chorus.Gateway;
using Chorus.Gateway.Core.Faces;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGatewayRuntime();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(IngressFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IngressFaceGuard>();
builder.Services.AddSingleton<ISessionActorRouter>(sp => new IpcSessionActorRouter(sp.GetRequiredService<GatewayIpcService>()));
builder.Services.AddSingleton(sp => new PlatformIngressFace(
    sp.GetRequiredService<IPlatformEventVerifier>(),
    sp.GetServices<IPlatformEventMapper>(),
    sp.GetRequiredService<IAudienceFactionRule>(),
    sp.GetRequiredService<EventDeduplicator>(),
    sp.GetRequiredService<IngressFaceGuard>(),
    sp.GetRequiredService<FaceCounters>(),
    sp.GetRequiredService<ISessionActorRouter>()));
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

if (BilibiliIngressBridgeHostedService.IsConfigured(builder.Configuration))
{
    builder.Services.AddHostedService(sp => BilibiliIngressBridgeHostedService.Create(sp));
}

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Ingress:AllowAll"))
    app.Logger.LogWarning("Ingress:AllowAll is enabled; platform signatures are not required. Development only.");
app.MapGatewayDiagnostics("Chorus.PlatformGateway");
app.MapPost("/ingress/{platform}", (string platform, HttpContext context, PlatformIngressFace face) => face.HandleAsync(platform, context));
app.Run();

static IReadOnlyDictionary<string, string> BuildFactionMap(IConfigurationSection section)
{
    return section.GetChildren()
        .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
        .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
}

