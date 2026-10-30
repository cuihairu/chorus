using System.Globalization;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-TAKE-06 room progress signal, host form: a host whose heartbeat stays healthy can
/// still hold a room whose actor stopped. The self-report carries each room's state, tick and
/// stop reason separately from process readiness, the control plane keeps them in
/// control.room_progress, and the stall query names the stopped room while its healthy
/// sibling — still advancing its head — stays out of the list.
/// </summary>
public sealed class RedesignM3RoomProgressHostTests
{
    [Fact]
    public async Task AStoppedRoomIsNamedStalledWhileItsHealthySiblingKeepsAdvancing()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
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

        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            }, clientTenant: "tenant-a", instanceId: "game-a");
        var hostProcess = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;
        Assert.False(hostProcess.HasExited, environment.HostOutput(hostProcess));

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-progress");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-m3-progress");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-progress"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-progress" });
        }
        var registered = await WaitForAsync(async () =>
            (await routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(instance =>
                instance.ProcessInstanceId == "game-a" && instance.Endpoint.Length > 0),
            TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game host did not self-register into the routing snapshot.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        // A binding points at one room at a time, so each room mounts its own streamer pair.
        var failingHost = await MountAsync(environment, simulator, "open-m3p-a");
        var failingGuest = await MountAsync(environment, simulator, "open-m3p-b");
        var healthyHost = await MountAsync(environment, simulator, "open-m3p-c");
        var healthyGuest = await MountAsync(environment, simulator, "open-m3p-d");

        // Two rooms on the same host: one whose module fails deterministically and one that
        // runs to its duration. The host's heartbeat never falters — only the per-room head
        // can tell the two apart.
        var controlStore = new RedesignControlStore(environment.DatabaseConnectionString);
        const string failingRoom = "room-m3p-fail";
        const string healthyRoom = "room-m3p-run";
        foreach (var (roomId, operationId, failAfterTick, roomHost, roomGuest) in new[]
                 {
                     (failingRoom, "m3p-start-fail", 5, failingHost, failingGuest),
                     (healthyRoom, "m3p-start-run", 0, healthyHost, healthyGuest)
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
            using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
            var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
                new PrepareRoomRequest
                {
                    RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epoch,
                    OperationId = $"{operationId}-prepare", DurationTicks = 200, RandomSeed = 777,
                    TickMs = 25, FailAfterTick = failAfterTick,
                    Participants =
                    {
                        new GameParticipantProto { BindingId = roomHost.BindingId, ParticipantId = roomHost.StreamerId, Faction = "red" },
                        new GameParticipantProto { BindingId = roomGuest.BindingId, ParticipantId = roomGuest.StreamerId, Faction = "blue" }
                    }
                });
            Assert.Equal("Running", prepared.Status);
        }

        // The failing room's actor stops after three strikes; the next self-report carries
        // the stopped state and its reason. The healthy room keeps moving its head.
        var stoppedReported = await WaitForAsync(async () =>
        {
            var row = await ReadProgressRowAsync(environment.DatabaseConnectionString, "game-a", failingRoom);
            return row is not null && row.State == "Stopped" && row.StopReason is { Length: > 0 };
        }, TimeSpan.FromSeconds(30));
        Assert.True(stoppedReported, "The stopped room never appeared in the progress report.");
        var stoppedRow = (await ReadProgressRowAsync(environment.DatabaseConnectionString, "game-a", failingRoom))!;
        Assert.Contains("module failed", stoppedRow.StopReason);

        var healthyRunning = await WaitForAsync(async () =>
        {
            var row = await ReadProgressRowAsync(environment.DatabaseConnectionString, "game-a", healthyRoom);
            return row is not null && row.State == "Running" && row.LastTick >= 2;
        }, TimeSpan.FromSeconds(30));
        Assert.True(healthyRunning, "The healthy room never appeared as running in the progress report.");
        var healthyBefore = (await ReadProgressRowAsync(environment.DatabaseConnectionString, "game-a", healthyRoom))!;
        var healthyAdvancing = await WaitForAsync(async () =>
        {
            var row = await ReadProgressRowAsync(environment.DatabaseConnectionString, "game-a", healthyRoom);
            return row is not null && row.LastTick > healthyBefore.LastTick;
        }, TimeSpan.FromSeconds(30));
        Assert.True(healthyAdvancing, "The healthy room's reported tick never advanced.");

        // The heartbeat evidence stays fresh while the room-level signal stands alone.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var observed = new NpgsqlCommand(
                "SELECT observed_at > clock_timestamp() - interval '10 seconds' FROM control.process WHERE process_instance_id='game-a'",
                connection);
            Assert.True(Convert.ToBoolean(await observed.ExecuteScalarAsync()),
                "The host's heartbeat is not fresh; the test would prove nothing.");
        }

        // The stall query names the stopped room and never the healthy sibling.
        var stalled = await controlStore.ListStalledRoomsAsync(stallSeconds: 5, freshSeconds: 10);
        Assert.Contains(stalled, room => room.RoomId == failingRoom && room.State == "Stopped");
        Assert.DoesNotContain(stalled, room => room.RoomId == healthyRoom);
    }

    private sealed record ProgressRow(string State, long LastTick, string? StopReason);

    private static async Task<ProgressRow?> ReadProgressRowAsync(string connectionString,
        string processInstanceId, string roomId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT state,last_tick,stop_reason FROM control.room_progress WHERE process_instance_id=@p AND room_id=@r",
            connection);
        command.Parameters.AddWithValue("p", processInstanceId);
        command.Parameters.AddWithValue("r", roomId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ProgressRow(reader.GetString(0), reader.GetInt64(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-m3p-" + openId;
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
