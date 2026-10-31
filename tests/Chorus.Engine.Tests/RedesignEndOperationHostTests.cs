using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-01 over real hosts: the end operation is a control-plane row, so the process
/// that recorded it can die between any two steps. The game host's begin lands on the
/// ControlServer, replays as the same operation after a lost reply, refuses a second
/// business id for the same room, and a control-plane restart reads the same row — same
/// id, phase and mount generations — and keeps answering under it. A late begin naming a
/// generation the mounts do not hold writes nothing and leaves the recorded operation
/// untouched.
/// </summary>
public sealed class RedesignEndOperationHostTests
{
    [Fact]
    public async Task ARestartedControlPlaneContinuesTheSameEndOperation()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        // A mounted binding and a started round — the mounts the end operation's fence reads.
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "end-op-host-a", "live-key-end-op-a");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-end-op");
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-end-op");
        using (var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server"))
        using (var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server"))
        {
            var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
            var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);
            var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-end-op-1",
                OwnerInstanceId = "gs-end-op", LeaseSeconds = 30
            });
            var started = await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
            {
                RoomId = "room-end-op-1", OwnerInstanceId = "gs-end-op", Epoch = claimed.Epoch,
                GameId = "game-a", ModuleVersion = "1.1.0", ConfigHash = "cfg-1-v2",
                PackageHash = "pkg-1-v2", OperationId = "end-op-round-1", TenantId = "tenant-a",
                Participants =
                {
                    new Chorus.Rpc.RoundParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                    }
                }
            });
            Assert.Equal("Preparing", started.Status);
        }

        // The game host's step-1 begin — the caller the service exists for.
        using var preChannel = environment.Channel(environment.ControlPort, gameCertificate,
            "control-server");
        var pre = new Chorus.Rpc.RoundEnd.RoundEndClient(preChannel);
        var begin = await pre.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
            EndOperationId = "end-op-1", RoundGeneration = 1, FinalTick = 120, ResultVersion = 3
        });
        Assert.Equal("end-op-1", begin.EndOperationId);
        Assert.Equal("ResultFrozen", begin.Phase);
        Assert.False(begin.Duplicate);

        // The reply was lost, not the operation: the same begin replays the one row, and a
        // second business id for the same round refuses.
        var replay = await pre.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
            EndOperationId = "end-op-1", RoundGeneration = 1, FinalTick = 120, ResultVersion = 3
        });
        Assert.True(replay.Duplicate);
        Assert.Equal("end-op-1", replay.EndOperationId);
        var secondError = await Assert.ThrowsAsync<RpcException>(() => pre.BeginEndAsync(
            new Chorus.Rpc.BeginEndRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
                EndOperationId = "end-op-2", RoundGeneration = 1, FinalTick = 120, ResultVersion = 3
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, secondError.StatusCode);

        var status = await pre.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
        });
        Assert.True(status.Found);
        Assert.Equal("end-op-1", status.EndOperationId);
        Assert.Equal("ResultFrozen", status.Phase);
        Assert.Equal(1, status.RoundGeneration);
        Assert.Equal(120, status.FinalTick);
        Assert.Equal(3, status.ResultVersion);
        var mount = Assert.Single(status.BindingGenerations);
        Assert.Equal(host.BindingId, mount.BindingId);
        Assert.Equal(1, mount.RoundGeneration);

        // The control plane dies between two steps and comes back on the same port: the
        // operation row is the truth it continues under.
        environment.Kill("Chorus.ControlServer");
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        using var postChannel = environment.Channel(environment.ControlPort, gameCertificate,
            "control-server");
        var post = new Chorus.Rpc.RoundEnd.RoundEndClient(postChannel);
        var resumed = await post.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
        });
        Assert.True(resumed.Found);
        Assert.Equal("end-op-1", resumed.EndOperationId);
        Assert.Equal("ResultFrozen", resumed.Phase);
        Assert.Equal(1, resumed.RoundGeneration);
        Assert.Equal(host.BindingId, Assert.Single(resumed.BindingGenerations).BindingId);

        // The same begin after the restart still replays the one operation — the row, not
        // the process, owns the business id.
        var replayAfterRestart = await post.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
            EndOperationId = "end-op-1", RoundGeneration = 1, FinalTick = 120, ResultVersion = 3
        });
        Assert.True(replayAfterRestart.Duplicate);

        // A late begin naming a round generation the mount does not hold writes nothing and
        // leaves the recorded operation where it stands.
        var lateError = await Assert.ThrowsAsync<RpcException>(() => post.BeginEndAsync(
            new Chorus.Rpc.BeginEndRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
                EndOperationId = "end-op-late", RoundGeneration = 2, FinalTick = 120, ResultVersion = 3
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, lateError.StatusCode);
        var untouched = await post.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
        });
        Assert.Equal("end-op-1", untouched.EndOperationId);
        Assert.Equal("ResultFrozen", untouched.Phase);
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId,
        string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new Chorus.Rpc.StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new Chorus.Rpc.LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new Chorus.Rpc.StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }
}
