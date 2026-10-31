using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-04 end to end: the upgrade window. A host that ships both versions runs a
/// new room under the 1.1.0 package (its flat 12 × level boost is a real score, 36) and
/// an old room under the 1.0.0 package (its seed-draw boost, 30 for this seed). When the
/// old rooms' owner dies mid-run, a host that staged only the new packages refuses the
/// old room's recovery by name — no substitute module, no frame — while the host that
/// still ships the old package recovers it under the old arithmetic and runs it to its
/// natural 1.0.0 result.
/// </summary>
public sealed class RedesignModuleUpgradeHostTests
{
    [Fact]
    public async Task TheUpgradeWindowRecoversTheOldRoomUnderTheOldPackageAndRefusesItWhereDropped()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortC = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}," +
                    $"game-c=https://localhost:{gamePortC}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "6",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "3"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });
        // Host A ships every version and hosts the two old rooms; host B staged only the
        // new packages (the old package is dropped from its runtime set, so an old room
        // must be refused there by name); host C ships every version and is the takeover
        // target that recovers the old room under the old package.
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            WithModules(GameEnvironment(environment.DataPort, controlEndpoint, gamePortB,
                "node-a/game-server#2"), "pkg-1-v2,pkg-duel-1,pkg-live-1"),
            clientTenant: "tenant-a", instanceId: "game-b");
        await environment.StartAsync("Chorus.GameServer", gamePortC, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortC, "node-a/game-server#3"),
            clientTenant: "tenant-a", instanceId: "game-c");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-up");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-up");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "up");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        // Phase 1: the new version runs to completion on the full-catalog host — the
        // flat 12 × level boost is the 1.1.0 arithmetic, not the 1.0.0 seed draw.
        const string roomNew = "room-up-new";
        const string roomOld = "room-up-old";
        const string roomDropped = "room-up-dropped";
        var newHost = await MountAsync(environment, simulator, "up-new-host");
        var newGuest = await MountAsync(environment, simulator, "up-new-guest");
        var (newEpoch, newSession) = await StartRoomAsync(environment, control, rounds,
            roomNew, "1.1.0", "cfg-1-v2", "pkg-1-v2", gamePortC, controlCertificate,
            newHost, newGuest, "up-start-new");
        await PushBoostAsync(environment, newEpoch, newSession, roomNew, "cmd-up-new");
        var newResultJson = await WaitForResultAsync(environment, rounds, gamePortC, roomNew,
            newEpoch, controlCertificate, "up-sync-new", TimeSpan.FromSeconds(90));
        using (var newResult = JsonDocument.Parse(newResultJson))
        {
            Assert.Equal(36L, ScoreOf(newResult, "red")); // 12 × level 3
            Assert.Equal(0L, ScoreOf(newResult, "blue"));
            Assert.Equal("red", WinnerOf(newResult));
        }

        // Phase 2: two old rooms start on host A back to back, each takes one boost, and
        // both are still mid-run when their owner dies — the interruption recovery has to
        // survive. The mounts are done up front so the two starts are as close as possible
        // and neither room finishes before the kill (long duration, no inputs after the
        // single boost).
        var oldHost = await MountAsync(environment, simulator, "up-old-host");
        var oldGuest = await MountAsync(environment, simulator, "up-old-guest");
        var droppedHost = await MountAsync(environment, simulator, "up-drop-host");
        var droppedGuest = await MountAsync(environment, simulator, "up-drop-guest");
        var (oldEpoch, oldSession) = await StartRoomAsync(environment, control, rounds,
            roomOld, GameModuleIdentity.ModuleVersion, GameModuleIdentity.ConfigHash,
            GameModuleIdentity.PackageHash, gamePortA, controlCertificate,
            oldHost, oldGuest, "up-start-old", durationTicks: 500);
        await PushBoostAsync(environment, oldEpoch, oldSession, roomOld, "cmd-up-old");
        var (droppedEpoch, droppedSession) = await StartRoomAsync(environment, control, rounds,
            roomDropped, GameModuleIdentity.ModuleVersion, GameModuleIdentity.ConfigHash,
            GameModuleIdentity.PackageHash, gamePortA, controlCertificate,
            droppedHost, droppedGuest, "up-start-dropped", durationTicks: 500);
        await PushBoostAsync(environment, droppedEpoch, droppedSession, roomDropped, "cmd-up-dropped");
        await WaitForHeadAsync(environment, roomOld, 3);
        await WaitForHeadAsync(environment, roomDropped, 3);

        // Phase 3: the owner dies mid-run; the frozen heads are what any recovery must
        // start from. Its lease hands both rooms over.
        environment.Kill("game-a");
        var headOld = await HeadAsync(environment, roomOld);
        var headDropped = await HeadAsync(environment, roomDropped);
        var leaseExpired = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomDropped
            });
            return ownership.Found
                && ownership.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }, TimeSpan.FromSeconds(40));
        Assert.True(leaseExpired, "The killed owner's lease never expired.");

        // Phase 4: the dropped-package host refuses the old room's recovery by name.
        var toB = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomDropped,
            ToInstanceId = "game-b", LeaseSeconds = 15, OperationId = "up-recover-b"
        });
        Assert.Equal(droppedEpoch + 1, toB.Epoch);
        using (var hostChannelB = environment.Channel(gamePortB, controlCertificate, "game-server"))
        {
            var refused = await new GameHost.GameHostClient(hostChannelB).RecoverRoomAsync(
                new RecoverRoomRequest
                {
                    RoomId = roomDropped, GameId = "game-a", OwnerInstanceId = "game-b",
                    Epoch = toB.Epoch, TickMs = 25
                }, deadline: DateTime.UtcNow.AddSeconds(30));
            Assert.False(refused.Recovered, refused.Note);
            Assert.Contains($"package \"{GameModuleIdentity.PackageHash}\"", refused.Note,
                StringComparison.Ordinal);
            Assert.Contains("pkg-1-v2, pkg-duel-1, pkg-live-1", refused.Note, StringComparison.Ordinal);
        }
        // No frame was committed under a substitute module: the head is exactly what the
        // killed owner left behind.
        Assert.Equal(headDropped, await HeadAsync(environment, roomDropped));

        // Phase 5: the full-catalog host recovers the old room under the old package and
        // runs it to its natural 1.0.0 result — the seed-draw boost, not the 1.1.0 flat one.
        var toC = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomOld,
            ToInstanceId = "game-c", LeaseSeconds = 15, OperationId = "up-recover-c"
        });
        Assert.Equal(oldEpoch + 1, toC.Epoch);
        using (var hostChannelC = environment.Channel(gamePortC, controlCertificate, "game-server"))
        {
            var recovered = await new GameHost.GameHostClient(hostChannelC).RecoverRoomAsync(
                new RecoverRoomRequest
                {
                    RoomId = roomOld, GameId = "game-a", OwnerInstanceId = "game-c",
                    Epoch = toC.Epoch, TickMs = 25
                }, deadline: DateTime.UtcNow.AddSeconds(30));
            Assert.True(recovered.Recovered, recovered.Note);
            Assert.True(recovered.LastTick >= headOld, "The recovery lost the killed owner's head.");
        }
        var oldResultJson = await WaitForResultAsync(environment, rounds, gamePortC, roomOld,
            toC.Epoch, controlCertificate, "up-sync-old", TimeSpan.FromSeconds(120));
        using (var oldResult = JsonDocument.Parse(oldResultJson))
        {
            Assert.InRange(ScoreOf(oldResult, "red"), 30, 34); // 10 × 3 plus the seed draw
            Assert.Equal(0L, ScoreOf(oldResult, "blue"));
            Assert.Equal("red", WinnerOf(oldResult));
        }
    }

    private static Dictionary<string, string> WithModules(
        Dictionary<string, string> environment, string packages)
    {
        environment["CHORUS_MODULE_PACKAGES"] = packages;
        return environment;
    }

    /// <summary>Claims the room for game-a, starts it under the given package identity and
    /// prepares it; returns the claimed epoch and the host's session for client input.</summary>
    private static async Task<(long Epoch, string HostSession)> StartRoomAsync(
        RedesignM0HostTests.HostEnvironment environment,
        ControlService.ControlServiceClient control, RoundStore.RoundStoreClient rounds,
        string roomId, string moduleVersion,
        string configHash, string packageHash, int gamePort,
        System.Security.Cryptography.X509Certificates.X509Certificate2 controlCertificate,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string operationId,
        int durationTicks = 120)
    {
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-a", LeaseSeconds = 30
        });
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-a", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = moduleVersion, ConfigHash = configHash, PackageHash = packageHash,
            OperationId = operationId, TenantId = "tenant-a",
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
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a",
                Epoch = claimed.Epoch, OperationId = operationId + "-prepare",
                DurationTicks = durationTicks, RandomSeed = 777, TickMs = 25, PackageHash = packageHash,
                Participants =
                {
                    new GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    },
                    new GameParticipantProto
                    {
                        BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue"
                    }
                }
            });
        Assert.Equal("Running", prepared.Status);
        return (claimed.Epoch, host.SessionId);
    }

    private static async Task PushBoostAsync(
        RedesignM0HostTests.HostEnvironment environment,
        long epoch, string hostSession, string roomId, string commandId)
    {
        using var inputCertificate = environment.Certificate("tenant-a", "streamer-gateway", "input-" + commandId);
        using var inputChannel = environment.Channel(environment.DataPort, inputCertificate, "data-server");
        await new RoundInput.RoundInputClient(inputChannel).AcceptClientInputAsync(
            new ClientInputRequest
            {
                SessionId = hostSession, RoomId = roomId, RoomEpoch = epoch, GameId = "game-a",
                CommandId = commandId, PayloadJson = """{"action":"boost","level":3}"""
            }, deadline: DateTime.UtcNow.AddSeconds(30));
    }

    /// <summary>Subscribes to the round's result stream and waits for the room to leave
    /// Running, then returns the result document the settlement produced.</summary>
    private static async Task<string> WaitForResultAsync(
        RedesignM0HostTests.HostEnvironment environment, RoundStore.RoundStoreClient rounds,
        int gamePort, string roomId, long epoch,
        System.Security.Cryptography.X509Certificates.X509Certificate2 controlCertificate,
        string syncId, TimeSpan timeout)
    {
        string? resultJson = null;
        using var syncCertificate = environment.Certificate("tenant-a", "streamer-server", syncId);
        using var syncChannel = environment.Channel(gamePort, syncCertificate, "game-server");
        using var subscriber = new RoundSync.RoundSyncClient(syncChannel).Subscribe(new SubscribeRequest
        {
            RoomId = roomId, GameId = "game-a", Epoch = epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 1
        }, deadline: DateTime.UtcNow.Add(timeout));
        var reading = Task.Run(async () =>
        {
            await foreach (var message in subscriber.ResponseStream.ReadAllAsync())
                if (message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                    resultJson = message.RoundResult.ResultJson;
        });
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", timeout);
        Assert.True(finished, $"The room {roomId} did not finish in time.");
        await reading.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(resultJson);
        return resultJson!;
    }

    private static Dictionary<string, string> GameEnvironment(
        int dataPort, string controlEndpoint, int gamePort, string slotId) => new()
    {
        ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
        ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
        ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
        ["CHORUS_SLOT_ID"] = slotId,
        ["CHORUS_REPORT_INTERVAL_MS"] = "500"
    };

    private static async Task RegisterHostsAsync(
        RedesignM0HostTests.HostEnvironment environment, RoutingService.RoutingClient routing, string openPrefix)
    {
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-" + openPrefix))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-" + openPrefix });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-b"
                && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-c"
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "up-" + openId;
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

    private static async Task<long> HeadAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(max(tick_id),0) FROM game.frame WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> WaitForHeadAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId, long floor)
    {
        long headTick = 0;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            headTick = await HeadAsync(environment, roomId);
            if (headTick >= floor) return headTick;
            await Task.Delay(100);
        }
        Assert.True(headTick >= floor, $"The room never reached head {floor} (saw {headTick}).");
        return headTick;
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

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(200);
        }
        return await probe();
    }
}
