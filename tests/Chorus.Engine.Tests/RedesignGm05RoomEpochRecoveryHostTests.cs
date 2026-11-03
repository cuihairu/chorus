using System.Globalization;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The GM-05 explicit recovery after a bump (FIX-GM-05): the operator bumps room A to epoch 2
/// (owner unchanged, Recovering), A's old actor parks at the fenced epoch and its loop ends —
/// the runtime retires the stopped actor (FIX-ROOM-04), so the dictionary no longer holds the
/// room — and a GM recovery request to the SAME owner resumes the room from the persistent head
/// at epoch 2 and reactivates it, confirmed through the route. Repeated bump and repeated
/// recovery are no-ops: no epoch re-raise, no second loop, and a lost reply replays against the
/// original operation id.
/// </summary>
public sealed class RedesignGm05RoomEpochRecoveryHostTests
{
    [Fact]
    public async Task BumpThenRecoverTheSameOwnerAtTheNewEpoch()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // Short registrar (AT25 shape) so the single host's lease stays live through the bump
        // and recovery windows — the fence must reject for the epoch the bump moved, never for
        // a stale lease.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "20",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "5"
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
            GameEnvironment(environment.DataPort, controlEndpoint, gamePort, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        var gameProcess = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;
        Assert.False(gameProcess.HasExited);

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-gm05");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var admin = new Admin.AdminClient(controlChannel);

        // The GM operator who issues the bump (GM-06 identity, GM-03 entry); the recovery entry
        // is the control-plane admin surface, driven here by the same operator identity.
        var operators = new RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-carol", "gm", ["tenant-a"], ["game-a"], "carol-token");

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-gm05"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-gm05" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game host did not self-register into the routing snapshot.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "open-gm05-host");
        var guest = await MountAsync(environment, simulator, "open-gm05-guest");

