using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-02 end to end: through the real gRPC faces, one data plane and one game
/// host, the cluster's two shipped packages each start a room and run it to a committed
/// result under their own rules — the same gift moves the default room and is noise to
/// the duel room, whose own boost rule decides it — while a room naming a package the
/// host does not ship is refused by name as FailedPrecondition and never starts under
/// the default example module instead.
/// </summary>
public sealed class RedesignModuleLoadHostTests
{
    private const int DurationTicks = 40;

    [Fact]
    public async Task TwoShippedPackagesEachRunARoomAndAMissingOneIsRefused()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var cluster = await BootAsync(environment);

        // The default package: a gift scores red 7.
        const string roomDefault = "room-mod-default";
        var defaultResult = await RunRoundAsync(environment, cluster, roomDefault, "pkg-1",
            """{"kind":"gift","amount":7}""");
        Assert.Equal(7L, ScoreOf(defaultResult, "red"));
        Assert.Equal(0L, ScoreOf(defaultResult, "blue"));
        Assert.Equal("red", WinnerOf(defaultResult));

        // The duel package: the same gift is noise, the fighters' boost decides — red 30.
        const string roomDuel = "room-mod-duel";
        var duelResult = await RunRoundAsync(environment, cluster, roomDuel, "pkg-duel-1",
            """{"action":"boost","level":3}""");
        Assert.Equal(30L, ScoreOf(duelResult, "red")); // 30 flat: the gift added nothing
        Assert.Equal(0L, ScoreOf(duelResult, "blue"));
        Assert.Equal("red", WinnerOf(duelResult));

