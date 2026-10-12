using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Gateway-side parity for the streamer-ws listener face gates (todo §3.5
/// router abstraction acceptance). The monolith's /ws gates are covered by
/// StreamerWsAuthEndpointTests and ProtocolVersionEndpointTests; these pin the
/// SAME close statuses, error payloads and face counters on the Gateway's /ws,
/// so the two endpoints cannot drift apart unnoticed.
///
/// RejectConnectionAsync always sends the error JSON as a Text frame and only
/// then closes, so every assertion below reads the error frame first and the
/// close status second. All gates fire before any engine IPC traffic, so no
/// engine process is needed.
/// </summary>
public sealed class StreamerWsGatewayParityTests
{
    private const string BaseUri = "ws";

    public sealed class LockedFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
            ConfigureUds(builder);
        }
    }

    public sealed class TokenFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var past = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
            var future = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000;
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good;tok-expired@" + past + ";tok-live@" + future);
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
            ConfigureUds(builder);
        }
    }

    public sealed class BodyCapFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
            builder.UseSetting("Faces:StreamerWs:MaxMessageBytes", "64");
            ConfigureUds(builder);
        }
    }

    /// <summary>
    /// Every Gateway test builds a real server-side IPC bus; isolate the
    /// socket paths so parallel test classes cannot bind the same endpoints.
    /// </summary>
    private static void ConfigureUds(IWebHostBuilder builder)
    {
        builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
        builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-parity-{Guid.NewGuid():N}");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<WebSocket> ConnectAsync(WebApplicationFactory<Chorus.Gateway.Program> factory)
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, BaseUri), CancellationToken.None);
    }

    private static async Task SendHelloAsync(WebSocket ws, object hello)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<(string? Text, WebSocketCloseStatus? CloseStatus)> ReceiveErrorAsync(WebSocket ws)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return (null, result.CloseStatus);
        }

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        result = await ws.ReceiveAsync(buffer, cts.Token);
        return (text, result.MessageType == WebSocketMessageType.Close ? result.CloseStatus : null);
    }

    private static async Task<JsonElement> GetStreamerWsAsync(WebApplicationFactory<Chorus.Gateway.Program> factory)
    {
        var client = factory.CreateClient();
        var faces = await client.GetFromJsonAsync<JsonElement>("/faces");
        return faces.GetProperty("streamerWs");
    }

    // ---------------------------------------------------------------- gates

    [Fact]
    public async Task NonHelloFirstMessage_RejectedWithNormalClosure()
    {
        using var factory = new LockedFactory();

        using var ws = await ConnectAsync(factory);
        await SendHelloAsync(ws, new { type = "ack", lastAppliedTickId = 1 });

        var (text, closeStatus) = await ReceiveErrorAsync(ws);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, closeStatus);
        Assert.NotNull(text);
        var err = JsonSerializer.Deserialize<JsonElement>(text!);
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Equal("First message must be hello", err.GetProperty("message").GetString());
    }

    [Fact]
    public async Task HelloOversize_RejectedWithMessageTooBig()
    {
        using var factory = new BodyCapFactory();

        using var ws = await ConnectAsync(factory);
        // Built by string concat: a raw string literal cannot carry the JSON
        // quotes it needs inside the hello payload.
        var oversized = "{\"type\":\"hello\",\"sessionId\":\"s-parity\",\"clientId\":\"c-parity\",\"token\":\"tok-good\",\"note\":\"" + new string('x', 200) + "\"}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(oversized), WebSocketMessageType.Text, true, CancellationToken.None);

        var (text, closeStatus) = await ReceiveErrorAsync(ws);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeStatus);
        Assert.Null(text);
        var faces = await GetStreamerWsAsync(factory);
        Assert.True(faces.GetProperty("messageTooLarge").GetInt64() >= 1);
    }

    [Fact]
    public async Task ProtocolMismatch_RejectedWithPolicyViolation()
    {
        using var factory = new LockedFactory();

        using var ws = await ConnectAsync(factory);
        await SendHelloAsync(ws, new
        {
            type = "hello",
            sessionId = "s-parity",
            clientId = "c-parity",
            protocolVersion = 99,
        });

        var (text, closeStatus) = await ReceiveErrorAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, closeStatus);
        Assert.NotNull(text);
        Assert.Contains("protocol_mismatch", text);
        var faces = await GetStreamerWsAsync(factory);
        Assert.True(faces.GetProperty("protocolMismatch").GetInt64() >= 1);
    }

    [Fact]
    public async Task MissingToken_RejectedFailClosed()
    {
        using var factory = new LockedFactory();

        using var ws = await ConnectAsync(factory);
        await SendHelloAsync(ws, new { type = "hello", sessionId = "s-parity", clientId = "c-parity" });

        var (text, closeStatus) = await ReceiveErrorAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, closeStatus);
        Assert.NotNull(text);
        Assert.Contains("missing_token", text);
        var faces = await GetStreamerWsAsync(factory);
        Assert.True(faces.GetProperty("unauthorized").GetInt64() >= 1);
    }

    [Fact]
    public async Task InvalidToken_RejectedFailClosed()
    {
        using var factory = new TokenFactory();

        using var ws = await ConnectAsync(factory);
        await SendHelloAsync(ws, new { type = "hello", sessionId = "s-parity", clientId = "c-parity", token = "tok-wrong" });

        var (text, closeStatus) = await ReceiveErrorAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, closeStatus);
        Assert.NotNull(text);
        Assert.Contains("invalid_token", text);
        var faces = await GetStreamerWsAsync(factory);
        Assert.True(faces.GetProperty("unauthorized").GetInt64() >= 1);
    }

    [Fact]
    public async Task ExpiredToken_RejectedFailClosed()
    {
        using var factory = new TokenFactory();

        using var ws = await ConnectAsync(factory);
        await SendHelloAsync(ws, new { type = "hello", sessionId = "s-parity", clientId = "c-parity", token = "tok-expired" });

        var (text, closeStatus) = await ReceiveErrorAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, closeStatus);
        Assert.NotNull(text);
        Assert.Contains("token_expired", text);
    }
}
