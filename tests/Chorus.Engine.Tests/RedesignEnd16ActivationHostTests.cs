using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-16, the production pairing path: the match coordinator prepares a fresh round only
/// as a pre-battle entity (Preparing, Tick 0), waits for a platform gateway to confirm the
/// routing version that carries the room, and only then activates the battle. The paired round
/// leaves Preparing and its single game loop runs to a result — the activation is gated on the
/// gateway's route confirmation, not an unconditional prepare (architecture 4.4 barrier).
/// </summary>
public sealed class RedesignEnd16ActivationHostTests
{
    [Fact]
    public async Task TheCoordinatorActivatesTheBattleOnlyAfterTheGatewayConfirmsTheRoute()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        // The coordinator runs at a test pace and asks for a short round, so the fact can watch
        // the paired room leave Preparing and run its single loop to a result.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_ROUND_DURATION_TICKS"] = "40",
                ["CHORUS_ROUTE_CONFIRM_TIMEOUT_MS"] = "10000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-rel16");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rel16"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rel16" });
        }
        // The gateway runs from the start: the coordinator's gate waits for it to confirm the
        // routing version the paired room appears in (FIX-END-16).
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(RedesignEndOperationHostTests.GatewayEnvironment(
                environment.DataPort, $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300"
            },
            instanceId: "gateway-rel16");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "rel16-a", "live-rel16-a");
        var guest = await MountAsync(environment, simulator, "rel16-b", "live-rel16-b");
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel16");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-rel16");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(controlDataChannel);
        var match = new Match.MatchClient(controlDataChannel);
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        // The gate applies only when a gateway is registered (FIX-END-16 boundary); the
        // gateway must be up and self-reported before the coordinator pairs, so this fact
        // exercises the strict confirm gate rather than the no-gateway pass-through.
        var gatewayRegistered = await WaitForAsync(async () =>
            (await routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(
                instance => instance.ProcessInstanceId == "gateway-rel16"), TimeSpan.FromSeconds(30));
        Assert.True(gatewayRegistered,
            "The gateway never registered itself — the route gate would be skipped, not confirmed.");

        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId,
            SessionId = host.SessionId, PartitionKey = partition, OperationId = "rel16-enq-a"
        });
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId,
            SessionId = guest.SessionId, PartitionKey = partition, OperationId = "rel16-enq-b"
        });

        // The coordinator pairs the two mounts into a fresh room, driving the new path:
        // reserve → start → deferred prepare (Preparing) → wait for the gateway's route
        // confirmation → activate → commit. Wait for the reservation to land and name the room.
        var roomId = "";
        var pairedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < pairedDeadline && roomId.Length == 0)
        {
            var hostTicket = await queue.GetTicketAsync(new GetTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
            var guestTicket = await queue.GetTicketAsync(new GetTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId });
            if (hostTicket.Ticket.State == "Committed" && guestTicket.Ticket.State == "Committed"
                && hostTicket.Ticket.ReservationId.Length > 0
                && hostTicket.Ticket.ReservationId == guestTicket.Ticket.ReservationId)
            {
                var reservation = await match.GetReservationAsync(new GetReservationRequest
                {
                    TenantId = "tenant-a", GameId = "game-a",
                    ReservationId = hostTicket.Ticket.ReservationId
                });
                roomId = reservation.Reservation.RoomId;
            }
            await Task.Delay(200);
        }
        Assert.True(roomId.Length > 0, "The coordinator never paired the released mounts." +
            $"\nControl tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer")}");

        // The paired room reaches Running only once the gateway confirmed its route — the
        // activation is gated, not unconditional. Its single loop then runs to a result
        // (Finishing), which is what proves the battle started once and ran through.
        var reachedRunning = false;
        var reachedFinishing = false;
        var ranDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < ranDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT status FROM game.room WHERE room_id=@room", connection);
                command.Parameters.AddWithValue("room", roomId);
                var status = Convert.ToString(await command.ExecuteScalarAsync());
                if (status == "Running") reachedRunning = true;
                if (status == "Finishing") { reachedFinishing = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(reachedRunning, "The paired room never left Preparing for Running — the " +
            "activation never landed." +
            $"\nControl tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer")}" +
            $"\nGateway tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "gateway-rel16")}");
        Assert.True(reachedFinishing, "The paired round's single game loop never ran to a result." +
            $"\nControl tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer")}");
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

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }
}
