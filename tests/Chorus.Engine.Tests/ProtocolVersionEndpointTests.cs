using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Protocol;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Golden tests for the streamer-ws wire-protocol version check (todo §8 P3
/// protocol versioning): legacy hellos without protocolVersion stay accepted,
/// a matching version is accepted, a mismatched version is rejected with
/// protocol_mismatch before authentication, and the rejection is counted.
/// Runs against the monolith host (Chorus.Server); the Gateway host shares
/// the same face components and policy.
/// </summary>
public sealed class ProtocolVersionEndpointTests
{
    private const string BaseUri = "ws";

    public sealed class TokenFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Faces:StreamerWs:AuthTokens", "tok-good");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "false");
        }
    }

    private static async Task<(WebSocket Ws, string? Text, WebSocketCloseStatus? CloseStatus)> ConnectAndHelloAsync(
        WebApplicationFactory<Chorus.Server.Program> factory,
        string? token,
        int? protocolVersion,
        string sessionId = "s-pv")
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, BaseUri), CancellationToken.None);

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["sessionId"] = sessionId,
            ["clientId"] = "c-pv",
            ["token"] = token,
            ["protocolVersion"] = protocolVersion,
        };
        var helloBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await ws.SendAsync(helloBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return (ws, null, result.CloseStatus);
        }

        return (ws, Encoding.UTF8.GetString(buffer, 0, result.Count), null);
    }

    private static async Task<WebSocketCloseStatus?> ReadCloseAsync(WebSocket ws)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        return result.MessageType == WebSocketMessageType.Close ? result.CloseStatus : null;
    }

    private static async Task<JsonElement> GetWsFacesAsync(WebApplicationFactory<Chorus.Server.Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>("/faces");
        return response.GetProperty("streamerWs");
    }

    [Fact]
    public async Task LegacyHello_WithoutVersion_Accepted_WelcomeEchoesCurrentVersion()
    {
        using var factory = new TokenFactory();

        var (ws, text, closeStatus) = await ConnectAndHelloAsync(factory, "tok-good", protocolVersion: null);

        Assert.Null(closeStatus);
        Assert.NotNull(text);
        Assert.Contains("welcome", text);
        Assert.Contains("\"protocolVersion\":" + ProtocolVersionPolicy.Current, text);
        ws.Dispose();
    }

    [Fact]
    public async Task MatchingVersion_Accepted()
    {
        using var factory = new TokenFactory();

        var (ws, text, closeStatus) = await ConnectAndHelloAsync(
            factory, "tok-good", protocolVersion: ProtocolVersionPolicy.Current, sessionId: "s-pv-match");

        Assert.Null(closeStatus);
        Assert.Contains("welcome", text);
        ws.Dispose();
    }

    [Fact]
    public async Task MismatchedVersion_Rejected_Closed_AndCounted()
    {
        using var factory = new TokenFactory();
        var facesBefore = await GetWsFacesAsync(factory);

        var (ws, text, closeStatus) = await ConnectAndHelloAsync(
            factory, "tok-good", protocolVersion: ProtocolVersionPolicy.Current + 999, sessionId: "s-pv-bad");

        Assert.NotNull(text);
        Assert.Contains($"protocol_mismatch: client={ProtocolVersionPolicy.Current + 999} server={ProtocolVersionPolicy.Current}", text);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await ReadCloseAsync(ws));
        ws.Dispose();

        var facesAfter = await GetWsFacesAsync(factory);
        Assert.Equal(
            facesBefore.GetProperty("protocolMismatch").GetInt64() + 1,
            facesAfter.GetProperty("protocolMismatch").GetInt64());
    }

    [Fact]
    public async Task VersionCheck_PrecedesAuth_MismatchWins_OverMissingToken()
    {
        using var factory = new TokenFactory();
        var facesBefore = await GetWsFacesAsync(factory);

        var (ws, text, _) = await ConnectAndHelloAsync(
            factory, token: null, protocolVersion: ProtocolVersionPolicy.Current + 1, sessionId: "s-pv-order");

        Assert.NotNull(text);
        Assert.Contains("protocol_mismatch", text);
        Assert.DoesNotContain("missing_token", text);
        ws.Dispose();

        var facesAfter = await GetWsFacesAsync(factory);
        Assert.Equal(
            facesBefore.GetProperty("unauthorized").GetInt64(),
            facesAfter.GetProperty("unauthorized").GetInt64());
        Assert.Equal(
            facesBefore.GetProperty("protocolMismatch").GetInt64() + 1,
            facesAfter.GetProperty("protocolMismatch").GetInt64());
    }
}
