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
using Cohort.Gateway.Core;
using Cohort.Gateway.Core.Faces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
builder.Services.AddSingleton(new SessionConfig());
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MatchRegistry>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(IngressFaceOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IngressFaceGuard>();
builder.Services.AddSingleton<FaceCounters>();
builder.Services.AddSingleton(StreamerWsFaceOptions.FromConfiguration(builder.Configuration));
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
    return new EventDeduplicator(sp.GetRequiredService<IMemoryCache>(), TimeSpan.FromSeconds(ttlSeconds));
});

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

// streamer-ws listener face: hello token auth (fail-closed), message body
// cap, ACK rate limit (docs/design/process-topology.md §4).
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Expected WebSocket request.");
        return;
    }

    var sessions = context.RequestServices.GetRequiredService<SessionManager>();
    var faceOptions = context.RequestServices.GetRequiredService<StreamerWsFaceOptions>();
    var faces = context.RequestServices.GetRequiredService<FaceCounters>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Cohort.StreamerWs");

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    faces.WsConnection();

    string helloText;
    try
    {
        helloText = await WebSocketHelpers.ReceiveTextAsync(ws, context.RequestAborted, faceOptions.MaxMessageBytes);
    }
    catch (WsMessageTooLargeException)
    {
        faces.WsMessageTooLarge();
        logger.LogWarning("WS hello exceeded {MaxBytes} bytes; closing connection", faceOptions.MaxMessageBytes);
        await RejectConnectionAsync(ws, WebSocketCloseStatus.MessageTooBig, "hello too large", error: null, context.RequestAborted);
        return;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to receive hello");
        await RejectConnectionAsync(ws, WebSocketCloseStatus.NormalClosure, "invalid hello",
            new ServerError(ProtocolTypes.Error, "Invalid hello"), context.RequestAborted);
        return;
    }

    ClientHello? hello;
    try
    {
        hello = JsonSerializer.Deserialize<ClientHello>(helloText, ProtocolJson.SerializerOptions);
    }
    catch (JsonException)
    {
        hello = null;
    }

    if (hello == null || !string.Equals(hello.Type, ProtocolTypes.Hello, StringComparison.Ordinal))
    {
        await RejectConnectionAsync(ws, WebSocketCloseStatus.NormalClosure, "first message must be hello",
            new ServerError(ProtocolTypes.Error, "First message must be hello"), context.RequestAborted);
        return;
    }

    var auth = faceOptions.Authenticate(hello.Token, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    if (!auth.Ok)
    {
        faces.WsUnauthorized();
        logger.LogWarning("WS connection rejected ({Reason}) for session {SessionId}", auth.Reason, hello.SessionId);
        await RejectConnectionAsync(ws, WebSocketCloseStatus.PolicyViolation, auth.Reason,
            new ServerError(ProtocolTypes.Error, $"unauthorized: {auth.Reason}"), context.RequestAborted);
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

    var ackWindowStartMs = 0L;
    var ackCount = 0;
    try
    {
        while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            string text;
            try
            {
                text = await WebSocketHelpers.ReceiveTextAsync(ws, context.RequestAborted, faceOptions.MaxMessageBytes);
            }
            catch (WsMessageTooLargeException)
            {
                faces.WsMessageTooLarge();
                logger.LogWarning("WS message exceeded {MaxBytes} bytes; closing connection", faceOptions.MaxMessageBytes);
                await RejectConnectionAsync(ws, WebSocketCloseStatus.MessageTooBig, "message too large", error: null, context.RequestAborted);
                return;
            }

            using var doc = JsonDocument.Parse(text);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, ProtocolTypes.Ack, StringComparison.Ordinal))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (ackWindowStartMs == 0 || nowMs - ackWindowStartMs >= 60_000)
                {
                    ackWindowStartMs = nowMs;
                    ackCount = 1;
                }
                else if (ackCount >= faceOptions.AckPerMinute)
                {
                    faces.WsAckDropped();
                    continue;
                }
                else
                {
                    ackCount++;
                }

                faces.WsAckAccepted();
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

static async Task RejectConnectionAsync(
    WebSocket ws,
    WebSocketCloseStatus closeStatus,
    string reason,
    object? error,
    CancellationToken cancellationToken)
{
    try
    {
        if (error != null)
        {
            await WebSocketHelpers.SendJsonAsync(ws, error, cancellationToken);
        }

        await ws.CloseAsync(closeStatus, reason, cancellationToken);
    }
    catch
    {
        // Client already gone; nothing to reject.
    }
}

// Exposes the entry point for WebApplicationFactory-based integration tests.
public partial class Program { }
