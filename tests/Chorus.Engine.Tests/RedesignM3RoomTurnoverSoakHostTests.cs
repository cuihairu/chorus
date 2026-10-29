using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Redesign.Rpc.Routing;
using Xunit;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-ROOM-07, the host leg: eight rounds cycle through one real game host back to back —
/// mount, claim, start, prepare, run to the natural result, settle — while the test samples
/// the host's memory. What the platform must recycle on its own is capacity, not history:
/// every round's bindings release when its round settles, no room stays Running on the
/// host's own reports, and the next round mounts and runs on the same host without any of
/// the previous seven standing in the way. The memory trend is recorded for the ledger;
/// the structural counts are what this test asserts.
/// </summary>
public sealed class RedesignM3RoomTurnoverSoakHostTests
{
    private readonly ITestOutputHelper _output;

    public RedesignM3RoomTurnoverSoakHostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EightRoundsTurnOverOnOneHostWithoutLingeringBindingsOrRunningRooms()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
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
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            }, clientTenant: "tenant-a", instanceId: "game-a");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-soak7");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-soak7");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-soak7"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-soak7" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game host did not self-register into the routing snapshot.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var settlement = new Settlement.SettlementClient(controlDataChannel);
        const int total = 8;
        var settledRooms = new List<string>();
        var rssTrend = new List<(int Round, long Rss)>();
        for (var index = 0; index < total; index++)
        {
            var roomId = $"room-soak7-{index}";
            var roomHost = await MountAsync(environment, simulator, $"soak7-{index}-host");
            var roomGuest = await MountAsync(environment, simulator, $"soak7-{index}-guest");
            var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
                OwnerInstanceId = "game-a", LeaseSeconds = 30
            });
            var started = await rounds.StartRoundAsync(new StartRoundRequest
            {
                RoomId = roomId, OwnerInstanceId = "game-a", Epoch = claimed.Epoch, GameId = "game-a",
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
                OperationId = "start-soak7-" + index, TenantId = "tenant-a",
                Participants =
                {
                    new RoundParticipantProto { BindingId = roomHost.BindingId, ParticipantId = roomHost.StreamerId, Role = "Host" },
                    new RoundParticipantProto { BindingId = roomGuest.BindingId, ParticipantId = roomGuest.StreamerId, Role = "Guest" }
                }
            });
            Assert.Equal("Preparing", started.Status);
            using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
            var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = claimed.Epoch,
                OperationId = "start-soak7-" + index + "-prepare", DurationTicks = 40, RandomSeed = 777 + index,
                TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = roomHost.BindingId, ParticipantId = roomHost.StreamerId, Faction = "red" },
                    new GameParticipantProto { BindingId = roomGuest.BindingId, ParticipantId = roomGuest.StreamerId, Faction = "blue" }
                }
            });
            Assert.Equal("Running", prepared.Status);

            // The round ends by itself, and the control plane finalizes what ended: result
            // committed by the host, Settled by FinalizeRound, mounts freed with it. A
            // Finishing hold-off answers FailedPrecondition and is retried, not assumed away.
            FinalizeRoundReply finalized = new();
            var settleDeadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < settleDeadline)
            {
                try
                {
                    finalized = await settlement.FinalizeRoundAsync(new FinalizeRoundRequest
                    {
                        RoomId = roomId, GameId = "game-a", OperationId = "finalize-soak7-" + index,
                        TenantId = "tenant-a"
                    });
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { }
                if (finalized.Status == "Settled") break;
                await Task.Delay(200);
            }
            Assert.Equal("Settled", finalized.Status);
            var pointersCleared = await WaitForAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
                await connection.OpenAsync();
                await using var pointers = new NpgsqlCommand(
                    "SELECT count(*) FROM live.binding WHERE binding_id IN (@a,@b) AND current_room_id IS NOT NULL",
                    connection);
                pointers.Parameters.AddWithValue("a", roomHost.BindingId);
                pointers.Parameters.AddWithValue("b", roomGuest.BindingId);
                return Convert.ToInt64(await pointers.ExecuteScalarAsync()) == 0;
            }, TimeSpan.FromSeconds(30));
            Assert.True(pointersCleared, $"Round {index} released its bindings late or never.");
            settledRooms.Add(roomId);
            host.Refresh();
            rssTrend.Add((index + 1, host.WorkingSet64));
            _output.WriteLine($"round {index + 1} settled; host rss={host.WorkingSet64 / (1024 * 1024)}MB");
        }

        Assert.False(host.HasExited, environment.HostOutput(host));
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            // No room stays Running on the host's own reports: the live-actor view the
            // control plane holds fell back to zero within one report cycle of the last
            // settlement (the rows follow the host's own cadence, so the check polls, and a
            // room that never reports Completed would hold the poll out to failure).
            var liveCleared = await WaitForAsync(async () =>
            {
                await using var running = new NpgsqlCommand(
                    "SELECT room_id FROM control.room_progress WHERE process_instance_id='game-a' AND state='Running'",
                    connection);
                await using var reader = await running.ExecuteReaderAsync();
                return !await reader.ReadAsync();
            }, TimeSpan.FromSeconds(10));
            Assert.True(liveCleared, "A room still reports Running after the soak.");
            await using var pointers = new NpgsqlCommand(
                "SELECT count(*) FROM live.binding WHERE current_room_id IS NOT NULL", connection);
            Assert.Equal(0, Convert.ToInt64(await pointers.ExecuteScalarAsync()));
            await using var rooms = new NpgsqlCommand(
                "SELECT count(*) FROM game.room WHERE status='Settled'", connection);
            Assert.Equal(total, Convert.ToInt64(await rooms.ExecuteScalarAsync()));
        }
        _output.WriteLine($"host rss MB trend: {string.Join(", ", rssTrend.Select(sample => sample.Rss / (1024 * 1024)))}");
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-soak7-" + openId;
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
