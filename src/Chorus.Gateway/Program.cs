using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Gateway;
using Chorus.Gateway.Core.Faces;
using Chorus.Gateway.Core.Metrics;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(IngressFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IngressFaceGuard>();
builder.Services.AddSingleton<FaceCounters>();
builder.Services.AddSingleton(StreamerWsFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(sp => new IpcSessionRouter(sp.GetRequiredService<GatewayIpcService>()));
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
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<DouyinPlatformEventVerifier>());
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<KuaishouPlatformEventVerifier>());
builder.Services.AddSingleton<PlatformEventVerifierRouter>(sp => new PlatformEventVerifierRouter(
    sp.GetServices<IPlatformNamedEventVerifier>(),
    fallback: sp.GetService<AllowAllPlatformEventVerifier>()));
builder.Services.AddSingleton<IPlatformEventVerifier>(sp => sp.GetRequiredService<PlatformEventVerifierRouter>());
builder.Services.AddSingleton<IPlatformEventMapper, TestPlatformEventMapper>();
builder.Services.AddSingleton<IPlatformEventMapper, DouyinPlatformEventMapper>();
builder.Services.AddSingleton<IPlatformEventMapper>(sp => new KuaishouPlatformEventMapper(
    messageEncryptKey: sp.GetRequiredService<IConfiguration>()["Ingress:Kuaishou:MsgEncryptKey"]));
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

// platform-ingress listener face: rate limit, body cap, timeout, per-platform
// verification and dedup (docs/design/process-topology.md §4).
app.MapPost("/ingress/{platform}", async (
    HttpContext context,
    string platform,
    GatewayIpcService ipc,
    IPlatformEventVerifier verifier,
    IEnumerable<IPlatformEventMapper> mappers,
    IAudienceFactionRule factionRule,
    EventDeduplicator dedup,
    IngressFaceGuard faceGuard,
    FaceCounters faces) =>
{
    var sourceKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!faceGuard.AllowRequest(sourceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
    {
        faces.IngressRateLimited();
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    using var requestCts = faceGuard.CreateRequestCancellationToken(context.RequestAborted);
    try
    {
        var body = await faceGuard.ReadBodyAsync(context.Request.Body, requestCts.Token);
        if (!body.Ok)
        {
            faces.IngressTooLarge();
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var raw = body.Body!;

        var headers = context.Request.Headers.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (!verifier.Verify(platform, raw, headers))
        {
            faces.IngressUnverified();
            return Results.Unauthorized();
        }

        var ingestTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AudienceEvent? ev = null;
        foreach (var mapper in mappers)
        {
            ev = mapper.TryMap(platform, raw, ingestTimeMs);
            if (ev != null)
            {
                break;
            }
        }

        if (ev == null)
        {
            faces.IngressUnmapped();
            return Results.BadRequest(new { error = "unmapped_event", platform });
        }

        ev = factionRule.Apply(ev);

        if (!dedup.TryReserve(ev.Platform, ev.EventId, out var lease))
        {
            faces.IngressDuplicates();
            return Results.Ok(new { ok = true, duplicated = true, sessionId = ev.SessionId, eventId = ev.EventId });
        }

        try
        {
            await ipc.PublishAudienceEventAsync(EnvelopeFactory.Create(IpcMessageTypes.GatewayAudienceEvent, ev.SessionId, ev, sourceEventId: ev.EventId), requestCts.Token);
        }
        catch
        {
            // Delivery failed: release the claim so a platform retry is treated
            // as a fresh delivery (Failed), never misremembered as a duplicate.
            dedup.Rollback(lease);
            throw;
        }

        dedup.Commit(lease);
        faces.IngressAccepted();
        return Results.Ok(new { ok = true, sessionId = ev.SessionId, eventId = ev.EventId });
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        faces.IngressTimedOut();
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
    catch
    {
        return Results.StatusCode(503);
    }
});

// streamer-ws listener face: hello token auth (fail-closed), message body
// cap, ACK rate limit (docs/design/process-topology.md §4). Shared with the
// monolith via StreamerWsFace; this host routes clients over the engine IPC.
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

