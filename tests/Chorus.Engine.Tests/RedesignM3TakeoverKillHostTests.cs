using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Redesign.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-TAKE-08: the takeover sequence interrupted by a real process kill at each of its
/// boundaries. After a transfer: the killed target's deadline — not its liveness — hands
/// the room to the sweep's reassignment and the replacement converges. During recovery:
/// the killed recoverer's committed head survives and the next takeover resumes from it.
/// After a lost confirm reply: the replay is idempotent, and the still-open operation
/// converges by the ownership row — the sweep records it Activated instead of stealing a
/// healthy holder. No injection point produces a fake Ready, a duplicate epoch bump or a
/// permanently Recovering room (AT25/28, single-machine).
/// </summary>
public sealed class RedesignM3TakeoverKillHostTests
{
    [Fact]
    public async Task AKillRightAfterTheTransferConvergesThroughReassignment()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The recovery-deadline sweep is the actor under test: it must decide within
        // seconds of the one-second deadline passing.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "1000",
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

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-kill8a");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-kill8a");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "kill8a");
        const string roomId = "room-kill8a";
        var epoch = await PrepareRoomOnHostAAsync(environment, control, rounds, gamePortA,
            controlCertificate, simulatorKey, roomId, "kill8a");
        var headTick = await WaitForHeadAsync(environment, roomId, 2);

        // The transfer commits; the target dies before it touches the room. The one-second
        // deadline is the whole budget and the sweep — not the target's liveness — decides.
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 1, OperationId = "kill8a-recovery"
        });
        Assert.Equal(epoch + 1, transferred.Epoch);
        environment.Kill("game-b");

        // No fake Ready: the room stays behind its Recovering fence until a replacement
        // actually recovers it. The sweep closes the killed target's operation Failed and
        // reassigns to the other host under a newer epoch.
        var reassigned = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,epoch,status FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync();
            return await reader.ReadAsync() && reader.GetString(0) == "game-a"
                && reader.GetInt64(1) == epoch + 2 && reader.GetString(2) == "Recovering";
        }, TimeSpan.FromSeconds(30));
        Assert.True(reassigned, "The sweep never reassigned the killed target's recovery.");

        // The replacement converges from the same persistent operation; exactly one bump
        // per step, and each operation records its own outcome.
        var recovered = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-a", LeaseSeconds = 15, TickMs = 25
        });
        Assert.True(recovered.Recovered);
        Assert.Equal(epoch + 2, recovered.Epoch);
        Assert.True(recovered.LastTick >= headTick);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operations = new NpgsqlCommand(
                "SELECT operation_id,phase,to_instance FROM control.recovery_operation WHERE room_id=@room ORDER BY created_at",
                connection);
            operations.Parameters.AddWithValue("room", roomId);
            await using var reader = await operations.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("kill8a-recovery", "Failed", "game-b"),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("reassign/room-kill8a/kill8a-recovery", "Activated", "game-a"),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.False(await reader.ReadAsync(), "The kill must not churn operations.");
        }
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The reassigned host did not finish the room.");
    }

    [Fact]
    public async Task AKillDuringRecoveryConvergesFromTheVictimHead()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The deadline sweep stays out of the way: this test drives both takeovers itself,
        // so the operations' own leases — not a timer — carry the sequence.
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

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-kill8b");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-kill8b");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "kill8b");
        const string roomId = "room-kill8b";
        var epoch = await PrepareRoomOnHostAAsync(environment, control, rounds, gamePortA,
            controlCertificate, simulatorKey, roomId, "kill8b");
        var firstHead = await WaitForHeadAsync(environment, roomId, 10);

        // The room moves to B and keeps committing past the head it recovered from.
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 6, OperationId = "kill8b-recovery"
        });
        Assert.Equal(epoch + 1, transferred.Epoch);
        var recoveredOnB = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 6, TickMs = 25
        });
        Assert.True(recoveredOnB.Recovered);
        var victimHead = await WaitForHeadAsync(environment, roomId, firstHead + 10);

        // The recoverer dies mid-run. Its lease runs out — the renewal does not extend a
        // dead holder — and the room moves again exactly through a new named operation;
        // the dead epoch is never re-enabled.
        environment.Kill("game-b");
        var leaseExpired = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId
            });
            return ownership.Found && ownership.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }, TimeSpan.FromSeconds(40));
        Assert.True(leaseExpired, "The killed recoverer's lease never expired.");

        var transferredBack = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-a", LeaseSeconds = 60, OperationId = "kill8b-recovery-2"
        });
        Assert.Equal(epoch + 2, transferredBack.Epoch);
        var recoveredOnA = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-a", LeaseSeconds = 15, TickMs = 25
        });
        Assert.True(recoveredOnA.Recovered);
        Assert.Equal(epoch + 2, recoveredOnA.Epoch);
        Assert.True(recoveredOnA.LastTick >= victimHead,
            "The second recovery lost the killed recoverer's committed head.");

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operations = new NpgsqlCommand(
                "SELECT operation_id,phase,to_instance,target_epoch FROM control.recovery_operation WHERE room_id=@room ORDER BY created_at",
                connection);
            operations.Parameters.AddWithValue("room", roomId);
            await using var reader = await operations.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("kill8b-recovery", "Activated", "game-b", epoch + 1),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("kill8b-recovery-2", "Activated", "game-a", epoch + 2),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            Assert.False(await reader.ReadAsync(), "The kill must not churn operations.");
        }
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The second recoverer did not finish the room.");
    }

    [Fact]
    public async Task ALostConfirmReplyReplaysAndTheOperationCatchesUpByTheOwnership()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // A fast sweep is the actor under test: the still-open operation must be recorded
        // by reading the ownership, long before any later deadline heuristic could misfire.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "250",
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

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-kill8c");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-kill8c");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "kill8c");
        const string roomId = "room-kill8c";
        var epoch = await PrepareRoomOnHostAAsync(environment, control, rounds, gamePortA,
            controlCertificate, simulatorKey, roomId, "kill8c");
        await WaitForHeadAsync(environment, roomId, 2);

        // The transfer commits with a two-second deadline; the target recovers and its
        // fenced confirm lands (the ownership flips Active), but the operator's bookkeeping
        // never runs — this test stands in for a control plane that died mid-call.
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 2, OperationId = "kill8c-recovery"
        });
        Assert.Equal(epoch + 1, transferred.Epoch);
        using var hostChannelB = environment.Channel(gamePortB, controlCertificate, "game-server");
        var gameHostB = new GameHost.GameHostClient(hostChannelB);
        RecoverRoomReply RecoverOnB() => gameHostB.RecoverRoom(new RecoverRoomRequest
        {
            RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-b",
            Epoch = transferred.Epoch, TickMs = 25
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        var first = RecoverOnB();
        Assert.True(first.Recovered);
        // The lost reply replays idempotently: the already-running actor answers again
        // without a second activation or a second epoch bump.
        var replay = RecoverOnB();
        Assert.True(replay.Recovered);

        // The operation row is still open — nobody recorded the outcome. The sweep reads
        // the ownership (the authority), sees the room already Active under exactly this
        // operation's target and epoch, and records it Activated instead of reassigning a
        // healthy holder away.
        var caughtUp = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand(
                "SELECT phase FROM control.recovery_operation WHERE operation_id='kill8c-recovery'", connection);
            return Convert.ToString(await operation.ExecuteScalarAsync()) == "Activated";
        }, TimeSpan.FromSeconds(30));
        Assert.True(caughtUp, "The sweep never recorded the already-converged recovery.");

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM control.recovery_operation WHERE room_id=@room", connection);
            count.Parameters.AddWithValue("room", roomId);
            Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
            await using var ownership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,epoch,status FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("game-b", epoch + 1, "Active"),
                (reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The recovered room did not finish on its holder.");
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
        var liveKey = "live-kill8-" + openId;
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
