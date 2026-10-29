using System.Globalization;
using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Redesign.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-TAKE-05 recovery-deadline sweep, host form: an open recovery whose recorded
/// deadline passes is never extended — the sweep closes the operation and moves the room to
/// a different host under a newer epoch. The timed-out target stays alive the whole time:
/// the deadline, not liveness, decides. The displaced target cannot reactivate its epoch —
/// the confirm path only admits the holder the ownership names.
/// </summary>
public sealed class RedesignM3RecoverySweepHostTests
{
    [Fact]
    public async Task ATimedOutRecoveryIsReassignedWithANewerEpochAndConverges()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The sweep runs on a short cadence so the deadline lands in test time; both game
        // hosts stay alive and registered throughout — the timeout is the operation's
        // recorded deadline, never a liveness guess.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "6",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "3",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "250"
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

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-sweep");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-at25s");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-sweep"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-sweep" });
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
        var host = await MountAsync(environment, simulator, "open-at25s-a");
        var guest = await MountAsync(environment, simulator, "open-at25s-b");

        const string roomId = "room-at25s";
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-a", LeaseSeconds = 10
        });
        var epoch = claimed.Epoch;
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-a", Epoch = epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "at25s-start", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        Assert.Equal("Preparing", started.Status);
        using var hostChannelA = environment.Channel(gamePortA, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannelA).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epoch,
                OperationId = "at25s-prepare", DurationTicks = 200, RandomSeed = 777, TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                    new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
                }
            });
        Assert.Equal("Running", prepared.Status);
        long headTick = 0;
        var headDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < headDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, headTick) = await FrameStatsAsync(connection, roomId);
            }
            if (headTick >= 2) break;
            await Task.Delay(100);
        }
        Assert.True(headTick >= 2, "The room never committed a head to recover from.");

        // The control plane transfers the room to B, and B — alive but busy elsewhere — never
        // runs its recovery. The one-second deadline is the whole budget: nothing extends it.
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 1, OperationId = "sweep-at25s-1"
        });
        Assert.Equal("Recovering", transferred.Status);
        Assert.Equal(epoch + 1, transferred.Epoch);
        Assert.False(hostB.HasExited, "The timed-out target must stay alive the whole time.");

        // The sweep closes the expired operation and reassigns the room to the other host
        // under a newer epoch — B keeps breathing, its recovery simply ran out of time.
        var reassigned = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,epoch,status FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync();
            return await reader.ReadAsync() && reader.GetString(0) == "game-a" &&
                reader.GetInt64(1) == epoch + 2 && reader.GetString(2) == "Recovering";
        }, TimeSpan.FromSeconds(30));
        Assert.True(reassigned, "The sweep never reassigned the timed-out recovery.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operations = new NpgsqlCommand(
                "SELECT operation_id,phase,to_instance FROM control.recovery_operation WHERE room_id=@room ORDER BY created_at",
                connection);
            operations.Parameters.AddWithValue("room", roomId);
            await using var reader = await operations.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("sweep-at25s-1", "Failed", "game-b"),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("reassign/room-at25s/sweep-at25s-1", reader.GetString(0));
            Assert.Equal("Transferred", reader.GetString(1));
            Assert.Equal("game-a", reader.GetString(2));
            Assert.False(await reader.ReadAsync(), "The sweep must not churn operations.");
        }

        // The displaced target cannot revive its epoch: B's own confirm is refused because
        // the ownership names A — the newer epoch never re-enables the old one.
        var stale = await Assert.ThrowsAsync<RpcException>(async () => await rounds.ConfirmRoomOwnershipAsync(
            new ConfirmRoomOwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = roomId, OwnerInstance = "game-b"
            }));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);
        Assert.Contains("game-a", stale.Status.Detail);

        // The reassigned operation converges: A recovers at the newer epoch and resumes.
        var recovered = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-a", LeaseSeconds = 15, TickMs = 25
        });
        Assert.True(recovered.Recovered);
        Assert.Equal("game-a", recovered.OwnerInstanceId);
        Assert.Equal(epoch + 2, recovered.Epoch);
        Assert.Equal("Running", recovered.Status);
        Assert.True(recovered.LastTick >= headTick);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand(
                "SELECT phase FROM control.recovery_operation WHERE operation_id='reassign/room-at25s/sweep-at25s-1'",
                connection);
            Assert.Equal("Activated", Convert.ToString(await operation.ExecuteScalarAsync()));
        }

        // A carries the room to its end on its own. The window is generous: in the full
        // suite many host tests run in parallel and a recovered room re-commits its whole
        // remaining schedule, so pacing assertions here measure convergence, not latency.
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The reassigned host did not finish the room.");
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

    private static async Task<(long Count, long MaxTick)> FrameStatsAsync(NpgsqlConnection connection,
        string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*),COALESCE(max(tick_id),0) FROM game.frame WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-at25s-" + openId;
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
