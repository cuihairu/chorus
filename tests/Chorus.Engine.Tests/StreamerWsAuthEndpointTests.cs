using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Golden tests for the streamer-ws listener face over the real /ws endpoint:
/// hello-token auth (missing/invalid/expired rejected, connection closed),
/// anonymous dev mode, fail-closed without configuration, message body cap,
/// and the per-connection ACK rate limit. Runs against the monolith host
/// (Chorus.Server); the Gateway host shares the same face components.
/// </summary>
public sealed class StreamerWsAuthEndpointTests
{
    private const string BaseUri = "ws";

    // ---------------------------------------------------------------- factories

    public sealed class TokenFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
        }
    }

    public sealed class ExpiryFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var past = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
            var future = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000;
            builder.UseSetting("Faces:StreamerWs:AuthTokens", $"tok-expired@{past};tok-live@{future}");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
        }
    }

    public sealed class AnonymousFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "true");
        }
    }

    public sealed class LockedFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
        }
    }

    public sealed class BodyCapFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
            builder.UseSetting("Faces:StreamerWs:MaxMessageBytes", "64");
        }
    }

    public sealed class AckLimitFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
            builder.UseSetting("Faces:StreamerWs:AckPerMinute", "2");
        }
    }

    public sealed class ResyncLimitFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
            builder.UseSetting("Faces:StreamerWs:ResyncPerMinute", "2");
        }
    }

    public sealed class BodyCapAnonFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "true");
            builder.UseSetting("Faces:StreamerWs:MaxMessageBytes", "64");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(WebSocket Ws, string? Text, WebSocketCloseStatus? CloseStatus)> ConnectAndHelloAsync(
        WebApplicationFactory<Chorus.Server.Program> factory,
        string? token,
        string sessionId = "s-ws",
        string? clientVersion = null)
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, BaseUri), CancellationToken.None);

        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            sessionId,
            clientId = "c-ws",
            token,
            clientVersion,
        });
        var helloBytes = Encoding.UTF8.GetBytes(hello);
        await ws.SendAsync(helloBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (ws, null, result.CloseStatus);
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            // A hot session's snapshot broadcast can hit the wire before the
            // welcome does; keep scanning for the handshake frame (welcome or
            // error) instead of failing on the interleave.
            if (text.Contains("\"type\":\"snapshot\""))
            {
                continue;
            }

            return (ws, text, null);
        }
    }

    private static async Task<WebSocketCloseStatus?> ReadCloseAsync(WebSocket ws)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        return result.MessageType == WebSocketMessageType.Close ? result.CloseStatus : null;
    }

    private static Task SendTextAsync(WebSocket ws, string text)
        => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Drain frames until one matches; snapshot broadcasts interleave.</summary>
    private static async Task<string> ReadUntilAsync(WebSocket ws, Func<string, bool> match)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"closed while waiting: {result.CloseStatus}");
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (match(text))
            {
                return text;
            }
        }
    }

    private static async Task<JsonElement> GetFacesAsync(WebApplicationFactory<Chorus.Server.Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>("/faces");
        return response.GetProperty("streamerWs");
    }

    // ---------------------------------------------------------------- auth

    [Fact]
    public async Task ValidToken_ReceivesWelcome()
    {
        using var factory = new TokenFactory();

        var (ws, text, closeStatus) = await ConnectAndHelloAsync(factory, "tok-good");

        Assert.Null(closeStatus);
        Assert.NotNull(text);
        Assert.Contains("welcome", text);
        Assert.Contains("s-ws", text);
        ws.Dispose();
    }

    [Fact]
    public async Task MissingToken_RejectedAndClosed()
    {
        using var factory = new TokenFactory();

        var (ws, text, _) = await ConnectAndHelloAsync(factory, token: null);

        Assert.NotNull(text);
        Assert.Contains("missing_token", text);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await ReadCloseAsync(ws));
        ws.Dispose();
    }

    [Fact]
    public async Task InvalidToken_RejectedAndClosed()
    {
        using var factory = new TokenFactory();

        var (ws, text, _) = await ConnectAndHelloAsync(factory, "tok-wrong");

        Assert.NotNull(text);
        Assert.Contains("invalid_token", text);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await ReadCloseAsync(ws));
        ws.Dispose();
    }

    [Fact]
    public async Task ExpiredToken_Rejected_LiveTokenAccepted()
    {
        using var factory = new ExpiryFactory();

        var (expiredWs, expiredText, _) = await ConnectAndHelloAsync(factory, "tok-expired", sessionId: "s-ws-exp");
        Assert.NotNull(expiredText);
        Assert.Contains("token_expired", expiredText);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await ReadCloseAsync(expiredWs));
        expiredWs.Dispose();

        var (liveWs, liveText, _) = await ConnectAndHelloAsync(factory, "tok-live", sessionId: "s-ws-live");
        Assert.NotNull(liveText);
        Assert.Contains("welcome", liveText);
        liveWs.Dispose();
    }

    [Fact]
    public async Task AnonymousMode_AcceptsTokenlessHello()
    {
        using var factory = new AnonymousFactory();

        var (ws, text, closeStatus) = await ConnectAndHelloAsync(factory, token: null);

        Assert.Null(closeStatus);
        Assert.Contains("welcome", text);
        ws.Dispose();
    }

    [Fact]
    public async Task FailClosed_NoConfig_RejectsEverything_AndCounts()
    {
        using var factory = new LockedFactory();

        var (ws1, text1, _) = await ConnectAndHelloAsync(factory, token: null, sessionId: "s-ws-fc1");
        Assert.Contains("missing_token", text1);
        ws1.Dispose();

        var (ws2, text2, _) = await ConnectAndHelloAsync(factory, "junk", sessionId: "s-ws-fc2");
        Assert.Contains("invalid_token", text2);
        ws2.Dispose();

        var faces = await GetFacesAsync(factory);
        Assert.Equal(2, faces.GetProperty("unauthorized").GetInt64());
    }

    // ---------------------------------------------------------------- message bounds

    [Fact]
    public async Task OversizedHello_ClosedWithMessageTooBig()
    {
        using var factory = new BodyCapFactory();
        var facesBefore = await GetFacesAsync(factory);

        var (ws, text, closeStatus) = await ConnectAndHelloAsync(
            factory, "tok-good", sessionId: "s-ws-big", clientVersion: new string('a', 200));

        Assert.Null(text);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeStatus);
        ws.Dispose();

        var facesAfter = await GetFacesAsync(factory);
        Assert.Equal(facesBefore.GetProperty("messageTooLarge").GetInt64() + 1, facesAfter.GetProperty("messageTooLarge").GetInt64());
    }

    [Fact]
    public async Task AckFlood_ExcessAcksDropped_AndCounted()
    {
        using var factory = new AckLimitFactory();

        var (ws, welcome, _) = await ConnectAndHelloAsync(factory, "tok-good", sessionId: "s-ws-ack");
        Assert.Contains("welcome", welcome);

        var facesBefore = await GetFacesAsync(factory);

        for (var i = 0; i < 5; i++)
        {
            var ack = JsonSerializer.Serialize(new
            {
                type = "ack",
                sessionId = "s-ws-ack",
                clientId = "c-ws",
                lastAppliedTickId = (long)i,
                clientTimeMs = 0L,
            });
            var bytes = Encoding.UTF8.GetBytes(ack);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // Ping after the acks: receiving the pong proves the server processed
        // every ack above (the receive loop is FIFO), so the counters are settled.
        // Snapshot pushes are interleaved on the same socket, so drain messages
        // until the pong arrives instead of asserting on the first receive.
        var ping = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        await ws.SendAsync(ping, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string pongText = "";
        while (!cts.IsCancellationRequested)
        {
            var pong = await ws.ReceiveAsync(buffer, cts.Token);
            if (pong.MessageType == WebSocketMessageType.Close)
            {
                Assert.Fail($"Connection closed while waiting for pong: {pong.CloseStatus}");
            }

            pongText = Encoding.UTF8.GetString(buffer, 0, pong.Count);
            if (pongText.Contains("\"type\":\"pong\""))
            {
                break;
            }
        }

        Assert.Contains("pong", pongText);

        var facesAfter = await GetFacesAsync(factory);
        Assert.Equal(facesBefore.GetProperty("ackAccepted").GetInt64() + 2, facesAfter.GetProperty("ackAccepted").GetInt64());
        Assert.Equal(facesBefore.GetProperty("ackDropped").GetInt64() + 3, facesAfter.GetProperty("ackDropped").GetInt64());
        ws.Dispose();
    }

    // ---------------------------------------------------------------- more loop bounds

    [Fact]
    public async Task MalformedJsonHello_RejectedWithFirstMustBeHello()
    {
        using var factory = new AnonymousFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, BaseUri), CancellationToken.None);

        await SendTextAsync(ws, "{ not json");

        var text = await ReadUntilAsync(ws, t => t.Contains("First message must be hello"));
        Assert.Contains("error", text);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ReadCloseAsync(ws));
        ws.Dispose();
    }

    [Fact]
    public async Task ClientClosesBeforeHello_ServerEndsQuietly()
    {
        // Close frame instead of a hello: the receive fails (non-size error)
        // and the rejection must complete without hanging the connection.
        using var factory = new AnonymousFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, BaseUri), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        // The server finishes the close handshake; nothing left to assert
        // beyond "it ended" - a regression here hangs the CloseAsync above.
        ws.Dispose();
    }

    [Fact]
    public async Task OversizedMessage_AfterHandshake_ClosedWithMessageTooBig()
    {
        // The hello itself must fit the 64-byte cap; the junk frame below will not.
        using var factory = new BodyCapAnonFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, BaseUri), CancellationToken.None);
        await SendTextAsync(ws, """{"type":"hello","sessionId":"s-cap","clientId":"c"}""");

        await ReadUntilAsync(ws, t => t.Contains("welcome"));

        await SendTextAsync(ws, new string('x', 200));

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await ReadCloseAsync(ws));
        ws.Dispose();
    }

    [Fact]
    public async Task ResyncFlood_ExcessResyncDropped_AndCounted()
    {
        using var factory = new ResyncLimitFactory();

        var (ws, welcome, _) = await ConnectAndHelloAsync(factory, "tok-good", sessionId: "s-ws-rsc");
        Assert.Contains("welcome", welcome);

        var facesBefore = await GetFacesAsync(factory);

        for (var i = 0; i < 5; i++)
        {
            await SendTextAsync(ws, $$"""{"type":"resync","sessionId":"s-ws-rsc","clientId":"c-ws","fromFrameId":{{i}}}""");
        }

        // FIFO proof like the ack-flood test: the pong means the loop drained
        // every resync above, so the drop counter has settled.
        await SendTextAsync(ws, "{\"type\":\"ping\"}");
        await ReadUntilAsync(ws, t => t.Contains("\"type\":\"pong\""));

        var facesAfter = await GetFacesAsync(factory);
        Assert.Equal(facesBefore.GetProperty("resyncDropped").GetInt64() + 3, facesAfter.GetProperty("resyncDropped").GetInt64());
        ws.Dispose();
    }

    [Fact]
    public async Task ClientAbort_AfterHandshake_EndsQuietly()
    {
        // Abrupt disconnect mid-serve: the loop's cancellation arm must end
        // the connection quietly (no unobserved fault, no leaked binding).
        using var factory = new AnonymousFactory();
        var (ws, welcome, _) = await ConnectAndHelloAsync(factory, token: null, sessionId: "s-ws-abort");
        Assert.Contains("welcome", welcome);

        var faces = await GetFacesAsync(factory);
        Assert.Equal(1, faces.GetProperty("connections").GetInt64());

        ws.Abort();
        await Task.Delay(300); // let the server loop observe the abort

        ws.Dispose();
    }
}
