using System.Net.WebSockets;
using System.Text.Json;
using Cohort.Engine.Abstractions;
using Cohort.Engine.Session;
using Cohort.Protocol;
using Cohort.Protocol.Messages;
using Cohort.Protocol.Models;
using Cohort.Server;
using Cohort.SampleGame;
using Cohort.Adapters.Abstractions;
using Cohort.Adapters.Platforms;
using Cohort.Gateway.Core.Faces;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
builder.Services.AddSingleton(new SessionConfig());
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MatchRegistry>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(IngressFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IngressFaceGuard>();
builder.Services.AddSingleton<FaceCounters>();
builder.Services.AddSingleton<AllowAllPlatformEventVerifier>();
builder.Services.AddSingleton(sp =>
    new DouyinPlatformEventVerifier(
        secret: sp.GetRequiredService<IConfiguration>()["Ingress:Douyin:AppSecret"],
        signatureHeader: sp.GetRequiredService<IConfiguration>()["Ingress:Douyin:SignatureHeader"]));
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<DouyinPlatformEventVerifier>());
builder.Services.AddSingleton<PlatformEventVerifierRouter>(sp => new PlatformEventVerifierRouter(
    sp.GetServices<IPlatformNamedEventVerifier>(),
    fallback: sp.GetService<AllowAllPlatformEventVerifier>()));
builder.Services.AddSingleton<IPlatformEventVerifier>(sp => sp.GetRequiredService<PlatformEventVerifierRouter>());
builder.Services.AddSingleton<IPlatformEventMapper, TestPlatformEventMapper>();
builder.Services.AddSingleton<IPlatformEventMapper, DouyinPlatformEventMapper>();
builder.Services.AddSingleton<IAudienceFactionRule>(_ =>
    new KeywordGiftAudienceFactionRule(
        commentKeywords: BuildFactionMap(builder.Configuration.GetSection("Ingress:FactionRules:CommentKeywords")),
        giftIds: BuildFactionMap(builder.Configuration.GetSection("Ingress:FactionRules:GiftIds"))));
builder.Services.AddSingleton(sp =>
{
    var ttlSeconds = sp.GetRequiredService<IConfiguration>().GetValue("Ingress:DedupTtlSeconds", 600);
    return new EventDeduplicator(sp.GetRequiredService<IMemoryCache>(), TimeSpan.FromSeconds(ttlSeconds));
});

var app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => new { name = "Cohort", ok = true });
app.MapGet("/health", () => Results.Ok());
app.MapGet("/sessions", (SessionManager sessions) => sessions.GetDiagnostics());
app.MapGet("/matches", (MatchRegistry matches) => matches.GetDiagnostics());
app.MapGet("/faces", (FaceCounters faces) => faces);

// platform-ingress listener face: rate limit, body cap, timeout, per-platform
// verification and dedup (docs/design/process-topology.md §4).
app.MapPost("/ingress/{platform}", async (
    HttpContext context,
    string platform,
    SessionManager sessions,
    MatchRegistry matches,
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

        if (!dedup.TryMark(ev.Platform, ev.EventId))
        {
            faces.IngressDuplicates();
            return Results.Ok(new { ok = true, duplicated = true, sessionId = ev.SessionId, eventId = ev.EventId });
        }

        var session = sessions.GetOrCreate(ev.SessionId);
        matches.Track(ev);
        await session.IngestAudienceEventAsync(ev);

        faces.IngressAccepted();
        return Results.Ok(new { ok = true, sessionId = ev.SessionId, eventId = ev.EventId, tick = session.TickId });
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        faces.IngressTimedOut();
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Expected WebSocket request.");
        return;
    }

    var sessions = context.RequestServices.GetRequiredService<SessionManager>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Cohort.StreamerWs");

    using var ws = await context.WebSockets.AcceptWebSocketAsync();

    ClientHello? hello;
    try
    {
        var helloText = await WebSocketHelpers.ReceiveTextAsync(ws, context.RequestAborted);
        hello = JsonSerializer.Deserialize<ClientHello>(helloText, ProtocolJson.SerializerOptions);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to parse hello");
        await WebSocketHelpers.SendJsonAsync(ws, new ServerError(ProtocolTypes.Error, "Invalid hello"), context.RequestAborted);
        return;
    }

    if (hello == null || !string.Equals(hello.Type, ProtocolTypes.Hello, StringComparison.Ordinal))
    {
        await WebSocketHelpers.SendJsonAsync(ws, new ServerError(ProtocolTypes.Error, "First message must be hello"), context.RequestAborted);
        return;
    }

    var sessionId = string.IsNullOrWhiteSpace(hello.SessionId) ? sessions.CreateSessionId() : hello.SessionId!;
    var clientId = string.IsNullOrWhiteSpace(hello.ClientId) ? $"c_{Guid.NewGuid():N}" : hello.ClientId!;
    var session = sessions.GetOrCreate(sessionId);

    var client = new WsSessionClient(clientId, sessionId, ws, logger);
    await session.AddClientAsync(client);

    await WebSocketHelpers.SendJsonAsync(ws, new ServerWelcome(
        Type: ProtocolTypes.Welcome,
        SessionId: sessionId,
        ClientId: clientId,
        TickDurationMs: sessions.Config.TickDurationMs,
        InputDelayTicks: sessions.Config.InputDelayTicks,
        SnapshotEveryTicks: sessions.Config.SnapshotEveryTicks,
        ServerTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    ), context.RequestAborted);

    try
    {
        while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var text = await WebSocketHelpers.ReceiveTextAsync(ws, context.RequestAborted);
            using var doc = JsonDocument.Parse(text);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, ProtocolTypes.Ack, StringComparison.Ordinal))
            {
                var ack = doc.RootElement.Deserialize<ClientAck>(ProtocolJson.SerializerOptions);
                if (ack != null)
                {
                    await session.AckAsync(clientId, ack.LastAppliedTickId, ack.ClientTimeMs ?? 0);
                }
            }
            else if (string.Equals(type, ProtocolTypes.Ping, StringComparison.Ordinal))
            {
                await WebSocketHelpers.SendJsonAsync(ws, new ServerPong(ProtocolTypes.Pong, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), context.RequestAborted);
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (WebSocketException)
    {
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "WebSocket loop error");
    }
    finally
    {
        await session.RemoveClientAsync(clientId);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
    }
});

app.Run();

static IReadOnlyDictionary<string, string> BuildFactionMap(IConfigurationSection section)
{
    return section.GetChildren()
        .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
        .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
}

// Exposes the entry point for WebApplicationFactory-based integration tests.
public partial class Program { }
