using System.Globalization;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The single GameServer admission budget (FIX-PERF-07), host form: a saturated instance
/// refuses a new allocation as ResourceExhausted — never a reply a control plane could
/// mistake for a duplicate — while the rooms it already runs keep ticking, and an invalid
/// budget configuration fails closed at startup instead of silently admitting unbounded
/// rooms.
/// </summary>
public sealed class RedesignAdmissionHostTests
{
    [Fact]
    public async Task ASaturatedGameServerRefusesNewRoomsButExistingRoomsKeepRunning()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });
        // FIX-PERF-07: this host admits exactly one room. The cap comes from configuration;
        // a host that runs one room is the saturated instance this acceptance describes.
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_GAME_MAX_ACTIVE_ROOMS"] = "1"
            }, clientTenant: "tenant-a", instanceId: "game-adm");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "open-adm-a");
        var guest = await MountAsync(environment, simulator, "open-adm-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-adm");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-adm");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        const string roomA = "room-adm-a";
        var claimedA = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomA,
            OwnerInstanceId = "game-adm", LeaseSeconds = 30
        });
        var epochA = claimedA.Epoch;
        var startedA = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomA, OwnerInstanceId = "game-adm", Epoch = epochA, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "adm-start-a", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        Assert.Equal("Preparing", startedA.Status);

        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        var preparedA = await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = roomA, GameId = "game-a", OwnerInstanceId = "game-adm", Epoch = epochA,
            OperationId = "adm-prepare-a", DurationTicks = 400, RandomSeed = 777, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        });
        Assert.False(preparedA.Duplicate);
        Assert.Equal("Running", preparedA.Status);
        var tickBefore = await ReadLastTickAsync(environment.DatabaseConnectionString, roomA);

        // The second room never reaches the host's data-plane work: the admission budget
        // refuses it as ResourceExhausted while the first room keeps ticking.
        var refusal = await Assert.ThrowsAsync<RpcException>(() => gameHost.PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = "room-adm-b", GameId = "game-a", OwnerInstanceId = "game-adm",
                Epoch = epochA, OperationId = "adm-prepare-b", DurationTicks = 400,
                RandomSeed = 888, TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = "binding-adm-b-host", ParticipantId = "streamer-adm-b", Faction = "red" },
                    new GameParticipantProto { BindingId = "binding-adm-b-guest", ParticipantId = "streamer-adm-c", Faction = "blue" }
                }
            }).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, refusal.StatusCode);
        Assert.Contains("admission cap", refusal.Status.Detail);

        // The saturated host keeps its admitted room moving: progress over a refusal, never
        // a stall triggered by the rejected allocation.
        var advanced = await WaitForAsync(async () =>
            await ReadLastTickAsync(environment.DatabaseConnectionString, roomA) is { } tick
                && tick > tickBefore, TimeSpan.FromSeconds(15));
        Assert.True(advanced, "The admitted room stopped ticking under a refused admission.");
    }

    [Fact]
    public async Task InvalidAdmissionConfigurationFailsClosedAtStartup()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // FIX-PERF-07: a mistyped budget value is a configuration error — the host refuses to
        // start, it does not silently disable the cap and admit unbounded rooms. The startup
        // probe only needs the host: no data server has to be running for the config to fail.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
                new Dictionary<string, string>
                {
                    ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                    ["CHORUS_GAME_MAX_ACTIVE_ROOMS"] = "not-a-number"
                }, clientTenant: "tenant-a"));
        Assert.Contains("must be a positive integer", error.Message);
    }

    private static async Task<long?> ReadLastTickAsync(string connectionString, string roomId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT last_tick FROM game.room WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return await command.ExecuteScalarAsync() is long tick ? tick : null;
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-adm-" + openId;
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(100);
        }
        return await probe();
    }
}
