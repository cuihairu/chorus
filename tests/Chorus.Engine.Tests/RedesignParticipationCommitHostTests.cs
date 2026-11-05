using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-04 host wiring: the control plane's participation-commit dispatcher delivers a
/// real round.started commit (the transaction that committed the participation set, the mounts'
/// round pointers and the Preparing room) to the StreamerServer hosting each participant, and
/// retires the outbox event only once delivered. An event whose participants all turned moot
/// (a round released the mounts before the dispatch) is retired without a push rather than
/// retried forever, and an unreachable owner instance keeps the event pending until the
/// endpoint resolves, then delivers — the idempotent receiver makes the repeats converge.
/// </summary>
public sealed class RedesignParticipationCommitHostTests
{
    [Fact]
    public async Task TheDispatcherDeliversACommittedRoundToTheStreamerServer()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        // The StreamerServer self-reports to the control plane (ownership + endpoint) only when
        // it knows the control endpoint — the dispatcher's target resolution needs both, so this
        // host wires it where the plain End16 host does not.
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
                ["CHORUS_ROUTE_CONFIRM_TIMEOUT_MS"] = "10000",
                ["CHORUS_PARTICIPATION_DISPATCH_INTERVAL_MS"] = "300"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", StreamerEnvironment(environment, simulatorKey, accessKey));
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-rel04");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rel04"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rel04" });
        }
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(RedesignEndOperationHostTests.GatewayEnvironment(
                environment.DataPort, $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300"
            },
            instanceId: "gateway-rel04");
        // The gate applies only when a gateway is registered; wait for it so the pair goes
        // through the strict confirm gate, as in the End16 host test.
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel04");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        Assert.True(await WaitForAsync(async () =>
            (await routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(
                instance => instance.ProcessInstanceId == "gateway-rel04"), TimeSpan.FromSeconds(30)),
            "The gateway never registered itself — the route gate would be skipped, not confirmed.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "rel04-a", "live-rel04-a");
        var guest = await MountAsync(environment, simulator, "rel04-b", "live-rel04-b");
        var roomId = await PairAndGetRoomAsync(environment, host, guest);

        // The dispatcher resolves the participants to this StreamerServer, pushes the commit
        // notice, and retires the event. This is the whole arrow: CS -> SS participation commit.
        var delivered = await WaitForAsync(async () =>
            await OutboxStatusAsync(environment, $"{roomId}/0/start") == "Done",
            TimeSpan.FromSeconds(30));
        Assert.True(delivered, "The dispatcher never delivered the round.started commit." +
            $"\nControl tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer")}");
    }

    [Fact]
    public async Task AnEventWhoseParticipantsTurnedMootIsRetiredWithoutDelivery()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_PARTICIPATION_DISPATCH_INTERVAL_MS"] = "300"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        // A round committed two mounts, then the terminal path released them (the round pointer
        // cleared) before the dispatcher's pass. The commit event still names them — but the
        // mounts no longer describe this round, so the dispatch must retire the event without a
        // push instead of retrying forever or delivering stale evidence. The state is staged
        // before the event exists, so the dispatcher only ever sees the moot shape.
        await InsertMootCommitAsync(environment, "moot-1");
        await InsertRoundStartedEventAsync(environment, "moot-1",
            [("binding-moot-1", 1L)]);

        var retired = await WaitForAsync(async () =>
            await OutboxStatusAsync(environment, "moot-1/0/start") == "Done",
            TimeSpan.FromSeconds(30));
        Assert.True(retired, "A moot commit was never retired — the dispatcher treats it as " +
            "unresolved and retries forever. Its participants were released before the dispatch." +
            $"\nControl tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer")}");
    }

    [Fact]
    public async Task AnUnreachableOwnerKeepsTheEventPendingThenDeliversOnceReachable()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        // The StreamerServer self-reports a bogus serving endpoint (nothing listens there) and
        // reports it once, so the test can later fix the process report — the dispatcher must
        // hold the event pending across passes, then deliver once the endpoint resolves.
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
                ["CHORUS_ROUTE_CONFIRM_TIMEOUT_MS"] = "10000",
                ["CHORUS_PARTICIPATION_DISPATCH_INTERVAL_MS"] = "300"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", StreamerEnvironment(environment, simulatorKey, accessKey,
                reportEndpoint: "https://localhost:1", reportIntervalMs: "60000"));
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-rel04b");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rel04b"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rel04b" });
        }
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(RedesignEndOperationHostTests.GatewayEnvironment(
                environment.DataPort, $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300"
            },
            instanceId: "gateway-rel04b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel04b");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        Assert.True(await WaitForAsync(async () =>
            (await routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(
                instance => instance.ProcessInstanceId == "gateway-rel04b"), TimeSpan.FromSeconds(30)),
            "The gateway never registered itself — the route gate would be skipped, not confirmed.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "rel04c-a", "live-rel04c-a");
        var guest = await MountAsync(environment, simulator, "rel04c-b", "live-rel04c-b");
        var roomId = await PairAndGetRoomAsync(environment, host, guest);
        var effectId = $"{roomId}/0/start";

        // The owner instance is unreachable: the event must never retire Done across passes —
        // it stays pending (or claimed-but-in-flight during a push attempt) until the instance
        // is reachable. A delivered event would be Done, which the retry discipline forbids.
        var deliveredEarly = false;
        for (var sample = 0; sample < 5 && !deliveredEarly; sample++)
        {
            await Task.Delay(400);
            deliveredEarly = await OutboxStatusAsync(environment, effectId) == "Done";
        }
        Assert.False(deliveredEarly, "An unreachable owner was delivered to — the event retired " +
            "while its endpoint was unreachable, instead of retrying.");

        // The instance becomes reachable (its process report is corrected): the next pass
        // resolves the endpoint and delivers, retiring the event.
        await FixStreamerEndpointAsync(environment, $"https://localhost:{environment.StreamerPort}");
        var delivered = await WaitForAsync(async () =>
            await OutboxStatusAsync(environment, effectId) == "Done", TimeSpan.FromSeconds(30));
        Assert.True(delivered, "The event never delivered after its endpoint resolved." +
            $"\nControl tail:\n{RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer")}");
    }

    private static Dictionary<string, string> StreamerEnvironment(
        RedesignM0HostTests.HostEnvironment environment, byte[] simulatorKey, byte[] accessKey,
        string? reportEndpoint = null, string? reportIntervalMs = null)
    {
        var env = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        if (reportEndpoint is not null) env["CHORUS_REPORT_ENDPOINT"] = reportEndpoint;
        if (reportIntervalMs is not null) env["CHORUS_REPORT_INTERVAL_MS"] = reportIntervalMs;
        return env;
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

    private static async Task<string> PairAndGetRoomAsync(
        RedesignM0HostTests.HostEnvironment environment,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest)
    {
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel04x");
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(controlDataChannel);
        var match = new Match.MatchClient(controlDataChannel);
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId,
            SessionId = host.SessionId, PartitionKey = partition, OperationId = "rel04-enq-a"
        });
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId,
            SessionId = guest.SessionId, PartitionKey = partition, OperationId = "rel04-enq-b"
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

    private static async Task<string?> OutboxStatusAsync(
        RedesignM0HostTests.HostEnvironment environment, string effectId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM infra.outbox WHERE effect_id=@effect", connection);
        command.Parameters.AddWithValue("effect", effectId);
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    /// <summary>Stages a room with two active participations whose mounts' round pointers were
    /// released (null): the moot shape the terminal path leaves behind before a dispatch.</summary>
    private static async Task InsertMootCommitAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES ('s-moot-1','tenant-a');
            INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                platform_room_key,started_at)
                VALUES ('session-moot-1','tenant-a','s-moot-1','simulator','app-1','key-moot-1',clock_timestamp());
            INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,
                state,current_room_id,round_generation)
                VALUES ('binding-moot-1','session-moot-1','tenant-a','game-a',1,'Ready',NULL,1);
            INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash)
                VALUES (@room,'tenant-a','game-a','Preparing',1,'pkg','mod','cfg');
            INSERT INTO game.participation(room_id,participant_id,binding_id,role,state)
                VALUES (@room,'s-moot-1','binding-moot-1','host','Active');
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertRoundStartedEventAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId, (string binding, long generation)[] participants)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@effect,'round.started',@payload::jsonb,'Pending')
            """, connection);
        command.Parameters.AddWithValue("effect", $"{roomId}/0/start");
        command.Parameters.AddWithValue("payload", System.Text.Json.JsonSerializer.Serialize(new
        {
            RoomId = roomId, TenantId = "tenant-a", GameId = "game-a", Epoch = 1L,
            Participants = participants.Select(p => new { BindingId = p.binding, Generation = p.generation })
        }));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task FixStreamerEndpointAsync(
        RedesignM0HostTests.HostEnvironment environment, string endpoint)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE control.process SET endpoint=@endpoint
            WHERE endpoint='https://localhost:1'
            """, connection);
        command.Parameters.AddWithValue("endpoint", endpoint);
        await command.ExecuteNonQueryAsync();
    }
}
