using System.Globalization;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Google.Protobuf;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The GM epoch-bump isolation on one host (FIX-GM-04): two rooms run on the same GameServer,
/// the operator bumps only room A through the GM entry, and the fence lands on A alone — A's
/// old-actor frame write, its old-epoch input and its snapshot write are each refused, its
/// committed history stays readable, and B's epoch, tick progress and route are untouched. No
/// owner change and no process restart: the bumped ownership keeps its holder and the host
/// process survives the whole window.
/// </summary>
public sealed class RedesignGm04RoomEpochBumpIsolationHostTests
{
    [Fact]
    public async Task BumpingOneRoomOnAHostFencesOnlyThatRoom()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The control plane seeds the host and runs the ownership registrar short (AT25 shape) so
        // the single host's lease stays live through the whole bump window — B must never be fenced
        // for a stale lease, only for the epoch the bump moved.
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

        // One game host carries both rooms — the isolation claim is that bumping A never touches B.
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePort, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        var gameProcess = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;
        Assert.False(gameProcess.HasExited);

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-gm04");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var admin = new Admin.AdminClient(controlChannel);

        // The GM operator who issues the bump (GM-06 identity, GM-03 entry).
        var operators = new RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-carol", "gm", ["tenant-a"], ["game-a"], "carol-token");

        // The node exists because an agent registered it; the host then reports itself into that
        // node (architecture 4.4). The registrar only renews owners it can observe, so the lease
        // stays live only once the host is registered.
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-gm04"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-gm04" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game host did not self-register into the routing snapshot.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        // Two rooms, two mounted streamers each — four mounts on the one host.
        var aHost = await MountAsync(environment, simulator, "open-gm04-a-host");
        var aGuest = await MountAsync(environment, simulator, "open-gm04-a-guest");
        var bHost = await MountAsync(environment, simulator, "open-gm04-b-host");
        var bGuest = await MountAsync(environment, simulator, "open-gm04-b-guest");

        const string roomA = "room-gm04-a";
        const string roomB = "room-gm04-b";
        const long epochA = 1;
        const long epochB = 1;

