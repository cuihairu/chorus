using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-ROOM-06: the same room taken over A→B→A with every process alive throughout. The
/// displaced host's old object learns of the handover only through the fence — its next
/// commit is refused, its loop stops and it retires by identity — so when the room comes
/// back the same process recovers fresh under the new epoch from the persistent head:
/// the old object neither blocks the activation nor revives its stale epoch, and at every
/// instant exactly one epoch on one host can commit (AT25 single-machine).
/// </summary>
public sealed class RedesignM3DoubleTakeoverHostTests
{
    [Fact]
    public async Task ACleanBackAndForthTakeoverRetiresTheOldObjectAndKeepsOneCommitter()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // Both migrations are driven by this test; the deadline sweep and the stall
        // governor stay out of the way so their guards are not what the sequence tests.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
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
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#2"),
            clientTenant: "tenant-a", instanceId: "game-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-room6");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-room6");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var frames = new FrameStore.FrameStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "room6");
        const string roomId = "room-room6";
        var epoch = await PrepareRoomOnHostAAsync(environment, control, rounds, gamePortA,
            controlCertificate, simulatorKey, roomId, "room6");
        var firstHead = await WaitForHeadAsync(environment, roomId, 10);

        // First leg: the room moves to B, which recovers and keeps committing.
        var toB = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 15, OperationId = "room6-back-1"
        });
        Assert.Equal(epoch + 1, toB.Epoch);
        var onB = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 6, TickMs = 25
        });
        Assert.True(onB.Recovered);
        var hostBHead = await WaitForHeadAsync(environment, roomId, firstHead + 10);

        // The displaced host's old object learns through the fence: its reports name the
        // room Stopped, and the retired status outlives the object (FIX-ROOM-04/05) —
        // evidence the stale loop is gone before the room returns.
        var oldObjectStopped = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var progress = new NpgsqlCommand(
                "SELECT state FROM control.room_progress WHERE process_instance_id='game-a' AND room_id=@room",
                connection);
            progress.Parameters.AddWithValue("room", roomId);
            return Convert.ToString(await progress.ExecuteScalarAsync()) == "Stopped";
        }, TimeSpan.FromSeconds(30));
        Assert.True(oldObjectStopped, "The displaced host's old object never stopped.");

        // A epoch-N commit is refused mid-flight (design §4): from the transfer instant the
        // fence, not liveness, decides who may write.
        var staleFromA = await Assert.ThrowsAsync<RpcException>(async () => await frames.CommitFrameAsync(
            new CommitFrameRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epoch,
                CommitId = "room6-stale-a", PrevTick = firstHead, PrevStateVersion = firstHead + 1,
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
            }, deadline: DateTime.UtcNow.AddSeconds(10)));
        Assert.Equal(StatusCode.FailedPrecondition, staleFromA.StatusCode);

        // Second leg: the room comes home. The same process must activate at the new epoch
        // from the persistent head — its old object may not stand in the way.
        var backToA = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-a", LeaseSeconds = 15, OperationId = "room6-back-2"
        });
        Assert.Equal(epoch + 2, backToA.Epoch);
        var onA = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-a", LeaseSeconds = 6, TickMs = 25
        });
        Assert.True(onA.Recovered, "The displaced host's old object blocked the reactivation.");
        Assert.Equal(epoch + 2, onA.Epoch);
        Assert.True(onA.LastTick >= hostBHead, "The reactivation lost the intermediate owner's head.");

        // The stale epochs stay dead: a direct recovery at B's now-displaced epoch is
        // refused instead of reviving old state, and B's commits are fenced like A's were.
        using var hostChannelA = environment.Channel(gamePortA, controlCertificate, "game-server");
        var staleRecovery = await new GameHost.GameHostClient(hostChannelA).RecoverRoomAsync(
            new RecoverRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a",
                Epoch = epoch + 1, TickMs = 25
            }, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.False(staleRecovery.Recovered, "A stale-epoch recovery claim revived old state.");
        var staleFromB = await Assert.ThrowsAsync<RpcException>(async () => await frames.CommitFrameAsync(
            new CommitFrameRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-b", Epoch = epoch + 1,
                CommitId = "room6-stale-b", PrevTick = hostBHead, PrevStateVersion = hostBHead + 1,
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
            }, deadline: DateTime.UtcNow.AddSeconds(10)));
        Assert.Equal(StatusCode.FailedPrecondition, staleFromB.StatusCode);

        // The ledger tells the same story once: two migrations, each recorded Activated
        // for exactly its target and epoch, and the ownership ends Active back on A.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operations = new NpgsqlCommand(
                "SELECT operation_id,phase,to_instance,target_epoch FROM control.recovery_operation WHERE room_id=@room ORDER BY created_at",
                connection);
            operations.Parameters.AddWithValue("room", roomId);
            await using var reader = await operations.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("room6-back-1", "Activated", "game-b", epoch + 1),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("room6-back-2", "Activated", "game-a", epoch + 2),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            Assert.False(await reader.ReadAsync(), "The back-and-forth must not churn operations.");
            await reader.DisposeAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,epoch,status FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            ownership.Parameters.AddWithValue("room", roomId);
            await using var ownerReader = await ownership.ExecuteReaderAsync();
            Assert.True(await ownerReader.ReadAsync());
            Assert.Equal(("game-a", epoch + 2, "Active"),
                (ownerReader.GetString(0), ownerReader.GetInt64(1), ownerReader.GetString(2)));
        }
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The twice-migrated room did not finish on its original host.");
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

    /// <summary>Registers the node, then waits until both game hosts self-report routes.</summary>
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
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");
    }

    /// <summary>Mounts a streamer pair, claims, starts and prepares the room on game-a;
    /// returns the claimed epoch.</summary>
    private static async Task<long> PrepareRoomOnHostAAsync(
        RedesignM0HostTests.HostEnvironment environment,
        ControlService.ControlServiceClient control, RoundStore.RoundStoreClient rounds,
        int gamePortA, X509Certificate2 controlCertificate, byte[] simulatorKey,
        string roomId, string openPrefix)
    {
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, openPrefix + "-host");
        var guest = await MountAsync(environment, simulator, openPrefix + "-guest");
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-a", LeaseSeconds = 30
        });
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-a", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-" + openPrefix, TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        Assert.Equal("Preparing", started.Status);
        using var hostChannel = environment.Channel(gamePortA, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = claimed.Epoch,
            OperationId = "start-" + openPrefix + "-prepare", DurationTicks = 200, RandomSeed = 777,
            TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        });
        Assert.Equal("Running", prepared.Status);
        return claimed.Epoch;
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-room6-" + openId;
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

    private static async Task<long> WaitForHeadAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId, long floor)
    {
        long headTick = 0;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT COALESCE(max(tick_id),0) FROM game.frame WHERE room_id=@room", connection);
                command.Parameters.AddWithValue("room", roomId);
                headTick = Convert.ToInt64(await command.ExecuteScalarAsync());
            }
            if (headTick >= floor) return headTick;
            await Task.Delay(100);
        }
        Assert.True(headTick >= floor, $"The room never reached head {floor} (saw {headTick}).");
        return headTick;
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
