using System.Net.WebSockets;
using System.Text.Json;
using Cohort.Adapters.Abstractions;
using Cohort.Adapters.Platforms;
using Cohort.Gateway;
using Cohort.Messaging.Ipc;
using Cohort.Protocol;
using Cohort.Protocol.Messages;
using Cohort.Protocol.Models;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
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

builder.Services.AddSingleton<GatewayIpcService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayIpcService>());

var app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => new { name = "Cohort.Gateway", ok = true });
app.MapGet("/health", () => Results.Ok());

app.MapPost("/ingress/{platform}", async (
    HttpContext context,
    string platform,
    GatewayIpcService ipc,
    IPlatformEventVerifier verifier,
    IEnumerable<IPlatformEventMapper> mappers,
    IAudienceFactionRule factionRule,
    EventDeduplicator dedup) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var raw = await reader.ReadToEndAsync(context.RequestAborted);

    var headers = context.Request.Headers.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    if (!verifier.Verify(platform, raw, headers))
    {
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
        return Results.BadRequest(new { error = "unmapped_event", platform });
    }

    ev = factionRule.Apply(ev);

    if (!dedup.TryMark(ev.Platform, ev.EventId))
    {
        return Results.Ok(new { ok = true, duplicated = true, sessionId = ev.SessionId, eventId = ev.EventId });
    }

    try
    {
        await ipc.PublishAudienceEventAsync(EnvelopeFactory.Create(IpcMessageTypes.GatewayAudienceEvent, ev.SessionId, ev), context.RequestAborted);
        return Results.Ok(new { ok = true, sessionId = ev.SessionId, eventId = ev.EventId });
    }
    catch
    {
        return Results.StatusCode(503);
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

    var ipc = context.RequestServices.GetRequiredService<GatewayIpcService>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Cohort.Gateway.WS");

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

    var sessionId = string.IsNullOrWhiteSpace(hello.SessionId) ? $"s_{Guid.NewGuid():N}" : hello.SessionId!;
    var clientId = string.IsNullOrWhiteSpace(hello.ClientId) ? $"c_{Guid.NewGuid():N}" : hello.ClientId!;

    var conn = new GatewayClientConnection(sessionId, clientId, ws);

    var welcomeTask = ipc.CreateWelcomeWaiter(sessionId, clientId, context.RequestAborted);
    try
    {
        await ipc.RegisterClientAsync(conn, context.RequestAborted);
    }
    catch
    {
        await WebSocketHelpers.SendJsonAsync(ws, new ServerError(ProtocolTypes.Error, "Engine unavailable"), context.RequestAborted);
        return;
    }

    ServerWelcome welcome;
    try
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeoutCts.Token);
        welcome = await welcomeTask.WaitAsync(linkedCts.Token);
    }
    catch
    {
        await WebSocketHelpers.SendJsonAsync(ws, new ServerError(ProtocolTypes.Error, "Engine welcome timeout"), context.RequestAborted);
        return;
    }

    await WebSocketHelpers.SendJsonAsync(ws, welcome, context.RequestAborted);

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
                    await ipc.PublishAckAsync(sessionId, clientId, ack.LastAppliedTickId, ack.ClientTimeMs ?? 0, context.RequestAborted);
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
        await ipc.UnregisterClientAsync(sessionId, clientId, CancellationToken.None);
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
