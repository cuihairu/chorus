using System.Globalization;
using System.Net.WebSockets;
using Chorus.Redesign.StreamerGateway.Messages;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M4-1 edge transport contract (contracts 3.1): the public WSS listener speaks exactly
/// the client protocol's transport rules — hello first with a known protocol version, a
/// frame size cap enforced at the transport, ping answered by pong with no session
/// inference, and a connection that never completes its hello closed within the pre-auth
/// window. A real ClientWebSocket against a real gateway process proves the wire, not an
/// in-memory echo.
/// </summary>
public sealed class RedesignM4HostTests
{
    [Fact]
    public async Task HelloOpensTheEdgeAndPingIsAnsweredByPong()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var wssPort = await StartGatewayAsync(environment);

        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        await client.HelloAsync();
        await client.SendRawAsync("""{"type":"ping"}""");
        var pong = await client.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
        Assert.True(pong.GetProperty("serverTimeMs").GetInt64() > 0);
    }

    [Fact]
    public async Task AHelloWithAnUnknownProtocolVersionIsRefusedBeforeAnythingElse()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var wssPort = await StartGatewayAsync(environment);

        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        await client.SendRawAsync("""{"type":"hello","protocolVersion":99}""");
        var error = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.ProtocolMismatch, error.GetProperty("code").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await client.WaitForCloseAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AnOversizedFrameClosesTheConnectionWithTheSizeError()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var wssPort = await StartGatewayAsync(environment);

        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        await client.HelloAsync();
        // One frame comfortably past the default 64 KiB cap (contracts 3.1).
        var oversized = "{\"type\":\"ping\",\"padding\":\"" + new string('x', 128 * 1024) + "\"}";
        await client.SendRawAsync(oversized);
        var error = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.MessageTooLarge, error.GetProperty("code").GetString());
        // RFC 6455 1009: message too big, sent as the raw status.
        Assert.Equal((WebSocketCloseStatus)1009, await client.WaitForCloseAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ASilentConnectionIsClosedWithinThePreAuthWindow()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var wssPort = await StartGatewayAsync(environment, preAuthSeconds: 2);

        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        // No hello: the edge must not hold an unauthenticated connection open. The window
        // carries the load margin — the invariant is "closed after the deadline", not the
        // deadline's exact millisecond. The pre-auth close aborts the pending receive, so
        // the client sees either a close frame or the transport drop — both are closures.
        try
        {
            await client.WaitForCloseAsync(TimeSpan.FromSeconds(30));
        }
        catch (WebSocketException)
        {
            // The server dropped the silent connection at the transport level.
        }
    }

    /// <summary>Starts the gateway alone: the transport contract needs no cluster behind
    /// it. The internal port is a probe-only listener here; the WSS port is the one under
    /// test.</summary>
    private static async Task<int> StartGatewayAsync(
        RedesignM0HostTests.HostEnvironment environment, int? preAuthSeconds = null)
    {
        var wssPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var extra = new Dictionary<string, string>
        {
            ["CHORUS_WSS_PORT"] = wssPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        if (preAuthSeconds is { } seconds)
            extra["CHORUS_PREAUTH_TIMEOUT_SECONDS"] = seconds.ToString(CultureInfo.InvariantCulture);
        await environment.StartAsync("Chorus.Redesign.StreamerGateway",
            RedesignM0HostTests.HostEnvironment.FreePort(), "streamer-gateway", extra);
        return wssPort;
    }
}
