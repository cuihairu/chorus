using System.Globalization;
using System.Runtime.InteropServices;
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
/// The M3-7 entity takeover mechanics (architecture 10.3, AT25 single-host form): the control
/// plane moves a live room's authority to a second instance once the current holder goes
/// silent, the handover fences the old epoch, and the displaced holder parks when it wakes.
/// The same-host SIGSTOP freeze is a process-level partition approximation — the holder is
/// alive but deaf — so the full cross-host AT25 form stays open in the ledger.
/// </summary>
public sealed class RedesignM3TakeoverHostTests
{
    private const int SigStop = 19;
    private const int SigCont = 18;

    [Fact]
    public async Task AFrozenOwnerIsTakenOverAndFencedAtTheOldEpoch()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The control plane seeds both hosts in its channel directory and runs the ownership
        // registrar short (architecture 4.2): a live holder keeps its lease, a silent one loses
        // it within seconds so the takeover is observable in test time.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
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

        // Two game hosts self-register: A holds the room, B is the takeover target.
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#2"),
            clientTenant: "tenant-a", instanceId: "game-b");
        var hostA = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;
        var hostB = environment.HostProcesses.Single(entry => entry.Instance == "game-b").Process;
        Assert.False(hostA.HasExited);
        Assert.False(hostB.HasExited);

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-7");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        // Round reads carry the caller's certificate tenant, so the room speaks through a
        // tenant-scoped identity while the control plane keeps its cluster channel.
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-at25");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        // The node exists because an agent registered it; the game hosts then report themselves
        // into that node (architecture 4.4) — the certificate must be the claimed instance.
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-7"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-7" });
        }

        // The registrar only renews owners it can observe, so the takeover waits until both
        // hosts are registered and A is demonstrably live before the room claims its lease.
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-b"
                    && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");

        // The victim room's participants are real mounted streamers — the store refuses a
        // round whose bindings do not exist.
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "open-at25-a");
        var guest = await MountAsync(environment, simulator, "open-at25-b");

        const string roomId = "room-at25";
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-a", LeaseSeconds = 10
        });
        Assert.True(claimed.Found);
        Assert.Equal("game-a", claimed.OwnerInstanceId);
        Assert.Equal("Active", claimed.Status);
        var epoch = claimed.Epoch;

        // The manual coordinator drives the room on A to a committed head past the snapshot.
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-a", Epoch = epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "at25-start", TenantId = "tenant-a",
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
                OperationId = "at25-prepare", DurationTicks = 200, RandomSeed = 777, TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                    new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
                }
            });
        Assert.Equal("Running", prepared.Status);

        // The victim room advances to a committed head past the tick-100 snapshot. The floor
        // needs the head beyond the anchor with margin; under full-suite load the tick pace
        // sags well below the ideal 40/s, so the wait carries the patience, not the floor.
        long headTick = 0;
        var headDeadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < headDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, headTick) = await FrameStatsAsync(connection, roomId);
            }
            if (headTick >= 130) break;
            await Task.Delay(100);
        }
        Assert.InRange(headTick, 110, 199);

        // A goes silent: SIGSTOP keeps the process alive but deaf (process-level partition
        // approximation). Its self-reports stop, so the registrar gate closes and the lease
        // runs out — the entity becomes takeable.
        _ = kill(hostA.Id, SigStop);
        var leaseExpired = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId
            });
            return ownership.Found && ownership.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }, TimeSpan.FromSeconds(40));
        Assert.True(leaseExpired, "The frozen holder's lease never expired.");

        // The data plane still serves the room while its owner is deaf.
        var duringOutage = await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" });
        Assert.True(duringOutage.Found);
        Assert.Equal("Running", duringOutage.Status);

        // The control plane hands the room to B: both epochs bump, the room parks Recovering.
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 15
        });
        Assert.Equal("game-b", transferred.OwnerInstanceId);
        Assert.Equal("Recovering", transferred.Status);
        Assert.Equal(epoch + 1, transferred.Epoch);

        // B confirms and recovers from the head at the bumped epoch and resumes ticking.
        var recovered = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
            ToInstanceId = "game-b", LeaseSeconds = 15, TickMs = 25
        });
        Assert.True(recovered.Recovered);
        Assert.Equal("game-b", recovered.OwnerInstanceId);
        Assert.Equal(epoch + 1, recovered.Epoch);
        Assert.Equal("Running", recovered.Status);
        Assert.InRange(recovered.LastTick, headTick, 199);
        // The recoverer replays the frames after the last snapshot and deposits a fresh
        // snapshot at the head it pinned.
        Assert.Equal(recovered.LastTick, await SnapshotTickAsync(environment, roomId));

        // The routing snapshot follows the handover: B holds the room at the bumped epoch.
        var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
        var route = Assert.Single(snapshot.Rooms, room => room.RoomId == roomId);
        Assert.Equal("game-b", route.OwnerInstanceId);
        Assert.Equal(epoch + 1, route.Epoch);

        // A stale write from the displaced owner is fenced: AT25 mechanics, one host form.
        using var probeCertificate = environment.Certificate("tenant-a", "game-server", "probe-m3-7");
        using var probeChannel = environment.Channel(environment.DataPort, probeCertificate, "data-server");
        var frames = new FrameStore.FrameStoreClient(probeChannel);
        var stale = await Assert.ThrowsAsync<RpcException>(async () => await frames.CommitFrameAsync(
            new CommitFrameRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epoch,
                CommitId = "stale-probe", PrevTick = headTick, PrevStateVersion = headTick,
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
            }));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);
        Assert.Contains("The room ownership fence rejected the caller.", stale.Status.Detail);

        // A wakes: its next frame commit is fenced at the old epoch and the actor parks
        // visibly instead of guessing forward — the evidence line must show the rejection.
        _ = kill(hostA.Id, SigCont);
        var parked = await WaitForAsync(() => Task.FromResult(
            environment.HostOutput(hostA).Contains("frame commit rejected", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20));
        Assert.True(parked, "The displaced owner never parked at the fenced epoch.");

        // B carries the room to its end on its own.
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(30));
        Assert.True(finished, "The takeover host did not finish the room.");
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

    private static async Task<long> SnapshotTickAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(max(tick_id),0) FROM game.snapshot WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-at25-" + openId;
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

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
