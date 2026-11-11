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
        // The empty set is vacuously confirmed (FIX-END-07): no gateway held route
        // authorization, so none can have pushed, and the ladder records DeliveryStopped
        // in the same pass — then mints the platform credential over it in the very next
        // breath (FIX-END-09), so the poll accepts any of the three phases, because the
        // continuation, not the parking, is this fact's claim.
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
            if (resumed.Phase is "StopDeliveryIssued" or "DeliveryStopped" or "PlatformTaskAuthorized") break;
            await Task.Delay(150);
        }
        Assert.NotNull(resumed);
        Assert.True(resumed!.Found);
        Assert.Equal("end-op-1", resumed.EndOperationId);
        Assert.True(resumed.Phase is "StopDeliveryIssued" or "DeliveryStopped" or "PlatformTaskAuthorized",
            $"The resumed phase is {resumed.Phase}.");
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
        Assert.True(untouched.Phase is "StopDeliveryIssued" or "DeliveryStopped" or "PlatformTaskAuthorized",
            $"The untouched phase is {untouched.Phase}.");
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
        // stop-delivery closure (FIX-END-07) is installed from the command the coordinator
        // pushes — this gateway stops pushing for the closed room on its own, and the raw
        // RPC legs below keep the receiver-side fence (FIX-END-06) under test exactly as an
        // old, closure-less gateway would meet it.
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
        // A liveness margin, not a performance bound: under a loaded shared runner the
        // settle chain (final frame, commit, report, begin) can stretch well past a quiet
        // machine's pace.
        var frozenDeadline = DateTime.UtcNow.AddSeconds(90);
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

        // The gateway confirmed (FIX-END-07): it installed the closure, drained and acked,
        // so the barrier completed — the pass that records DeliveryStopped mints the
        // platform credential over it in the same breath (FIX-END-09), so the poll accepts
        // either dress of the completed barrier. The confirmation standing is what the
        // rest of the fact needs: the closure gate, not the receiver's fence, now answers
        // this gateway's late webhooks.
        var stopped = false;
        var stoppedDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < stoppedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var phase = new NpgsqlCommand(
                    "SELECT phase FROM control.end_operation WHERE room_id='room-end-fence-1'", connection);
                if (await phase.ExecuteScalarAsync() is string phaseValue
                    && phaseValue is "DeliveryStopped" or "PlatformTaskAuthorized")
                { stopped = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(stopped,
            "The gateway never confirmed its delivery stop, so the barrier never completed.");

        // The receiver's fence at the RPC edge, first as the room's own frozen status
        // refuses it — a raw push (exactly what an old, closure-less gateway sends) meets
        // the status gate while the operation's own generation fence waits behind it.
        using (var rawCertificateA = environment.Certificate("tenant-a", "platform-gateway", "push-end-fence-a"))
        using (var rawChannelA = environment.Channel(gamePort, rawCertificateA, "game-server"))
        {
            var statusError = await Assert.ThrowsAsync<RpcException>(() =>
                new Chorus.Rpc.RoomEvents.RoomEventsClient(rawChannelA).DeliverEventAsync(
                    new Chorus.Rpc.DeliverRoomEventRequest
                    {
                        TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-fence-1",
                        OwnerInstanceId = "game-fence", Epoch = owner.Epoch, EventId = "fence-gift-raw-a",
                        Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
                        RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":300}"""
                    }).ResponseAsync);
            Assert.Equal(StatusCode.InvalidArgument, statusError.StatusCode);
            Assert.Contains("does not accept input", statusError.Status.Detail);
        }

        // The late push through the face: the closure answers with the event's durable
        // home — attributed to the room in the persistent inbox, never a room-inbox
        // delivery and never a stateless 400 (FIX-END-07).
        var byClosure = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"fence-live-1","eventId":"fence-gift-2","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":100}""");
        Assert.True(byClosure.StatusCode == HttpStatusCode.OK,
            $"The closed room's late webhook was not accepted: {byClosure.StatusCode}");
        var homed = Assert.Single(ReadReceipts(await byClosure.Content.ReadAsStringAsync()));
        Assert.Equal("Attributed", homed.GetProperty("disposition").GetString());
        Assert.Equal(host.BindingId, homed.GetProperty("bindingId").GetString());

        // The recovery shape (FIX-END-06's hole): the status column flips back to Running —
        // a reopened room would accept input again if the column were the only gate. The
        // closure is generation-based, not status-based: the same webhook still answers its
        // durable home, and the raw RPC below still meets the standing operation.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var flip = new NpgsqlCommand(
                "UPDATE game.room SET status='Running' WHERE room_id='room-end-fence-1'", connection);
            await flip.ExecuteNonQueryAsync();
        }
        var byOperation = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"fence-live-1","eventId":"fence-gift-3","kind":"Gift","giftId":"g3","giftCount":1,"giftValue":100}""");
        Assert.True(byOperation.StatusCode == HttpStatusCode.OK,
            $"The reopened status column reopened nothing: {byOperation.StatusCode}");
        Assert.Single(ReadReceipts(await byOperation.Content.ReadAsStringAsync()));

        // The standing operation at the RPC edge: a push carrying the round's own generation
        // claim (the number the confirmed snapshot hands the face) is refused by the
        // operation standing for that very generation, whatever the status column says.
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

        // The late arrivals reach their terminal disposition (FIX-END-07): the face's
        // ingest asked the closure for one more drain pass, and the pass disposed them
        // OutOfRound with the room named — queryable, terminal, never delivered.
        var disposed = false;
        var disposedDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < disposedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var terminal = new NpgsqlCommand("""
                    SELECT count(*) FROM input.event
                    WHERE source_dedup_key IN ('fence-gift-2','fence-gift-3')
                      AND disposition='OutOfRound' AND attributed_room_id='room-end-fence-1'
                    """, connection);
                if (Convert.ToInt64(await terminal.ExecuteScalarAsync()) == 2) { disposed = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(disposed, "The closed round's late events never reached a terminal disposition.");

        // The settlement stands: the accepted event stays queryable in its terminal state,
        // the late webhooks hold their durable rows but never a delivery, the raw refusals
        // left no trace at all, the owner was never revoked — the fence is the operation
        // row, not a torn-down ownership — and the verdict stands exactly where it froze,
        // with the gateway's own answer recorded beside it.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal("Consumed", await new NpgsqlCommand(
                "SELECT state FROM input.delivery WHERE event_id='fence-gift-1'", connection)
                .ExecuteScalarAsync());
            Assert.Equal("room-end-fence-1", await new NpgsqlCommand(
                "SELECT attributed_room_id FROM input.event WHERE event_id='fence-gift-1'", connection)
                .ExecuteScalarAsync());
            await using (var lateDeliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE event_id IN " +
                "('fence-gift-2','fence-gift-3','fence-gift-raw','fence-gift-raw-a')", connection))
                Assert.Equal(0L, Convert.ToInt64(await lateDeliveries.ExecuteScalarAsync()));
            await using (var rawTraces = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE event_id IN " +
                "('fence-gift-raw','fence-gift-raw-a')", connection))
                Assert.Equal(0L, Convert.ToInt64(await rawTraces.ExecuteScalarAsync()));
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
            // The gateway's answer stands recorded at the exact frozen version, with the
            // watermark its push leg observed for the room.
            await using (var responder = new NpgsqlCommand("""
                SELECT r.state, r.answered_at IS NOT NULL, r.cutoff_sequence >= 1,
                       c.close_route_version <> 0
                FROM control.stop_delivery_responder r
                JOIN control.stop_delivery c ON c.end_operation_id = r.end_operation_id
                WHERE r.end_operation_id='room-end-fence-1/end'
                  AND r.process_instance_id='gateway-fence'
                """, connection))
            await using (var reader = await responder.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync(),
                    "The confirming gateway holds no responder row.");
                Assert.Equal("Stopped", reader.GetString(0));
                Assert.True(reader.GetBoolean(1), "The answer's receipt time was never recorded.");
                Assert.True(reader.GetBoolean(2), "The cutoff watermark was never recorded.");
                Assert.True(reader.GetBoolean(3), "The command row carries no frozen version.");
            }
            // The ladder rests past the barrier FIX-END-09 minted: either dress of the
            // completed barrier is the honest standing, and neither reopens input.
            var fencePhase = await new NpgsqlCommand(
                "SELECT phase FROM control.end_operation WHERE room_id='room-end-fence-1'", connection)
                .ExecuteScalarAsync() as string;
            Assert.True(fencePhase is "DeliveryStopped" or "PlatformTaskAuthorized",
                $"The ladder left the completed barrier: {fencePhase}.");
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

        // Route authorization first — and the full responder-set membership. The report
        // ingest refuses a host whose node row is absent (the gateways' first self-report
        // ticks fire during boot, before the test registers node-a above), so a process
        // row is born by a later 500 ms tick, not by boot. The process × deployment ×
        // confirm triple is exactly what the issue's responder insert joins: killing b
        // before its row is born would drop it from the frozen set — offline must stay
        // pending, never skipped.
        var authorized = false;
        var authorizedDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < authorizedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT count(DISTINCT p.process_instance_id)
                    FROM control.process p
                    JOIN control.deployment d ON d.slot_id = p.slot_id
                    JOIN control.routing_confirm c ON c.process_instance_id = p.process_instance_id
                    WHERE d.role = 'platform-gateway'
                      AND p.process_instance_id IN ('gateway-delivery-a','gateway-delivery-b')
                    """, connection);
                if (Convert.ToInt64(await command.ExecuteScalarAsync()) == 2) { authorized = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(authorized, "The gateways never stood in the responder set's rows.");

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
        Assert.True(issued, "The coordinator never reached StopDeliveryIssued. Host consoles:\n"
            + string.Join("\n---\n", environment.HostProcesses.Select(pair =>
            {
                var output = environment.HostOutput(pair.Process);
                return $"[{pair.Instance}]\n" + (output.Length > 1200 ? output[^1200..] : output);
            })));

        // The live gateway confirms (FIX-END-07): it installed the closure, drained its
        // observed pairs and acked, so its responder row left Pending — the ack's own
        // evidence, landed within the test's patience.
        var confirmed = false;
        var confirmedDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < confirmedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT state FROM control.stop_delivery_responder" +
                    " WHERE end_operation_id='room-delivery-1/end'" +
                    " AND process_instance_id='gateway-delivery-a'", connection);
                if (await command.ExecuteScalarAsync() is string state
                    && state == "Stopped") { confirmed = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(confirmed, "The live gateway never confirmed its delivery stop.");

        // The recorded command: the room, the generation the begin derived, and a close version
        // frozen at issue; the responder set: both gateways — the dead one counted (a) — the
        // live one Stopped with its answer recorded, the dead one still Pending (the barrier
        // FIX-END-07 parks on).
        long closeVersion = 0;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT round_generation,close_route_version,
                    (SELECT count(*) FROM control.stop_delivery_responder r
                     WHERE r.end_operation_id='room-delivery-1/end' AND r.state='Pending'),
                    (SELECT count(*) FROM control.stop_delivery_responder r
                     WHERE r.end_operation_id='room-delivery-1/end'
                       AND r.process_instance_id='gateway-delivery-b'),
                    (SELECT r.answered_at IS NOT NULL
                     FROM control.stop_delivery_responder r
                     WHERE r.end_operation_id='room-delivery-1/end'
                       AND r.process_instance_id='gateway-delivery-a')
                FROM control.stop_delivery WHERE room_id='room-delivery-1'
                """, connection);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync(), "The stop-delivery command was never persisted.");
                Assert.Equal(1L, reader.GetInt64(0));
                closeVersion = reader.GetInt64(1);
                Assert.NotEqual(0L, closeVersion);
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(1L, reader.GetInt64(3));
                Assert.True(reader.GetBoolean(4), "The live responder's answer was never recorded.");
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
        // command row, the live responder's answer standing, the dead responder still
        // pending — and the phase parked on it — while the first delivery stays the only
        // non-duplicate the gateway ever saw.
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
            Assert.Equal(1L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
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

    /// <summary>
    /// FIX-END-07, architecture 6.4 step 4 end to end: the gateway that confirmed installed
    /// the closure, disposed the round's pending inbox input terminally (OutOfRound, room
    /// named, never delivered), and reported DeliveryStopped with its own facts — the exact
    /// frozen version and the cutoff watermark its push leg observed. The barrier is the
    /// recorded responder set: this room's only authorized gateway answers, the phase
    /// records DeliveryStopped on its ack, and a late webhook after the confirmation still
    /// finds its durable home and its terminal disposition through one more drain pass.
    /// </summary>
    [Fact]
    public async Task ALiveGatewayDisposesPendingInputAndConfirmsDeliveryStopped()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
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
        // The discovery net stays lazy — the host's own report is the only begin — while
        // the coordinator walks fast so the command, the confirmation and the barrier land
        // inside the test's patience. The ownership keys keep the claim alive through the
        // run and the game endpoint names the host the coordinator will push to.
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
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-stopdrain");
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort, controlEndpoint, gatewayPort))
            {
                ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Ingress__Platforms__0"] = "douyin",
                ["Ingress__AllowAll"] = "true",
                ["Ingress__Douyin__AppSecret"] = "stopdrain-secret"
            },
            instanceId: "gateway-stopdrain");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-stopdrain"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-stopdrain" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "stopdrain-open-1", "stopdrain-live-1");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-stopdrain");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-stopdrain");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-stopdrain-1",
            OwnerInstanceId = "game-stopdrain", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-stopdrain-1", OwnerInstanceId = "game-stopdrain", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-stopdrain", TenantId = "tenant-a",
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
                RoomId = "room-stopdrain-1", GameId = "game-a", OwnerInstanceId = "game-stopdrain",
                Epoch = owner.Epoch, OperationId = "run-stopdrain", DurationTicks = 400, RandomSeed = 777,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The round's pending inbox input: attributed to the binding, no room yet — the
        // item only a drain pass can dispose, waiting in the shared inbox before the end
        // sequence even begins.
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-stopdrain-ingest");
        using var ingestChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var pending = new Chorus.Rpc.IngestBatchRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "ingest-stopdrain-0"
        };
        pending.Events.Add(new Chorus.Rpc.IngestEvent
        {
            SourceDedupKey = "stopdrain-gift-0", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "stopdrain-live-1", PayloadJson = """{"kind":"gift","amount":10}"""
        });
        await new Chorus.Rpc.PlatformIngest.PlatformIngestClient(ingestChannel).IngestBatchAsync(pending);

        // The face's picture warms before the first push: the snapshot must carry the live
        // key's streamer and the room's owner endpoint, and hold its version still across
        // two reads so the face's confirmed cache converges (FIX-END-03).
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        var warm = false;
        var warmDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < warmDeadline)
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamerThere = snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == "stopdrain-live-1");
            var roomThere = snapshot.Rooms.FirstOrDefault(room => room.RoomId == "room-stopdrain-1")
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

        // The event the round consumes on its way to the final frame — the push leg whose
        // durable reply sequence becomes the ack's cutoff watermark.
        using var http = await HttpClientAsync(environment);
        var pushed = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"stopdrain-live-1","eventId":"stopdrain-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        Assert.True(pushed.StatusCode == HttpStatusCode.OK,
            $"The webhook was not accepted: {pushed.StatusCode}");
        Assert.Equal("DurableAccepted", Assert.Single(ReadReceipts(
            await pushed.Content.ReadAsStringAsync())).GetProperty("disposition").GetString());

        // The round settles, the host's report begins the operation, the coordinator issues
        // the command, the gateway installs the closure, drains the pending item terminally
        // and acks — the pass that records DeliveryStopped on its confirmation mints the
        // platform credential over it in the same breath (FIX-END-09), so the poll accepts
        // either dress of the completed barrier.
        var settled = false;
        // A liveness margin, not a performance bound — the chain this waits on (final
        // frame, settle, report, command, drain, ack) is exactly the one a loaded shared
        // runner stretches.
        var settleDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < settleDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var consumed = new NpgsqlCommand(
                    "SELECT count(*) FROM input.delivery WHERE event_id='stopdrain-gift-1' AND state='Consumed'",
                    connection);
                await using var stopped = new NpgsqlCommand(
                    "SELECT phase FROM control.end_operation WHERE room_id='room-stopdrain-1'", connection);
                if (Convert.ToInt64(await consumed.ExecuteScalarAsync()) == 1
                    && await stopped.ExecuteScalarAsync() is string phase
                    && phase is "DeliveryStopped" or "PlatformTaskAuthorized") { settled = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(settled, "The round never settled through the completed delivery-stop barrier.");

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            // The gateway's answer: its own responder row at Stopped with the receipt time,
            // the watermark its push leg observed and the count of the items it disposed —
            // the pending gift of the closed round, OutOfRound with the room named and
            // never delivered into it.
            await using (var responder = new NpgsqlCommand("""
                SELECT r.state, r.answered_at IS NOT NULL, r.cutoff_sequence >= 1,
                       r.drained_out_of_round >= 1
                FROM control.stop_delivery_responder r
                WHERE r.end_operation_id='room-stopdrain-1/end'
                  AND r.process_instance_id='gateway-stopdrain'
                """, connection))
            await using (var reader = await responder.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync(), "The confirming gateway holds no responder row.");
                Assert.Equal("Stopped", reader.GetString(0));
                Assert.True(reader.GetBoolean(1), "The answer's receipt time was never recorded.");
                Assert.True(reader.GetBoolean(2), "The cutoff watermark was never recorded.");
                Assert.True(reader.GetBoolean(3), "The pending item's disposal was never counted.");
            }
            await using (var disposed = new NpgsqlCommand(
                "SELECT disposition,attributed_room_id FROM input.event" +
                " WHERE source_dedup_key='stopdrain-gift-0'", connection))
            await using (var reader = await disposed.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync(), "The pending gift never became an event row.");
                Assert.Equal("OutOfRound", reader.GetString(0));
                Assert.Equal("room-stopdrain-1", reader.GetString(1));
            }
            Assert.Equal(0L, Convert.ToInt64(await new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE event_id='stopdrain-gift-0'", connection)
                .ExecuteScalarAsync()));
        }

        // A late webhook after the confirmation: the closure answers with the durable home,
        // and the face's request for one more drain pass disposes it terminally — the
        // confirmation did not end the closed round's disposal duty.
        var late = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"stopdrain-live-1","eventId":"stopdrain-gift-2","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":100}""");
        Assert.True(late.StatusCode == HttpStatusCode.OK,
            $"The closed room's late webhook was not accepted: {late.StatusCode}");
        Assert.Single(ReadReceipts(await late.Content.ReadAsStringAsync()));
        var terminal = false;
        var terminalDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < terminalDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var disposed = new NpgsqlCommand(
                    "SELECT count(*) FROM input.event WHERE source_dedup_key='stopdrain-gift-2'" +
                    " AND disposition='OutOfRound' AND attributed_room_id='room-stopdrain-1'", connection);
                if (Convert.ToInt64(await disposed.ExecuteScalarAsync()) == 1) { terminal = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(terminal, "The late webhook never reached its terminal disposition.");

        // The settlement facts stand: the consumed gift stays consumed with exactly its one
        // delivery, the late gift holds its durable row without any delivery, and the
        // ladder stays where the barrier recorded it.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal("Consumed", await new NpgsqlCommand(
                "SELECT state FROM input.delivery WHERE event_id='stopdrain-gift-1'", connection)
                .ExecuteScalarAsync());
            Assert.Equal(0L, Convert.ToInt64(await new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE event_id='stopdrain-gift-2'", connection)
                .ExecuteScalarAsync()));
            Assert.Equal(1L, Convert.ToInt64(await new NpgsqlCommand(
                "SELECT count(*) FROM control.stop_delivery", connection)
                .ExecuteScalarAsync()));
            // The ladder rests past the barrier FIX-END-09 minted — either dress of the
            // completed barrier is where it stands.
            var drainPhase = await new NpgsqlCommand(
                "SELECT phase FROM control.end_operation WHERE room_id='room-stopdrain-1'", connection)
                .ExecuteScalarAsync() as string;
            Assert.True(drainPhase is "DeliveryStopped" or "PlatformTaskAuthorized",
                $"The ladder left the completed barrier: {drainPhase}.");
        }
    }

    /// <summary>
    /// FIX-END-08, the barrier's completeness end to end: the confirmation owed is every
    /// current holder of route authorization. The round's own pusher reports a dead
    /// endpoint — authorized, confirming its snapshot, but unreachable for the command —
    /// so the phase parks at StopDeliveryIssued. A second gateway that authorizes only
    /// after the command stood joins the barrier, receives the re-push and answers, and
    /// its answer does not clear the unreachable holder: the phase parks still. Only the
    /// holder's death — its reports aging past the barrier's stale window — exits it, and
    /// the exit is safe because the standing operation is itself the fence: a push from
    /// the dead gateway's own identity, carrying the round's own generation claim, is
    /// refused at the receiver with the operation named.
    /// </summary>
    [Fact]
    public async Task AMissedGatewayHoldsTheBarrierUntilItAcksOrItsAuthorizationLapses()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        // The unreachable holder's advertised face: a live port number nobody serves, so
        // the control plane's pushes to it fail while its own outbound legs — the report,
        // the routing confirm, the webhook pushes — all work.
        var deadEndpointPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var lateGatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
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
        // The coordinator walks fast and the barrier's stale window is short (the lapse leg
        // must land inside the test's patience), while the discovery net stays lazy — the
        // host's own report is the begin's only caller.
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
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-hold");
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        // The round's pusher: a live gateway whose ADVERTISED endpoint is dead — authorized
        // and confirming, unreachable for the stop-delivery command.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort, controlEndpoint,
                deadEndpointPort))
            {
                ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Ingress__Platforms__0"] = "douyin",
                ["Ingress__AllowAll"] = "true",
                ["Ingress__Douyin__AppSecret"] = "hold-secret"
            },
            instanceId: "gateway-hold-a");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-hold"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-hold" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "hold-open-1", "hold-live-1");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-hold");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-hold");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-hold-1",
            OwnerInstanceId = "game-hold", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-hold-1", OwnerInstanceId = "game-hold", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-hold", TenantId = "tenant-a",
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
                RoomId = "room-hold-1", GameId = "game-a", OwnerInstanceId = "game-hold",
                Epoch = owner.Epoch, OperationId = "run-hold", DurationTicks = 400, RandomSeed = 777,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The face's picture warms before the push: the snapshot must carry the live key's
        // streamer and the room's owner endpoint, and hold its version still (FIX-END-03).
        var warm = false;
        var warmDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < warmDeadline)
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamerThere = snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == "hold-live-1");
            var roomThere = snapshot.Rooms.FirstOrDefault(room => room.RoomId == "room-hold-1")
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

        // The round's own event, pushed by the unreachable-to-be holder: its ingress works,
        // its push leg works, its confirmation of the snapshot works — everything but the
        // face the command must reach.
        using var http = await HttpClientAsync(environment);
        var pushed = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"hold-live-1","eventId":"hold-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        Assert.True(pushed.StatusCode == HttpStatusCode.OK,
            $"The webhook was not accepted: {pushed.StatusCode}");
        Assert.Equal("DurableAccepted", Assert.Single(ReadReceipts(
            await pushed.Content.ReadAsStringAsync())).GetProperty("disposition").GetString());

        // The round settles and the operation begins; the command issues and the phase
        // parks — the holder never heard it.
        var parked = false;
        var parkedDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < parkedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT eo.phase,
                        (SELECT r.state FROM control.stop_delivery_responder r
                         WHERE r.end_operation_id='room-hold-1/end'
                           AND r.process_instance_id='gateway-hold-a')
                    FROM control.end_operation eo WHERE eo.room_id='room-hold-1'
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && reader.GetString(0) == "StopDeliveryIssued"
                    && reader.GetString(1) == "Pending") { parked = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, "The phase never parked on the unreachable holder.");

        // The late holder: a second gateway, real endpoint, authorized only now. The
        // barrier grows to cover it, the re-push carries it the command, and it answers —
        // and the phase still parks, because the unreachable holder owes its own answer.
        await environment.StartAsync("Chorus.PlatformGateway", lateGatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, controlEndpoint, lateGatewayPort),
            instanceId: "gateway-hold-b");
        var answered = false;
        var answeredDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < answeredDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT r.state FROM control.stop_delivery_responder r
                              WHERE r.end_operation_id='room-hold-1/end'
                                AND r.process_instance_id='gateway-hold-b'),
                           (SELECT eo.phase FROM control.end_operation eo
                              WHERE eo.room_id='room-hold-1')
                    """, connection);
                // The late holder's row appears only when the barrier resolves it; NULL is
                // the not-yet, and the loop keeps waiting.
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && reader.GetString(0) == "Stopped"
                    && reader.GetString(1) == "StopDeliveryIssued")
                { answered = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(answered,
            "The late holder never answered, or its answer wrongly cleared the parked phase.");

        // The holder dies: its reports stop, its authorization lapses past the barrier's
        // window, and only then the barrier completes — the exit is the lapse, never a
        // timeout of patience. The pass that records DeliveryStopped mints the platform
        // credential over it in the same breath (FIX-END-09), so the poll accepts either
        // dress of the opened barrier.
        environment.Kill("gateway-hold-a");
        var stopped = false;
        var stoppedDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < stoppedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                if (await new NpgsqlCommand(
                    "SELECT phase FROM control.end_operation WHERE room_id='room-hold-1'",
                    connection).ExecuteScalarAsync() is string phase
                    && phase is "DeliveryStopped" or "PlatformTaskAuthorized")
                { stopped = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(stopped, "The barrier never opened after the holder's authorization lapsed.");

        // The network 'recovers' and the old identity pushes: the standing operation is
        // the fence, so the dead gateway's own claim — the round's own generation — is
        // refused at the receiver with the operation named (FIX-END-06's fence standing
        // behind FIX-END-08's exit). The status column is flipped open first — the fence
        // must refuse on its own, not hide behind the room-status gate.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var flip = new NpgsqlCommand(
                "UPDATE game.room SET status='Running' WHERE room_id='room-hold-1'", connection);
            await flip.ExecuteNonQueryAsync();
        }
        using var zombieCertificate = environment.Certificate("tenant-a", "platform-gateway", "gateway-hold-a");
        using var zombieChannel = environment.Channel(gamePort, zombieCertificate, "game-server");
        var zombieError = await Assert.ThrowsAsync<RpcException>(() =>
            new Chorus.Rpc.RoomEvents.RoomEventsClient(zombieChannel).DeliverEventAsync(
                new Chorus.Rpc.DeliverRoomEventRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", RoomId = "room-hold-1",
                    OwnerInstanceId = "game-hold", Epoch = owner.Epoch, EventId = "hold-zombie-raw",
                    Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
                    RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":300}"""
                }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, zombieError.StatusCode);
        Assert.Contains("closed by the end operation", zombieError.Status.Detail);
        Assert.Contains("room-hold-1/end", zombieError.Status.Detail);

        // The barrier's own evidence: the answered holder's facts recorded, the missed
        // holder's row exactly as it stood — Pending, the honest trace of the
        // confirmation that never came — and the zombie push wrote nothing.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var responders = new NpgsqlCommand("""
                SELECT r.process_instance_id, r.state, r.answered_at IS NOT NULL
                FROM control.stop_delivery_responder r
                WHERE r.end_operation_id='room-hold-1/end'
                ORDER BY r.process_instance_id
                """, connection))
            await using (var reader = await responders.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("gateway-hold-a", reader.GetString(0));
                Assert.Equal("Pending", reader.GetString(1));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("gateway-hold-b", reader.GetString(0));
                Assert.Equal("Stopped", reader.GetString(1));
                Assert.True(reader.GetBoolean(2));
                Assert.False(await reader.ReadAsync());
            }
            Assert.Equal("Consumed", await new NpgsqlCommand(
                "SELECT state FROM input.delivery WHERE event_id='hold-gift-1'", connection)
                .ExecuteScalarAsync());
            Assert.Equal(0L, Convert.ToInt64(await new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE event_id='hold-zombie-raw'", connection)
                .ExecuteScalarAsync()));
        }
    }

    /// <summary>
    /// FIX-END-09, the platform end call's pre-gate end to end: the end task is recorded at
    /// the stop-delivery issue — durably bound to the operation's barrier credential — and
    /// while the barrier stands incomplete (an unreachable authorization holder parks the
    /// phase) the platform receives nothing, however many executor passes list the task: no
    /// lease, no attempt, no call. The barrier completes — the holder's authorization lapses,
    /// the ladder authorizes — and the very next pass issues the task's first and only call.
    /// </summary>
    [Fact]
    public async Task APlatformEndTaskWaitsForTheBarrierToAuthorizeItsFirstCall()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        // The unreachable holder's advertised face: authorized and confirming its routing,
        // but the stop-delivery command cannot land, so the barrier parks on its answer.
        var deadEndpointPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var lateGatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
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
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-hold2");
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        var taskEnvironment = new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
            controlEndpoint, deadEndpointPort))
        {
            ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
            ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
            ["CHORUS_TASK_LEASE_SECONDS"] = "60",
            ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
            ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
        };
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            taskEnvironment, instanceId: "gateway-hold2-a");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-hold2"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-hold2" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "hold2-open-1", "hold2-live-1");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-hold2");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-hold2");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-hold2-1",
            OwnerInstanceId = "game-hold2", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-hold2-1", OwnerInstanceId = "game-hold2", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-hold2", TenantId = "tenant-a",
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
                RoomId = "room-hold2-1", GameId = "game-a", OwnerInstanceId = "game-hold2",
                Epoch = owner.Epoch, OperationId = "run-hold2", DurationTicks = 400, RandomSeed = 888,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The round settles and the operation begins; the task is recorded at the issue and
        // the phase parks on the unreachable holder — the state in which the whole gate is
        // under test: the task listed, the barrier not yet won.
        var parked = false;
        var parkedDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < parkedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM live.task
                              WHERE barrier_end_operation_id='room-hold2-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-hold2-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && reader.GetInt64(0) == 1 && reader.GetString(1) == "StopDeliveryIssued")
                { parked = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, "The end task was never recorded on the parked barrier.");

        // The gate holds across executor passes: the reconciler lists the task every 300ms
        // and issues nothing — no lease is even taken — while the barrier stands incomplete.
        await Task.Delay(2000);
        Assert.Equal(0, platform.Calls("room-hold2-1/end"));
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT status, attempt_count, lease_owner IS NULL FROM live.task
                WHERE barrier_end_operation_id='room-hold2-1/end'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Pending", reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.True(reader.GetBoolean(2));
        }

        // A second gateway joins with a real face: it hears the command, drains and acks —
        // and the phase still parks, because the unreachable holder owes its answer. Its ack
        // proves the strongest form of the gate's claim: an executor that HAS confirmed
        // delivery stopped still waits for the ladder's authorization before the platform
        // hears anything.
        await environment.StartAsync("Chorus.PlatformGateway", lateGatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                controlEndpoint, lateGatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
            },
            instanceId: "gateway-hold2-b");
        var secondAcked = false;
        var secondDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < secondDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT r.state FROM control.stop_delivery_responder r
                              WHERE r.end_operation_id='room-hold2-1/end'
                                AND r.process_instance_id='gateway-hold2-b'),
                           (SELECT eo.phase FROM control.end_operation eo
                              WHERE eo.room_id='room-hold2-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && reader.GetString(0) == "Stopped"
                    && reader.GetString(1) == "StopDeliveryIssued")
                { secondAcked = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(secondAcked,
            "The second gateway never acked, or its ack wrongly cleared the parked phase.");
        await Task.Delay(1000);
        Assert.Equal(0, platform.Calls("room-hold2-1/end"));

        // The barrier completes the END-08 way: the unreachable holder dies, its
        // authorization lapses past the stale window, the ladder authorizes — and the very
        // next executor pass issues the task's first call.
        environment.Kill("gateway-hold2-a");
        var called = false;
        var calledDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < calledDeadline)
        {
            if (platform.Calls("room-hold2-1/end") > 0) { called = true; break; }
            await Task.Delay(150);
        }
        Assert.True(called, "The authorized end task never called the platform.");

        // The task completed on the platform's own record — the one call — and the ladder
        // walks on: the completed answer is the close credential, so the phase records
        // PlatformClosed in the same walk (FIX-END-10).
        var completed = false;
        string lastTask = "", lastPhase = "";
        var completedDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < completedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-hold2-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-hold2-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync())
                {
                    lastTask = reader.GetString(0);
                    lastPhase = reader.GetString(1);
                    if (lastTask == "Completed" && lastPhase == "PlatformClosed")
                    { completed = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(completed, $"The end task never completed through to the closed phase " +
            $"(task: {lastTask}, phase: {lastPhase}). Gateway-b console tail:\n" +
            HostTailFor(environment, "gateway-hold2-b"));
        await Task.Delay(2000);
        Assert.Equal(1, platform.Calls("room-hold2-1/end"));
    }

    /// <summary>
    /// FIX-END-10, the close's scope end to end: the round's own end operation calls the
    /// platform with exactly the round-end category — the gameplay unbind and the live end
    /// are recorded as grounded non-calls on the task itself, because the mount and the
    /// live outlive the round and close in their own later operations — and the platform's
    /// success answer is the credential the ladder's PlatformClosed step records.
    /// </summary>
    [Fact]
    public async Task APlainRoundChangeClosesOnlyTheRoundOnThePlatform()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start();
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
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-close");
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
            },
            instanceId: "gateway-close-a");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-close"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-close" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "close-open-1", "close-live-1");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-close");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-close");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-close-1",
            OwnerInstanceId = "game-close", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-close-1", OwnerInstanceId = "game-close", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-close", TenantId = "tenant-a",
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
                RoomId = "room-close-1", GameId = "game-a", OwnerInstanceId = "game-close",
                Epoch = owner.Epoch, OperationId = "run-close", DurationTicks = 400, RandomSeed = 999,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The round settles, the barrier completes on the gateway's own ack, the authorized
        // task calls the platform once, and the success answer closes the operation — the
        // whole walk, with the patience a loaded shared runner owes it.
        var closed = false;
        string lastTask = "", lastPhase = "";
        var closedDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < closedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-close-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-close-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    lastTask = reader.GetString(0);
                    lastPhase = reader.GetString(1);
                    if (lastTask == "Completed" && lastPhase == "PlatformClosed") { closed = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(closed, $"The end task never closed the operation " +
            $"(task: {lastTask}, phase: {lastPhase}). Gateway console tail:\n" +
            HostTailFor(environment, "gateway-close-a"));

        // The platform heard exactly the round end — never an unbind, never a live end.
        Assert.Equal(1, platform.Calls("room-close-1/end"));
        Assert.All(platform.Kinds.Values, kind => Assert.Equal("end-round", kind));

        // The two non-calls are recorded with their ground on the task itself, and the
        // mount the live carries is still standing: the round changed, the live did not.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT desired->'categories'->>'roundEnd',
                       desired->'categories'->>'gameplayUnbind',
                       desired->'categories'->>'liveEnd',
                       observed->>'observedJson' IS NOT NULL OR observed IS NOT NULL
                FROM live.task WHERE barrier_end_operation_id='room-close-1/end'
                """, connection);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("apply", reader.GetString(0));
                Assert.StartsWith("not-required:", reader.GetString(1));
                Assert.StartsWith("not-required:", reader.GetString(2));
                Assert.True(reader.GetBoolean(3),
                    "The success answer's observation was never recorded.");
            }
            // The mount itself is untouched: the round changed, the live it carries did not.
            var stillBound = new NpgsqlCommand(
                "SELECT count(*) FROM live.binding WHERE binding_id=@binding", connection);
            stillBound.Parameters.AddWithValue("binding", host.BindingId);
            Assert.Equal(1L, Convert.ToInt64(await stillBound.ExecuteScalarAsync()));
        }
    }

    /// <summary>
    /// FIX-END-12 end to end: the release is the coordinator's own call, and nothing frees
    /// before the whole evidence stands. With the platform close unanswered — no executor
    /// ever claimed the end task — the round holds every seat it took (the room finishing,
    /// the participations active, the mount pointed, the ownership live), and the data
    /// plane refuses the release to an authorized caller naming where the ladder stands.
    /// The executor appears, the close lands, and the next coordinator pass finalizes with
    /// the end operation's own id: room, participations, pointer, held capacity and
    /// ownership free in the one finalize transaction — and the replayed call reads the
    /// same receipt back as a duplicate instead of releasing twice.
    /// </summary>
    [Fact]
    public async Task TheReleaseWaitsForThePlatformCloseThenFreesTheRoundAtomically()
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
        // The executor is deliberately absent at first: the end task is recorded and
        // authorized, but no gateway exists to claim it, so the platform close cannot land.
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
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-rel");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rel"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rel" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "rel-open-1", "rel-live-1");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-rel");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-rel-1",
            OwnerInstanceId = "game-rel", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-rel-1", OwnerInstanceId = "game-rel", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-rel", TenantId = "tenant-a",
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
                RoomId = "room-rel-1", GameId = "game-a", OwnerInstanceId = "game-rel",
                Epoch = owner.Epoch, OperationId = "run-rel", DurationTicks = 60, RandomSeed = 333,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The round settles and the ladder authorizes the platform end task — and there it
        // parks: recorded, authorized, unclaimed, because no executor exists yet.
        var parked = false;
        string parkedTask = "", parkedPhase = "";
        var parkedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < parkedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-rel-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-rel-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    parkedTask = reader.GetString(0);
                    parkedPhase = reader.GetString(1);
                    if (parkedTask == "Pending" && parkedPhase == "PlatformTaskAuthorized")
                    { parked = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, $"The end task never parked authorized (task: {parkedTask}, phase: {parkedPhase}).");

        // Nothing freed: the room holds every seat it took, and an authorized caller is
        // refused by the data plane's own evidence check — the ladder's phase is named.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var holds = new NpgsqlCommand("""
                SELECT (SELECT status FROM game.room WHERE room_id='room-rel-1'),
                       (SELECT count(*) FROM game.participation
                          WHERE room_id='room-rel-1' AND state='Active'),
                       (SELECT count(*) FROM live.binding
                          WHERE binding_id=@binding AND current_room_id='room-rel-1'),
                       (SELECT status FROM control.ownership
                          WHERE kind='room' AND entity_id='room-rel-1')
                """, connection);
            holds.Parameters.AddWithValue("binding", host.BindingId);
            await using var reader = await holds.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Finishing", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal("Active", reader.GetString(3));
        }
        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            new Chorus.Rpc.Settlement.SettlementClient(dataChannel).FinalizeRoundAsync(new Chorus.Rpc.FinalizeRoundRequest
            {
                RoomId = "room-rel-1", GameId = "game-a", TenantId = "tenant-a",
                OperationId = "room-rel-1/end"
            }, deadline: DateTime.UtcNow.AddSeconds(10)).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("PlatformTaskAuthorized", refused.Status.Detail);

        // The executor appears: the task claims, calls the platform once, and the close
        // lands — the release follows on the coordinator's next pass, under the end
        // operation's own business id.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
            },
            instanceId: "gateway-rel-a");
        var released = false;
        string lastPhase = "", lastStatus = "";
        var releaseDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < releaseDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT phase FROM control.end_operation WHERE room_id='room-rel-1'),
                           (SELECT status FROM game.room WHERE room_id='room-rel-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync())
                {
                    lastPhase = reader.GetString(0);
                    lastStatus = reader.GetString(1);
                    // FIX-END-14 walks the ladder on through Done in the same pass, so
                    // Released is transient — any of the post-release phases is the
                    // release's evidence (the frees asserted below are what it stands for).
                    if (lastPhase is "Released" or "Destroyed" or "Done" && lastStatus == "Settled")
                    { released = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(released, $"The release never landed (phase: {lastPhase}, status: {lastStatus})." +
            $" Gateway console tail:\n{HostTailFor(environment, "gateway-rel-a")}");
        Assert.Equal(1, platform.Calls("room-rel-1/end"));

        // One transaction freed every hold: participation closed (the held capacity with
        // it), pointer cleared, ownership withdrawn at the bumped epoch, and the settled
        // outbox effect stands.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var freed = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM game.participation
                          WHERE room_id='room-rel-1' AND state IN ('Active','Offline')),
                       (SELECT count(*) FROM live.binding
                          WHERE binding_id=@binding AND current_room_id IS NOT NULL),
                       (SELECT status||':'||epoch FROM control.ownership
                          WHERE kind='room' AND entity_id='room-rel-1'),
                       (SELECT kind FROM infra.outbox WHERE effect_id='room-rel-1/0/settle')
                """, connection);
            freed.Parameters.AddWithValue("binding", host.BindingId);
            await using var reader = await freed.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal($"Released:{owner.Epoch + 1}", reader.GetString(2));
            Assert.Equal("round.settled", reader.GetString(3));
        }

        // The receipt floor: the same business id reads the release back as a duplicate —
        // status settled, nothing freed a second time, the epoch unmoved.
        var replay = await new Chorus.Rpc.Settlement.SettlementClient(dataChannel).FinalizeRoundAsync(
            new Chorus.Rpc.FinalizeRoundRequest
            {
                RoomId = "room-rel-1", GameId = "game-a", TenantId = "tenant-a",
                OperationId = "room-rel-1/end"
            }, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.True(replay.Duplicate);
        Assert.Equal("Settled", replay.Status);
        await Task.Delay(1500);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var epoch = new NpgsqlCommand("""
                SELECT status,epoch FROM control.ownership
                WHERE kind='room' AND entity_id='room-rel-1'
                """, connection);
            await using var reader = await epoch.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Released", reader.GetString(0));
            Assert.Equal(owner.Epoch + 1, reader.GetInt64(1));
        }
    }

    /// <summary>
    /// FIX-END-13, the reopened queue: the mount's next-round eligibility follows the
    /// release receipt alone. While the platform close is still outstanding — failed or
    /// unknown, the executor absent — the queue refuses the very mount and the status face
    /// still names the room; once the release lands, the same mount queues again, the match
    /// coordinator pairs the two mounts into a fresh round, and the old round's late
    /// arrivals stay behind: the replay answers from its receipt with the first attribution
    /// and route version kept, and a push claiming the old round's generation cannot enter
    /// the new room.
    /// </summary>
    [Fact]
    public async Task TheReleasedMountRequeuesWhileLateOldEventsStayInTheOldRound()
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
        // The executor is deliberately absent at first — the platform close cannot land,
        // which is exactly the failed/unknown window that must keep the queue shut. The
        // match coordinator runs at a test pace and asks for a long round, so the fact can
        // act inside the paired room instead of racing its settlement.
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
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_ROUND_DURATION_TICKS"] = "600"
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
            clientTenant: "tenant-a", instanceId: "game-rel13");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rel13"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rel13" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountWithCredentialAsync(environment, simulator, "rel13-a", "live-rel13-a");
        var guest = await MountAsync(environment, simulator, "rel13-b", "live-rel13-b");
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel13");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-rel13");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);
        // The control plane's own data-plane leg: the coordinator drives the ladder and the
        // queue answers it, so the fact speaks with the same authority.
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new Chorus.Rpc.MatchQueue.MatchQueueClient(controlDataChannel);
        var input = new Chorus.Rpc.RoundInput.RoundInputClient(controlDataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-q-1",
            OwnerInstanceId = "game-rel13", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-q-1", OwnerInstanceId = "game-rel13", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-q-1", TenantId = "tenant-a",
            Participants =
            {
                new Chorus.Rpc.RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                },
                new Chorus.Rpc.RoundParticipantProto
                {
                    BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest"
                }
            }
        })).Status);
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        Assert.Equal("Running", (await new Chorus.Rpc.GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new Chorus.Rpc.PrepareRoomRequest
            {
                RoomId = "room-q-1", GameId = "game-a", OwnerInstanceId = "game-rel13",
                Epoch = owner.Epoch, OperationId = "run-q-1", DurationTicks = 120, RandomSeed = 1313,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    },
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue"
                    }
                }
            })).Status);

        // The status face carries the mount's round facts while the round stands — the
        // eligibility is computed from them on every read, never cached ahead of the store.
        using var statusCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-rel13-status");
        using var statusChannel = environment.Channel(environment.StreamerPort, statusCertificate, "streamer-server");
        var status = new Chorus.Rpc.StreamerSession.StreamerSessionClient(statusChannel);
        var inRound = await status.GameplayStatusAsync(new Chorus.Rpc.GameplayStatusRequest
            { TenantId = "tenant-a", AccessCredential = host.AccessCredential });
        Assert.Equal(host.BindingId, inRound.BindingId);
        Assert.Equal("room-q-1", inRound.CurrentRoomId);
        Assert.Equal(1L, inRound.RoundGeneration);

        // One gift rides the running round — its committed receipt is the dedup floor the
        // late replay must meet unchanged.
        var gift = await input.AcceptRoomEventAsync(new Chorus.Rpc.AcceptRoomEventRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-q-1",
            OwnerInstanceId = "game-rel13", Epoch = owner.Epoch, EventId = "rel13-gift-1",
            Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
            RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":300}"""
        });
        Assert.False(gift.Duplicate);
        Assert.Equal(1L, gift.RouteVersion);
        var consumed = false;
        var consumedDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < consumedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                if (Convert.ToString(await new NpgsqlCommand(
                    "SELECT state FROM input.delivery WHERE event_id='rel13-gift-1'", connection)
                    .ExecuteScalarAsync()) == "Consumed") { consumed = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(consumed, "The running round never consumed the gift.");

        // The round ends; the ladder parks authorized with the executor absent — the
        // platform close is neither succeeded nor observed: it simply cannot land. Two
        // mounts mean two recorded end tasks; both must stand Pending.
        var parked = false;
        long parkedTasks = 0, parkedPending = 0;
        string parkedPhase = "";
        var parkedDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < parkedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM live.task WHERE barrier_end_operation_id='room-q-1/end'),
                           (SELECT count(*) FROM live.task
                              WHERE barrier_end_operation_id='room-q-1/end' AND status='Pending'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-q-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2))
                {
                    parkedTasks = reader.GetInt64(0);
                    parkedPending = reader.GetInt64(1);
                    parkedPhase = reader.GetString(2);
                    if (parkedTasks == 2 && parkedPending == 2
                        && parkedPhase == "PlatformTaskAuthorized")
                    { parked = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, $"The end tasks never parked authorized (tasks: {parkedTasks}, " +
            $"pending: {parkedPending}, phase: {parkedPhase}).");

        // The queue stays shut for exactly this mount while the platform close is
        // outstanding, and the status face still names the room it holds.
        var refused = await Assert.ThrowsAsync<RpcException>(() => queue.EnqueueTicketAsync(
            new Chorus.Rpc.EnqueueTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId,
                SessionId = host.SessionId, PartitionKey = partition, OperationId = "rel13-enq-a"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("already participates", refused.Status.Detail);
        var stillInRound = await status.GameplayStatusAsync(new Chorus.Rpc.GameplayStatusRequest
            { TenantId = "tenant-a", AccessCredential = host.AccessCredential });
        Assert.Equal("room-q-1", stillInRound.CurrentRoomId);

        // The executor appears; the release lands on the coordinator's next pass under the
        // end operation's own business id.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
            },
            instanceId: "gateway-rel13");
        var released = false;
        string lastPhase = "", lastStatus = "";
        var releaseDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < releaseDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT phase FROM control.end_operation WHERE room_id='room-q-1'),
                           (SELECT status FROM game.room WHERE room_id='room-q-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync())
                {
                    lastPhase = reader.GetString(0);
                    lastStatus = reader.GetString(1);
                    // FIX-END-14 walks the ladder on through Done in the same pass, so any
                    // post-release phase is the release's evidence.
                    if (lastPhase is "Released" or "Destroyed" or "Done" && lastStatus == "Settled")
                    { released = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(released, $"The release never landed (phase: {lastPhase}, status: {lastStatus})." +
            $" Control tail:\n{HostTailFor(environment, "Chorus.ControlServer")}" +
            $"\nGateway tail:\n{HostTailFor(environment, "gateway-rel13")}");

        // The receipt is what reopened the mount: same binding, no room, the round
        // generation kept as the mount's monotone counter.
        var freed = await status.GameplayStatusAsync(new Chorus.Rpc.GameplayStatusRequest
            { TenantId = "tenant-a", AccessCredential = host.AccessCredential });
        Assert.Equal(host.BindingId, freed.BindingId);
        Assert.Equal("", freed.CurrentRoomId);
        Assert.Equal(1L, freed.RoundGeneration);

        // The same mounts queue again and the coordinator pairs them into a fresh round on
        // the same host — the eligibility the release opened is real, not cosmetic.
        await queue.EnqueueTicketAsync(new Chorus.Rpc.EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId,
            SessionId = host.SessionId, PartitionKey = partition, OperationId = "rel13-enq-a2"
        });
        await queue.EnqueueTicketAsync(new Chorus.Rpc.EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId,
            SessionId = guest.SessionId, PartitionKey = partition, OperationId = "rel13-enq-b2"
        });
        var match = new Chorus.Rpc.Match.MatchClient(controlDataChannel);
        var room2 = "";
        var pairedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < pairedDeadline && room2.Length == 0)
        {
            var hostTicket = await queue.GetTicketAsync(new Chorus.Rpc.GetTicketRequest
                { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
            var guestTicket = await queue.GetTicketAsync(new Chorus.Rpc.GetTicketRequest
                { TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId });
            if (hostTicket.Ticket.State == "Committed" && guestTicket.Ticket.State == "Committed"
                && hostTicket.Ticket.ReservationId.Length > 0
                && hostTicket.Ticket.ReservationId == guestTicket.Ticket.ReservationId)
            {
                var reservation = await match.GetReservationAsync(new Chorus.Rpc.GetReservationRequest
                {
                    TenantId = "tenant-a", GameId = "game-a",
                    ReservationId = hostTicket.Ticket.ReservationId
                });
                room2 = reservation.Reservation.RoomId;
            }
            await Task.Delay(200);
        }
        Assert.True(room2.Length > 0, "The coordinator never paired the released mounts." +
            $" Control tail:\n{HostTailFor(environment, "Chorus.ControlServer")}");
        var mountedAgain = false;
        var mountedDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < mountedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var binding = new NpgsqlCommand(
                    "SELECT current_room_id,round_generation FROM live.binding WHERE binding_id=@binding",
                    connection);
                binding.Parameters.AddWithValue("binding", host.BindingId);
                if (await binding.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && reader.GetString(0) == room2 && reader.GetInt64(1) == 2)
                { mountedAgain = true; break; }
            }
            await Task.Delay(150);
        }
        Assert.True(mountedAgain, "The paired round never took the mounts.");
        var requeued = await status.GameplayStatusAsync(new Chorus.Rpc.GameplayStatusRequest
            { TenantId = "tenant-a", AccessCredential = host.AccessCredential });
        Assert.Equal(room2, requeued.CurrentRoomId);
        Assert.Equal(2L, requeued.RoundGeneration);

        // The old round's late arrivals stay behind, three ways. First the floor: nothing
        // may enter the new room across all three probes.
        long room2Events = 0;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var before = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE attributed_room_id=@room", connection);
            before.Parameters.AddWithValue("room", room2);
            room2Events = Convert.ToInt64(await before.ExecuteScalarAsync());
        }

        // 1) The verbatim replay answers from its committed receipt — Duplicate, the first
        //    attribution's route version, no new row anywhere.
        var replay = await input.AcceptRoomEventAsync(new Chorus.Rpc.AcceptRoomEventRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-q-1",
            OwnerInstanceId = "game-rel13", Epoch = owner.Epoch, EventId = "rel13-gift-1",
            Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
            RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":300}"""
        });
        Assert.True(replay.Duplicate);
        Assert.Equal(1L, replay.RouteVersion);

        // 2) A fresh event for the old room: the old authority is gone with the release —
        //    the ownership fence refuses, and no trace is written.
        var staleRoom = await Assert.ThrowsAsync<RpcException>(() => input.AcceptRoomEventAsync(
            new Chorus.Rpc.AcceptRoomEventRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-q-1",
                OwnerInstanceId = "game-rel13", Epoch = owner.Epoch, EventId = "rel13-gift-9",
                Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
                RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":90}"""
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, staleRoom.StatusCode);
        Assert.Contains("ownership fence", staleRoom.Status.Detail);

        // 3) An old-claim push aimed at the NEW room: the current authority admits the
        //    caller, and the route fence still refuses — both generations named.
        string owner2 = ""; var epoch2 = 0L;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var authority = new NpgsqlCommand("""
                SELECT owner_process_instance_id,epoch FROM control.ownership
                WHERE kind='room' AND entity_id=@room
                """, connection);
            authority.Parameters.AddWithValue("room", room2);
            if (await authority.ExecuteReaderAsync() is { } authorityReader && await authorityReader.ReadAsync())
            {
                owner2 = authorityReader.GetString(0);
                epoch2 = authorityReader.GetInt64(1);
            }
        }
        var staleClaim = await Assert.ThrowsAsync<RpcException>(() => input.AcceptRoomEventAsync(
            new Chorus.Rpc.AcceptRoomEventRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = room2,
                OwnerInstanceId = owner2, Epoch = epoch2, EventId = "rel13-gift-10",
                Platform = "simulator", AppId = "app-1", BindingId = host.BindingId,
                RoundGeneration = 1, PayloadJson = """{"kind":"gift","amount":90}"""
            }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, staleClaim.StatusCode);
        Assert.Contains("claims round generation 1", staleClaim.Status.Detail);
        Assert.Contains("holds 2", staleClaim.Status.Detail);

        // Nothing entered the new round, the refused pushes left no trace, and the accepted
        // gift never moved: its attribution and its consumption stand exactly as first
        // committed.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var after = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE attributed_room_id=@room", connection);
            after.Parameters.AddWithValue("room", room2);
            Assert.Equal(room2Events, Convert.ToInt64(await after.ExecuteScalarAsync()));
            await using var traces = new NpgsqlCommand("""
                SELECT count(*) FROM input.event
                WHERE event_id IN ('rel13-gift-9','rel13-gift-10')
                """, connection);
            Assert.Equal(0L, Convert.ToInt64(await traces.ExecuteScalarAsync()));
            Assert.Equal("room-q-1", await new NpgsqlCommand(
                "SELECT attributed_room_id FROM input.event WHERE event_id='rel13-gift-1'", connection)
                .ExecuteScalarAsync());
            Assert.Equal("Consumed", await new NpgsqlCommand(
                "SELECT state FROM input.delivery WHERE event_id='rel13-gift-1'", connection)
                .ExecuteScalarAsync());
        }
    }

    /// <summary>
    /// FIX-END-14: the settled round's runtime entity leaves the game host through the
    /// ladder's own destroy step — and only through it. Before the release the normal
    /// destroy's ceiling (the ownership row's own epoch, which equals the running epoch)
    /// spares the live entity on the host; once the release lands, the coordinator
    /// broadcasts the destroy with the row's bumped epoch and the ladder walks to Done.
    /// The destroy moves no persistent fact — the ownership stays at exactly the release's
    /// bump, the result, the settle outbox and the operation's own record all stand — and
    /// a repeated destroy answers clean from the entity's absence, never a throw.
    /// </summary>
    [Fact]
    public async Task TheReleasedRoundDestroysItsEntityAfterTheReleaseAndKeepsTheRecords()
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
        // The executor is deliberately absent at first: the end task parks authorized and
        // the ladder holds short of the release, which is where the pre-release destroy
        // probe below does its work.
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
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-rel14");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rel14"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rel14" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "rel14-open-1", "rel14-live-1");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rel14");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-rel14");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-rel14-1",
            OwnerInstanceId = "game-rel14", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-rel14-1", OwnerInstanceId = "game-rel14", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-rel14", TenantId = "tenant-a",
            Participants =
            {
                new Chorus.Rpc.RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                }
            }
        })).Status);
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var gameHost = new Chorus.Rpc.GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(
            new Chorus.Rpc.PrepareRoomRequest
            {
                RoomId = "room-rel14-1", GameId = "game-a", OwnerInstanceId = "game-rel14",
                Epoch = owner.Epoch, OperationId = "run-rel14", DurationTicks = 60, RandomSeed = 333,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The round settles and the ladder parks authorized with no executor to claim the
        // end task — the release, and with it the destroy step, cannot run yet.
        var parked = false;
        string parkedTask = "", parkedPhase = "";
        var parkedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < parkedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-rel14-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-rel14-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    parkedTask = reader.GetString(0);
                    parkedPhase = reader.GetString(1);
                    if (parkedTask == "Pending" && parkedPhase == "PlatformTaskAuthorized")
                    { parked = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, $"The end task never parked authorized (task: {parkedTask}, phase: {parkedPhase}).");

        // The pre-release probe: the normal destroy's ceiling is the ownership row's own
        // epoch — which before the finalize equals the running epoch — so the live entity
        // is at, not below, the ceiling and every host spares it. The host's answer is the
        // entity's presence: only a live actor can be spared.
        var early = await gameHost.DestroyRoomAsync(new Chorus.Rpc.DestroyRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-rel14-1",
            ReleaseEpoch = owner.Epoch, EndOperationId = "room-rel14-1/end"
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.False(early.Destroyed);
        Assert.True(early.SparedNewGeneration);

        // The executor appears: the platform close lands, the release frees the round, and
        // the coordinator's own destroy step broadcasts at the row's bumped epoch — the
        // ladder now walks Released through Destroyed to Done in the same pass.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
            },
            instanceId: "gateway-rel14");
        var finished = false;
        string lastPhase = "", lastStatus = "";
        var finishDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < finishDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT phase FROM control.end_operation WHERE room_id='room-rel14-1'),
                           (SELECT status FROM game.room WHERE room_id='room-rel14-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync())
                {
                    lastPhase = reader.GetString(0);
                    lastStatus = reader.GetString(1);
                    if (lastPhase == "Done" && lastStatus == "Settled") { finished = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(finished, $"The ladder never finished through the destroy (phase: {lastPhase}, status: {lastStatus})." +
            $" Control tail:\n{HostTailFor(environment, "Chorus.ControlServer")}");
        Assert.Equal(1, platform.Calls("room-rel14-1/end"));

        // The destroy moved no persistent fact: the ownership stays at exactly the
        // release's bump (the destroy adds no epoch of its own), the result, the settle
        // outbox and the operation's own row — the dedup record every replay answers from —
        // all stand exactly as the release left them.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var records = new NpgsqlCommand("""
                SELECT (SELECT status||':'||epoch FROM control.ownership
                          WHERE kind='room' AND entity_id='room-rel14-1'),
                       (SELECT count(*) FROM game.result WHERE room_id='room-rel14-1'),
                       (SELECT kind FROM infra.outbox WHERE effect_id='room-rel14-1/0/settle'),
                       (SELECT phase FROM control.end_operation WHERE room_id='room-rel14-1')
                """, connection);
            await using var reader = await records.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal($"Released:{owner.Epoch + 1}", reader.GetString(0));
            Assert.True(reader.GetInt64(1) >= 1, "The committed result did not stand.");
            Assert.Equal("round.settled", reader.GetString(2));
            Assert.Equal("Done", reader.GetString(3));
        }

        // The repeated destroy answers from the entity's absence — clean, never a throw —
        // and the absence is itself the evidence the broadcast removed it: the same call
        // that spared the live entity before the release now finds nothing to spare.
        var repeat = await gameHost.DestroyRoomAsync(new Chorus.Rpc.DestroyRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-rel14-1",
            ReleaseEpoch = owner.Epoch + 1, EndOperationId = "room-rel14-1/end"
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.False(repeat.Destroyed);
        Assert.False(repeat.SparedNewGeneration);
    }

    /// <summary>
    /// FIX-END-10, the lost answer: the platform applies the end call but its reply never
    /// arrives — the executor's patience ends first, the task parks OutcomeUnknown, and the
    /// ladder may not record the close on an outcome nobody observed. The same operation id
    /// is the resolution: the status face's official record grounds the success, the task
    /// completes without a second call, and only then the phase records PlatformClosed.
    /// </summary>
    [Fact]
    public async Task ATimedOutPlatformCallNeverClosesAndTheOperationIdResolvesIt()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start();
        platform.ScriptOperation("room-resolve-1/end", new DevelopmentTaskPlatform.Script
        {
            Apply = true, HangAnswer = TimeSpan.FromSeconds(10)
        });
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
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-resolve");
        // The executor's patience is short and its deadline shorter: the unknown parks fast,
        // and the takeover query that resolves it is due almost immediately after.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "5",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "3"
            },
            instanceId: "gateway-resolve-a");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-resolve"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-resolve" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "resolve-open-1", "resolve-live-1");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-resolve");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-resolve");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-resolve-1",
            OwnerInstanceId = "game-resolve", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-resolve-1", OwnerInstanceId = "game-resolve", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-resolve", TenantId = "tenant-a",
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
                RoomId = "room-resolve-1", GameId = "game-a", OwnerInstanceId = "game-resolve",
                Epoch = owner.Epoch, OperationId = "run-resolve", DurationTicks = 400, RandomSeed = 1010,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The call leaves, its answer is lost, the task parks OutcomeUnknown — and the
        // ladder stands at the authorization: a timeout is not a success, however long the
        // wait.
        var parked = false;
        string parkedTask = "", parkedPhase = "";
        var parkedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < parkedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-resolve-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-resolve-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    parkedTask = reader.GetString(0);
                    parkedPhase = reader.GetString(1);
                    if (parkedTask == "OutcomeUnknown" && parkedPhase == "PlatformTaskAuthorized")
                    { parked = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, $"The lost answer never parked the task unknown " +
            $"(task: {parkedTask}, phase: {parkedPhase}). Gateway console tail:\n" +
            HostTailFor(environment, "gateway-resolve-a"));

        // The same operation id resolves it: the status face's official record grounds the
        // apply, the task completes without a second call, and the close follows.
        var resolved = false;
        string resolvedTask = "", resolvedPhase = "";
        var resolvedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < resolvedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-resolve-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-resolve-1')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    resolvedTask = reader.GetString(0);
                    resolvedPhase = reader.GetString(1);
                    if (resolvedTask == "Completed" && resolvedPhase == "PlatformClosed")
                    { resolved = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(resolved, $"The official record never resolved the unknown " +
            $"(task: {resolvedTask}, phase: {resolvedPhase}).");

        // The resolution was a query, never a re-call: the platform heard one execute and
        // at least one status, and the task's attempt count still says one.
        Assert.Equal(1, platform.Calls("room-resolve-1/end"));
        Assert.True(platform.StatusQueries("room-resolve-1/end") >= 1,
            "The resolution never asked the platform for its official record.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(1, Convert.ToInt32(await new NpgsqlCommand(
                "SELECT attempt_count FROM live.task WHERE barrier_end_operation_id='room-resolve-1/end'",
                connection).ExecuteScalarAsync()));
        }
    }

    /// <summary>
    /// FIX-END-10, the unanswerable unknown: the platform applies the call, loses the
    /// answer, and cannot answer for it either — and this platform's re-issue is not
    /// idempotent, so no blind retry is ever safe. The task escalates to NeedsAttention
    /// with its ground, the ladder stays open at the authorization, and the platform hears
    /// exactly the one call it already owed.
    /// </summary>
    [Fact]
    public async Task AnUnanswerableUnknownOnAnUnsafePlatformReachesNeedsAttention()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start();
        platform.ScriptOperation("room-attend-1/end", new DevelopmentTaskPlatform.Script
        {
            Apply = true, HangAnswer = TimeSpan.FromSeconds(10), StatusCannotAnswer = true
        });
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
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5"
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
            clientTenant: "tenant-a", instanceId: "game-attend");
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>(GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
            {
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
                // This platform's re-issue cannot be trusted: the only safe moves left are
                // the question (which it also cannot answer) and the escalation.
                ["CHORUS_DEV_PLATFORM_IDEMPOTENT_REISSUE"] = "false",
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "5",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "3"
            },
            instanceId: "gateway-attend-a");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-attend"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-attend" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "attend-open-1", "attend-live-1");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-attend");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-attend");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(dataChannel);

        var owner = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-attend-1",
            OwnerInstanceId = "game-attend", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = "room-attend-1", OwnerInstanceId = "game-attend", Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-attend", TenantId = "tenant-a",
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
                RoomId = "room-attend-1", GameId = "game-a", OwnerInstanceId = "game-attend",
                Epoch = owner.Epoch, OperationId = "run-attend", DurationTicks = 400, RandomSeed = 1111,
                TickMs = 25,
                Participants =
                {
                    new Chorus.Rpc.GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    }
                }
            })).Status);

        // The answer is lost and the status face cannot answer for it: the only remaining
        // safe move is the escalation — NeedsAttention, with the ground named.
        var escalated = false;
        string escalatedTask = "", escalatedPhase = "", escalatedError = "";
        var escalatedDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < escalatedDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT status FROM live.task WHERE barrier_end_operation_id='room-attend-1/end'),
                           (SELECT phase FROM control.end_operation WHERE room_id='room-attend-1'),
                           (SELECT last_error FROM live.task WHERE barrier_end_operation_id='room-attend-1/end')
                    """, connection);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    escalatedTask = reader.GetString(0);
                    escalatedPhase = reader.GetString(1);
                    escalatedError = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (escalatedTask == "NeedsAttention") { escalated = true; break; }
                }
            }
            await Task.Delay(150);
        }
        Assert.True(escalated, $"The unanswerable unknown never reached NeedsAttention " +
            $"(task: {escalatedTask}, phase: {escalatedPhase}). Gateway console tail:\n" +
            HostTailFor(environment, "gateway-attend-a"));
        Assert.Contains("not idempotent", escalatedError);

        // The attention is not a close: the ladder stands open at the authorization, the
        // platform heard exactly its one call, and the questions it could not answer are
        // on the record too.
        var stableDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < stableDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                Assert.Equal("PlatformTaskAuthorized", await new NpgsqlCommand(
                    "SELECT phase FROM control.end_operation WHERE room_id='room-attend-1'",
                    connection).ExecuteScalarAsync());
            }
            await Task.Delay(500);
        }
        Assert.Equal(1, platform.Calls("room-attend-1/end"));
        Assert.True(platform.StatusQueries("room-attend-1/end") >= 1,
            "The escalation never even asked the platform it cannot question.");
    }

    /// <summary>The captured console output of one started host, by instance id. A fact may
    /// kill and restart a host under the same instance id (M2-10 restarts the data plane);
    /// the newest start — the live process — is the console worth quoting.</summary>
    private static string HostOutputFor(RedesignM0HostTests.HostEnvironment environment, string instance) =>
        environment.HostOutput(environment.HostProcesses.Last(pair => pair.Instance == instance).Process);

    /// <summary>The console tail a failure message quotes, however short the console ran —
    /// an eager range on a quiet host would out-of-range before the assert could speak.</summary>
    internal static string HostTailFor(RedesignM0HostTests.HostEnvironment environment, string instance)
    {
        var output = HostOutputFor(environment, instance);
        return output.Length > 1200 ? output[^1200..] : output;
    }

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

    /// <summary>
    /// FIX-END-09's witness: an in-process HTTP platform that records every executed
    /// operation by id and answers each call as executed. One counter per operation id is
    /// the whole observation — the pre-gate's claim is about whether any call happens at
    /// all, and the exactly-once claim is the platform's own idempotent record.
    /// </summary>
    /// <summary>The platform as a recording and a script (FIX-END-10): every execute call is
    /// recorded with the kind its desired state declares; per operation the call may apply
    /// or not, its answer may be lost to a hang (the outcome the executor could not
    /// observe), and the status face either answers the official record under the same
    /// operation id or cannot answer at all — the three platform shapes the close's
    /// acceptance walks.</summary>
    /// <summary>A gateway with no ingress duty: it self-reports and watches routing only — the
    /// two legs whose rows the stop-delivery responder set reads.</summary>
    internal static Dictionary<string, string> GatewayEnvironment(
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
        var mount = await MountWithCredentialAsync(environment, simulator, openId, liveKey);
        return (mount.StreamerId, mount.SessionId, mount.BindingId);
    }

    /// <summary>The mount with its access credential, for facts that poll the status face
    /// as the streamer's own session would (FIX-END-13).</summary>
    private static async Task<(string StreamerId, string SessionId, string BindingId, string AccessCredential)>
        MountWithCredentialAsync(RedesignM0HostTests.HostEnvironment environment,
            DevelopmentPlatformSimulator simulator, string openId, string liveKey)
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
        return (login.StreamerId, login.SessionId, mounted.BindingId, login.AccessCredential);
    }
}
