using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-01/04 over real hosts: the end operation is a control-plane row, so the process
/// that recorded it can die between any two steps. The game host's begin lands on the
/// ControlServer, replays as the same operation after a lost reply, refuses a second
/// business id for the same room, and a control-plane restart reads the same row — same
/// id, phase and mount generations — and keeps answering under it. A begin arriving after
/// the mounts left the room writes nothing and leaves the recorded operation untouched.
/// Over a full cluster the frozen result is reported once from its own receipt (or, a host
/// killed after the final commit, begun by the discovery net from that receipt); the battle
/// ticks stop at the final frame, a replayed begin re-awards nothing, and a closing input
/// arriving after the freeze cannot change the verdict. Once the operation stands, the
/// receiver-side input fence (FIX-END-06) refuses a gateway's late pushes through the whole
/// chain — whatever the room's status column says — while the settlement's own writes keep
/// their authority.
/// </summary>
public sealed class RedesignEndOperationHostTests
{
    [Fact]
    public async Task ARestartedControlPlaneContinuesTheSameEndOperation()
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
        // The coordinator stays lazy: this is the begin's own contract (one operation, replay,
        // restart continuity) — the phase ladder's driver is END-05's subject, not this one.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string> { ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "600000" });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        // A mounted binding and a started round — the mounts the end operation's fence reads.
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "end-op-host-a", "live-key-end-op-a");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-end-op");
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-end-op");
        using (var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server"))
        using (var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server"))
        {
            var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
            var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);
            var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-end-op-1",
                OwnerInstanceId = "gs-end-op", LeaseSeconds = 30
            });
            var started = await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
            {
                RoomId = "room-end-op-1", OwnerInstanceId = "gs-end-op", Epoch = claimed.Epoch,
                GameId = "game-a", ModuleVersion = "1.1.0", ConfigHash = "cfg-1-v2",
                PackageHash = "pkg-1-v2", OperationId = "end-op-round-1", TenantId = "tenant-a",
                Participants =
                {
                    new Chorus.Rpc.RoundParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                    }
                }
            });
            Assert.Equal("Preparing", started.Status);
        }

        // The game host's step-1 begin — the caller the service exists for.
        using var preChannel = environment.Channel(environment.ControlPort, gameCertificate,
            "control-server");
        var pre = new Chorus.Rpc.RoundEnd.RoundEndClient(preChannel);
        var begin = await pre.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
            EndOperationId = "end-op-1", FinalTick = 120, ResultVersion = 3
        });
        Assert.Equal("end-op-1", begin.EndOperationId);
        Assert.Equal("ResultFrozen", begin.Phase);
        Assert.False(begin.Duplicate);

        // The reply was lost, not the operation: the same begin replays the one row, and a
        // second business id for the same round refuses.
        var replay = await pre.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
            EndOperationId = "end-op-1", FinalTick = 120, ResultVersion = 3
        });
        Assert.True(replay.Duplicate);
        Assert.Equal("end-op-1", replay.EndOperationId);
        var secondError = await Assert.ThrowsAsync<RpcException>(() => pre.BeginEndAsync(
            new Chorus.Rpc.BeginEndRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
                EndOperationId = "end-op-2", FinalTick = 120, ResultVersion = 3
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, secondError.StatusCode);

        var status = await pre.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
        });
        Assert.True(status.Found);
        Assert.Equal("end-op-1", status.EndOperationId);
        Assert.Equal("ResultFrozen", status.Phase);
        Assert.Equal(1, status.RoundGeneration);
        Assert.Equal(120, status.FinalTick);
        Assert.Equal(3, status.ResultVersion);
        var mount = Assert.Single(status.BindingGenerations);
        Assert.Equal(host.BindingId, mount.BindingId);
        Assert.Equal(1, mount.RoundGeneration);

        // The control plane dies between two steps and comes back on the same port: the
        // operation row is the truth it continues under. The restarted coordinator's first
        // pass continues the sequence (FIX-END-05) — the marker, the command with its empty
        // responder set (no gateways reported here), the phase — under the same business id.
        environment.Kill("Chorus.ControlServer");
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        using var postChannel = environment.Channel(environment.ControlPort, gameCertificate,
            "control-server");
        var post = new Chorus.Rpc.RoundEnd.RoundEndClient(postChannel);
        Chorus.Rpc.GetEndStatusReply? resumed = null;
        var resumeDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < resumeDeadline)
        {
            resumed = await post.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
            });
            if (resumed.Phase == "StopDeliveryIssued") break;
            await Task.Delay(150);
        }
        Assert.NotNull(resumed);
        Assert.True(resumed!.Found);
        Assert.Equal("end-op-1", resumed.EndOperationId);
        Assert.Equal("StopDeliveryIssued", resumed.Phase);
        Assert.Equal(1, resumed.RoundGeneration);
        Assert.Equal(120, resumed.FinalTick);
        Assert.Equal(3, resumed.ResultVersion);
        Assert.Equal(host.BindingId, Assert.Single(resumed.BindingGenerations).BindingId);

        // The same begin after the restart still replays the one operation — the row, not
        // the process, owns the business id.
        var replayAfterRestart = await post.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
            EndOperationId = "end-op-1", FinalTick = 120, ResultVersion = 3
        });
        Assert.True(replayAfterRestart.Duplicate);

        // A late begin arriving after the mount left the room writes nothing — the pointer
        // moved on, the lateness the fence actually reads (FIX-END-04) — and leaves the
        // recorded operation where it stands.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var move = new NpgsqlCommand(
                "UPDATE live.binding SET current_room_id='room-moved-on' WHERE binding_id=@binding",
                connection);
            move.Parameters.AddWithValue("binding", host.BindingId);
            await move.ExecuteNonQueryAsync();
        }
        var lateError = await Assert.ThrowsAsync<RpcException>(() => post.BeginEndAsync(
            new Chorus.Rpc.BeginEndRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1",
                EndOperationId = "end-op-late", FinalTick = 120, ResultVersion = 3
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, lateError.StatusCode);
        var untouched = await post.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
        });
        Assert.Equal("end-op-1", untouched.EndOperationId);
        Assert.Equal("StopDeliveryIssued", untouched.Phase);
    }

    [Fact]
    public async Task TheFrozenResultIsReportedOnceAndClosingInputDoesNotChangeTheVerdict()
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
        // The discovery net is pushed far out: here the host's own report is the only caller
        // that can begin the operation, so the row's presence proves the fast path
        // (FIX-END-04), not the sweep. The coordinator is lazy for the same reason: the
        // frozen verdict's standing is this test's subject, not the ladder's walk.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "600000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        // A control plane is provisioned and the host's data-plane identity carries
        // tenant-a, so the step-1 report (FIX-END-04) is wired.
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{environment.GamePort}",
                // The slot id stays the default {node}/game-server — a custom id would be
                // rejected by the control plane's slot shape and crash the self-report loop.
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "open-end-report-a", "live-end-report-a");
        var guest = await MountAsync(environment, simulator, "open-end-report-b", "live-end-report-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-end-a");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-end-a");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-end-report-1",
            OwnerInstanceId = "gs-end-report", LeaseSeconds = 30
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-end-report-1", OwnerInstanceId = "gs-end-report", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-end-report", TenantId = "tenant-a",
            Participants =
            {
                new Chorus.Rpc.RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new Chorus.Rpc.RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);

        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new Chorus.Rpc.GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new Chorus.Rpc.PrepareRoomRequest
        {
            RoomId = "room-end-report-1", GameId = "game-a", OwnerInstanceId = "gs-end-report",
            Epoch = owner.Epoch, OperationId = "run-end-report", DurationTicks = 40, RandomSeed = 2026,
            TickMs = 25,
            Participants =
            {
                new Chorus.Rpc.GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                new Chorus.Rpc.GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);

        // A red gift the round consumes (so red wins) and a blue gift that, if the closing
        // input applied after the freeze, would flip the verdict.
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-end-a");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var ingest = new Chorus.Rpc.PlatformIngest.PlatformIngestClient(gatewayChannel);
        var batch = new Chorus.Rpc.IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-end-a" };
        batch.Events.Add(new Chorus.Rpc.IngestEvent
        {
            SourceDedupKey = "gift-end-a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-end-report-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new Chorus.Rpc.IngestEvent
        {
            SourceDedupKey = "gift-end-b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-end-report-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await ingest.IngestBatchAsync(batch);
        await new Chorus.Rpc.RoundInput.RoundInputClient(dataChannel).RouteEventsAsync(
            new Chorus.Rpc.RouteEventsRequest { Platform = "simulator", AppId = "app-1", OperationId = "route-end-a", MaxEvents = 50 });

        // The round runs to its final frame, settles, and the host's report begins the one
        // operation from the committed receipt.
        var roomStatus = "";
        var runDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < runDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT status FROM game.room WHERE room_id='room-end-report-1'", connection);
                roomStatus = (string)(await command.ExecuteScalarAsync())!;
            }
            if (roomStatus is "Finishing" or "Settled") break;
            await Task.Delay(150);
        }
        Assert.True(roomStatus is "Finishing" or "Settled", "The room never froze its result.");

        // The report carries the receipt the room actually persisted — evidence first, then
        // the operation's own facts: one row, the room's own business id, ResultFrozen, and
        // the round generation the mounts really hold.
        long lastTick = 0, stateVersion = 0;
        string resultJson = "";
        long grantedCount = 0;
        var reportDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < reportDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using (var room = new NpgsqlCommand(
                    "SELECT last_tick,state_version FROM game.room WHERE room_id='room-end-report-1'", connection))
                await using (var reader = await room.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    lastTick = reader.GetInt64(0);
                    stateVersion = reader.GetInt64(1);
                }
                await using (var result = new NpgsqlCommand(
                    "SELECT result::text FROM game.result WHERE room_id='room-end-report-1'", connection))
                    resultJson = (string)(await result.ExecuteScalarAsync())!;
                await using (var grants = new NpgsqlCommand(
                    "SELECT count(*) FROM settlement.grant WHERE room_id='room-end-report-1' AND status='Granted'", connection))
                    grantedCount = Convert.ToInt64(await grants.ExecuteScalarAsync());
                await using (var operation = new NpgsqlCommand(
                    "SELECT 1 FROM control.end_operation WHERE room_id='room-end-report-1'", connection))
                    if (grantedCount == 2 && await operation.ExecuteScalarAsync() is not null) break;
            }
            await Task.Delay(150);
        }
        Assert.Equal(2L, grantedCount);
        Assert.Equal(40L, lastTick);
        using (var document = System.Text.Json.JsonDocument.Parse(resultJson))
            Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = new NpgsqlCommand("""
                SELECT end_operation_id,phase,round_generation,final_tick,result_version
                FROM control.end_operation WHERE room_id='room-end-report-1'
                """, connection);
            await using var reader = await operation.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("room-end-report-1/end", reader.GetString(0));
            Assert.Equal("ResultFrozen", reader.GetString(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(lastTick, reader.GetInt64(3));
            Assert.Equal(stateVersion, reader.GetInt64(4));
        }

        // The battle ticks stopped at the final frame: the status answers the same receipt
        // while the operation stands at ResultFrozen.
        using var roundEndChannel = environment.Channel(environment.ControlPort, gameCertificate, "control-server");
        var roundEnd = new Chorus.Rpc.RoundEnd.RoundEndClient(roundEndChannel);
        var statusBefore = await roundEnd.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-report-1"
        });
        Assert.True(statusBefore.Found);
        Assert.Equal("ResultFrozen", statusBefore.Phase);
        Assert.Equal(lastTick, statusBefore.FinalTick);
        Assert.Equal(stateVersion, statusBefore.ResultVersion);

        // A replayed begin (the "lost reply" shape) answers Duplicate and re-triggers
        // nothing: the settlement grants and the frozen verdict stay exactly as they were —
        // a lost notification never double-awards.
        var replay = await roundEnd.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-report-1",
            EndOperationId = "room-end-report-1/end", FinalTick = lastTick, ResultVersion = stateVersion
        });
        Assert.True(replay.Duplicate);
        await AssertVerdictUnchangedAsync(environment, lastTick, stateVersion, resultJson, 2L);

        // The closing input arrives after the result froze: the room no longer accepts
        // input, the push is refused at the durable edge, and the verdict — winner, receipt,
        // grants — is untouched. ClosingInput cannot change the outcome.
        using var pushCertificate = environment.Certificate("tenant-a", "platform-gateway", "push-end-a");
        using var pushChannel = environment.Channel(environment.GamePort, pushCertificate, "game-server");
        var closingError = await Assert.ThrowsAsync<RpcException>(async () =>
            await new Chorus.Rpc.RoomEvents.RoomEventsClient(pushChannel).DeliverEventAsync(
                new Chorus.Rpc.DeliverRoomEventRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-report-1",
                    OwnerInstanceId = "gs-end-report", Epoch = owner.Epoch, EventId = "closing-input-1",
                    Platform = "simulator", AppId = "app-1", BindingId = guest.BindingId,
                    RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":300}"""
                }));
        Assert.Equal(StatusCode.InvalidArgument, closingError.StatusCode);
        Assert.Contains("does not accept input", closingError.Status.Detail);
        await AssertVerdictUnchangedAsync(environment, lastTick, stateVersion, resultJson, 2L);
    }

    [Fact]
    public async Task AStandingEndOperationRefusesLatePushesWhileSettlementStands()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
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
        // The coordinator walks fast (the ladder must reach StopDeliveryIssued inside the
        // test's patience) while the discovery net stays lazy — the host's own report is the
        // begin's only caller. The wide lease keeps the late pushes fenced on the operation,
        // never on a lapsed owner picture a StaleOwner would answer instead.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        // The host that settles the round reports its own receipt, so its report begins the
        // operation (FIX-END-04) — the same host the late pushes will still name as owner.
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-fence");
        // The gateway's public face: the webhook in, the confirmed routing snapshot out. Its
        // stop-delivery record is an ack, not a route closure (FIX-END-05) — END-07's active
        // stop is future work, so this gateway keeps pushing and meets the receiver-side
        // fence exactly as an old gateway would.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort, controlEndpoint, gatewayPort))
            {
                ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Ingress__Platforms__0"] = "douyin",
                ["Ingress__AllowAll"] = "true",
                ["Ingress__Douyin__AppSecret"] = "end-fence-secret"
            },
            instanceId: "gateway-fence");
        // The node row the two self-reporting processes report under.
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-fence"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-fence" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "end-fence-open-a", "fence-live-1");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-end-fence");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-end-fence");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-end-fence-1",
            OwnerInstanceId = "game-fence", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-end-fence-1", OwnerInstanceId = "game-fence", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-end-fence", TenantId = "tenant-a",
            Participants =
            {
                new Chorus.Rpc.RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                }
            }
        })).Status);
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        Assert.Equal("Running", (await new Chorus.Rpc.GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new Chorus.Rpc.PrepareRoomRequest
            {
                RoomId = "room-end-fence-1", GameId = "game-a", OwnerInstanceId = "game-fence",
                Epoch = owner.Epoch, OperationId = "run-end-fence", DurationTicks = 800, RandomSeed = 777,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The face's picture warms before the first push: the snapshot must carry the live
        // key's streamer and the room's owner endpoint, and hold its version still across two
        // reads so the face's confirmed cache converges (FIX-END-03) — a cache still closing
        // its gate would answer no resolve and the webhook would wait in the durable leg
        // instead of pushing.
        var warm = false;
        var warmDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < warmDeadline)
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamerThere = snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == "fence-live-1");
            var roomThere = snapshot.Rooms.FirstOrDefault(room => room.RoomId == "room-end-fence-1")
                is { Endpoint.Length: > 0 };
            if (streamerThere && roomThere)
            {
                var before = snapshot.Version;
                await Task.Delay(600);
                var after = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
                if (before == after) { warm = true; break; }
            }
            else await Task.Delay(200);
        }
        Assert.True(warm, "The gateway's routing snapshot never carried the room's route.");

        // The event the round consumes on its way to the final frame.
        using var http = await HttpClientAsync(environment);
        var pushed = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"fence-live-1","eventId":"fence-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        Assert.True(pushed.StatusCode == HttpStatusCode.OK,
            $"The webhook was not accepted: {pushed.StatusCode}");
        var accepted = Assert.Single(ReadReceipts(await pushed.Content.ReadAsStringAsync()));
        Assert.Equal("DurableAccepted", accepted.GetProperty("disposition").GetString());

        // The round settles, the host's report begins the one operation, and the coordinator
        // walks the ladder to the stop-delivery command.
        long lastTick = 0, stateVersion = 0;
        string resultJson = "";
        var frozen = false;
        var frozenDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < frozenDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using (var consumed = new NpgsqlCommand(
                    "SELECT count(*) FROM input.delivery WHERE event_id='fence-gift-1' AND state='Consumed'", connection))
                    await using (var operation = new NpgsqlCommand(
                        "SELECT 1 FROM control.end_operation WHERE room_id='room-end-fence-1'", connection))
                    await using (var room = new NpgsqlCommand(
                        "SELECT last_tick,state_version FROM game.room WHERE room_id='room-end-fence-1'", connection))
                    await using (var result = new NpgsqlCommand(
                        "SELECT result::text FROM game.result WHERE room_id='room-end-fence-1'", connection))
                    {
                        var consumedCount = Convert.ToInt64(await consumed.ExecuteScalarAsync());
                        var operationThere = await operation.ExecuteScalarAsync() is not null;
                        if (consumedCount == 1 && operationThere
                            && await result.ExecuteScalarAsync() is string json)
                        {
                            await using var reader = await room.ExecuteReaderAsync();
                            Assert.True(await reader.ReadAsync());
                            lastTick = reader.GetInt64(0);
                            stateVersion = reader.GetInt64(1);
                            resultJson = json;
                            frozen = true;
                        }
                    }
            }
            if (frozen) break;
            await Task.Delay(150);
        }
        Assert.True(frozen, "The round never settled with its event consumed and its operation begun.");

        var issued = false;
        var issuedDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < issuedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var phase = new NpgsqlCommand(
                    "SELECT phase FROM control.end_operation WHERE room_id='room-end-fence-1'", connection);
                if (await phase.ExecuteScalarAsync() is string phaseValue
                    && phaseValue == "StopDeliveryIssued") { issued = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(issued, "The coordinator never reached StopDeliveryIssued.");

        // The late push through the face: the room's own frozen status refuses it first — a
        // visible 400, and nothing written.
        var byStatus = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"fence-live-1","eventId":"fence-gift-2","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":100}""");
        Assert.Equal(HttpStatusCode.BadRequest, byStatus.StatusCode);
        Assert.Contains("does not accept input",
            await byStatus.Content.ReadAsStringAsync());

        // The recovery shape (FIX-END-06's hole): the status column flips back to Running —
        // a reopened room would accept input again if the column were the only gate. The
        // standing operation is the fence now: the same gateway, the same route, still a 400
        // naming the closed generation's operation.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var flip = new NpgsqlCommand(
                "UPDATE game.room SET status='Running' WHERE room_id='room-end-fence-1'", connection);
            await flip.ExecuteNonQueryAsync();
        }
        var byOperation = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"fence-live-1","eventId":"fence-gift-3","kind":"Gift","giftId":"g3","giftCount":1,"giftValue":100}""");
        Assert.Equal(HttpStatusCode.BadRequest, byOperation.StatusCode);
        var refusal = await byOperation.Content.ReadAsStringAsync();
        Assert.Contains("closed by the end operation", refusal);
        Assert.Contains("room-end-fence-1/end", refusal);

        // The same refusal at the RPC edge: a push carrying the round's own generation claim
        // (the number the confirmed snapshot hands the face) is refused by the operation
        // standing for that very generation.
        using var pushCertificate = environment.Certificate("tenant-a", "platform-gateway", "push-end-fence");
        using var pushChannel = environment.Channel(gamePort, pushCertificate, "game-server");
        var fenceError = await Assert.ThrowsAsync<RpcException>(() =>
            new Chorus.Rpc.RoomEvents.RoomEventsClient(pushChannel).DeliverEventAsync(
                new Chorus.Rpc.DeliverRoomEventRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-fence-1",
                    OwnerInstanceId = "game-fence", Epoch = owner.Epoch, EventId = "fence-gift-raw",
                    Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
                    RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":300}"""
                }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, fenceError.StatusCode);
        Assert.Contains("closed by the end operation", fenceError.Status.Detail);
        Assert.Contains("room-end-fence-1/end", fenceError.Status.Detail);

        // The settlement stands: the accepted event stays queryable in its terminal state,
        // the refused pushes wrote nothing, the owner was never revoked — the fence is the
        // operation row, not a torn-down ownership — and the verdict and the ladder stand
        // exactly where they were.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal("Consumed", await new NpgsqlCommand(
                "SELECT state FROM input.delivery WHERE event_id='fence-gift-1'", connection)
                .ExecuteScalarAsync());
            Assert.Equal("room-end-fence-1", await new NpgsqlCommand(
                "SELECT attributed_room_id FROM input.event WHERE event_id='fence-gift-1'", connection)
                .ExecuteScalarAsync());
            await using (var late = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE event_id IN ('fence-gift-2','fence-gift-3','fence-gift-raw')",
                connection))
                Assert.Equal(0L, Convert.ToInt64(await late.ExecuteScalarAsync()));
            await using (var lateDeliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE event_id IN ('fence-gift-2','fence-gift-3','fence-gift-raw')",
                connection))
                Assert.Equal(0L, Convert.ToInt64(await lateDeliveries.ExecuteScalarAsync()));
            await using (var ownership = new NpgsqlCommand(
                "SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id='room-end-fence-1'",
                connection))
            await using (var reader = await ownership.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Active", reader.GetString(0));
                Assert.Equal(owner.Epoch, reader.GetInt64(1));
            }
            await using (var room = new NpgsqlCommand(
                "SELECT last_tick,state_version FROM game.room WHERE room_id='room-end-fence-1'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(lastTick, reader.GetInt64(0));
                Assert.Equal(stateVersion, reader.GetInt64(1));
            }
            Assert.Equal(resultJson, (string)(await new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-end-fence-1'", connection)
                .ExecuteScalarAsync())!);
            Assert.Equal("StopDeliveryIssued", await new NpgsqlCommand(
                "SELECT phase FROM control.end_operation WHERE room_id='room-end-fence-1'", connection)
                .ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task ADiscoverySweepBeginsTheEndOperationForAKilledHost()
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
        // The discovery net runs fast: the room's own persisted receipt is the evidence it
        // begins from, and the grace keeps it just behind the freeze. The coordinator stays
        // lazy: the sweep's begin is this test's subject, not the ladder's walk.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "150",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "1",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "600000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        // No CHORUS_CONTROL_ENDPOINT: the host's step-1 report is deterministically absent,
        // so the sweep is the only caller that can begin the operation — the exact shape of
        // a host killed after its final commit.
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "open-end-kill-a", "live-end-kill-a");
        var guest = await MountAsync(environment, simulator, "open-end-kill-b", "live-end-kill-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-end-b");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-end-b");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-end-kill-1",
            OwnerInstanceId = "gs-end-kill", LeaseSeconds = 30
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-end-kill-1", OwnerInstanceId = "gs-end-kill", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-end-kill", TenantId = "tenant-a",
            Participants =
            {
                new Chorus.Rpc.RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new Chorus.Rpc.RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);

        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new Chorus.Rpc.GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new Chorus.Rpc.PrepareRoomRequest
        {
            RoomId = "room-end-kill-1", GameId = "game-a", OwnerInstanceId = "gs-end-kill",
            Epoch = owner.Epoch, OperationId = "run-end-kill", DurationTicks = 20, RandomSeed = 777,
            TickMs = 25,
            Participants =
            {
                new Chorus.Rpc.GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                new Chorus.Rpc.GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);

        // The final frame commits and settles; the host's report never lands (no control
        // endpoint), so the room's own receipt is the only evidence left.
        var roomStatus = "";
        var runDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < runDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT status FROM game.room WHERE room_id='room-end-kill-1'", connection);
                roomStatus = (string)(await command.ExecuteScalarAsync())!;
            }
            if (roomStatus is "Finishing" or "Settled") break;
            await Task.Delay(150);
        }
        Assert.True(roomStatus is "Finishing" or "Settled", "The room never froze its result.");

        // The host dies right after the final commit — the fast path is gone with it.
        environment.Kill("Chorus.GameServer");

        // The discovery net begins the one operation from the room's persisted receipt, and
        // the control plane answers it: the room's own business id and evidence.
        long lastTick = 0, stateVersion = 0;
        var discovered = "";
        var discoveredDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < discoveredDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using (var room = new NpgsqlCommand(
                    "SELECT last_tick,state_version FROM game.room WHERE room_id='room-end-kill-1'", connection))
                await using (var reader = await room.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    lastTick = reader.GetInt64(0);
                    stateVersion = reader.GetInt64(1);
                }
                await using (var operation = new NpgsqlCommand(
                    "SELECT end_operation_id FROM control.end_operation WHERE room_id='room-end-kill-1'", connection))
                    if (await operation.ExecuteScalarAsync() is string operationId)
                    {
                        discovered = operationId;
                        break;
                    }
            }
            await Task.Delay(150);
        }
        Assert.Equal("room-end-kill-1/end", discovered);

        using var roundEndChannel = environment.Channel(environment.ControlPort, gameCertificate, "control-server");
        var roundEnd = new Chorus.Rpc.RoundEnd.RoundEndClient(roundEndChannel);
        var statusReply = await roundEnd.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-kill-1"
        });
        Assert.True(statusReply.Found);
        Assert.Equal("room-end-kill-1/end", statusReply.EndOperationId);
        Assert.Equal("ResultFrozen", statusReply.Phase);
        Assert.Equal(lastTick, statusReply.FinalTick);
        Assert.Equal(stateVersion, statusReply.ResultVersion);
        Assert.Equal(2, statusReply.BindingGenerations.Count);
    }

    [Fact]
    public async Task TheCoordinatorPersistsAndDeliversTheStopDeliveryCommand()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gatewayPortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPortB = RedesignM0HostTests.HostEnvironment.FreePort();
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
        // The discovery net stays lazy — the begin below is the only caller — while the
        // coordinator walks fast, so the phase ladder lands inside the test's patience.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPortA, "platform-gateway",
            GatewayEnvironment(environment.DataPort, controlEndpoint, gatewayPortA),
            instanceId: "gateway-delivery-a");
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPortB, "platform-gateway",
            GatewayEnvironment(environment.DataPort, controlEndpoint, gatewayPortB),
            instanceId: "gateway-delivery-b");

        // The node row both gateways self-report under.
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-delivery"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-delivery" });
        }

        // Route authorization first: each gateway applied a routing version and the control
        // plane heard its confirm — the rows the responder set is derived from.
        var authorized = false;
        var authorizedDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < authorizedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT count(*) FROM control.routing_confirm
                    WHERE process_instance_id IN ('gateway-delivery-a','gateway-delivery-b')
                    """, connection);
                if (Convert.ToInt64(await command.ExecuteScalarAsync()) == 2) { authorized = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(authorized, "The gateways never confirmed their routing versions.");

        // A mounted binding and a started round — the mounts the begin's fence reads; the round
        // itself never needs to run, the begin freezes what the mounts entered.
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "delivery-open-a", "live-delivery-a");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-delivery");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-delivery");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);
        var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-delivery-1",
            OwnerInstanceId = "gs-delivery", LeaseSeconds = 30
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-delivery-1", OwnerInstanceId = "gs-delivery", Epoch = claimed.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-delivery", TenantId = "tenant-a",
            Participants =
            {
                new Chorus.Rpc.RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                }
            }
        })).Status);

        // The second gateway dies with its authorization standing: its process and confirm rows
        // never leave, so the command must still count it — offline is pending, never skipped.
        environment.Kill("gateway-delivery-b");

        // Step 1: the begin freezes the result; steps 2-3: the coordinator records the marker,
        // persists the command with its responder set, pushes, and records the phase.
        using var roundEndChannel = environment.Channel(environment.ControlPort, gameCertificate, "control-server");
        var roundEnd = new Chorus.Rpc.RoundEnd.RoundEndClient(roundEndChannel);
        var begin = await roundEnd.BeginEndAsync(new Chorus.Rpc.BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-delivery-1",
            EndOperationId = "room-delivery-1/end", FinalTick = 120, ResultVersion = 3
        });
        Assert.Equal("ResultFrozen", begin.Phase);
        var issued = false;
        var issuedDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < issuedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT phase FROM control.end_operation WHERE room_id='room-delivery-1'", connection);
                if (await command.ExecuteScalarAsync() is string phase
                    && phase == "StopDeliveryIssued") { issued = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(issued, "The coordinator never reached StopDeliveryIssued.");

        // The recorded command: the room, the generation the begin derived, and a close version
        // frozen at issue; the responder set: both gateways — the dead one counted (a).
        long closeVersion = 0;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var command = new NpgsqlCommand("""
                SELECT round_generation,close_route_version,
                    (SELECT count(*) FROM control.stop_delivery_responder r
                     WHERE r.end_operation_id='room-delivery-1/end' AND r.state='Pending'),
                    (SELECT count(*) FROM control.stop_delivery_responder r
                     WHERE r.end_operation_id='room-delivery-1/end'
                       AND r.process_instance_id='gateway-delivery-b')
                FROM control.stop_delivery WHERE room_id='room-delivery-1'
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync(), "The stop-delivery command was never persisted.");
                Assert.Equal(1L, reader.GetInt64(0));
                closeVersion = reader.GetInt64(1);
                Assert.NotEqual(0L, closeVersion);
                Assert.Equal(2L, reader.GetInt64(2));
                Assert.Equal(1L, reader.GetInt64(3));
            }
        }

        // The live gateway received the command at the exact recorded version — the delivery
        // leg's evidence on its console channel; the dead gateway heard nothing.
        var liveOutput = HostOutputFor(environment, "gateway-delivery-a");
        Assert.Contains(
            $"stop-delivery received room=room-delivery-1 version={closeVersion} " +
            "operation=room-delivery-1/end duplicate=False", liveOutput);
        Assert.DoesNotContain("stop-delivery received", HostOutputFor(environment, "gateway-delivery-b"));

        // Repeated passes re-deliver the same recorded command and move nothing (b): one
        // command row, both responders still pending, the phase where it stands — and the
        // first delivery stays the only non-duplicate the gateway ever saw.
        await Task.Delay(1500);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var commands = new NpgsqlCommand(
                "SELECT count(*) FROM control.stop_delivery", connection);
            Assert.Equal(1L, Convert.ToInt64(await commands.ExecuteScalarAsync()));
            await using var phase = new NpgsqlCommand(
                "SELECT phase FROM control.end_operation WHERE room_id='room-delivery-1'", connection);
            Assert.Equal("StopDeliveryIssued", await phase.ExecuteScalarAsync());
            await using var pending = new NpgsqlCommand("""
                SELECT count(*) FROM control.stop_delivery_responder
                WHERE end_operation_id='room-delivery-1/end' AND state='Pending'
                """, connection);
            Assert.Equal(2L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
        }
        var delivered = liveOutput.Split('\n')
            .Count(line => line.Contains("stop-delivery received room=room-delivery-1"));
        var firstDeliveries = liveOutput.Split('\n')
            .Count(line => line.Contains("stop-delivery received room=room-delivery-1")
                && line.Contains("duplicate=False"));
        Assert.True(delivered >= 1, "The live gateway never logged the command.");
        Assert.Equal(1, firstDeliveries);
        var status = await roundEnd.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-delivery-1"
        });
        Assert.True(status.Found);
        Assert.Equal("StopDeliveryIssued", status.Phase);
    }

    /// <summary>The captured console output of one started host, by instance id.</summary>
    private static string HostOutputFor(RedesignM0HostTests.HostEnvironment environment, string instance) =>
        environment.HostOutput(environment.HostProcesses.Single(pair => pair.Instance == instance).Process);

    /// <summary>Posts one platform webhook to the gateway's public ingress face.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, int ingressPort,
        string platform, string appId, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://localhost:{ingressPort}/ingress/{platform}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(PlatformIngressEndpoint.AppIdHeader, appId);
        return await http.SendAsync(request);
    }

    /// <summary>An HTTP client that trusts the environment's test certificate authority.</summary>
    private static async Task<HttpClient> HttpClientAsync(RedesignM0HostTests.HostEnvironment environment)
    {
        var root = X509Certificate2.CreateFromPem(environment.RootCertificatePem);
        var nodeAuthority = X509Certificate2.CreateFromPem(environment.NodeAuthorityCertificatePem);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, chain, _) =>
            {
                if (certificate is null) return false;
                using var candidate = new X509Certificate2(certificate);
                using var validator = new X509Chain();
                validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                validator.ChainPolicy.CustomTrustStore.Add(root);
                validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
                return validator.Build(candidate);
            }
        };
        return await Task.FromResult(new HttpClient(handler));
    }

    private static List<JsonElement> ReadReceipts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("receipts").EnumerateArray()
            .Select(receipt => receipt.Clone()).ToList();
    }

    /// <summary>A gateway with no ingress duty: it self-reports and watches routing only — the
    /// two legs whose rows the stop-delivery responder set reads.</summary>
    private static Dictionary<string, string> GatewayEnvironment(
        int dataPort, string controlEndpoint, int reportPort) => new()
    {
        ["CHORUS_DATA_PORT"] = dataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["CHORUS_DEV_PLATFORM_ENDPOINT"] = "http://127.0.0.1:9/",
        ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
        ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{reportPort}",
        ["CHORUS_REPORT_INTERVAL_MS"] = "500"
    };

    /// <summary>The frozen verdict — receipt, result, grants — is untouched: the operation's
    /// replay and the late closing input must both leave it exactly where it stood.</summary>
    private static async Task AssertVerdictUnchangedAsync(
        RedesignM0HostTests.HostEnvironment environment, long lastTick, long stateVersion,
        string resultJson, long grantedCount)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using (var room = new NpgsqlCommand(
            "SELECT last_tick,state_version FROM game.room WHERE room_id='room-end-report-1'", connection))
        await using (var reader = await room.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(lastTick, reader.GetInt64(0));
            Assert.Equal(stateVersion, reader.GetInt64(1));
        }
        Assert.Equal(resultJson, (string)(await new NpgsqlCommand(
            "SELECT result::text FROM game.result WHERE room_id='room-end-report-1'", connection)
            .ExecuteScalarAsync())!);
        Assert.Equal(grantedCount, Convert.ToInt64(await new NpgsqlCommand(
            "SELECT count(*) FROM settlement.grant WHERE room_id='room-end-report-1' AND status='Granted'", connection)
            .ExecuteScalarAsync()));
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId,
        string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new Chorus.Rpc.StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new Chorus.Rpc.LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new Chorus.Rpc.StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }
}
