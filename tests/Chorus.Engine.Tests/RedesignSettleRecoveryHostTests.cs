using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The interrupted-settlement recovery end-to-end (FIX-SETTLE-06): the settler dies on the
/// first grant of a finished round (AT20's kill), the business executor completes the
/// manifest on its own, the coordinator releases the room, and the same mount — freed by the
/// one-transaction release — opens the next round, which receives input and ends normally.
/// The first round's rewards are never duplicated across the rolled-back try and the
/// executor's drive, and the release leaves no seat, mount pointer or ownership behind.
/// </summary>
public sealed class RedesignSettleRecoveryHostTests
{
    [Fact]
    public async Task AnInterruptedSettlementAutoRecoversAndTheSameMountOpensTheNextRound()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
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
        // The ladder drives the round to release (FIX-END-12): the coordinator walks the
        // phases and the gateway executes the platform end task. The business executor runs
        // on a test cadence — it is the sole recovery for the settler's death, and the
        // data plane's settler dies on the first grant once, so the executor's own pass is
        // never shot down.
        using var platform = DevelopmentTaskPlatform.Start(
            RedesignM0HostTests.HostEnvironment.FreePort);
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "200",
                ["CHORUS_SETTLE_EXECUTOR_INTERVAL_MS"] = "1500"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server",
            new Dictionary<string, string> { ["CHORUS_SETTLE_CRASH_AFTER_GRANT"] = "1" });
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{environment.GamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a");
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(RedesignEndOperationHostTests.GatewayEnvironment(
                environment.DataPort, $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300"
            });

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-s6-a", "live-s6-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-s6-b", "live-s6-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-s6");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var routing = new RoundInput.RoundInputClient(gameChannel);
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var ingest = new PlatformIngest.PlatformIngestClient(gatewayChannel);
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);

        // Round 1: a finished round whose settler dies on the first grant.
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-s6-1",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-s6-1", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-s6-1",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-s6-1", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-s6-1", DurationTicks = 40, RandomSeed = 6262, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        await IngestAndRouteAsync(ingest, routing, "batch-s6-1", "route-s6-1",
            ("gift-s6-1a", "live-s6-a", 100), ("gift-s6-1b", "live-s6-b", 30));

        // The settler dies on the first grant — the rolled-back try leaves nothing applied.
        var died = false;
        var diedDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < diedDeadline)
        {
            var console = HostConsole(environment, "Chorus.DataServer");
            if (console.Contains("The settler died after grant")) { died = true; break; }
            await Task.Delay(150);
        }
        Assert.True(died, "The injected settler death never fired.");

        // The business executor alone recovers the manifest: both grants land, one ledger
        // line each, and the settle queue empties.
        var completed = false;
        var completedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < completedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var grants = new NpgsqlCommand(
                    "SELECT count(*) FROM settlement.grant WHERE room_id='room-s6-1' AND status='Granted'",
                    connection);
                if (Convert.ToInt64(await grants.ExecuteScalarAsync()) == 2) { completed = true; break; }
            }
            await Task.Delay(200);
        }
        Assert.True(completed, "The business executor never completed the rewards. Control tail:\n" +
            RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer"));

        // The coordinator releases the round: the ladder's platform close landed and the
        // finalize fired under the end operation's own id — Settled, participations closed,
        // mounts freed, ownership withdrawn at the bumped epoch.
        var released = false;
        var releaseDeadline = DateTime.UtcNow.AddSeconds(240);
        while (DateTime.UtcNow < releaseDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var state = new NpgsqlCommand("""
                    SELECT (SELECT phase FROM control.end_operation WHERE room_id='room-s6-1'),
                           (SELECT status FROM game.room WHERE room_id='room-s6-1'),
                           (SELECT status||':'||epoch FROM control.ownership
                              WHERE kind='room' AND entity_id='room-s6-1')
                    """, connection);
                if (await state.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2)
                    && reader.GetString(0) is "Released" or "Destroyed" or "Done"
                    && reader.GetString(1) == "Settled"
                    && reader.GetString(2) == $"Released:{owner.Epoch + 1}")
                { released = true; break; }
            }
            await Task.Delay(200);
        }
        Assert.True(released, "The coordinator never released the settled round. Control tail:\n" +
            RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer") +
            "\nData tail:\n" + RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.DataServer"));

        // The first round's rewards are one effect per grant — the rolled-back try and the
        // executor's drive are two ledger lines, not four. The release freed the seats, the
        // mount pointers and the ownership with nothing left holding the round.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var ledger = new NpgsqlCommand("""
                SELECT count(DISTINCT operation_id),count(*) FROM player.ledger
                WHERE reason LIKE 'settlement:room-s6-1/%'
                """, connection))
            {
                await using var reader = await ledger.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
            await using (var participation = new NpgsqlCommand("""
                SELECT count(*) FROM game.participation WHERE room_id='room-s6-1' AND state<>'Closed'
                """, connection))
            {
                Assert.Equal(0L, Convert.ToInt64(await participation.ExecuteScalarAsync()));
            }
            await using (var task = new NpgsqlCommand(
                "SELECT count(*) FROM control.settle_task WHERE room_id='room-s6-1'", connection))
            {
                Assert.Equal(0L, Convert.ToInt64(await task.ExecuteScalarAsync()));
            }
            await using (var outbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-s6-1/0/settle'", connection))
            {
                Assert.Equal("round.settled", await outbox.ExecuteScalarAsync());
            }
            await using (var binding = new NpgsqlCommand(
                "SELECT current_room_id,round_generation FROM live.binding WHERE binding_id=@binding",
                connection))
            {
                binding.Parameters.AddWithValue("binding", hostBinding);
                await using var reader = await binding.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0) || reader.GetString(0).Length == 0,
                    "The mount pointer still names the released round.");
                Assert.Equal(1L, reader.GetInt64(1));
            }
        }

        // Round 2: the same mount opens the next round, receives input, runs and ends — the
        // second round settles normally because the settler's death was once-only.
        var nextOwner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-s6-2",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-s6-2", OwnerInstanceId = "game-1", Epoch = nextOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-s6-2",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-s6-2", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = nextOwner.Epoch,
            OperationId = "run-s6-2", DurationTicks = 40, RandomSeed = 7373, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        await IngestAndRouteAsync(ingest, routing, "batch-s6-2", "route-s6-2",
            ("gift-s6-2a", "live-s6-a", 50), ("gift-s6-2b", "live-s6-b", 20));

        var round2 = false;
        var round2Deadline = DateTime.UtcNow.AddSeconds(240);
        while (DateTime.UtcNow < round2Deadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var state = new NpgsqlCommand("""
                    SELECT (SELECT status FROM game.room WHERE room_id='room-s6-2'),
                           (SELECT count(*) FROM settlement.grant WHERE room_id='room-s6-2' AND status='Granted'),
                           (SELECT round_generation FROM live.binding WHERE binding_id=@binding)
                    """, connection);
                state.Parameters.AddWithValue("binding", hostBinding);
                if (await state.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && reader.GetString(0) == "Settled"
                    && !reader.IsDBNull(1) && reader.GetInt64(1) == 2
                    && !reader.IsDBNull(2) && reader.GetInt64(2) == 2)
                { round2 = true; break; }
            }
            await Task.Delay(200);
        }
        Assert.True(round2, "The second round never received input and ended. Control tail:\n" +
            RedesignEndOperationHostTests.HostTailFor(environment, "Chorus.ControlServer"));

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand("""
                SELECT count(DISTINCT operation_id),count(*) FROM player.ledger
                WHERE reason LIKE 'settlement:room-s6-2/%'
                """, connection);
            await using var reader = await ledger.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
        }
    }

    private static async Task IngestAndRouteAsync(PlatformIngest.PlatformIngestClient ingest,
        RoundInput.RoundInputClient routing, string batchId, string routeId,
        params (string DedupKey, string CanonicalKey, int Amount)[] events)
    {
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = batchId };
        foreach (var (dedup, canonical, amount) in events)
            batch.Events.Add(new IngestEvent
            {
                SourceDedupKey = dedup, Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
                CanonicalRoomKey = canonical, PayloadJson = $$"""{"kind":"gift","amount":{{amount}}}"""
            });
        await ingest.IngestBatchAsync(batch);
        await routing.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = routeId, MaxEvents = 50
        });
    }

    private static string HostConsole(RedesignM0HostTests.HostEnvironment environment, string instance) =>
        environment.HostOutput(environment.HostProcesses.Last(pair => pair.Instance == instance).Process);

    private static async Task<(LoginReadyReply Login, string BindingId)> MountAsync(
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
        return (login, mounted.BindingId);
    }
}
