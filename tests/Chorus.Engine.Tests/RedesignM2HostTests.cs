using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
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
        // Only the control plane and game hosts prepare rooms; recovery fails visibly until M2-4.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-role");
        using var streamerHostChannel = environment.Channel(environment.GamePort, streamerCertificate,
            "game-server");
        var denied = await Assert.ThrowsAsync<RpcException>(() => new GameHost.GameHostClient(
            streamerHostChannel).PrepareRoomAsync(healthyPrepare).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        var unimplemented = await Assert.ThrowsAsync<RpcException>(() => gameHost.RecoverRoomAsync(
            new RecoverRoomRequest { RoomId = "room-m2-4", GameId = "game-a" }).ResponseAsync);
        Assert.Equal(StatusCode.Unimplemented, unimplemented.StatusCode);
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
