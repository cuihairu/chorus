using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// M2 round admission and routing across real hosts: the control plane claims the room
/// ownership, the room owner admits a round over gRPC, the gateway feeds platform events into
/// the round inbox, and a DataServer crash between those steps loses nothing (receipt recovery).
/// </summary>
public sealed class RedesignM2HostTests
{
    [Fact]
    public async Task RoundAdmissionAndAttributionAcrossRealHostsSurviveDataServerRestart()
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
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-host", "live-m2-host");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-guest", "live-m2-guest");

        // The control plane claims the room ownership; the room owner admits the round.
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-1",
            OwnerInstanceId = "game-1", LeaseSeconds = 15
        });
        Assert.Equal(1, owner.Epoch);

        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var request = new StartRoundRequest
        {
            RoomId = "room-m2-1", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-1"
        };
        request.Participants.Add(new RoundParticipantProto
        {
            BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host"
        });
        request.Participants.Add(new RoundParticipantProto
        {
            BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest"
        });
        var admission = await rounds.StartRoundAsync(request);
        Assert.False(admission.Duplicate);
        Assert.Equal("Preparing", admission.Status);
        Assert.Equal(1, admission.RoundGeneration);
        Assert.Equal(owner.Epoch, admission.Epoch);

        // The gateway ingests platform events; the owner routes them into the round inbox.
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var ingest = new PlatformIngest.PlatformIngestClient(gatewayChannel);
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-1" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-1", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-host", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-2", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-host", PayloadJson = """{"kind":"gift","amount":50}"""
        });
        var ingested = await ingest.IngestBatchAsync(batch);
        Assert.False(ingested.Duplicate);
        Assert.All(ingested.Receipts, receipt => Assert.Equal(hostBinding, receipt.BindingId));

        var input = new RoundInput.RoundInputClient(gameChannel);
        var routed = await input.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-1", MaxEvents = 50
        });
        Assert.False(routed.Duplicate);
        Assert.Equal(2, routed.Events.Count);
        Assert.All(routed.Events, item =>
        {
            Assert.Equal("Routed", item.Disposition);
            Assert.Equal("room-m2-1", item.RoomId);
        });
        Assert.Equal([1L, 2L], routed.Events.Select(item => item.Sequence).OrderBy(s => s).ToArray());

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var room = new NpgsqlCommand(
                "SELECT status,epoch FROM game.room WHERE room_id='room-m2-1'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Preparing", reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using (var participations = new NpgsqlCommand(
                "SELECT count(*) FROM game.participation WHERE room_id='room-m2-1' AND state='Active'",
                connection))
                Assert.Equal(2L, Convert.ToInt64(await participations.ExecuteScalarAsync()));
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*),max(sequence) FROM input.delivery
                WHERE consumer_scope='room:room-m2-1' AND state='Pending' AND route_version=1
                """, connection))
            await using (var reader = await deliveries.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
        }

        // The DataServer dies between admission and any further work; a restarted process
        // replays the committed admission from the receipt instead of double-admitting.
        environment.Kill("Chorus.DataServer");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using var restartCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var restartChannel = environment.Channel(environment.DataPort, restartCertificate, "data-server");
        var restarted = new RoundStore.RoundStoreClient(restartChannel);
        var replayed = await restarted.StartRoundAsync(request);
        Assert.True(replayed.Duplicate);
        Assert.Equal(admission.RoundGeneration, replayed.RoundGeneration);
        var status = await restarted.GetRoundAsync(new GetRoundRequest { GameId = "game-a", RoomId = "room-m2-1" });
        Assert.True(status.Found);
        Assert.Equal("Preparing", status.Status);
        Assert.Equal(2, status.Participants.Count);

        // Only the control plane and the room owner admit rounds; a streamer role and another
        // tenant's owner are both rejected — the latter visibly at the participant fence.
        using var streamerRoleCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-role");
        using var streamerRoleChannel = environment.Channel(environment.DataPort, streamerRoleCertificate,
            "data-server");
        var deniedRole = await Assert.ThrowsAsync<RpcException>(() => new RoundStore.RoundStoreClient(
            streamerRoleChannel).StartRoundAsync(request).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, deniedRole.StatusCode);
        using var otherTenantCertificate = environment.Certificate("tenant-b", "game-server", "game-other");
        using var otherTenantChannel = environment.Channel(environment.DataPort, otherTenantCertificate,
            "data-server");
        var deniedTenant = await Assert.ThrowsAsync<RpcException>(() => new RoundStore.RoundStoreClient(
            otherTenantChannel).StartRoundAsync(request).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, deniedTenant.StatusCode);
    }

    [Fact]
    public async Task ClientCommandAcceptedDurablyOnlyWhileParticipationActive()
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
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (login, binding) = await MountAsync(environment, simulator, "open-m2-cli", "live-m2-cli");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var owner = await new ControlService.ControlServiceClient(controlChannel).ClaimOwnershipAsync(
            new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-2",
                OwnerInstanceId = "game-1", LeaseSeconds = 15
            });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var admission = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-2", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-1",
            Participants = { new RoundParticipantProto
            {
                BindingId = binding, ParticipantId = login.StreamerId, Role = "Host"
            } }
        });
        Assert.Equal(1, admission.RoundGeneration);

        // The streamer path accepts the command durably; the exact retry replays the receipt.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-path");
        using var streamerChannel = environment.Channel(environment.DataPort, streamerCertificate, "data-server");
        var input = new RoundInput.RoundInputClient(streamerChannel);
        var command = new ClientInputRequest
        {
            SessionId = login.SessionId, RoomId = "room-m2-2", RoomEpoch = owner.Epoch, GameId = "game-a",
            CommandId = "cmd-1", PayloadJson = """{"action":"boost","level":2}"""
        };
        var accepted = await input.AcceptClientInputAsync(command);
        Assert.False(accepted.Duplicate);
        Assert.Equal(1L, accepted.Sequence);
        Assert.Equal(1L, accepted.RouteVersion);
        var retry = await input.AcceptClientInputAsync(command);
        Assert.True(retry.Duplicate);
        Assert.Equal(1L, retry.Sequence);

        // Only the streamer path and the room owner carry client commands into the round.
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var denied = await Assert.ThrowsAsync<RpcException>(() => new RoundInput.RoundInputClient(
            gatewayChannel).AcceptClientInputAsync(command).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        // After the round aborts, the same valid session no longer commands this room.
        var aborted = await rounds.AbortRoundAsync(new AbortRoundRequest
        {
            RoomId = "room-m2-2", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            Reason = "host-left", OperationId = "abort-op-1"
        });
        Assert.Equal("Aborted", aborted.Status);
        var rejected = await Assert.ThrowsAsync<RpcException>(() =>
        {
            command.CommandId = "cmd-2";
            return input.AcceptClientInputAsync(command).ResponseAsync;
        });
        Assert.Equal(StatusCode.FailedPrecondition, rejected.StatusCode);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var events = new NpgsqlCommand("""
                SELECT count(*) FROM input.event
                WHERE platform='@chorus' AND disposition='Routed' AND attributed_room_id='room-m2-2'
                """, connection))
                Assert.Equal(1L, Convert.ToInt64(await events.ExecuteScalarAsync()));
            await using (var deliveries = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-m2-2'", connection))
                Assert.Equal(1L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
            // One receipt row: the retry replays cmd-1, the rejected cmd-2 rolled back.
            await using (var receipts = new NpgsqlCommand("""
                SELECT count(*) FROM infra.operation_receipt WHERE operation_kind='input.client'
                  AND target_id='room-m2-2'
                """, connection))
                Assert.Equal(1L, Convert.ToInt64(await receipts.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task FrameCommitsSurviveDataServerRestartAndReplayFromReceipts()
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
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (login, binding) = await MountAsync(environment, simulator, "open-m2-frame", "live-m2-frame");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var owner = await new ControlService.ControlServiceClient(controlChannel).ClaimOwnershipAsync(
            new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-3",
                OwnerInstanceId = "game-1", LeaseSeconds = 15
            });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-3", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-1",
            Participants = { new RoundParticipantProto
            {
                BindingId = binding, ParticipantId = login.StreamerId, Role = "Host"
            } }
        });
        // Room activation (Preparing → Running with the initial snapshot) lands in M2-3; the
        // frame boundary under test observes a Running room.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var activate = new NpgsqlCommand(
                "UPDATE game.room SET status='Running' WHERE room_id='room-m2-3'", connection);
            await activate.ExecuteNonQueryAsync();
        }

        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-1" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-1", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-frame", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-2", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-frame", PayloadJson = """{"kind":"gift","amount":50}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        var input = new RoundInput.RoundInputClient(gameChannel);
        await input.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-1", MaxEvents = 50
        });

        // The owner reads the round inbox and commits the first authoritative tick.
        var frames = new FrameStore.FrameStoreClient(gameChannel);
        var inbox = await frames.ReadInboxAsync(new ReadInboxRequest
        {
            RoomId = "room-m2-3", GameId = "game-a", MaxEvents = 50
        });
        Assert.Equal(0, inbox.Watermark);
        Assert.Equal(2, inbox.Items.Count);
        var commit = new CommitFrameRequest
        {
            RoomId = "room-m2-3", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            CommitId = "tick-1", PrevTick = 0, PrevStateVersion = 0,
            InputEventIds = { inbox.Items.Select(item => item.EventId) },
            StateHash = Google.Protobuf.ByteString.CopyFrom([1, 2, 3]),
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
        };
        commit.Effects.Add(new FrameEffectProto
        {
            Kind = "score", PayloadJson = """{"player":"p1","points":10}"""
        });
        var committed = await frames.CommitFrameAsync(commit);
        Assert.False(committed.Duplicate);
        Assert.Equal(1, committed.Tick);
        Assert.Equal(2, committed.Watermark);

        // The DataServer dies after the commit; a restarted process replays the stored frame
        // receipt, the inbox is drained, and only the room owner may commit further ticks.
        environment.Kill("Chorus.DataServer");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using var restartCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var restartChannel = environment.Channel(environment.DataPort, restartCertificate, "data-server");
        var restarted = new FrameStore.FrameStoreClient(restartChannel);
        var replayed = await restarted.CommitFrameAsync(commit);
        Assert.True(replayed.Duplicate);
        Assert.Equal(1, replayed.Tick);
        var drained = await restarted.ReadInboxAsync(new ReadInboxRequest
        {
            RoomId = "room-m2-3", GameId = "game-a", MaxEvents = 50
        });
        Assert.Equal(2, drained.Watermark);
        Assert.Empty(drained.Items);
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate,
            "data-server");
        var probe = await new FrameStore.FrameStoreClient(controlDataChannel).ReadInboxAsync(
            new ReadInboxRequest { RoomId = "room-m2-3", GameId = "game-a", MaxEvents = 50 });
        Assert.Equal(2, probe.Watermark);
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-role");
        using var streamerChannel = environment.Channel(environment.DataPort, streamerCertificate, "data-server");
        var deniedRole = await Assert.ThrowsAsync<RpcException>(() => new FrameStore.FrameStoreClient(
            streamerChannel).CommitFrameAsync(commit).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, deniedRole.StatusCode);
        var deniedControl = await Assert.ThrowsAsync<RpcException>(() => new FrameStore.FrameStoreClient(
            controlDataChannel).CommitFrameAsync(commit).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, deniedControl.StatusCode);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var frame = new NpgsqlCommand("""
                SELECT count(*),max(tick_id) FROM game.frame WHERE room_id='room-m2-3'
                """, connection))
            await using (var reader = await frame.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using (var room = new NpgsqlCommand(
                "SELECT status,last_tick,state_version FROM game.room WHERE room_id='room-m2-3'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Running", reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
            }
        }
    }

    [Fact]
    public async Task GameServerRunsRoundsToResultAndIsolatesModuleFailures()
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
        // The game host serves under a cluster certificate and speaks for tenant-a on the
        // data plane; it holds no database credentials of its own.
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-run-a", "live-m2-run-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-run-b", "live-m2-run-b");
        var (fault, faultBinding) = await MountAsync(environment, simulator, "open-m2-run-c", "live-m2-run-c");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        // Two rooms in one game-server process: a healthy round and a round whose module
        // fails deterministically from tick 2 (the documented fault seam).
        var healthyOwner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-4",
            OwnerInstanceId = "game-1", LeaseSeconds = 30
        });
        var healthyRequest = new StartRoundRequest
        {
            RoomId = "room-m2-4", OwnerInstanceId = "game-1", Epoch = healthyOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-4",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        };
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(healthyRequest)).Status);
        var faultOwner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-5",
            OwnerInstanceId = "game-1", LeaseSeconds = 30
        });
        var faultRequest = new StartRoundRequest
        {
            RoomId = "room-m2-5", OwnerInstanceId = "game-1", Epoch = faultOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-5",
            Participants =
            {
                new RoundParticipantProto { BindingId = faultBinding, ParticipantId = fault.StreamerId, Role = "Host" }
            }
        };
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(faultRequest)).Status);

        // The control plane hands the admitted rooms to the game host, which derives the
        // initial state, deposits the tick-0 snapshot, activates and starts ticking.
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        var healthyPrepare = new PrepareRoomRequest
        {
            RoomId = "room-m2-4", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = healthyOwner.Epoch,
            OperationId = "run-op-4", DurationTicks = 40, RandomSeed = 12345, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        };
        var prepared = await gameHost.PrepareRoomAsync(healthyPrepare);
        Assert.False(prepared.Duplicate);
        Assert.Equal("Running", prepared.Status);
        Assert.Equal(1L, prepared.StateVersion);
        var faultPrepare = new PrepareRoomRequest
        {
            RoomId = "room-m2-5", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = faultOwner.Epoch,
            OperationId = "run-op-5", DurationTicks = 40, RandomSeed = 999, TickMs = 25, FailAfterTick = 2,
            Participants =
            {
                new GameParticipantProto { BindingId = faultBinding, ParticipantId = fault.StreamerId, Faction = "red" }
            }
        };
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(faultPrepare)).Status);

        // Platform gifts for all three mounts; one routing pass fills both round inboxes.
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-1" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-run-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-run-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-c", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-run-c", PayloadJson = """{"kind":"gift","amount":5}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-1", MaxEvents = 50
        });

        // The faulted room parks visibly; the healthy round keeps ticking to its final frame —
        // isolation evidence: one process, two rooms, one failure, zero cross-damage (AT13).
        string healthy = "", faulted = "";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                healthy = await RoomStatusAsync(connection, "room-m2-4");
                faulted = await RoomStatusAsync(connection, "room-m2-5");
            }
            if (healthy == "Finishing" && faulted == "Suspended") break;
            await Task.Delay(200);
        }
        Assert.Equal("Finishing", healthy);
        Assert.Equal("Suspended", faulted);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            // The healthy round ran its full script: 40 frames, inputs consumed, result frozen.
            await using (var frames = new NpgsqlCommand("""
                SELECT count(*),max(tick_id) FROM game.frame WHERE room_id='room-m2-4'
                """, connection))
            await using (var reader = await frames.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(40L, reader.GetInt64(0));
                Assert.Equal(40L, reader.GetInt64(1));
            }
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*) FILTER (WHERE state='Consumed'),count(*),
                    max(sequence) FILTER (WHERE state='Consumed')
                FROM input.delivery WHERE consumer_scope='room:room-m2-4'
                """, connection))
            await using (var reader = await deliveries.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
                Assert.Equal(2L, reader.GetInt64(2));
            }
            await using (var cursor = new NpgsqlCommand("""
                SELECT consumed_contiguous_sequence FROM input.cursor WHERE consumer_scope='room:room-m2-4'
                """, connection))
                Assert.Equal(2L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
            await using (var room = new NpgsqlCommand(
                "SELECT last_tick,state_version,state_hash FROM game.room WHERE room_id='room-m2-4'", connection))
            {
                await using var reader = await room.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(40L, reader.GetInt64(0));
                Assert.Equal(41L, reader.GetInt64(1));
                Assert.False(reader.IsDBNull(2));
            }
            string resultJson;
            await using (var result = new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-m2-4'", connection))
                resultJson = (string)(await result.ExecuteScalarAsync())!;
            using (var document = System.Text.Json.JsonDocument.Parse(resultJson))
            {
                Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());
                var ranking = document.RootElement.GetProperty("ranking");
                Assert.Equal(2, ranking.GetArrayLength());
                Assert.Equal(100, ranking[0].GetProperty("score").GetInt64());
                Assert.Equal(30, ranking[1].GetProperty("score").GetInt64());
            }
            // The module's score projection left exactly two effect rows (one per faction).
            await using (var effects = new NpgsqlCommand("""
                SELECT count(*) FROM infra.outbox
                WHERE effect_id LIKE 'room-m2-4/%' AND kind='score'
                """, connection))
                Assert.Equal(2L, Convert.ToInt64(await effects.ExecuteScalarAsync()));
            // Both rooms pinned their tick-0 snapshot at prepare time (recovery anchor, M2-4).
            await using (var snapshots = new NpgsqlCommand("""
                SELECT count(*) FROM game.snapshot
                WHERE room_id IN ('room-m2-4','room-m2-5') AND tick_id=0 AND verified=false
                """, connection))
                Assert.Equal(2L, Convert.ToInt64(await snapshots.ExecuteScalarAsync()));
            // The faulted room died at its seam: at most one frame, suspended visibly.
            await using (var faultFrames = new NpgsqlCommand(
                "SELECT count(*),COALESCE(max(tick_id),0) FROM game.frame WHERE room_id='room-m2-5'", connection))
            await using (var reader = await faultFrames.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetInt64(0) <= 1);
                Assert.True(reader.GetInt64(1) <= 1);
            }
            await using (var suspendOutbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-m2-5/0/suspend'", connection))
                Assert.Equal("round.suspended", await suspendOutbox.ExecuteScalarAsync());
        }

        // A second PrepareRoom for a room this process already knows answers duplicate.
        var duplicate = await gameHost.PrepareRoomAsync(healthyPrepare);
        Assert.True(duplicate.Duplicate);
        // Only the control plane and game hosts prepare rooms; a finished room recovers its
        // tail without restarting battle ticks.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-role");
        using var streamerHostChannel = environment.Channel(environment.GamePort, streamerCertificate,
            "game-server");
        var denied = await Assert.ThrowsAsync<RpcException>(() => new GameHost.GameHostClient(
            streamerHostChannel).PrepareRoomAsync(healthyPrepare).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        var tail = await gameHost.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-m2-4", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = healthyOwner.Epoch, TickMs = 25
        });
        Assert.True(tail.Recovered);
        Assert.Equal("Finishing", tail.Status);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var frames = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id='room-m2-4'", connection);
            var afterTail = Convert.ToInt64(await frames.ExecuteScalarAsync());
            Assert.Equal(40L, afterTail); // no battle tick was restarted
        }
    }

    [Fact]
    public async Task KilledGameServerRecoversAtTheHeadAndRunsToTheSameResult()
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
        var gameEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            gameEnvironment, clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-run-d", "live-m2-6-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-run-e", "live-m2-6-b");
        var (twinHost, twinHostBinding) = await MountAsync(environment, simulator, "open-m2-run-f", "live-m2-7-a");
        var (twinGuest, twinGuestBinding) = await MountAsync(environment, simulator, "open-m2-run-g", "live-m2-7-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
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

        // The victim round runs long enough to pass its tick-100 checkpoint before the crash;
        // the recovered tail plus the concurrent twin can outlast a bare lease under load.
        using var victimClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-6",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = victimClaim.Owner;
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-6", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-6",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        // The gifts are routed while the room is still Preparing so the crash loop below can
        // begin sampling the tick the instant the room starts. Routing before Prepare keeps the
        // starved setup (Ingest+Route) from outliving the room's whole 5s run on a loaded CI
        // runner — which would let the room finish to tick 200 before the loop ever sampled a
        // kill point in [130,199] (the acceptance window for a live mid-battle crash).
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-6" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-6a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-6-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-6b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-6-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await ingest.IngestBatchAsync(batch);
        await routing.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-6", MaxEvents = 50
        });
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-6", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-6", DurationTicks = 200, RandomSeed = 777, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);

        // Crash the game host mid-battle, past the tick-100 checkpoint (AT19 kill point).
        // The patience carries the pacing (2026-10-29: the full gate's shared Postgres sagged
        // per-tick commits to a few ticks per second); the floor stays the kill-point fact.
        long killTick = 0;
        var crashDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < crashDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, killTick) = await FrameStatsAsync(connection, "room-m2-6");
            }
            if (killTick >= 130) break;
            await Task.Delay(100);
        }
        Assert.InRange(killTick, 130, 199);
        environment.Kill("Chorus.GameServer");
        // The actor keeps committing until the process actually dies; the head after the kill
        // is the ground truth the recovery must land on.
        long headTick;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            (_, headTick) = await FrameStatsAsync(connection, "room-m2-6");
        }
        Assert.InRange(headTick, killTick, 199);

        // A fresh process on the same port replays the frames after the tick-100 snapshot,
        // verifies every state hash, pins the head and resumes ticking.
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            gameEnvironment, clientTenant: "tenant-a");
        var recovered = await gameHost.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-m2-6", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = owner.Epoch, TickMs = 25
        });
        Assert.True(recovered.Recovered);
        Assert.Equal("Running", recovered.Status);
        Assert.Equal(100L, recovered.SnapshotTick);
        Assert.Equal(headTick, recovered.LastTick);
        Assert.Equal((int)(headTick - 100), recovered.ReplayedFrames);

        // An untouched twin with the same module configuration and the same gifts is the
        // determinism reference: crash and recovery must not move the outcome.
        using var twinClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-7",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var twinOwner = twinClaim.Owner;
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-7", OwnerInstanceId = "game-1", Epoch = twinOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-7",
            Participants =
            {
                new RoundParticipantProto { BindingId = twinHostBinding, ParticipantId = twinHost.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = twinGuestBinding, ParticipantId = twinGuest.StreamerId, Role = "Guest" }
            }
        })).Status);
        // Same input ordering as the victim: the gifts land in the twin's inbox while it is
        // still Preparing, so both rooms run the identical script from the first tick.
        var twinBatch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-7" };
        twinBatch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-7a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-7-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        twinBatch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-7b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-7-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await ingest.IngestBatchAsync(twinBatch);
        await routing.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-7", MaxEvents = 50
        });
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-7", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = twinOwner.Epoch,
            OperationId = "run-op-7", DurationTicks = 200, RandomSeed = 777, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = twinHostBinding, ParticipantId = twinHost.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = twinGuestBinding, ParticipantId = twinGuest.StreamerId, Faction = "blue" }
            }
        })).Status);

        var finishDeadline = DateTime.UtcNow.AddSeconds(30);
        string victim = "", twin = "";
        while (DateTime.UtcNow < finishDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                victim = await RoomStatusAsync(connection, "room-m2-6");
                twin = await RoomStatusAsync(connection, "room-m2-7");
            }
            if (victim == "Finishing" && twin == "Finishing") break;
            await Task.Delay(200);
        }
        Assert.Equal("Finishing", victim);
        Assert.Equal("Finishing", twin);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            // The recovered round completed its script: contiguous ticks 1..200, no gaps and
            // no restart from zero — the resume continued at killTick + 1.
            var (victimFrames, victimMax) = await FrameStatsAsync(connection, "room-m2-6");
            Assert.Equal(200L, victimFrames);
            Assert.Equal(200L, victimMax);
            var (twinFrames, twinMax) = await FrameStatsAsync(connection, "room-m2-7");
            Assert.Equal(200L, twinFrames);
            Assert.Equal(200L, twinMax);
            await using (var room = new NpgsqlCommand(
                "SELECT last_tick,state_version FROM game.room WHERE room_id='room-m2-6'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(200L, reader.GetInt64(0));
                Assert.Equal(201L, reader.GetInt64(1));
            }
            // Crash and recovery did not move the deterministic outcome (AT19).
            string victimResult, twinResult;
            await using (var result = new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-m2-6'", connection))
                victimResult = (string)(await result.ExecuteScalarAsync())!;
            await using (var result = new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-m2-7'", connection))
                twinResult = (string)(await result.ExecuteScalarAsync())!;
            Assert.Equal(twinResult, victimResult);
            using (var document = System.Text.Json.JsonDocument.Parse(victimResult))
            {
                Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());
                Assert.Equal(100, document.RootElement.GetProperty("ranking")[0].GetProperty("score").GetInt64());
                Assert.Equal(30, document.RootElement.GetProperty("ranking")[1].GetProperty("score").GetInt64());
            }
            // Snapshot chain: the prepare anchor, the tick-100 checkpoint, the store-verified
            // recovery head at the crash tick, and the final checkpoint.
            await using (var snapshots = new NpgsqlCommand(
                "SELECT tick_id,verified FROM game.snapshot WHERE room_id='room-m2-6' ORDER BY tick_id",
                connection))
            await using (var reader = await snapshots.ExecuteReaderAsync())
            {
                var rows = new List<(long, bool)>();
                while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetBoolean(1)));
                Assert.Equal([(0L, false), (100L, false), (headTick, true), (200L, false)], rows);
            }
            // The inputs survived the crash and were consumed exactly once.
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*) FILTER (WHERE state='Consumed'),count(*)
                FROM input.delivery WHERE consumer_scope='room:room-m2-6'
                """, connection))
                Assert.Equal(2L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task CorruptSnapshotsFallBackToTheAnchorOrSuspendVisibly()
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
        var gameEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            gameEnvironment, clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-run-h", "live-m2-8-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-run-i", "live-m2-8-b");
        var (lone, loneBinding) = await MountAsync(environment, simulator, "open-m2-run-j", "live-m2-9-a");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
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

        // A long round first; once it passes tick 130 a short round joins so both are
        // mid-battle at the shared crash.
        using var longClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-8",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = longClaim.Owner;
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-8", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-8",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        // Same ordering as the AT19 crash test: the gifts are routed into the Preparing
        // room's inbox so the kill-point polls below start sampling the instant the room
        // starts, without a starved Ingest+Route stretch in which the room could finish.
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-8" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-8a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-8-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-8b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-8-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await ingest.IngestBatchAsync(batch);
        await routing.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-8", MaxEvents = 50
        });
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-8", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-8", DurationTicks = 200, RandomSeed = 555, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            var tickDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < tickDeadline)
            {
                (_, var longTick) = await FrameStatsAsync(connection, "room-m2-8");
                if (longTick >= 130) break;
                await Task.Delay(100);
            }
        }
        // The short round joins the same host; the crash must catch both mid-battle.
        var loneOwner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-9",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-9", OwnerInstanceId = "game-1", Epoch = loneOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-9",
            Participants =
            {
                new RoundParticipantProto { BindingId = loneBinding, ParticipantId = lone.StreamerId, Role = "Host" }
            }
        })).Status);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-9", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = loneOwner.Epoch,
            OperationId = "run-op-9", DurationTicks = 60, RandomSeed = 888, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = loneBinding, ParticipantId = lone.StreamerId, Faction = "red" }
            }
        })).Status);

        long longKill = 0, shortKill = 0;
        var crashDeadline2 = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < crashDeadline2)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, longKill) = await FrameStatsAsync(connection, "room-m2-8");
                (_, shortKill) = await FrameStatsAsync(connection, "room-m2-9");
            }
            if (longKill >= 160 && shortKill >= 30) break;
            await Task.Delay(100);
        }
        Assert.InRange(longKill, 160, 199);
        Assert.InRange(shortKill, 30, 59);
        environment.Kill("Chorus.GameServer");
        // The heads at actual process death are the recovery ground truth. A frame commit
        // that was already in flight to the DataServer can land after the kill signal —
        // the owner is dead but the store transaction finishes on its own — so the heads
        // are polled to stability before they are frozen as the truth (a single fast read
        // races that last landing frame on a quick host).
        long longHead = -1, shortHead = -1;
        var settled = false;
        var settleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < settleDeadline && !settled)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, var nextLong) = await FrameStatsAsync(connection, "room-m2-8");
                (_, var nextShort) = await FrameStatsAsync(connection, "room-m2-9");
                settled = longHead >= 0 && nextLong == longHead && nextShort == shortHead;
                longHead = nextLong;
                shortHead = nextShort;
            }
            if (!settled) await Task.Delay(150);
        }
        Assert.InRange(longHead, longKill, 199);
        Assert.InRange(shortHead, shortKill, 59);

        // Disk-level corruption between crash and recovery: the long round's newest snapshot
        // loses its hash commitment; the short round's only anchor becomes unreadable.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var corruptHash = new NpgsqlCommand("""
                UPDATE game.snapshot SET payload_hash=decode('0000000000000000000000000000000000000000000000000000000000000000','hex')
                WHERE room_id='room-m2-8' AND tick_id=100
                """, connection))
                await corruptHash.ExecuteNonQueryAsync();
            await using (var corruptPayload = new NpgsqlCommand("""
                UPDATE game.snapshot SET payload=convert_to('corrupted','UTF8')
                WHERE room_id='room-m2-9' AND tick_id=0
                """, connection))
                await corruptPayload.ExecuteNonQueryAsync();
        }

        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            gameEnvironment, clientTenant: "tenant-a");
        // The hash mismatch demotes the newest snapshot; the tick-0 anchor replays the whole
        // battle with per-frame verification and the round still finishes.
        var fallback = await gameHost.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-m2-8", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = owner.Epoch, TickMs = 25
        });
        Assert.True(fallback.Recovered);
        Assert.Equal("Running", fallback.Status);
        Assert.Equal(0L, fallback.SnapshotTick);
        Assert.Equal(longHead, fallback.LastTick);
        Assert.Equal((int)longHead, fallback.ReplayedFrames);
        // No snapshot survives for the short round: it parks visibly instead of guessing.
        var parked = await gameHost.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-m2-9", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = loneOwner.Epoch, TickMs = 25
        });
        Assert.False(parked.Recovered);
        Assert.Equal("Suspended", parked.Status);

        var finishDeadline = DateTime.UtcNow.AddSeconds(30);
        string longRoom = "";
        while (DateTime.UtcNow < finishDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                longRoom = await RoomStatusAsync(connection, "room-m2-8");
            }
            if (longRoom == "Finishing") break;
            await Task.Delay(200);
        }
        Assert.Equal("Finishing", longRoom);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            var (frames, maxTick) = await FrameStatsAsync(connection, "room-m2-8");
            Assert.Equal(200L, frames);
            Assert.Equal(200L, maxTick);
            string fallbackResult;
            await using (var result = new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-m2-8'", connection))
                fallbackResult = (string)(await result.ExecuteScalarAsync())!;
            using (var document = System.Text.Json.JsonDocument.Parse(fallbackResult))
                Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());
            // The suspended room keeps exactly the frames it had at the crash.
            var (shortFrames, shortMax) = await FrameStatsAsync(connection, "room-m2-9");
            Assert.Equal(shortHead, shortFrames);
            Assert.Equal(shortHead, shortMax);
            await using (var shortStatus = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-m2-9'", connection))
                Assert.Equal("Suspended", await shortStatus.ExecuteScalarAsync());
            await using (var suspendOutbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-m2-9/0/suspend'", connection))
                Assert.Equal("round.suspended", await suspendOutbox.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task ADeeplyCorruptWindowFallsBackBeyondTheDefaultPair()
    {
        // FIX-READ-04, the host leg: every anchor above the admission row is corrupt on
        // disk. The recovery must deepen the read on demand — past two whole windows —
        // replay the whole battle from the tick-0 anchor with per-frame verification, and
        // finish the round, never park while an untried anchor remains. The 400-plus
        // frame replay also travels as bounded pages (FIX-READ-05): no single reply on
        // the wire carries the whole plan.
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
        var gameEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            gameEnvironment, clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-run-l", "live-m2-11-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-run-m", "live-m2-11-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);

        // A 300-tick round anchors at 0, 100 and 200; the kill lands mid-battle past the
        // last anchor so all three exist at the crash.
        using var claim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-11",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = claim.Owner;
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-11", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-11",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-11", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-11", DurationTicks = 500, RandomSeed = 999, TickMs = 10,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        long head = 0;
        var crashDeadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < crashDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, head) = await FrameStatsAsync(connection, "room-m2-11");
            }
            if (head >= 420) break;
            await Task.Delay(100);
        }
        Assert.InRange(head, 420, 499);
        environment.Kill("Chorus.GameServer");
        // The head at actual process death is the recovery ground truth. A frame commit
        // already in flight to the DataServer can land after the kill signal, so the head
        // is polled to stability before it becomes the truth.
        var headSettled = false;
        var headDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < headDeadline && !headSettled)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                (_, var next) = await FrameStatsAsync(connection, "room-m2-11");
                headSettled = next == head;
                head = next;
            }
            if (!headSettled) await Task.Delay(150);
        }
        Assert.InRange(head, 420, 499);

        // Both anchors of the normal window are corrupt — one lost its bytes, one lost its
        // hash commitment — while the tick-0 anchor below them stays honest.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var anchors = new NpgsqlCommand(
                "SELECT count(*) FROM game.snapshot WHERE room_id='room-m2-11'", connection))
                Assert.Equal(5L, Convert.ToInt64(await anchors.ExecuteScalarAsync()));
            // Every anchor above the admission row dies, in both corruption shapes — the
            // fallback has to deepen past two windows before the tick-0 anchor appears.
            await using (var corruptPayload = new NpgsqlCommand("""
                UPDATE game.snapshot SET payload=convert_to('corrupted','UTF8')
                WHERE room_id='room-m2-11' AND tick_id IN (200,400)
                """, connection))
                await corruptPayload.ExecuteNonQueryAsync();
            await using (var corruptHash = new NpgsqlCommand("""
                UPDATE game.snapshot SET payload_hash=decode('0000000000000000000000000000000000000000000000000000000000000000','hex')
                WHERE room_id='room-m2-11' AND tick_id IN (100,300)
                """, connection))
                await corruptHash.ExecuteNonQueryAsync();
        }

        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            gameEnvironment, clientTenant: "tenant-a");
        var fallback = await gameHost.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-m2-11", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = owner.Epoch, TickMs = 10
        });
        Assert.True(fallback.Recovered);
        Assert.Equal("Running", fallback.Status);
        // Only the deepened read can see the tick-0 anchor: the default window's two
        // candidates are both corrupt, so this answer is the on-demand fallback's.
        Assert.Equal(0L, fallback.SnapshotTick);
        Assert.Equal(head, fallback.LastTick);
        Assert.Equal((int)head, fallback.ReplayedFrames);

        var finishDeadline = DateTime.UtcNow.AddSeconds(60);
        string roomStatus = "";
        while (DateTime.UtcNow < finishDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                roomStatus = await RoomStatusAsync(connection, "room-m2-11");
            }
            if (roomStatus == "Finishing") break;
            await Task.Delay(200);
        }
        Assert.Equal("Finishing", roomStatus);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            var (frames, maxTick) = await FrameStatsAsync(connection, "room-m2-11");
            Assert.Equal(500L, frames);
            Assert.Equal(500L, maxTick);
            string resultJson;
            await using (var result = new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-m2-11'", connection))
                resultJson = (string)(await result.ExecuteScalarAsync())!;
            using var document = System.Text.Json.JsonDocument.Parse(resultJson);
            Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("winner").GetString()));
            // The recovery never marks unverified material as success: the head it replayed
            // to is pinned as a fresh store-verified snapshot.
            await using (var pinned = new NpgsqlCommand(
                "SELECT count(*) FROM game.snapshot WHERE room_id='room-m2-11' AND tick_id=@head AND verified",
                connection))
            {
                pinned.Parameters.AddWithValue("head", head);
                Assert.Equal(1L, Convert.ToInt64(await pinned.ExecuteScalarAsync()));
            }
        }
    }

    [Fact]
    public async Task SettledRoundFinalizesFreesMountsAndOpensTheNextRound()
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
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-run-k", "live-m2-10-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-run-l", "live-m2-10-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
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
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");

        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-10",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-10", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-10",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-10", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-10", DurationTicks = 40, RandomSeed = 4242, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-10" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-10a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-10-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-10b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-10-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await ingest.IngestBatchAsync(batch);
        await routing.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-10", MaxEvents = 50
        });

        // The round finishes and the host settles the manifest: every grant lands its one
        // ledger line and the projection follows (AT20 settlement half).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        long grantedCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var grants = new NpgsqlCommand("""
                    SELECT count(*) FROM settlement.grant WHERE room_id='room-m2-10' AND status='Granted'
                    """, connection);
                grantedCount = Convert.ToInt64(await grants.ExecuteScalarAsync());
            }
            if (grantedCount == 2) break;
            await Task.Delay(200);
        }
        Assert.Equal(2L, grantedCount);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var ledger = new NpgsqlCommand("""
                SELECT count(DISTINCT operation_id),count(*) FROM player.ledger
                WHERE reason LIKE 'settlement:room-m2-10/%'
                """, connection))
            {
                await using var reader = await ledger.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
            await using (var balances = new NpgsqlCommand("""
                SELECT balance FROM player.asset
                WHERE tenant_id='tenant-a' AND game_id='game-a' AND asset_id='points' AND player_id=@player
                """, connection))
            {
                balances.Parameters.AddWithValue("player", host.StreamerId);
                Assert.Equal(100L, Convert.ToInt64(await balances.ExecuteScalarAsync()));
            }
        }
        var board = await new Settlement.SettlementClient(controlDataChannel).ReadLeaderboardAsync(
            new ReadLeaderboardRequest
            {
                GameId = "game-a", BoardId = "points", MaxEntries = 10, TenantId = "tenant-a"
            });
        Assert.Equal(2, board.Entries.Count);
        Assert.Equal(host.StreamerId, board.Entries[0].PlayerId);
        Assert.Equal(100L, board.Entries[0].Score);
        Assert.Equal(30L, board.Entries[1].Score);

        // A data-plane restart between settlement and finalization changes nothing: the
        // repeated result send replays the manifest progress (AT27 settlement part).
        environment.Kill("Chorus.DataServer");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        var settleRetry = await new Settlement.SettlementClient(gameChannel).CommitResultAsync(
            new CommitResultRequest { RoomId = "room-m2-10", GameId = "game-a", OperationId = "settle-10" });
        Assert.True(settleRetry.Duplicate);
        Assert.Equal(0, settleRetry.GrantsPending);
        Assert.Equal(2, settleRetry.GrantsGranted);
        // A tenant caller cannot act for another tenant, and a cluster caller must name one.
        var foreignTenant = await Assert.ThrowsAsync<RpcException>(() =>
            new Settlement.SettlementClient(gameChannel).CommitResultAsync(new CommitResultRequest
            {
                RoomId = "room-m2-10", GameId = "game-a", OperationId = "settle-foreign",
                TenantId = "tenant-b"
            }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, foreignTenant.StatusCode);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand("""
                SELECT count(*) FROM player.ledger WHERE reason LIKE 'settlement:room-m2-10/%'
                """, connection);
            Assert.Equal(2L, Convert.ToInt64(await ledger.ExecuteScalarAsync()));
        }

        // The control plane finalizes: Settled, participations closed, mounts freed,
        // ownership withdrawn — and the same mounts open the next round (AT20).
        var finalized = await new Settlement.SettlementClient(controlDataChannel).FinalizeRoundAsync(
            new FinalizeRoundRequest
            {
                RoomId = "room-m2-10", GameId = "game-a", OperationId = "finalize-10", TenantId = "tenant-a"
            });
        Assert.Equal("Settled", finalized.Status);
        Assert.Equal(owner.Epoch + 1, finalized.Epoch);
        var nextOwner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-11",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-11", OwnerInstanceId = "game-1", Epoch = nextOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-11",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-11", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = nextOwner.Epoch,
            OperationId = "run-op-11", DurationTicks = 20, RandomSeed = 4242, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        var nextDeadline = DateTime.UtcNow.AddSeconds(20);
        string next = "";
        while (DateTime.UtcNow < nextDeadline)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                next = await RoomStatusAsync(connection, "room-m2-11");
                await using var binding = new NpgsqlCommand(
                    "SELECT round_generation FROM live.binding WHERE binding_id=@binding", connection);
                binding.Parameters.AddWithValue("binding", hostBinding);
                if (Convert.ToInt64(await binding.ExecuteScalarAsync()) != 2L) next = "";
            }
            if (next == "Finishing") break;
            await Task.Delay(200);
        }
        Assert.Equal("Finishing", next);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var room = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-m2-10'", connection);
            Assert.Equal("Settled", await room.ExecuteScalarAsync());
            await using var participation = new NpgsqlCommand("""
                SELECT count(*) FROM game.participation WHERE room_id='room-m2-10' AND state<>'Closed'
                """, connection);
            Assert.Equal(0L, Convert.ToInt64(await participation.ExecuteScalarAsync()));
            await using var ownership = new NpgsqlCommand(
                "SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id='room-m2-10'",
                connection);
            await using (var reader = await ownership.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Released", reader.GetString(0));
                Assert.Equal(owner.Epoch + 1, reader.GetInt64(1));
            }
            await using var outbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-m2-10/0/settle'", connection);
            Assert.Equal("round.settled", await outbox.ExecuteScalarAsync());
        }
        // Only the control plane and game hosts finalize; a streamer role is denied.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-role");
        using var streamerChannel = environment.Channel(environment.DataPort, streamerCertificate, "data-server");
        var denied = await Assert.ThrowsAsync<RpcException>(() => new Settlement.SettlementClient(
            streamerChannel).FinalizeRoundAsync(new FinalizeRoundRequest
        {
            RoomId = "room-m2-10", GameId = "game-a", OperationId = "finalize-denied"
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    [Fact]
    public async Task SlowSubscribersAreRebaselinedWhileFastSubscribersStreamContiguousDeltas()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
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
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-sync-a", "live-m2-12-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-sync-b", "live-m2-12-b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        // The 600-tick room is work-bound (two data-plane calls per tick) and can outrun the
        // 60-second lease cap: an expired lease fences the owner's frame commits out mid-round.
        // The control-plane claimant renews the active owner's lease (architecture 4.2).
        using var roundClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-12",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = roundClaim.Owner;
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-12", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-12",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-12", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-12", DurationTicks = 600, RandomSeed = 2424, TickMs = 5,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-12" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-12a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-12-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-12b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-12-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-12", MaxEvents = 50
        });

        // Two subscribers on the streamer edge: one reads continuously, one stays silent
        // past the bounded queue's capacity — the room may not wait for either (AT15).
        using var edgeCertificate = environment.Certificate("tenant-a", "streamer-server", "edge-1");
        using var edgeChannel = environment.Channel(environment.GamePort, edgeCertificate, "game-server");
        var sync = new RoundSync.RoundSyncClient(edgeChannel);
        using var fastCall = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-12", GameId = "game-a", Epoch = owner.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 0
        });
        using var slowCall = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-12", GameId = "game-a", Epoch = owner.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 0
        });
        var fast = new List<SyncMessage>();
        while (await fastCall.ResponseStream.MoveNext())
        {
            fast.Add(fastCall.ResponseStream.Current);
            if (fast[^1].KindCase == SyncMessage.KindOneofCase.RoundResult) break;
        }
        // The fast reader saw exactly one baseline snapshot and a gapless delta chain into
        // the frozen result — its chain contiguity is the evidence the room never stalled.
        Assert.Equal(SyncMessage.KindOneofCase.Snapshot, fast[0].KindCase);
        Assert.Equal(600L, fast[^1].RoundResult.Tick);
        using (var document = System.Text.Json.JsonDocument.Parse(fast[^1].RoundResult.ResultJson))
            Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());
        var fastFinal = WalkSyncChain(fast, out var fastSnapshots, out var fastDeltas);
        Assert.Equal(1, fastSnapshots);
        Assert.True(fastDeltas >= 400, $"the fast reader saw {fastDeltas} deltas");
        Assert.Equal(601L, fastFinal);

        // The silent reader's transport window exhausts and its 64-message queue
        // overflows while the round kept ticking; the backlog is dropped and a snapshot
        // of the then-current head re-baselines it. The room never waited for it — the
        // fast reader's chain already reached the frozen result.
        var slow = new List<SyncMessage>();
        while (await slowCall.ResponseStream.MoveNext())
        {
            slow.Add(slowCall.ResponseStream.Current);
            if (slow[^1].KindCase == SyncMessage.KindOneofCase.RoundResult) break;
        }
        var slowFinal = WalkSyncChain(slow, out var slowSnapshots, out _);
        Assert.True(slowSnapshots >= 2,
            $"a lagging subscriber must be re-baselined with a snapshot, saw {slowSnapshots}");
        Assert.Equal(601L, slowFinal);
        using (var document = System.Text.Json.JsonDocument.Parse(slow[^1].RoundResult.ResultJson))
            Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());
    }

    [Fact]
    public async Task DisconnectedSubscriberReconnectsToAHeadSnapshotAndTheSameResult()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
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
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-sync-c", "live-m2-13-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-sync-d", "live-m2-13-b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        using var reconnectClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-13",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = reconnectClaim.Owner;
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-13", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-13",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-13", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-13", DurationTicks = 140, RandomSeed = 1313, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-13" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-13a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-13-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-13b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-13-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-13", MaxEvents = 50
        });

        using var edgeCertificate = environment.Certificate("tenant-a", "streamer-server", "edge-2");
        using var edgeChannel = environment.Channel(environment.GamePort, edgeCertificate, "game-server");
        var sync = new RoundSync.RoundSyncClient(edgeChannel);
        // A subscriber on a stale epoch learns the current one and resubscribes — it is
        // never streamed frames it cannot validate (8.3).
        using (var stale = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-13", GameId = "game-a", Epoch = owner.Epoch + 7
        }))
        {
            Assert.True(await stale.ResponseStream.MoveNext());
            Assert.Equal(SyncMessage.KindOneofCase.ResyncRequired, stale.ResponseStream.Current.KindCase);
            Assert.Equal(owner.Epoch, stale.ResponseStream.Current.ResyncRequired.CurrentEpoch);
            Assert.False(await stale.ResponseStream.MoveNext());
        }

        // The client applies a few deltas, then its connection dies mid-round.
        long appliedTick = 0, appliedVersion = 0;
        using (var connection = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-13", GameId = "game-a", Epoch = owner.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 0
        }))
        {
            while (await connection.ResponseStream.MoveNext())
            {
                var message = connection.ResponseStream.Current;
                if (message.KindCase == SyncMessage.KindOneofCase.Snapshot)
                {
                    appliedTick = message.Snapshot.Tick;
                    appliedVersion = message.Snapshot.StateVersion;
                }
                else if (message.KindCase == SyncMessage.KindOneofCase.Delta)
                {
                    appliedTick = message.Delta.Tick;
                    appliedVersion = message.Delta.StateVersion;
                }
                if (appliedTick >= 12) break;
            }
        }
        // The round continues without the departed subscriber (AT06 reconnect half).
        await using (var probe = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await probe.OpenAsync();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var (frames, maxTick) = (0L, 0L);
            while (DateTime.UtcNow < deadline)
            {
                (frames, maxTick) = await FrameStatsAsync(probe, "room-m2-13");
                if (maxTick > appliedTick + 10) break;
                await Task.Delay(200);
            }
            Assert.True(maxTick > appliedTick + 10, $"the round stalled at {maxTick}");
        }

        // The reconnect carries its applied baseline; a baseline behind the head answers
        // with a full snapshot and the chain runs gapless into the frozen result.
        var reconnected = new List<SyncMessage>();
        using (var resumed = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-13", GameId = "game-a", Epoch = owner.Epoch,
            LastAppliedTick = appliedTick, LastAppliedStateVersion = appliedVersion
        }))
        {
            while (await resumed.ResponseStream.MoveNext())
            {
                reconnected.Add(resumed.ResponseStream.Current);
                if (resumed.ResponseStream.Current.KindCase == SyncMessage.KindOneofCase.RoundResult) break;
            }
        }
        Assert.Equal(SyncMessage.KindOneofCase.Snapshot, reconnected[0].KindCase);
        Assert.True(reconnected[0].Snapshot.Tick > appliedTick,
            "a baseline behind the head must re-baseline with a snapshot");
        var finalVersion = WalkSyncChain(reconnected, out _, out _);
        Assert.Equal(141L, finalVersion);
        Assert.Equal(140L, reconnected[^1].RoundResult.Tick);
        var result = reconnected[^1].RoundResult.ResultJson;
        await using (var probe = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await probe.OpenAsync();
            await using var committed = new NpgsqlCommand(
                "SELECT result::text FROM game.result WHERE room_id='room-m2-13'", probe);
            // The streamed result and the committed result agree entry for entry — both
            // normalized through the same parser before the comparison.
            using var committedDocument = System.Text.Json.JsonDocument.Parse(
                (await committed.ExecuteScalarAsync())!.ToString()!);
            using var streamedDocument = System.Text.Json.JsonDocument.Parse(result);
            // Structural comparison: Postgres re-serializes jsonb with its own key order;
            // the ranking entries and their order are what must agree.
            Assert.Equal(RankingOf(committedDocument), RankingOf(streamedDocument));
        }
        // A late subscriber to the finished room receives the final snapshot and result.
        using (var late = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-13", GameId = "game-a", Epoch = owner.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 0
        }))
        {
            Assert.True(await late.ResponseStream.MoveNext());
            Assert.Equal(SyncMessage.KindOneofCase.Snapshot, late.ResponseStream.Current.KindCase);
            Assert.True(await late.ResponseStream.MoveNext());
            Assert.Equal(SyncMessage.KindOneofCase.RoundResult, late.ResponseStream.Current.KindCase);
            Assert.False(await late.ResponseStream.MoveNext());
        }
    }

    [Fact]
    public async Task ClientCommandsSurfaceAsReceiptsInTheDeltaStream()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
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
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-sync-e", "live-m2-14-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-m2-sync-f", "live-m2-14-b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        using var commandsClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-14",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = commandsClaim.Owner;
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-14", OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-14",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-14", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-op-14", DurationTicks = 120, RandomSeed = 777, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-14" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-14a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-14-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-14b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-m2-14-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-14", MaxEvents = 50
        });

        using var edgeCertificate = environment.Certificate("tenant-a", "streamer-server", "edge-3");
        using var edgeChannel = environment.Channel(environment.GamePort, edgeCertificate, "game-server");
        var sync = new RoundSync.RoundSyncClient(edgeChannel);
        using var stream = sync.Subscribe(new SubscribeRequest
        {
            RoomId = "room-m2-14", GameId = "game-a", Epoch = owner.Epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 0
        });
        var messages = new List<SyncMessage>();
        // The host's client command mid-round: a durable boost through the streamer edge
        // (architecture 6.3); the same command id retried answers duplicate.
        await Task.Delay(600);
        using var clientEdge = environment.Channel(environment.DataPort,
            environment.Certificate("tenant-a", "streamer-server", "edge-3-data"), "data-server");
        var input = new RoundInput.RoundInputClient(clientEdge);
        var command = new ClientInputRequest
        {
            SessionId = host.SessionId, RoomId = "room-m2-14", RoomEpoch = owner.Epoch,
            GameId = "game-a", CommandId = "cmd-boost-1",
            PayloadJson = """{"action":"boost","level":3}"""
        };
        var accepted = await input.AcceptClientInputAsync(command);
        Assert.False(accepted.Duplicate);
        Assert.True(accepted.Sequence > 0);
        var retried = await input.AcceptClientInputAsync(command);
        Assert.True(retried.Duplicate);

        while (await stream.ResponseStream.MoveNext())
        {
            messages.Add(stream.ResponseStream.Current);
            if (stream.ResponseStream.Current.KindCase == SyncMessage.KindOneofCase.RoundResult) break;
        }
        // The command lands exactly once: one receipt on the delta that consumed it, with
        // the boost visible in that frame's score effect and in the frozen ranking.
        var receiptDeltas = messages.Where(message =>
            message.KindCase == SyncMessage.KindOneofCase.Delta
            && message.Delta.Commands.Any(receipt => receipt.CommandId == "cmd-boost-1")).ToList();
        Assert.Single(receiptDeltas);
        var receiptDelta = receiptDeltas[0].Delta;
        Assert.Contains(receiptDelta.InputEventIds, eventId => eventId.Length > 0);
        Assert.Contains(receiptDelta.Effects, effect =>
            effect.Kind == "score" && effect.PayloadJson.Contains("\"red\""));
        WalkSyncChain(messages, out _, out _);
        var ranking = messages[^1].RoundResult.ResultJson;
        // red = gift 100 + boost 30..34 (the seed term varies, the range does not); blue
        // keeps its 30 — the command credited exactly the host's faction.
        using (var document = System.Text.Json.JsonDocument.Parse(ranking))
        {
            var scores = new Dictionary<string, long>();
            foreach (var entry in document.RootElement.GetProperty("ranking").EnumerateArray())
                scores[entry.GetProperty("faction").GetString()!] = entry.GetProperty("score").GetInt64();
            Assert.InRange(scores["red"], 130, 134);
            Assert.Equal(30L, scores["blue"]);
        }
    }

    [Fact]
    public async Task CapacityPressureRejectsNewRoundsAndRetentionNeverBreaksTheChain()
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
        // One active room is all this node admits: pressure refuses new rounds visibly with
        // a counted reason instead of freeing space quietly (AT21).
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server",
            new Dictionary<string, string> { ["CHORUS_MAX_ACTIVE_ROOMS"] = "1" });
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-m2-ret-a", "live-m2-15-a");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        using var controlDataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");

        using var claim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-15",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-15", OwnerInstanceId = "game-1", Epoch = claim.Owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-15",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" }
            }
        })).Status);
        var secondOwner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-m2-15b",
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var refused = await Assert.ThrowsAsync<RpcException>(() => rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = "room-m2-15b", OwnerInstanceId = "game-1", Epoch = secondOwner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-op-15b",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" }
            }
        }).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, refused.StatusCode);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var counter = new NpgsqlCommand(
                "SELECT count FROM ops.capacity_rejection WHERE kind='start-round:active-rooms'",
                connection);
            Assert.Equal(1L, Convert.ToInt64(await counter.ExecuteScalarAsync()));
        }

        // The admitted round runs to settlement, then the retention boundary answers with its
        // guards: the settled round is inside its window, and an explicit override frees the
        // prefix below the verified anchor without touching the chain above it.
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = "room-m2-15", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = claim.Owner.Epoch,
            OperationId = "run-op-15", DurationTicks = 20, RandomSeed = 1515, TickMs = 25,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" }
            }
        })).Status);

        FinalizeRoundReply finalized = new();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                finalized = await new Settlement.SettlementClient(controlDataChannel).FinalizeRoundAsync(
                    new FinalizeRoundRequest
                    {
                        RoomId = "room-m2-15", GameId = "game-a", OperationId = "finalize-15", TenantId = "tenant-a"
                    });
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { }
            if (finalized.Status == "Settled") break;
            await Task.Delay(200);
        }
        Assert.Equal("Settled", finalized.Status);

        // The settled round sits inside its retention window: the default answer is a visible
        // refusal, and only the explicit override releases anything.
        var retention = new Retention.RetentionClient(controlDataChannel);
        var windowed = await Assert.ThrowsAsync<RpcException>(() => retention.PruneRoundAsync(
            new PruneRoundRequest
            {
                RoomId = "room-m2-15", GameId = "game-a", OperationId = "prune-15", TenantId = "tenant-a"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, windowed.StatusCode);
        Assert.Contains("retention window", windowed.Status.Detail);
        var pruned = await retention.PruneRoundAsync(new PruneRoundRequest
        {
            RoomId = "room-m2-15", GameId = "game-a", OperationId = "prune-15",
            TenantId = "tenant-a", OverrideRetentionWindow = true
        });
        // The admission snapshot at tick 0 is the room's only snapshot, so the honest reply
        // frees nothing rather than thinning the room's single fallback anchor.
        Assert.Equal(0L, pruned.KeepTick);
        Assert.Equal(0L, pruned.FramesDeleted);
        var recovery = await new FrameStore.FrameStoreClient(gameChannel).LoadRecoveryAsync(
            new LoadRecoveryRequest { RoomId = "room-m2-15", GameId = "game-a" });
        Assert.Equal("Settled", recovery.Status);
        Assert.Equal(20L, recovery.LastTick);
        Assert.Equal(20, recovery.Frames.Count);
        Assert.Equal(1L, recovery.Frames[0].Tick);
        // Retention belongs to the control plane alone.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "ret-edge");
        using var streamerChannel = environment.Channel(environment.DataPort, streamerCertificate, "data-server");
        var denied = await Assert.ThrowsAsync<RpcException>(() =>
            new Retention.RetentionClient(streamerChannel).PruneRoundAsync(new PruneRoundRequest
            {
                RoomId = "room-m2-15", GameId = "game-a", OperationId = "prune-15-denied", TenantId = "tenant-a"
            }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    /// <summary>
    /// A claimed ownership with background renewal: the control plane extends an active
    /// owner's lease every 20 seconds without bumping the epoch (architecture 4.2), so a
    /// work-bound room cannot outlive its fence under load. Disposing stops the renewal.
    /// </summary>
    private sealed class RenewableClaim : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly Task renewal;
        public OwnershipReply Owner { get; }

        private RenewableClaim(OwnershipReply owner, Task renewal, CancellationTokenSource cancellation)
        {
            Owner = owner;
            this.renewal = renewal;
            this.cancellation = cancellation;
        }

        public static async Task<RenewableClaim> ClaimAsync(
            ControlService.ControlServiceClient control, OwnershipRequest request)
        {
            var owner = await control.ClaimOwnershipAsync(request,
                deadline: DateTime.UtcNow.AddSeconds(10));
            var cancellation = new CancellationTokenSource();
            var renewal = RenewAsync(control, request, cancellation.Token);
            return new RenewableClaim(owner, renewal, cancellation);
        }

        private static async Task RenewAsync(
            ControlService.ControlServiceClient control, OwnershipRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                    await control.ClaimOwnershipAsync(request,
                        deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Grpc.Core.RpcException) { /* the environment is disposing; renewal ends */ }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try { renewal.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Walks a subscriber's stream validating the 8.3 chain: every delta applies to exactly
    /// the version last applied, a snapshot re-baselines, the result freezes the chain.
    /// Returns the final applied version.
    /// </summary>
    private static long WalkSyncChain(List<SyncMessage> messages, out int snapshots, out int deltas)
    {
        var applied = -1L;
        snapshots = 0;
        deltas = 0;
        foreach (var message in messages)
        {
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                    snapshots++;
                    applied = message.Snapshot.StateVersion;
                    break;
                case SyncMessage.KindOneofCase.Delta:
                    deltas++;
                    Assert.Equal(applied, message.Delta.BaseVersion);
                    applied = message.Delta.StateVersion;
                    break;
                case SyncMessage.KindOneofCase.RoundResult:
                    Assert.Equal(applied, message.RoundResult.StateVersion);
                    break;
                default:
                    Assert.Fail($"Unexpected sync message kind {message.KindCase}.");
                    break;
            }
        }
        return applied;
    }

    /// <summary>The ranking entries of a round result in order: (faction, score).</summary>
    private static List<(string Faction, long Score)> RankingOf(System.Text.Json.JsonDocument document) =>
        document.RootElement.GetProperty("ranking").EnumerateArray()
            .Select(entry => (entry.GetProperty("faction").GetString()!,
                entry.GetProperty("score").GetInt64()))
            .ToList();

    private static async Task<(long Count, long MaxTick)> FrameStatsAsync(NpgsqlConnection connection,
        string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*),COALESCE(max(tick_id),0) FROM game.frame WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    private static async Task<string> RoomStatusAsync(NpgsqlConnection connection, string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM game.room WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return await command.ExecuteScalarAsync() as string ?? "";
    }

    /// <summary>Logs a simulator streamer in through the real StreamerServer and mounts game-a.</summary>
    private static async Task<(LoginReadyReply Login, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-" + openId);
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
