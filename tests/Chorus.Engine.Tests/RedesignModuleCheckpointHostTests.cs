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
/// FIX-MODULE-03 end to end: a room under the live package (FullCheckpoint, honestly
/// non-deterministic — clock jitter in every boost) checkpoints every committed frame,
/// and when its owner dies for real the takeover recovers it from the head checkpoint
/// alone: zero replayed frames, a head that only keeps growing across the kill, and a
/// finished round whose live-scored state survived the interruption. The data plane
/// holds the evidence — every frame row of the room joins a snapshot row on both sides
/// of the kill, which is exactly what a replay-only room cannot offer.
/// </summary>
public sealed class RedesignModuleCheckpointHostTests
{
    [Fact]
    public async Task ALiveRoomSurvivesItsOwnersKillByCheckpointNotReplay()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The deadline sweep stays out of the way: this test drives the takeover itself,
        // so the killed owner's lease — not a timer — hands the room over.
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

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-ckpt");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-ckpt");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "ckpt");
        const string roomId = "room-ckpt-live";
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (epoch, hostSession) = await OpenLiveRoundAsync(environment, control, rounds, simulator,
            roomId, gamePortA, controlCertificate);

        // One boost while the room runs: the live module scores it 30 plus a clock
        // jitter — a live fact only the checkpoint can carry across the kill.
        using (var inputCertificate = environment.Certificate("tenant-a", "streamer-gateway", "ckpt-input"))
        using (var inputChannel = environment.Channel(environment.DataPort, inputCertificate, "data-server"))
        {
            await new RoundInput.RoundInputClient(inputChannel).AcceptClientInputAsync(
                new ClientInputRequest
                {
                    SessionId = hostSession, RoomId = roomId, RoomEpoch = epoch, GameId = "game-a",
                    CommandId = "cmd-ckpt-boost", PayloadJson = """{"action":"boost","level":3}"""
                }, deadline: DateTime.UtcNow.AddSeconds(30));
        }

        // The every-tick checkpoint is already a data-plane fact before any recovery:
        // every frame committed so far joins a snapshot row.
        var headTick = await WaitForHeadAsync(environment, roomId, 30);
        Assert.Equal(0, await FramesWithoutCheckpointAsync(environment, roomId));

        // The real interruption: the owner dies mid-run. Its lease is the only thing
        // standing between the room and the next owner, and renewal died with it.
        environment.Kill("game-a");
        var leaseExpired = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId
            });
            return ownership.Found
                && ownership.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }, TimeSpan.FromSeconds(40));
        Assert.True(leaseExpired, "The killed owner's lease never expired.");

        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 15, OperationId = "ckpt-recovery"
        });
        Assert.Equal(epoch + 1, transferred.Epoch);
        using var hostChannelB = environment.Channel(gamePortB, controlCertificate, "game-server");
        var gameHostB = new GameHost.GameHostClient(hostChannelB);
        var recovered = await gameHostB.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-b",
            Epoch = transferred.Epoch, TickMs = 25
        }, deadline: DateTime.UtcNow.AddSeconds(30));

        // The no-replay proof: a non-deterministic room recovered at its committed head.
        Assert.True(recovered.Recovered, recovered.Note);
        Assert.True(recovered.LastTick >= headTick, "The recovery lost the killed owner's head.");
        Assert.Equal(recovered.LastTick, recovered.SnapshotTick);
        Assert.Equal(0, recovered.ReplayedFrames);
        Assert.Contains("without replay", recovered.Note, StringComparison.Ordinal);

        // The recovered room runs to its natural result on the new owner, and its
        // live-scored state crossed the kill: red holds the boost plus one jitter.
        string? resultJson = null;
        using var syncCertificate = environment.Certificate("tenant-a", "streamer-server", "sync-ckpt");
        using var syncChannel = environment.Channel(gamePortB, syncCertificate, "game-server");
        using var subscriber = new RoundSync.RoundSyncClient(syncChannel).Subscribe(new SubscribeRequest
        {
            RoomId = roomId, GameId = "game-a", Epoch = transferred.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 1
        }, deadline: DateTime.UtcNow.AddMinutes(4));
        var reading = Task.Run(async () =>
        {
            await foreach (var message in subscriber.ResponseStream.ReadAllAsync())
                if (message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                    resultJson = message.RoundResult.ResultJson;
        });
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The recovered live room did not finish on its new owner.");
        await reading.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(resultJson);
        using var result = JsonDocument.Parse(resultJson!);
        var red = ScoreOf(result, "red");
        Assert.InRange(red, 30, 32); // 10 × level 3 plus one clock jitter of 0..2
        Assert.Equal(0L, ScoreOf(result, "blue"));
        Assert.Equal("red", WinnerOf(result));

        // Both tenures checkpointed every committed frame: no frame row of this room
        // exists without its snapshot twin, including the frames after the recovery.
        Assert.Equal(0, await FramesWithoutCheckpointAsync(environment, roomId));
    }

    /// <summary>Mounts the streamer pair, claims the room for game-a and starts the round
    /// under the live package's identity; returns the claimed epoch and the host's
    /// session for client input.</summary>
    private static async Task<(long Epoch, string HostSession)> OpenLiveRoundAsync(
        RedesignM0HostTests.HostEnvironment environment,
        ControlService.ControlServiceClient control, RoundStore.RoundStoreClient rounds,
        DevelopmentPlatformSimulator simulator, string roomId, int gamePortA,
        System.Security.Cryptography.X509Certificates.X509Certificate2 controlCertificate)
    {
        var host = await MountAsync(environment, simulator, "ckpt-host");
        var guest = await MountAsync(environment, simulator, "ckpt-guest");
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-a", LeaseSeconds = 30
        });
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-a", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "0.9.0", ConfigHash = "cfg-live-1", PackageHash = "pkg-live-1",
            OperationId = "ckpt-start", TenantId = "tenant-a",
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
        using var hostChannel = environment.Channel(gamePortA, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = claimed.Epoch,
            OperationId = "ckpt-prepare", DurationTicks = 300, RandomSeed = 777, TickMs = 25,
            PackageHash = "pkg-live-1",
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

    /// <summary>Counts the room's committed frames that carry no snapshot row — zero is
    /// the every-tick checkpoint invariant, on either side of a kill.</summary>
    private static async Task<long> FramesWithoutCheckpointAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM game.frame f LEFT JOIN game.snapshot s "
            + "ON s.room_id=f.room_id AND s.tick_id=f.tick_id "
            + "WHERE f.room_id=@room AND s.tick_id IS NULL", connection);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-ckpt-" + openId;
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
