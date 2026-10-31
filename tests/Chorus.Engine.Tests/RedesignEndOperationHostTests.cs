using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
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
/// arriving after the freeze cannot change the verdict.
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
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
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
        // operation row is the truth it continues under.
        environment.Kill("Chorus.ControlServer");
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        using var postChannel = environment.Channel(environment.ControlPort, gameCertificate,
            "control-server");
        var post = new Chorus.Rpc.RoundEnd.RoundEndClient(postChannel);
        var resumed = await post.GetEndStatusAsync(new Chorus.Rpc.GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-end-op-1"
        });
        Assert.True(resumed.Found);
        Assert.Equal("end-op-1", resumed.EndOperationId);
        Assert.Equal("ResultFrozen", resumed.Phase);
        Assert.Equal(1, resumed.RoundGeneration);
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
        Assert.Equal("ResultFrozen", untouched.Phase);
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
        // (FIX-END-04), not the sweep.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600"
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
                    PayloadJson = """{"kind":"gift","amount":300}"""
                }));
        Assert.Equal(StatusCode.InvalidArgument, closingError.StatusCode);
        Assert.Contains("does not accept input", closingError.Status.Detail);
        await AssertVerdictUnchangedAsync(environment, lastTick, stateVersion, resultJson, 2L);
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
        // begins from, and the grace keeps it just behind the freeze.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "150",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "1"
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
