using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-17, the streamer platform bring-up and repeated start (architecture 5.1-5.3): the
/// normal start runs credential → verify → entity load → LoginReady → GameplayReady → registered
/// route → battle, an invalid launch credential logs no one in, no credential ever reaches a log
/// line, and a repeated bring-up while a live round runs is fenced from building a second room —
/// the re-mount is refused, the mount keeps its original binding and room (architecture 6.2).
/// Real-platform samples stay NOT RUN; the simulator is the only bring-up credential source.
/// </summary>
public sealed class RedesignEnd17BringUpHostTests
{
    [Fact]
    public async Task TheBringUpChainRunsFromCredentialToBattleAndRegistersTheRoute()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var (simulator, _) = await StartGatewayClusterAsync(environment, "end17-a", platform);
        var host = await MountAsync(environment, simulator, "end17-open-a", "live-key-end17-a", "mount-end17-a");
        var guest = await MountAsync(environment, simulator, "end17-open-b", "live-key-end17-b", "mount-end17-b");

        // The mount registered its route source (FIX-END-02): the snapshot carries the streamer
        // row the gateway resolves events by, at the mount's generation.
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-end17");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        var routeRegistered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            return snapshot.Streamers.Any(row => row.BindingId == host.BindingId)
                && snapshot.Streamers.Any(row => row.BindingId == guest.BindingId);
        }, TimeSpan.FromSeconds(30));
        Assert.True(routeRegistered,
            "The mounted streamers never registered their route source." +
            $"\nStreamer tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.StreamerServer")}");

        // The paired mounts walk the whole production path: reserve → start → prepare →
        // gateway confirms the route → activate → the round runs to a result (battle started).
        var roomId = await PairAndWaitRoomAsync(environment, host, guest, "end17-a", "end17-b");
        await WaitForRoomStateAsync(environment, roomId, "Finishing", "The bring-up round never finished.");

        // An invalid launch credential (forged signature) logs no one in — the fence is the
        // platform verification, so the bring-up never reaches a session.
        var forged = simulator.IssueCredential("app-1", "end17-open-a", "live-key-end17-a",
            TimeSpan.FromMinutes(10));
        forged = forged[..^4] + "AAAA"; // corrupt the HMAC tail without touching the header shape.
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-end17");
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var denied = await Assert.ThrowsAsync<RpcException>(() => session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = forged,
            LoginAttemptId = "attempt-forged",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-forged"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        // AT32's software face on the bring-up path: nothing the hosts printed carries the wire
        // secrets — the launch credential, the resume secret, or the access credential.
        var output = environment.AllHostOutput;
        Assert.DoesNotContain(host.Credential, output, StringComparison.Ordinal);
        Assert.DoesNotContain(host.AccessCredential, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(host.ResumeSecret), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepresentedBringUpIsFencedFromBuildingASecondRoom()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var (simulator, _) = await StartGatewayClusterAsync(environment, "end17-b", platform);
        var host = await MountAsync(environment, simulator, "end17-open-c", "live-key-end17-c", "mount-end17-c");
        var guest = await MountAsync(environment, simulator, "end17-open-d", "live-key-end17-d", "mount-end17-d");

        var roomId = await PairAndWaitRoomAsync(environment, host, guest, "end17-c", "end17-d");

        // The streamer re-brings-up while its round is live: the re-mount must be refused (the
        // round holds a live participation), so a fresh binding can never walk into a second room.
        var credential = simulator.IssueCredential("app-1", "end17-open-c", "live-key-end17-c",
            TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-end17c");
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var relogin = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-end17-c2",
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-end17-c2"
        });
        var refused = await Assert.ThrowsAsync<RpcException>(() => session.StartGameplayAsync(
            new StartGameplayRequest
            {
                AccessCredential = relogin.AccessCredential, GameId = "game-a",
                OperationId = "mount-end17-c2"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);

        // The recovery view still names the original mount and the live round: the streamer's
        // one room is the round it is already in, not a second one.
        var status = await session.GameplayStatusAsync(new GameplayStatusRequest
        {
            AccessCredential = relogin.AccessCredential
        });
        Assert.Equal(host.BindingId, status.BindingId);
        Assert.Equal(roomId, status.CurrentRoomId);
        Assert.True(status.RoundGeneration >= 1);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand("""
                SELECT count(*) FROM game.participation
                WHERE participant_id=@streamer AND state IN ('Active','Offline')
                """, connection);
            count.Parameters.AddWithValue("streamer", host.StreamerId);
            Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }

        // The one round still runs to its result — the refused re-mount never disturbed it.
        await WaitForRoomStateAsync(environment, roomId, "Finishing",
            "The refused re-mount broke the live round; it never finished.");
    }

    private static async Task<(DevelopmentPlatformSimulator Simulator,
        Dictionary<string, string> StreamerEnvironment)> StartGatewayClusterAsync(
        RedesignM0HostTests.HostEnvironment environment, string suffix, DevelopmentTaskPlatform platform)
    {
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
            clientTenant: "tenant-a", instanceId: "game-" + suffix);
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-" + suffix))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-" + suffix });
        }
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(RedesignEndOperationHostTests.GatewayEnvironment(
                environment.DataPort, $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300"
            },
            instanceId: "gateway-" + suffix);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        return (simulator, streamerEnvironment);
    }

    private static async Task<string> PairAndWaitRoomAsync(
        RedesignM0HostTests.HostEnvironment environment, Mounted host, Mounted guest, string opA, string opB)
    {
        using var dataCertificate = environment.Certificate("@cluster", "control-server", "control-end17");
        using var dataChannel = environment.Channel(environment.DataPort, dataCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(dataChannel);
        var match = new Match.MatchClient(dataChannel);
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId,
            SessionId = host.SessionId, PartitionKey = partition, OperationId = "enq-" + opA
        });
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId,
            SessionId = guest.SessionId, PartitionKey = partition, OperationId = "enq-" + opB
        });

        var roomId = "";
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && roomId.Length == 0)
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
        return roomId;
    }

    private static async Task WaitForRoomStateAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId, string expected, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id=@room", connection);
            command.Parameters.AddWithValue("room", roomId);
            var status = Convert.ToString(await command.ExecuteScalarAsync());
            if (status == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail(failure +
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

    private static async Task<Mounted> MountAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentPlatformSimulator simulator, string openId, string liveKey, string operationId)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        var resumeSecret = RandomNumberGenerator.GetBytes(32);
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(resumeSecret),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = operationId
        });
        return new Mounted(login.StreamerId, login.SessionId, mounted.BindingId, credential,
            login.AccessCredential, resumeSecret);
    }

    private sealed record Mounted(string StreamerId, string SessionId, string BindingId,
        string Credential, string AccessCredential, byte[] ResumeSecret);
}