        var claimedA = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomA,
            OwnerInstanceId = "game-a", LeaseSeconds = 15
        });
        var claimedB = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomB,
            OwnerInstanceId = "game-a", LeaseSeconds = 15
        });
        Assert.Equal(epochA, claimedA.Epoch);
        Assert.Equal(epochB, claimedB.Epoch);

        // Admit and run both rooms on the same host.
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-gm04");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        await rounds.StartRoundAsync(RoundRequest(roomA, "gm04-start-a", aHost, aGuest));
        await rounds.StartRoundAsync(RoundRequest(roomB, "gm04-start-b", bHost, bGuest));

        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        var preparedA = await gameHost.PrepareRoomAsync(PrepareRequest(roomA, "gm04-prepare-a", aHost, aGuest, seed: 101));
        var preparedB = await gameHost.PrepareRoomAsync(PrepareRequest(roomB, "gm04-prepare-b", bHost, bGuest, seed: 202));
        Assert.Equal("Running", preparedA.Status);
        Assert.Equal("Running", preparedB.Status);

        // Both rooms commit a committed head whose history the bump must not lose.
        long headA = 0, headB = 0;
        var headDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < headDeadline)
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            headA = await FrameHeadAsync(connection, roomA);
            headB = await FrameHeadAsync(connection, roomB);
            if (headA >= 20 && headB >= 20) break;
            await Task.Delay(100);
        }
        Assert.InRange(headA, 15, 1999);
        Assert.InRange(headB, 15, 1999);

        // --- Bump only A through the GM entry (operator gated, GM-03). ---
        var gm = new GmOperatorClient(admin, "gm-carol", "carol-token");
        var bump = await gm.BumpRoomEpochAsync("tenant-a", "game-a", roomA, "gm04-op-bump-a", "bump A only",
            expectedEpoch: epochA);
        Assert.False(bump.Duplicate);
        Assert.Equal(epochA, bump.OldEpoch);
        Assert.Equal(epochA + 1, bump.NewEpoch);
        Assert.Equal("game-a", bump.OwnerInstanceId);   // no owner change
        Assert.Equal("Recovering", bump.OwnershipStatus);
        Assert.Equal("Pending", bump.RouteSyncStatus);

        // A's old actor parks at the fenced epoch — its next frame write is refused and the room
        // stops visibly instead of guessing forward (the fence rejection evidence line).
        var parkedA = await WaitForAsync(() => Task.FromResult(
            environment.HostOutput(gameProcess).Contains($"room {roomA} stopping: frame commit rejected",
                StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
        Assert.True(parkedA, "The bumped room's actor never parked at the fenced epoch.");

        // --- A: the old-actor frame write is fenced. ---
        using var probeCertificate = environment.Certificate("tenant-a", "game-server", "probe-gm04");
        using var probeChannel = environment.Channel(environment.DataPort, probeCertificate, "data-server");
        var frames = new FrameStore.FrameStoreClient(probeChannel);
        var staleFrame = await Assert.ThrowsAsync<RpcException>(async () => await frames.CommitFrameAsync(
            new CommitFrameRequest
            {
                RoomId = roomA, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epochA,
                CommitId = "stale-frame-probe", PrevTick = headA, PrevStateVersion = headA,
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
            }));
        Assert.Equal(StatusCode.FailedPrecondition, staleFrame.StatusCode);

        // --- A: the old-epoch snapshot write is fenced (a valid payload hash reaches the fence). ---
        var payload = new byte[] { 1, 2, 3 };
        var staleSnapshot = await Assert.ThrowsAsync<RpcException>(async () => await frames.PutSnapshotAsync(
            new PutSnapshotRequest
            {
                RoomId = roomA, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epochA,
                Tick = headA, SnapshotPayload = ByteString.CopyFrom(payload),
                SnapshotHash = ByteString.CopyFrom(SHA256.HashData(payload)),
                SnapshotSchemaVersion = 1, OperationId = "stale-snapshot-probe"
            }));
        Assert.Equal(StatusCode.FailedPrecondition, staleSnapshot.StatusCode);

        // --- A: the old-epoch input is fenced (bindingless push — the fence precedes attribution). ---
        var input = new RoundInput.RoundInputClient(probeChannel);
        var staleInput = await Assert.ThrowsAsync<RpcException>(async () => await input.AcceptRoomEventAsync(
            new AcceptRoomEventRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = roomA, OwnerInstanceId = "game-a",
                Epoch = epochA, EventId = "stale-input-probe", Platform = "simulator",
                AppId = "app-1", PayloadJson = """{"kind":"gift","amount":1}"""
            }));
        Assert.Equal(StatusCode.FailedPrecondition, staleInput.StatusCode);

        // --- A's committed history stays readable: the round is found and its frames are intact. ---
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            var round = await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomA, GameId = "game-a" });
            Assert.True(round.Found);
            var frameHead = await FrameHeadAsync(connection, roomA);
            Assert.InRange(frameHead, 15, 1999);
        }

        // --- B is untouched: epoch still 1, ownership Active, and it keeps ticking past the bump. ---
        var bHeadAtBump = headB;
        var bGrew = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await FrameHeadAsync(connection, roomB) > bHeadAtBump;
        }, TimeSpan.FromSeconds(30));
        Assert.True(bGrew, "Room B stopped ticking after room A was bumped.");

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var bOwnership = new NpgsqlCommand(
                "SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id=@room", connection);
            bOwnership.Parameters.AddWithValue("room", roomB);
            await using var bReader = await bOwnership.ExecuteReaderAsync();
            Assert.True(await bReader.ReadAsync());
            Assert.Equal("Active", bReader.GetString(0));
            Assert.Equal(epochB, bReader.GetInt64(1));
        }

        // --- B's route is unchanged in the snapshot; A's follows the bump. ---
        var after = await routing.GetRoutingSnapshotAsync(new Empty());
        var routeA = Assert.Single(after.Rooms, room => room.RoomId == roomA);
        Assert.Equal("game-a", routeA.OwnerInstanceId);
        Assert.Equal(epochA + 1, routeA.Epoch);
        var routeB = Assert.Single(after.Rooms, room => room.RoomId == roomB);
        Assert.Equal("game-a", routeB.OwnerInstanceId);
        Assert.Equal(epochB, routeB.Epoch);

        // --- No owner change and no process restart: A keeps its holder and the host is alive. ---
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var aOwnership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,status,epoch FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            aOwnership.Parameters.AddWithValue("room", roomA);
            await using var aReader = await aOwnership.ExecuteReaderAsync();
            Assert.True(await aReader.ReadAsync());
            Assert.Equal("game-a", aReader.GetString(0));
            Assert.Equal("Recovering", aReader.GetString(1));
            Assert.Equal(epochA + 1, aReader.GetInt64(2));
        }
        Assert.False(gameProcess.HasExited, "The game host exited during the bump window.");
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
        var liveKey = "live-gm04-" + openId;
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
