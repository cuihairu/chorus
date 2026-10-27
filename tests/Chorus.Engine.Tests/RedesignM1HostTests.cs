using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>M1 StreamerServer Login/Resume acceptance against real DataServer and StreamerServer hosts.</summary>
public sealed class RedesignM1HostTests
{
    [Fact]
    public async Task SimulatorLaunchLoginsRotatesResumeAndFencesTenantsAcrossRealHosts()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var clientCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-client-1");
        using var channel = environment.Channel(environment.StreamerPort, clientCertificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);

        var credential = simulator.IssueCredential("app-1", "open-m1", "live-m1", TimeSpan.FromMinutes(5));
        var firstSecret = RandomNumberGenerator.GetBytes(32);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential, LoginAttemptId = "attempt-1",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(firstSecret),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });
        Assert.Equal(1, login.SessionVersion);
        Assert.False(string.IsNullOrEmpty(login.StreamerId));
        Assert.False(string.IsNullOrEmpty(login.SessionId));
        Assert.False(string.IsNullOrEmpty(login.AccessCredential));
        Assert.True(login.AccessExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var identity = login.StreamerId;

        // The entity profile was initialized through the DataServer boundary before LoginReady.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT version FROM player.profile WHERE tenant_id='tenant-a' AND game_id='game-a' AND player_id=@player",
                connection);
            command.Parameters.AddWithValue("player", identity);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        // Resume rotates the secret and is idempotent for the same operation.
        var secondSecret = RandomNumberGenerator.GetBytes(32);
        var resume = await session.ResumeAsync(new ResumeRequest
        {
            PreviousSessionId = login.SessionId, ResumeOperationId = "resume-op-1",
            PreviousSecret = Google.Protobuf.ByteString.CopyFrom(firstSecret),
            NextSecret = Google.Protobuf.ByteString.CopyFrom(secondSecret),
            GatewayInstanceId = "gateway-2", ConnectionId = "connection-2"
        });
        Assert.Equal(2, resume.SessionVersion);
        Assert.False(resume.Duplicate);
        var retry = await session.ResumeAsync(new ResumeRequest
        {
            PreviousSessionId = login.SessionId, ResumeOperationId = "resume-op-1",
            PreviousSecret = Google.Protobuf.ByteString.CopyFrom(firstSecret),
            NextSecret = Google.Protobuf.ByteString.CopyFrom(secondSecret),
            GatewayInstanceId = "gateway-2", ConnectionId = "connection-2"
        });
        Assert.True(retry.Duplicate);
        Assert.Equal(resume.SessionId, retry.SessionId);
        Assert.Equal(2, retry.SessionVersion);

        // A new platform login takes over; the old session can no longer rotate.
        var thirdSecret = RandomNumberGenerator.GetBytes(32);
        var takeover = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential, LoginAttemptId = "attempt-2",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(thirdSecret),
            GatewayInstanceId = "gateway-3", ConnectionId = "connection-3"
        });
        Assert.Equal(identity, takeover.StreamerId);
        Assert.Equal(3, takeover.SessionVersion);
        var replaced = await Assert.ThrowsAsync<RpcException>(() => session.ResumeAsync(new ResumeRequest
        {
            PreviousSessionId = login.SessionId, ResumeOperationId = "resume-op-x",
            PreviousSecret = Google.Protobuf.ByteString.CopyFrom(firstSecret),
            NextSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-2", ConnectionId = "connection-2"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, replaced.StatusCode);

        // Forged launch credentials and unknown applications never create sessions.
        var forged = await Assert.ThrowsAsync<RpcException>(() => session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential[..^4] + "AAAA",
            LoginAttemptId = "attempt-3",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-9"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, forged.StatusCode);
        var unmapped = await Assert.ThrowsAsync<RpcException>(() => session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-other", LaunchCredential = "x", LoginAttemptId = "attempt-4",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-9"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, unmapped.StatusCode);

        // A tenant-b caller cannot resume tenant-a's session.
        using var otherTenant = environment.Certificate("tenant-b", "streamer-server", "streamer-client-2");
        using var otherChannel = environment.Channel(environment.StreamerPort, otherTenant, "streamer-server");
        var otherSession = new StreamerSession.StreamerSessionClient(otherChannel);
        var crossTenant = await Assert.ThrowsAsync<RpcException>(() => otherSession.ResumeAsync(new ResumeRequest
        {
            PreviousSessionId = takeover.SessionId, ResumeOperationId = "resume-op-ct",
            PreviousSecret = Google.Protobuf.ByteString.CopyFrom(thirdSecret),
            NextSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-9", ConnectionId = "connection-9"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, crossTenant.StatusCode);

        // The access credential validates against the issuing key and carries the live session.
        var validated = Chorus.Redesign.Rpc.ChorusAccessCredential.Validate(accessKey, login.AccessCredential);
        Assert.NotNull(validated);
        Assert.Equal(identity, validated.Value.StreamerId);
        Assert.Equal(login.SessionId, validated.Value.SessionId);
        Assert.Null(Chorus.Redesign.Rpc.ChorusAccessCredential.Validate(
            RandomNumberGenerator.GetBytes(32), login.AccessCredential));
    }

    [Fact]
    public async Task GatewayAttachRotatesGenerationsAndStaleCloseNeverDetachesTheNewConnection()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var clientCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-client-1");
        using var channel = environment.Channel(environment.StreamerPort, clientCertificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);

        var credential = simulator.IssueCredential("app-1", "open-attach", "live-attach", TimeSpan.FromMinutes(5));
        var secret = RandomNumberGenerator.GetBytes(32);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential, LoginAttemptId = "attempt-1",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(secret),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });

        // The access credential consumer validates the live session and reports the bound connection.
        var access = await session.ValidateAccessAsync(
            new ValidateAccessRequest { AccessCredential = login.AccessCredential });
        Assert.Equal(login.StreamerId, access.StreamerId);
        Assert.Equal(login.SessionId, access.SessionId);
        Assert.Equal(1, access.SessionVersion);
        Assert.Equal("gateway-1", access.GatewayInstanceId);
        Assert.Equal(1, access.ConnectionGeneration);
        Assert.True(access.Online);

        // A gateway switch attaches with the current secret and bumps the generation; retry is idempotent.
        var attach = await session.AttachConnectionAsync(new AttachConnectionRequest
        {
            SessionId = login.SessionId, OperationId = "attach-op-1",
            CurrentSecret = Google.Protobuf.ByteString.CopyFrom(secret),
            GatewayInstanceId = "gateway-2", ConnectionId = "connection-2"
        });
        Assert.Equal(2, attach.ConnectionGeneration);
        Assert.False(attach.Duplicate);
        var attachRetry = await session.AttachConnectionAsync(new AttachConnectionRequest
        {
            SessionId = login.SessionId, OperationId = "attach-op-1",
            CurrentSecret = Google.Protobuf.ByteString.CopyFrom(secret),
            GatewayInstanceId = "gateway-2", ConnectionId = "connection-2"
        });
        Assert.True(attachRetry.Duplicate);
        Assert.Equal(2, attachRetry.ConnectionGeneration);

        // A stale close from the old gateway and a wrong generation for the current pair change nothing.
        var stale = await session.ConnectionClosedAsync(new ConnectionClosedRequest
        {
            SessionId = login.SessionId, GatewayInstanceId = "gateway-1",
            ConnectionId = "connection-1", ConnectionGeneration = 1
        });
        Assert.False(stale.Applied);
        var wrongGeneration = await session.ConnectionClosedAsync(new ConnectionClosedRequest
        {
            SessionId = login.SessionId, GatewayInstanceId = "gateway-2",
            ConnectionId = "connection-2", ConnectionGeneration = 99
        });
        Assert.False(wrongGeneration.Applied);
        var stillOnline = await session.ValidateAccessAsync(
            new ValidateAccessRequest { AccessCredential = login.AccessCredential });
        Assert.True(stillOnline.Online);
        Assert.Equal("gateway-2", stillOnline.GatewayInstanceId);
        Assert.Equal(2, stillOnline.ConnectionGeneration);

        // The current connection closes; the same credential now reports offline, it is not rejected.
        var close = await session.ConnectionClosedAsync(new ConnectionClosedRequest
        {
            SessionId = login.SessionId, GatewayInstanceId = "gateway-2",
            ConnectionId = "connection-2", ConnectionGeneration = 2
        });
        Assert.True(close.Applied);
        var offline = await session.ValidateAccessAsync(
            new ValidateAccessRequest { AccessCredential = login.AccessCredential });
        Assert.False(offline.Online);

        // A forged credential never reaches the session state.
        var forged = await Assert.ThrowsAsync<RpcException>(() => session.ValidateAccessAsync(
            new ValidateAccessRequest { AccessCredential = login.AccessCredential[..^4] + "AAAA" }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, forged.StatusCode);

        // A foreign tenant can neither validate nor close another tenant's session.
        using var otherCertificate = environment.Certificate("tenant-b", "streamer-server", "streamer-client-2");
        using var otherChannel = environment.Channel(environment.StreamerPort, otherCertificate, "streamer-server");
        var otherSession = new StreamerSession.StreamerSessionClient(otherChannel);
        var crossValidate = await Assert.ThrowsAsync<RpcException>(() => otherSession.ValidateAccessAsync(
            new ValidateAccessRequest { AccessCredential = login.AccessCredential }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, crossValidate.StatusCode);
        var crossClose = await Assert.ThrowsAsync<RpcException>(() => otherSession.ConnectionClosedAsync(
            new ConnectionClosedRequest
            {
                SessionId = login.SessionId, GatewayInstanceId = "gateway-2",
                ConnectionId = "connection-2", ConnectionGeneration = 2
            }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, crossClose.StatusCode);
    }
}