        const string room = "room-gm05";
        const long epoch1 = 1;

        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = room,
            OwnerInstanceId = "game-a", LeaseSeconds = 15
        });
        Assert.Equal(epoch1, claimed.Epoch);

        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-gm05");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        await rounds.StartRoundAsync(RoundRequest(room, "gm05-start", host, guest));

        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        var prepared = await gameHost.PrepareRoomAsync(PrepareRequest(room, "gm05-prepare", host, guest, seed: 303));
        Assert.Equal("Running", prepared.Status);

        long headAtBump = 0;
        var headDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < headDeadline)
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            headAtBump = await FrameHeadAsync(connection, room);
            if (headAtBump >= 20) break;
            await Task.Delay(100);
        }
        Assert.InRange(headAtBump, 15, 1999);

        // --- Bump only this room through the GM entry (operator gated, GM-03). ---
        var gm = new GmOperatorClient(admin, "gm-carol", "carol-token");
        var bump = await gm.BumpRoomEpochAsync("tenant-a", "game-a", room, "gm05-op-bump-a",
            "bump before the gm05 recovery", expectedEpoch: epoch1);
        Assert.False(bump.Duplicate);
        Assert.Equal(epoch1, bump.OldEpoch);
        Assert.Equal(epoch1 + 1, bump.NewEpoch);
        Assert.Equal("game-a", bump.OwnerInstanceId);
        Assert.Equal("Recovering", bump.OwnershipStatus);

        // A second bump under a different id while the room is Recovering is refused — a
        // repeated bump never raises the epoch past the first, the room must recover first.
        var bumpWhileRecovering = await Assert.ThrowsAsync<RpcException>(async () => await gm.BumpRoomEpochAsync(
            "tenant-a", "game-a", room, "gm05-op-bump-a-2", "second bump", expectedEpoch: epoch1));
        Assert.Equal(StatusCode.FailedPrecondition, bumpWhileRecovering.StatusCode);

        // A's old actor parks at the fenced epoch and its loop ends; the runtime retires the
        // stopped actor (FIX-ROOM-04), so the dictionary no longer holds the room.
        var parked = await WaitForAsync(() => Task.FromResult(
            environment.HostOutput(gameProcess).Contains($"room {room} stopping: frame commit rejected",
                StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
        Assert.True(parked, "The bumped room's actor never parked at the fenced epoch.");

        long headAtPark = 0;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            headAtPark = await FrameHeadAsync(connection, room);
        }
        // The loop exit is a local timer; the settle gives the retire room to land so the
        // same-host recovery takes the replay path instead of answering a stale actor.
        await Task.Delay(800);

        // --- The GM asks the SAME owner to recover at the new epoch (the admin recovery
        // entry, FIX-TAKE-03 replay shape). A stale attempt before the actor retired answers
        // Recovered=false harmlessly and the next one lands. ---
        AdminRoomRecoveryReply recovered = null!;
        var recoverDeadline = DateTime.UtcNow.AddSeconds(60);
        var successRecoverId = "";
        for (var attempt = 1; DateTime.UtcNow < recoverDeadline; attempt++)
        {
            successRecoverId = $"gm05-op-recover-a-{attempt}";
            recovered = await admin.RetryRoomRecoveryAsync(new AdminRoomRecoveryRequest
            {
                RequestId = successRecoverId, Operator = "gm-carol",
                Reason = "bump left the room Recovering; recover it on the same owner",
                TenantId = "tenant-a", GameId = "game-a", RoomId = room,
                ToInstanceId = "game-a", TickMs = 25
            });
            if (recovered.Recovered) break;
            await Task.Delay(500);
        }
        Assert.True(recovered.Recovered, "The bumped room was never recovered on its owner.");
        Assert.Equal("Running", recovered.Status);
        Assert.Equal("game-a", recovered.OwnerInstanceId);
        Assert.Equal(epoch1 + 1, recovered.Epoch);
        // The recovered head is the persisted head the actor parked on — replay resumed from it.
        Assert.True(recovered.LastTick >= headAtPark,
            $"Recovery resumed from tick {recovered.LastTick}, before the parked head {headAtPark}.");

        // --- Re-activated at the new epoch: ownership Active, epoch 2, same owner. ---
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var owned = new NpgsqlCommand(
                "SELECT owner_process_instance_id,status,epoch FROM control.ownership " +
                "WHERE kind='room' AND entity_id=@room", connection);
            owned.Parameters.AddWithValue("room", room);
            await using var reader = await owned.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("game-a", reader.GetString(0));
            Assert.Equal("Active", reader.GetString(1));
            Assert.Equal(epoch1 + 1, reader.GetInt64(2));
        }

        // The recovered room actually ticks again past the parked head (resumed from the
        // persistent head, one loop only).
        var tickedPast = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await FrameHeadAsync(connection, room) > headAtPark;
        }, TimeSpan.FromSeconds(30));
        Assert.True(tickedPast, "The recovered room never ticked past the parked head.");

        // No second loop: a second writer at the same epoch would fight the tick chain and
        // park visibly, so the head growth above plus the absence of a new park line is the
        // single-loop evidence. The one park line predates the recovery.
        var parkCount = CountParkLines(environment.HostOutput(gameProcess), room);

        // --- Repeated recovery (new id) answers from the live actor, not a new one: same
        // epoch, Running, no second loop. ---
        var again = await admin.RetryRoomRecoveryAsync(new AdminRoomRecoveryRequest
        {
            RequestId = "gm05-op-recover-a-again", Operator = "gm-carol",
            Reason = "confirm the recovered room", TenantId = "tenant-a", GameId = "game-a",
            RoomId = room, ToInstanceId = "game-a", TickMs = 25
        });
        Assert.True(again.Recovered);
        Assert.Equal("Running", again.Status);
        Assert.Equal(epoch1 + 1, again.Epoch);

        // --- Repeated bump (same id) replays the recorded outcome: no epoch re-raise, and a
        // lost reply queries the original result by operation id. ---
        var replay = await gm.BumpRoomEpochAsync("tenant-a", "game-a", room, "gm05-op-bump-a",
            "bump before the gm05 recovery", expectedEpoch: epoch1);
        Assert.True(replay.Duplicate);
        Assert.Equal(epoch1, replay.OldEpoch);
        Assert.Equal(epoch1 + 1, replay.NewEpoch);

        // --- A lost recovery reply replays against the original operation id as a duplicate:
        // the recovery was already acted, never re-done. ---
        var lostReply = await admin.RetryRoomRecoveryAsync(new AdminRoomRecoveryRequest
        {
            RequestId = successRecoverId, Operator = "gm-carol",
            Reason = "bump left the room Recovering; recover it on the same owner",
            TenantId = "tenant-a", GameId = "game-a", RoomId = room,
            ToInstanceId = "game-a", TickMs = 25
        });
        Assert.True(lostReply.Duplicate);

        // The recovery and its repeats left the epoch at 2 and exactly the one loop ticking.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var owned = new NpgsqlCommand(
                "SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id=@room", connection);
            owned.Parameters.AddWithValue("room", room);
            await using var reader = await owned.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Active", reader.GetString(0));
            Assert.Equal(epoch1 + 1, reader.GetInt64(1));
        }
        var keptTicking = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await FrameHeadAsync(connection, room) > headAtPark + 5;
        }, TimeSpan.FromSeconds(30));
        Assert.True(keptTicking, "The recovered room stopped after the repeat recovery.");
        Assert.Equal(parkCount, CountParkLines(environment.HostOutput(gameProcess), room));

        // --- The route confirms the re-activation: the room's route carries the new epoch on
        // its owner, and the host process survived the whole bump+recovery window. ---
        var after = await routing.GetRoutingSnapshotAsync(new Empty());
        var route = Assert.Single(after.Rooms, item => item.RoomId == room);
        Assert.Equal("game-a", route.OwnerInstanceId);
        Assert.Equal(epoch1 + 1, route.Epoch);
        Assert.False(gameProcess.HasExited, "The game host exited during the recovery window.");
    }

    private static int CountParkLines(string output, string roomId)
    {
        var marker = $"room {roomId} stopping: frame commit rejected";
        var count = 0;
        var index = 0;
        while ((index = output.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }
        return count;
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

    private static StartRoundRequest RoundRequest(string roomId, string operationId,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest) => new()
    {
        RoomId = roomId, OwnerInstanceId = "game-a", Epoch = 1, GameId = "game-a",
        ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = operationId,
        Participants =
        {
            new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
            new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
        }
    };

    private static PrepareRoomRequest PrepareRequest(string roomId, string operationId,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, int seed) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = 1,
        OperationId = operationId, DurationTicks = 2000, RandomSeed = seed, TickMs = 25,
        Participants =
        {
            new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
            new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
        }
    };

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-gm05-" + openId;
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

    private static async Task<long> FrameHeadAsync(NpgsqlConnection connection, string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(max(tick_id),0) FROM game.frame WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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
