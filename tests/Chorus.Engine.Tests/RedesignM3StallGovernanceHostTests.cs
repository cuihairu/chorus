using System.Globalization;
using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-TAKE-07 stall governor, host form. Three rooms share one healthy-heartbeat host:
/// one freezes silently (the actor hang seam — the head pins while the process keeps
/// reporting), one fails its module deterministically, one runs normally. The governor
/// migrates only the silent freeze — to the other host under a newer epoch, where it
/// recovers from the pinned head and finishes — parks the deterministic fault where the
/// actor left it (a migration would repeat the fault), and leaves the healthy room alone.
/// The old host's writes stay fenced: after the transfer its epoch is stale and the data
/// plane refuses the frame.
/// </summary>
public sealed class RedesignM3StallGovernanceHostTests
{
    [Fact]
    public async Task ASilentFreezeMigratesWhileModuleFaultsAndHealthyRoomsStayPut()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // A short stall cadence puts the threshold in test time; the recovery-deadline sweep
        // stays out of the way — this test exercises the governor, not the deadline.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "500",
                ["CHORUS_STALL_SECONDS"] = "2",
                ["CHORUS_FRESH_SECONDS"] = "10",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
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

        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#2"),
            clientTenant: "tenant-a", instanceId: "game-b");
        var hostA = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;
        var hostB = environment.HostProcesses.Single(entry => entry.Instance == "game-b").Process;
        Assert.False(hostA.HasExited, environment.HostOutput(hostA));
        Assert.False(hostB.HasExited, environment.HostOutput(hostB));

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-stall");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-m3-stall");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var frames = new FrameStore.FrameStoreClient(gameChannel);

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-stall"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-stall" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-b"
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        // A binding points at one room at a time: each room mounts its own streamer pair.
        var hangHost = await MountAsync(environment, simulator, "open-m3s-a");
        var hangGuest = await MountAsync(environment, simulator, "open-m3s-b");
        var moduleHost = await MountAsync(environment, simulator, "open-m3s-c");
        var moduleGuest = await MountAsync(environment, simulator, "open-m3s-d");
        var healthyHost = await MountAsync(environment, simulator, "open-m3s-e");
        var healthyGuest = await MountAsync(environment, simulator, "open-m3s-f");

        const string hangRoom = "room-m3s-hang";
        const string moduleRoom = "room-m3s-module";
        const string healthyRoom = "room-m3s-run";
        foreach (var (roomId, operationId, failAfterTick, hangAfterTick, roomHost, roomGuest) in new[]
                 {
                     (hangRoom, "m3s-start-hang", 0, 8, hangHost, hangGuest),
                     (moduleRoom, "m3s-start-module", 5, 0, moduleHost, moduleGuest),
                     (healthyRoom, "m3s-start-run", 0, 0, healthyHost, healthyGuest)
                 })
        {
            var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
                OwnerInstanceId = "game-a", LeaseSeconds = 30
            });
            var epoch = claimed.Epoch;
            var started = await rounds.StartRoundAsync(new StartRoundRequest
            {
                RoomId = roomId, OwnerInstanceId = "game-a", Epoch = epoch, GameId = "game-a",
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
                OperationId = operationId, TenantId = "tenant-a",
                Participants =
                {
                    new RoundParticipantProto { BindingId = roomHost.BindingId, ParticipantId = roomHost.StreamerId, Role = "Host" },
                    new RoundParticipantProto { BindingId = roomGuest.BindingId, ParticipantId = roomGuest.StreamerId, Role = "Guest" }
                }
            });
            Assert.Equal("Preparing", started.Status);
            using var hostChannel = environment.Channel(gamePortA, controlCertificate, "game-server");
            var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
                new PrepareRoomRequest
                {
                    RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epoch,
                    OperationId = $"{operationId}-prepare", DurationTicks = 200, RandomSeed = 777,
                    TickMs = 25, FailAfterTick = failAfterTick, HangAfterTick = hangAfterTick,
                    HangMs = hangAfterTick > 0 ? 15_000 : 0,
                    Participants =
                    {
                        new GameParticipantProto { BindingId = roomHost.BindingId, ParticipantId = roomHost.StreamerId, Faction = "red" },
                        new GameParticipantProto { BindingId = roomGuest.BindingId, ParticipantId = roomGuest.StreamerId, Faction = "blue" }
                    }
                });
            Assert.Equal("Running", prepared.Status);
        }

        // The governor migrates the silent freeze: the frozen evidence only acts while the
        // Active ownership still names the generation it froze in, so the transfer lands at
        // a newer epoch and the target recovers from the pinned head (the recovered actor
        // carries no hang seam).
        var migrated = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand(
                "SELECT phase,to_instance FROM control.recovery_operation WHERE room_id=@room", connection);
            operation.Parameters.AddWithValue("room", hangRoom);
            await using var reader = await operation.ExecuteReaderAsync();
            return await reader.ReadAsync() && reader.GetString(0) == "Activated"
                && reader.GetString(1) == "game-b";
        }, TimeSpan.FromSeconds(60));
        Assert.True(migrated, "The governor never migrated the silently frozen room.");

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operations = new NpgsqlCommand(
                "SELECT count(*) FROM control.recovery_operation WHERE room_id=@room", connection);
            operations.Parameters.AddWithValue("room", hangRoom);
            Assert.Equal(1, Convert.ToInt32(await operations.ExecuteScalarAsync()));
            foreach (var untouched in new[] { moduleRoom, healthyRoom })
            {
                await using var others = new NpgsqlCommand(
                    "SELECT count(*) FROM control.recovery_operation WHERE room_id=@room", connection);
                others.Parameters.AddWithValue("room", untouched);
                Assert.Equal(0, Convert.ToInt32(await others.ExecuteScalarAsync()));
            }
            // The recovered room's ownership names the target at the newer epoch.
            await using var ownership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,epoch FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            ownership.Parameters.AddWithValue("room", hangRoom);
            await using var ownerReader = await ownership.ExecuteReaderAsync();
            Assert.True(await ownerReader.ReadAsync());
            Assert.Equal(("game-b", 2L), (ownerReader.GetString(0), ownerReader.GetInt64(1)));
        }

        // Old writes stay fenced (design §4): after the transfer the frozen host's epoch is
        // stale and the data plane refuses its frame — the fence, not a guess, ends it.
        var stale = await Assert.ThrowsAsync<RpcException>(async () => await frames.CommitFrameAsync(
            new CommitFrameRequest
            {
                RoomId = hangRoom, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = 1,
                CommitId = "m3s-stale-commit", PrevTick = 7, PrevStateVersion = 8,
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
            }, deadline: DateTime.UtcNow.AddSeconds(10)));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);

        // The recovered room converges on the target and the untouched rooms finish where
        // they started: one migration, no churn, no collateral moves.
        var hangFinished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = hangRoom, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(hangFinished, "The recovered room never finished on the target.");
        var healthyFinished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = healthyRoom, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(healthyFinished, "The healthy room did not finish on its own host.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var parked = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id=@room", connection);
            parked.Parameters.AddWithValue("room", moduleRoom);
            Assert.Equal("Suspended", await parked.ExecuteScalarAsync());
        }
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

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-m3s-" + openId;
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
            await Task.Delay(200);
        }
        return await probe();
    }
}
