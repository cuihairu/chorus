using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
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

    // ---- The login round (contracts 3.1, 3.3) over the full cluster ----

    [Fact]
    public async Task LoginOverTheEdgeReturnsLoginReadyAndKeepsTheConnectionUsable()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem,
            environment.NodeAuthorityCertificatePem);
        await client.HelloAsync();

        var secret = RandomNumberGenerator.GetBytes(32);
        await client.SendAsync(Login(simulator, "open-m4-login", secret));
        var ready = await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        Assert.False(string.IsNullOrEmpty(ready.GetProperty("streamerId").GetString()));
        Assert.False(string.IsNullOrEmpty(ready.GetProperty("sessionId").GetString()));
        Assert.Equal(1, ready.GetProperty("sessionVersion").GetInt64());
        Assert.False(string.IsNullOrEmpty(ready.GetProperty("accessCredential").GetString()));
        Assert.True(ready.GetProperty("accessExpiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(ready.GetProperty("resumeRegistered").GetBoolean());

        // The connection stays usable after the session exists.
        await client.SendRawAsync("""{"type":"ping"}""");
        var pong = await client.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
        Assert.True(pong.GetProperty("serverTimeMs").GetInt64() > 0);
    }

    [Fact]
    public async Task ASecondLoginDisplacesTheFirstConnectionWithASessionReplacedNotice()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        var secret = RandomNumberGenerator.GetBytes(32);
        await first.SendAsync(Login(simulator, "open-m4-replace", secret));
        var firstReady = await first.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));

        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await second.HelloAsync();
        await second.SendAsync(Login(simulator, "open-m4-replace", RandomNumberGenerator.GetBytes(32)));
        var secondReady = await second.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        Assert.Equal(firstReady.GetProperty("sessionVersion").GetInt64() + 1,
            secondReady.GetProperty("sessionVersion").GetInt64());

        // The active leg: the old connection is told why it is going away, then closed.
        var replaced = await first.ExpectAsync(EdgeTypes.SessionReplaced, TimeSpan.FromSeconds(30));
        Assert.False(string.IsNullOrEmpty(replaced.GetProperty("reason").GetString()));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await first.WaitForCloseAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task LogoutEndsTheSessionAndClosesTheConnection()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await client.HelloAsync();
        var secret = RandomNumberGenerator.GetBytes(32);
        await client.SendAsync(Login(simulator, "open-m4-logout", secret));
        var ready = await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        var sessionId = ready.GetProperty("sessionId").GetString()!;

        await client.SendAsync(new EdgeLogout { OperationId = "logout-op-1" });
        // A normal close, after the revocation is durable — logout has no downlink ack.
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await client.WaitForCloseAsync(TimeSpan.FromSeconds(30)));

        // The session is really gone: a resume with the held secret is refused, not re-logged.
        using var retry = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await retry.HelloAsync();
        await retry.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-after-logout",
            ResumeCredential = secret,
            NextResumeSecret = RandomNumberGenerator.GetBytes(32),
            PreviousSessionId = sessionId
        });
        var error = await retry.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.SessionRevoked, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResumeRotatesTheSecretAndDuplicatesDoNotDisplaceAgain()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        await first.SendAsync(Login(simulator, "open-m4-resume", firstSecret));
        var login = await first.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));

        var secondSecret = RandomNumberGenerator.GetBytes(32);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await second.HelloAsync();
        await second.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-op-1",
            ResumeCredential = firstSecret,
            NextResumeSecret = secondSecret,
            PreviousSessionId = login.GetProperty("sessionId").GetString()!
        });
        var resumed = await second.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        Assert.Equal(2, resumed.GetProperty("sessionVersion").GetInt64());
        Assert.NotEqual(login.GetProperty("accessCredential").GetString(),
            resumed.GetProperty("accessCredential").GetString()); // rotation re-issues access
        // The rotated-away connection is displaced with the notice.
        await first.ExpectAsync(EdgeTypes.SessionReplaced, TimeSpan.FromSeconds(30));

        // The exact retry replays the same grant on a third connection and displaces nothing:
        // the second connection keeps answering pings afterwards.
        using var third = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await third.HelloAsync();
        await third.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-op-1",
            ResumeCredential = firstSecret,
            NextResumeSecret = secondSecret,
            PreviousSessionId = login.GetProperty("sessionId").GetString()!
        });
        var retried = await third.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        Assert.Equal(resumed.GetProperty("sessionId").GetString(), retried.GetProperty("sessionId").GetString());
        Assert.Equal(2, retried.GetProperty("sessionVersion").GetInt64());
        await second.SendRawAsync("""{"type":"ping"}""");
        await second.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SessionScoredUplinksBeforeALoginAreRefusedWithNoSession()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, _) = await StartEdgeClusterAsync(environment);
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem,
            environment.NodeAuthorityCertificatePem);
        await client.HelloAsync();

        foreach (var type in new[] { "startGameplay", "gameCommand", "ack" })
        {
            await client.SendRawAsync($$"""{"type":"{{type}}"}""");
            var error = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
            Assert.Equal(EdgeErrorCodes.NoSession, error.GetProperty("code").GetString());
        }
        // The refusal leaves the connection open for the login that fixes it.
        await client.SendRawAsync("""{"type":"ping"}""");
        await client.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AnUnmappedApplicationIsRefusedAtTheEdgeWithoutLeavingIt()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, _) = await StartEdgeClusterAsync(environment);
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem,
            environment.NodeAuthorityCertificatePem);
        await client.HelloAsync();

        await client.SendAsync(new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-not-mapped", LaunchCredential = "whatever",
            LoginAttemptId = "attempt-x", NextResumeSecret = RandomNumberGenerator.GetBytes(32)
        });
        var error = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, error.GetProperty("code").GetString());
        // The edge refused before any RPC left the process; the connection is still usable.
        await client.SendRawAsync("""{"type":"ping"}""");
        await client.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ABadPlatformCredentialIsRefusedAndTheConnectionStaysUsable()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, environment.RootCertificatePem,
            environment.NodeAuthorityCertificatePem);
        await client.HelloAsync();

        var valid = simulator.IssueCredential("app-1", "open-m4-bad", "live-m4", TimeSpan.FromMinutes(5));
        var tampered = valid[..^4] + "AAAA";
        await client.SendAsync(new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = tampered,
            LoginAttemptId = "attempt-1", NextResumeSecret = RandomNumberGenerator.GetBytes(32)
        });
        var error = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.SessionRevoked, error.GetProperty("code").GetString());
        await client.SendRawAsync("""{"type":"ping"}""");
        await client.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HostOutputNeverContainsTheWireSecrets()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await client.HelloAsync();
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        var launchCredential = simulator.IssueCredential("app-1", "open-m4-secrets", "live-m4",
            TimeSpan.FromMinutes(5));
        await client.SendAsync(new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = launchCredential,
            LoginAttemptId = "attempt-1", NextResumeSecret = firstSecret
        });
        var login = await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        var secondSecret = RandomNumberGenerator.GetBytes(32);
        await client.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-op-1",
            ResumeCredential = firstSecret,
            NextResumeSecret = secondSecret,
            PreviousSessionId = login.GetProperty("sessionId").GetString()!
        });
        await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        await client.SendAsync(new EdgeLogout { OperationId = "logout-op-1" });
        await client.WaitForCloseAsync(TimeSpan.FromSeconds(30));

        // AT32's software face: nothing the hosts printed carries the wire secrets — the
        // launch credential, either resume secret in base64, or the access credential.
        var output = environment.AllHostOutput;
        Assert.DoesNotContain(launchCredential, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(firstSecret), output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(secondSecret), output, StringComparison.Ordinal);
        Assert.DoesNotContain(login.GetProperty("accessCredential").GetString()!, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpentResumeSecretCannotStartAnotherRotation()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await client.HelloAsync();
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        await client.SendAsync(Login(simulator, "open-m4-spent", firstSecret));
        var login = await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));

        var secondSecret = RandomNumberGenerator.GetBytes(32);
        await client.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-op-1",
            ResumeCredential = firstSecret,
            NextResumeSecret = secondSecret,
            PreviousSessionId = login.GetProperty("sessionId").GetString()!
        });
        await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));

        // The old secret was consumed by the first rotation: a different operation id cannot
        // redeem it again (contracts 3.3).
        using var replay = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await replay.HelloAsync();
        await replay.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-op-2",
            ResumeCredential = firstSecret,
            NextResumeSecret = RandomNumberGenerator.GetBytes(32),
            PreviousSessionId = login.GetProperty("sessionId").GetString()!
        });
        var error = await replay.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.SessionRevoked, error.GetProperty("code").GetString());
    }

    private static EdgeLoginWithPlatformLaunch Login(DevelopmentPlatformSimulator simulator,
        string openId, byte[] nextSecret)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        return new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId, NextResumeSecret = nextSecret
        };
    }

    /// <summary>The full edge topology: control, data, streamer and the gateway. The
    /// StreamerServer and the gateway both register with the control plane — the session
    /// replacement notice needs the gateway's endpoint in routing, and the faster report
    /// interval keeps that visibility inside the test windows.</summary>
    private static async Task<(int WssPort, DevelopmentPlatformSimulator Simulator)> StartEdgeClusterAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // The node exists because an agent registered it; the streamer and gateway hosts then
        // report themselves into that node (architecture 4.4). Without this row the control
        // plane refuses every self-report as a stale agent, routing stays empty and the
        // session replacement notice cannot find the gateway's endpoint.
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m4-edge"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m4-edge" });
        }

        var streamerPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.StreamerServer", streamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}"
            });
        var wssPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.Redesign.StreamerGateway",
            RedesignM0HostTests.HostEnvironment.FreePort(), "streamer-gateway",
            new Dictionary<string, string>
            {
                ["CHORUS_WSS_PORT"] = wssPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
                ["CHORUS_STREAMER_ENDPOINT"] = $"https://localhost:{streamerPort}",
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            });
        return (wssPort, new DevelopmentPlatformSimulator(simulatorKey));
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