        // A package the host does not ship: refused by name, FailedPrecondition, and the
        // room never runs — no fallback to the default example module.
        const string roomMissing = "room-mod-missing";
        var opened = await OpenRoundAsync(environment, cluster, roomMissing, "9.9.9", "cfg-gone", "pkg-gone");
        using (var refuseCertificate = environment.Certificate("@cluster", "control-server", "mod-refuse"))
        using (var refuseChannel = environment.Channel(cluster.GamePort, refuseCertificate, "game-server"))
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () =>
                await new GameHost.GameHostClient(refuseChannel).PrepareRoomAsync(
                    RoomRequest(roomMissing, opened.Epoch, "pkg-gone", opened.Host, opened.Guest,
                        "mod-missing-prepare")));
            Assert.Equal(StatusCode.FailedPrecondition, error.Status.StatusCode);
            Assert.Contains("pkg-gone", error.Status.Detail, StringComparison.Ordinal);
            Assert.Contains("pkg-1", error.Status.Detail, StringComparison.Ordinal);
        }

        // The refused room is still where the data plane left it: admitted (Preparing),
        // never Running — nothing silently started it under the default module.
        Assert.Equal("Preparing", await RoundStatusAsync(environment, roomMissing));
    }

    /// <summary>Opens a round under the named package, pushes one input and returns the
    /// module's frozen result document the stream delivered.</summary>
    private static async Task<JsonDocument> RunRoundAsync(
        RedesignM0HostTests.HostEnvironment environment, ModuleCluster cluster, string roomId,
        string packageHash, string inputJson)
    {
        var (version, config) = packageHash switch
        {
            "pkg-1" => ("1.0.0", "cfg-1"),
            _ => ("0.9.0", "cfg-duel-1")
        };
        var opened = await OpenRoundAsync(environment, cluster, roomId, version, config, packageHash);

        using var prepareCertificate = environment.Certificate("@cluster", "control-server", "mod-" + roomId);
        using var prepareChannel = environment.Channel(cluster.GamePort, prepareCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(prepareChannel).PrepareRoomAsync(
            RoomRequest(roomId, opened.Epoch, packageHash, opened.Host, opened.Guest,
                "mod-" + roomId + "-prepare"));
        Assert.Equal("Running", prepared.Status);

        using var syncCertificate = environment.Certificate("tenant-a", "streamer-server", "sync-mod");
        using var syncChannel = environment.Channel(cluster.GamePort, syncCertificate, "game-server");
        using var subscriber = new RoundSync.RoundSyncClient(syncChannel).Subscribe(new SubscribeRequest
        {
            RoomId = roomId, GameId = "game-a", Epoch = opened.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 1
        }, deadline: DateTime.UtcNow.AddMinutes(4));
        string? resultJson = null;
        var reading = Task.Run(async () =>
        {
            await foreach (var message in subscriber.ResponseStream.ReadAllAsync())
                if (message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                    resultJson = message.RoundResult.ResultJson;
        });

        using var inputCertificate = environment.Certificate("tenant-a", "streamer-gateway", "mod-input");
        using var inputChannel = environment.Channel(environment.DataPort, inputCertificate, "data-server");
        await new RoundInput.RoundInputClient(inputChannel).AcceptClientInputAsync(new ClientInputRequest
        {
            SessionId = opened.Host.SessionId, RoomId = roomId, RoomEpoch = opened.Epoch, GameId = "game-a",
            CommandId = "cmd-" + roomId, PayloadJson = inputJson
        }, deadline: DateTime.UtcNow.AddSeconds(30));

        await reading.WaitAsync(TimeSpan.FromSeconds(150));
        Assert.NotNull(resultJson);
        return JsonDocument.Parse(resultJson!);
    }

    private static PrepareRoomRequest RoomRequest(string roomId, long epoch, string packageHash,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string operationId) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch,
        OperationId = operationId, DurationTicks = DurationTicks, RandomSeed = 777, TickMs = 25,
        PackageHash = packageHash,
        Participants =
        {
            new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
            new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
        }
    };

    /// <summary>Claims the room and starts the round on the data plane under the named
    /// package identity, then mounts the round's two participants.</summary>
    private static async Task<(long Epoch,
        (string StreamerId, string SessionId, string BindingId) Host,
        (string StreamerId, string SessionId, string BindingId) Guest)>
        OpenRoundAsync(RedesignM0HostTests.HostEnvironment environment, ModuleCluster cluster,
            string roomId, string moduleVersion, string configHash, string packageHash)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "mod-open");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var claimed = await new ControlService.ControlServiceClient(controlChannel).ClaimOwnershipAsync(
            new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
                OwnerInstanceId = "game-1", LeaseSeconds = 15
            });
        Assert.True(claimed.Found);

        var host = await MountAsync(environment, cluster.Simulator, "mod-a-" + roomId);
        var guest = await MountAsync(environment, cluster.Simulator, "mod-b-" + roomId);

        using var roundCertificate = environment.Certificate("tenant-a", "game-server", "mod-rounds");
        using var roundChannel = environment.Channel(environment.DataPort, roundCertificate, "data-server");
        var started = await new RoundStore.RoundStoreClient(roundChannel).StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = moduleVersion, ConfigHash = configHash, PackageHash = packageHash,
            OperationId = roomId + "/start", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                },
                new RoundParticipantProto
                {
                    BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest"
                }
            }
        });
        Assert.Equal("Preparing", started.Status);
        return (claimed.Epoch, host, guest);
    }

    private static async Task<string> RoundStatusAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        using var certificate = environment.Certificate("tenant-a", "game-server", "mod-status");
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        var reply = await new RoundStore.RoundStoreClient(channel).GetRoundAsync(
            new GetRoundRequest { RoomId = roomId, GameId = "game-a" },
            deadline: DateTime.UtcNow.AddSeconds(30));
        return reply.Status;
    }

    private static long ScoreOf(JsonDocument result, string faction)
    {
        foreach (var entry in result.RootElement.GetProperty("ranking").EnumerateArray())
            if (entry.GetProperty("faction").GetString() == faction)
                return entry.GetProperty("score").GetInt64();
        throw new InvalidOperationException($"The result names no {faction} faction.");
    }

    private static string WinnerOf(JsonDocument result) =>
        result.RootElement.GetProperty("winner").GetString()!;

    /// <summary>Boots control, data, a simulator-backed streamer server and one game host
    /// wired straight to the data plane — no fault injection here.</summary>
    private static async Task<ModuleCluster> BootAsync(RedesignM0HostTests.HostEnvironment environment)
    {
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-1=https://localhost:{gamePort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-mod"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-mod" });
        }

        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}"
            });

        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            }, clientTenant: "tenant-a", instanceId: "game-1");
        return new ModuleCluster(gamePort, new DevelopmentPlatformSimulator(simulatorKey));
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-mod-" + openId;
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

    private sealed record ModuleCluster(int GamePort, DevelopmentPlatformSimulator Simulator);
}
