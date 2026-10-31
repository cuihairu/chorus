using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.GameServer;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-01 end to end: the routing layer pushes an attributed platform event straight
/// at the game host it believes owns the room. The reply disposition is the whole contract:
/// DurableAccepted and Duplicate mean the event is committed into the room's inbox (the
/// module really consumed the pushed boost — its 12 × level arithmetic shows in the final
/// score), StaleOwner answers an owner picture this host does not hold, Overloaded answers
/// a closed inbound bound before any durable write, and OutcomeUnknown answers a data
/// plane whose commit state the host cannot know — the caller retries the same event id.
/// </summary>
public sealed class RedesignRoomEventPushHostTests
{
    [Fact]
    public async Task ARoomEventPushIsDurableBeforeConfirmedAndIdempotentByEventId()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
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
        // Host A serves the room and takes pushes; host B's inbound bound is closed, so
        // every DeliverEvent there answers Overloaded before anything durable happens.
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        var closed = GameEnvironment(environment.DataPort, controlEndpoint, gamePortB,
            "node-a/game-server#2");
        closed["CHORUS_ROOM_EVENT_INBOUND"] = "0";
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server", closed,
            clientTenant: "tenant-a", instanceId: "game-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-push");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-push");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        // The routing layer pushes under the platform-gateway role. Each room takes its
        // own mount pair (a binding points at one room at a time); the outsider mounts
        // but joins no room, so its binding points nowhere near room A.
        using var pushCertificate = environment.Certificate("tenant-a", "platform-gateway", "push-gw");
        var hostA = await MountAsync(environment, simulator, "push-host-a");
        var guestA = await MountAsync(environment, simulator, "push-guest-a");
        var hostB = await MountAsync(environment, simulator, "push-host-b");
        var guestB = await MountAsync(environment, simulator, "push-guest-b");
        var hostC = await MountAsync(environment, simulator, "push-host-c");
        var guestC = await MountAsync(environment, simulator, "push-guest-c");
        var outsider = await MountAsync(environment, simulator, "push-outsider");

        const string roomA = "room-push-a";
        const string roomB = "room-push-b";
        const string roomC = "room-push-c";
        var (epochA, _) = await StartRoomAsync(environment, control, rounds, roomA,
            "game-a", gamePortA, controlCertificate, hostA, guestA, "push-start-a", durationTicks: 200);
        var (epochB, _) = await StartRoomAsync(environment, control, rounds, roomB,
            "game-b", gamePortB, controlCertificate, hostB, guestB, "push-start-b", durationTicks: 400);
        // The long room outlives this test's data plane: its actor is the seat from which
        // the OutcomeUnknown probe is made after the store dies.
        var (epochC, _) = await StartRoomAsync(environment, control, rounds, roomC,
            "game-a", gamePortA, controlCertificate, hostC, guestC, "push-start-c", durationTicks: 800);

        DeliverRoomEventRequest Request(string roomId, long epoch, string eventId, string bindingId,
            string payload = """{"action":"boost","level":3}""") => new()
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomId, OwnerInstanceId = roomId == roomB
                ? "game-b" : "game-a",
            Epoch = epoch, EventId = eventId, Platform = "simulator", AppId = "app-1",
            BindingId = bindingId, PayloadJson = payload
        };
        async Task<DeliverRoomEventReply> DeliverAsync(int port, DeliverRoomEventRequest request)
        {
            using var channel = environment.Channel(port, pushCertificate, "game-server");
            return await new RoomEvents.RoomEventsClient(channel).DeliverEventAsync(request,
                deadline: DateTime.UtcNow.AddSeconds(30));
        }

        // A stale owner picture is refused without touching the data plane — both an
        // epoch the room moved past at the owning host and the right epoch at a host that
        // does not hold the room.
        var staleEpoch = await DeliverAsync(gamePortA, Request(roomA, epochA + 7, "gift-stale",
            hostA.BindingId));
        Assert.Equal(RoomRuntime.StaleOwner, staleEpoch.Disposition);
        var otherHost = await DeliverAsync(gamePortB, Request(roomA, epochA, "gift-other",
            hostA.BindingId));
        Assert.Equal(RoomRuntime.StaleOwner, otherHost.Disposition);

        // The durable accept: committed inbox entry first, confirmation after.
        var accepted = await DeliverAsync(gamePortA, Request(roomA, epochA, "gift-1", hostA.BindingId));
        Assert.Equal(RoomRuntime.DurableAccepted, accepted.Disposition);
        Assert.Equal(1L, accepted.Sequence);
        Assert.Equal(1L, accepted.RouteVersion);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var row = new NpgsqlCommand("""
                SELECT disposition,platform FROM input.event WHERE event_id='gift-1'
                """, connection);
            await using var reader = await row.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Routed", reader.GetString(0));
            Assert.Equal("simulator", reader.GetString(1));
        }

        // The exact retry replays the committed outcome; the same event id under a
        // different payload is an error, not a duplicate.
        var retry = await DeliverAsync(gamePortA, Request(roomA, epochA, "gift-1", hostA.BindingId));
        Assert.Equal(RoomRuntime.Duplicate, retry.Disposition);
        Assert.Equal(1L, retry.Sequence);
        var conflict = await Assert.ThrowsAsync<RpcException>(() => DeliverAsync(gamePortA,
            Request(roomA, epochA, "gift-1", hostA.BindingId, """{"action":"boost","level":9}""")));
        Assert.Equal(StatusCode.InvalidArgument, conflict.StatusCode);
        // A binding that does not point at this room is an attribution error; a gateway
        // may not push into another tenant's rooms.
        var wrongBinding = await Assert.ThrowsAsync<RpcException>(() => DeliverAsync(gamePortA,
            Request(roomA, epochA, "gift-foreign", outsider.BindingId)));
        Assert.Equal(StatusCode.InvalidArgument, wrongBinding.StatusCode);
        using (var otherTenantCertificate = environment.Certificate("tenant-b", "platform-gateway", "push-gw-b"))
        using (var otherTenantChannel = environment.Channel(gamePortA, otherTenantCertificate, "game-server"))
        {
            var crossRequest = Request(roomA, epochA, "gift-cross", hostA.BindingId);
            crossRequest.TenantId = "tenant-b";
            var crossTenant = await Assert.ThrowsAsync<RpcException>(async () =>
                await new RoomEvents.RoomEventsClient(otherTenantChannel).DeliverEventAsync(crossRequest,
                    deadline: DateTime.UtcNow.AddSeconds(30)));
            Assert.Equal(StatusCode.PermissionDenied, crossTenant.StatusCode);
        }

        // The closed inbound bound refuses before any durable write — nothing of this
        // room reached the store.
        var overloaded = await DeliverAsync(gamePortB, Request(roomB, epochB, "gift-b1", hostB.BindingId));
        Assert.Equal(RoomRuntime.Overloaded, overloaded.Disposition);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var none = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE attributed_room_id=@room", connection);
            none.Parameters.AddWithValue("room", roomB);
            Assert.Equal(0L, Convert.ToInt64(await none.ExecuteScalarAsync()));
        }

        // The pushed boost really became a module input: the flat 12 × level arithmetic of
        // the 1.1.0 package is the room's final score.
        var resultJson = await WaitForResultAsync(environment, rounds, gamePortA, roomA, epochA,
            controlCertificate, "push-sync-a", TimeSpan.FromSeconds(90));
        using (var result = JsonDocument.Parse(resultJson))
        {
            Assert.Equal(36L, ScoreOf(result, "red")); // 12 × level 3
            Assert.Equal(0L, ScoreOf(result, "blue"));
            Assert.Equal("red", WinnerOf(result));
        }

        // The store dies mid-flight of a push: the host cannot know whether the durable
        // leg committed, so the disposition says so — and the event id stays the caller's
        // key for the retry that will decide between Duplicate and DurableAccepted.
        environment.Kill("Chorus.DataServer");
        var unknown = await DeliverAsync(gamePortA, Request(roomC, epochC, "gift-c1", hostC.BindingId));
        Assert.Equal(RoomRuntime.OutcomeUnknown, unknown.Disposition);
    }

    [Fact]
    public async Task AFullRoomSeatListRefusesVisiblyAndNeverCommits()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
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
        // FIX-INPUT-03: one seat per room on this host — the second fresh event must find
        // the list full and be refused before any durable write.
        var seats = GameEnvironment(environment.DataPort, controlEndpoint, gamePort,
            "node-a/game-server");
        seats["CHORUS_ROOM_INBOUND_PER_ROOM"] = "1";
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server", seats,
            clientTenant: "tenant-a", instanceId: "game-a");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-ib");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-ib");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        using var pushCertificate = environment.Certificate("tenant-a", "platform-gateway", "push-ib");
        var host = await MountAsync(environment, simulator, "seat-host-a");
        var guest = await MountAsync(environment, simulator, "seat-guest-a");
        const string roomIb = "room-seat-ib";
        var (epoch, _) = await StartRoomAsync(environment, control, rounds, roomIb,
            "game-a", gamePort, controlCertificate, host, guest, "seat-start-a", durationTicks: 400);

        DeliverRoomEventRequest Request(string eventId, long requestEpoch, string bindingId) => new()
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomIb, OwnerInstanceId = "game-a",
            Epoch = requestEpoch, EventId = eventId, Platform = "simulator", AppId = "app-1",
            BindingId = bindingId, PayloadJson = """{"action":"boost","level":3}"""
        };
        async Task<DeliverRoomEventReply> DeliverAsync(DeliverRoomEventRequest request)
        {
            using var channel = environment.Channel(gamePort, pushCertificate, "game-server");
            return await new RoomEvents.RoomEventsClient(channel).DeliverEventAsync(request,
                deadline: DateTime.UtcNow.AddSeconds(30));
        }

        // The first event commits and takes the room's single seat.
        var accepted = await DeliverAsync(Request("seat-gift-1", epoch, host.BindingId));
        Assert.Equal(RoomRuntime.DurableAccepted, accepted.Disposition);

        // The seat list is full: a second fresh event is refused visibly before any
        // durable write — nothing of it reached the store.
        var refused = await DeliverAsync(Request("seat-gift-2", epoch, host.BindingId));
        Assert.Equal(RoomRuntime.Overloaded, refused.Disposition);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE event_id='seat-gift-2'", connection);
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        // An idempotent replay of the seated, committed event never blocks on the full
        // list — the stored outcome replays as Duplicate.
        var replay = await DeliverAsync(Request("seat-gift-1", epoch, host.BindingId));
        Assert.Equal(RoomRuntime.Duplicate, replay.Disposition);

        // A stale owner picture stays an ownership fact even with the list full — the
        // fence runs before any capacity judgment.
        var stale = await DeliverAsync(Request("seat-gift-3", epoch + 5, host.BindingId));
        Assert.Equal(RoomRuntime.StaleOwner, stale.Disposition);

        // Exactly one event was committed into the room.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM input.event WHERE attributed_room_id=@room", connection);
            command.Parameters.AddWithValue("room", roomIb);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ASeatedPushIsConsumedByTheFrameCommitAtomically()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
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
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePort, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-sc");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-sc");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "game-a");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        using var pushCertificate = environment.Certificate("tenant-a", "platform-gateway", "push-sc");
        var host = await MountAsync(environment, simulator, "seat-host-b");
        var guest = await MountAsync(environment, simulator, "seat-guest-b");
        const string roomSc = "room-seat-sc";
        var (epoch, _) = await StartRoomAsync(environment, control, rounds, roomSc,
            "game-a", gamePort, controlCertificate, host, guest, "seat-start-b", durationTicks: 400);

        DeliverRoomEventRequest Request(string eventId) => new()
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = roomSc, OwnerInstanceId = "game-a",
            Epoch = epoch, EventId = eventId, Platform = "simulator", AppId = "app-1",
            BindingId = host.BindingId, PayloadJson = """{"action":"boost","level":3}"""
        };
        async Task<DeliverRoomEventReply> DeliverAsync(DeliverRoomEventRequest request)
        {
            using var channel = environment.Channel(gamePort, pushCertificate, "game-server");
            return await new RoomEvents.RoomEventsClient(channel).DeliverEventAsync(request,
                deadline: DateTime.UtcNow.AddSeconds(30));
        }

        // The push commits durably and takes its seat; the tick's frame commit consumes the
        // delivery and records the input in the same transaction (FIX-INPUT-04).
        var accepted = await DeliverAsync(Request("sc-gift-1"));
        Assert.Equal(RoomRuntime.DurableAccepted, accepted.Disposition);
        var consumed = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var delivery = new NpgsqlCommand(
                "SELECT state FROM input.delivery WHERE event_id='sc-gift-1'", connection);
            if (await delivery.ExecuteScalarAsync() as string != "Consumed") return false;
            await using var frame = new NpgsqlCommand(
                "SELECT input_refs FROM game.frame WHERE room_id=@room ORDER BY tick_id", connection);
            frame.Parameters.AddWithValue("room", roomSc);
            await using var reader = await frame.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (reader.GetString(0).Contains("\"sc-gift-1\"")) return true;
            return false;
        }, TimeSpan.FromSeconds(60));
        Assert.True(consumed, "The seated push was never consumed by a frame commit.");

        // A retry of the same event id replays the stored outcome and changes nothing: the
        // delivery stays a single consumed row, and the boost lands exactly once.
        var replay = await DeliverAsync(Request("sc-gift-1"));
        Assert.Equal(RoomRuntime.Duplicate, replay.Disposition);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var rows = new NpgsqlCommand("""
                SELECT count(*),count(*) FILTER (WHERE state='Consumed') FROM input.delivery
                WHERE event_id='sc-gift-1'
                """, connection);
            await using var reader = await rows.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        // The consumed boost really became a module input: the flat 12 × level arithmetic
        // of the 1.1.0 package is the room's final score — once, not twice.
        var resultJson = await WaitForResultAsync(environment, rounds, gamePort, roomSc, epoch,
            controlCertificate, "seat-sync-b", TimeSpan.FromSeconds(90));
        using (var result = JsonDocument.Parse(resultJson))
        {
            Assert.Equal(36L, ScoreOf(result, "red"));
            Assert.Equal(0L, ScoreOf(result, "blue"));
        }
    }

    /// <summary>Claims the room for the named owner instance, starts it under the 1.1.0
    /// package identity (flat 12 × level arithmetic) and prepares it on that host.</summary>
    private static async Task<(long Epoch, string HostSession)> StartRoomAsync(
        RedesignM0HostTests.HostEnvironment environment,
        ControlService.ControlServiceClient control, RoundStore.RoundStoreClient rounds,
        string roomId, string ownerInstance, int gamePort,
        X509Certificate2 controlCertificate,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string operationId,
        int durationTicks = 120)
    {
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = ownerInstance, LeaseSeconds = 30
        });
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = ownerInstance, Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "1.1.0", ConfigHash = "cfg-1-v2", PackageHash = "pkg-1-v2",
            OperationId = operationId, TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                },
                new RoundParticipantProto
                {
                    BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest"
                }
            }
        });
        Assert.Equal("Preparing", started.Status);
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = ownerInstance,
                Epoch = claimed.Epoch, OperationId = operationId + "-prepare",
                DurationTicks = durationTicks, RandomSeed = 777, TickMs = 25, PackageHash = "pkg-1-v2",
                Participants =
                {
                    new GameParticipantProto
                    {
                        BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                    },
                    new GameParticipantProto
                    {
                        BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue"
                    }
                }
            });
        Assert.Equal("Running", prepared.Status);
        return (claimed.Epoch, host.SessionId);
    }

    /// <summary>Subscribes to the round's result stream and waits for the room to leave
    /// Running, then returns the result document the settlement produced.</summary>
    private static async Task<string> WaitForResultAsync(
        RedesignM0HostTests.HostEnvironment environment, RoundStore.RoundStoreClient rounds,
        int gamePort, string roomId, long epoch, X509Certificate2 controlCertificate,
        string syncId, TimeSpan timeout)
    {
        string? resultJson = null;
        using var syncCertificate = environment.Certificate("tenant-a", "streamer-server", syncId);
        using var syncChannel = environment.Channel(gamePort, syncCertificate, "game-server");
        using var subscriber = new RoundSync.RoundSyncClient(syncChannel).Subscribe(new SubscribeRequest
        {
            RoomId = roomId, GameId = "game-a", Epoch = epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 1
        }, deadline: DateTime.UtcNow.Add(timeout));
        var reading = Task.Run(async () =>
        {
            await foreach (var message in subscriber.ResponseStream.ReadAllAsync())
                if (message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                    resultJson = message.RoundResult.ResultJson;
        });
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
            != "Running", timeout);
        Assert.True(finished, $"The room {roomId} did not finish in time.");
        await reading.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(resultJson);
        return resultJson!;
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

    private static async Task RegisterHostsAsync(
        RedesignM0HostTests.HostEnvironment environment, RoutingService.RoutingClient routing,
        params string[] instanceIds)
    {
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-push"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-push" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return instanceIds.All(id => snapshot.Instances.Any(instance =>
                instance.ProcessInstanceId == id && instance.Endpoint.Length > 0));
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "push-" + openId;
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

    private static long ScoreOf(JsonDocument result, string faction)
    {
        foreach (var entry in result.RootElement.GetProperty("ranking").EnumerateArray())
            if (entry.GetProperty("faction").GetString() == faction)
                return entry.GetProperty("score").GetInt64();
        throw new InvalidOperationException($"The result names no {faction} faction.");
    }

    private static string WinnerOf(JsonDocument result) =>
        result.RootElement.GetProperty("winner").GetString()!;

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
}
