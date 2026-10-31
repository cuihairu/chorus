using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-02 end to end: the gateway's webhook face resolves each callback's canonical live
/// key through the control plane's versioned routing snapshot and pushes the event straight at
/// the game host that owns the room — two rooms on two different hosts each receive only their
/// own events. A late event (the room's ownership moved on) is refused before any write into
/// the new round, and a callback whose live key names no routed room waits durably in the
/// persistent inbox — an explicit pending, never a drop.
/// </summary>
public sealed class RedesignPlatformGatewayRoutingHostTests
{
    [Fact]
    public async Task AGatewayRoutesEachEventToItsOwnRoomAndRefusesLateOnes()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
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
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#2"),
            clientTenant: "tenant-a", instanceId: "game-b");

        // The gateway faces the platforms and the control plane: its routing watch is the only
        // route source, and the self-report lets it be reached if routing ever needs to.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, ingressPort, controlEndpoint, gatewayPort),
            instanceId: "gateway-route");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-route");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-route");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        using var pushCertificate = environment.Certificate("tenant-a", "platform-gateway", "route-gw");
        var hostA = await MountAsync(environment, simulator, "gw-open-a", "gw-host-a");
        var guestA = await MountAsync(environment, simulator, "gw-open-guest-a", "gw-guest-a");
        var hostB = await MountAsync(environment, simulator, "gw-open-b", "gw-host-b");
        var guestB = await MountAsync(environment, simulator, "gw-open-guest-b", "gw-guest-b");

        const string roomA = "room-route-a";
        const string roomB = "room-route-b";
        var (epochA, _) = await StartRoomAsync(environment, control, rounds, roomA,
            "game-a", gamePortA, controlCertificate, hostA, guestA, "route-start-a", durationTicks: 400);
        var (epochB, _) = await StartRoomAsync(environment, control, rounds, roomB,
            "game-b", gamePortB, controlCertificate, hostB, guestB, "route-start-b", durationTicks: 400);

        using var http = await HttpClientAsync(environment);

        // The routing watch is event-driven and the face's cache lags the control plane by one
        // notice round-trip: the first push must land after the cache is warm, or the face falls
        // back to the durable leg and the routed path is never exercised. Wait for the snapshot
        // to carry the whole route (both live keys, both rooms with a serving owner), then for
        // the version to hold still across a couple of reads so the watch has caught up.
        var warm = await WaitForRoutingWarmAsync(routing);
        Assert.True(warm, "The gateway's routing snapshot never carried the two routes.");

        // The two rooms live on two different hosts: each callback's event must reach only its
        // own room. The first push answers DurableAccepted and is durably in that room's inbox.
        var pushedA = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"gw-host-a","eventId":"route-gift-a1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        AssertDurable(pushedA, "route-gift-a1");
        var pushedB = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"gw-host-b","eventId":"route-gift-b1","kind":"Gift","giftId":"g2","giftCount":2,"giftValue":50}""");
        AssertDurable(pushedB, "route-gift-b1");

        // Each event landed in its own room — no cross-push, no wrong attribution.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT event_id,attributed_room_id,disposition FROM input.event
                WHERE event_id IN ('route-gift-a1','route-gift-b1') ORDER BY event_id
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string EventId, string Room, string Disposition)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            Assert.Equal(("route-gift-a1", roomA, "Routed"), rows[0]);
            Assert.Equal(("route-gift-b1", roomB, "Routed"), rows[1]);
        }

        // A retry of an already-routed event id replays its stored outcome — the event stays in
        // the room that first accepted it (the durable spine replays before the ownership fence).
        var retried = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"gw-host-a","eventId":"route-gift-a1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var retryReceipt = Assert.Single(ReadReceipts(await retried.Content.ReadAsStringAsync()));
        Assert.True(retryReceipt.GetProperty("duplicate").GetBoolean());
        Assert.Equal(roomA, (await RoomForAsync(environment, "route-gift-a1"))!);

        // The room's authority moves on (a new owner picture, one epoch ahead, no endpoint the
        // gateway could reach). A late retry of the already-routed id must not follow the new
        // round: the gateway's route now resolves to no reachable owner, the event waits in the
        // durable attribution leg, and the receipt replay keeps it in the room that first
        // accepted it — one row, one room, nothing written into the moved-on round.
        var moved = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomA,
            OwnerInstanceId = "game-a#late", LeaseSeconds = 30
        });
        Assert.Equal(epochA + 1, moved.Epoch);
        // The gateway's routing watch refreshes on the version change; the patient wait lets it
        // observe the new owner picture before the retry (the monitor polls every 500 ms).
        var observed = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            return snapshot.Rooms.Any(room => room.RoomId == roomA
                && room.OwnerInstanceId == "game-a#late" && room.Epoch == epochA + 1);
        }, TimeSpan.FromSeconds(30));
        Assert.True(observed, "The control plane did not publish the moved-on owner.");
        var late = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"gw-host-a","eventId":"route-gift-a1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        var lateReceipt = Assert.Single(ReadReceipts(await late.Content.ReadAsStringAsync()));
        Assert.True(lateReceipt.GetProperty("duplicate").GetBoolean());
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*),count(distinct attributed_room_id) FROM input.event WHERE event_id='route-gift-a1'",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        // A callback whose live key names no mount and no room has no valid attribution — it
        // waits durably, an explicit pending never a drop: the data plane's attribution leg is
        // the migration baseline and answers it with the persistent inbox.
        var orphaned = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"gw-never-mounted","eventId":"route-orphan-1","kind":"Gift","giftId":"g4","giftCount":1,"giftValue":7}""");
        Assert.Equal(HttpStatusCode.OK, orphaned.StatusCode);
        var pending = Assert.Single(ReadReceipts(await orphaned.Content.ReadAsStringAsync()));
        Assert.Equal("PendingAttribution", pending.GetProperty("disposition").GetString());
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT disposition FROM input.event WHERE source_dedup_key='route-orphan-1'", connection);
            Assert.Equal("PendingAttribution", await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task AMigratedRoomStillReceivesOnePushAndRetriesNeverDouble()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
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
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#3"),
            clientTenant: "tenant-a", instanceId: "game-b");
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, ingressPort, controlEndpoint, gatewayPort),
            instanceId: "gateway-route");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-mig");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-mig");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "mig-open-a", "mig-host-a");
        var guest = await MountAsync(environment, simulator, "mig-open-guest-a", "mig-guest-a");

        // One room on host A; the face's route is warm before the first push, as ever.
        const string room = "room-mig-a";
        var (epoch, _) = await StartRoomAsync(environment, control, rounds, room,
            "game-a", gamePortA, controlCertificate, host, guest, "mig-start-a", durationTicks: 400);
        var warm = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            return snapshot.Streamers.Any(streamer => streamer.Platform == "simulator"
                && streamer.CanonicalLiveSessionKey == "mig-host-a")
                && snapshot.Rooms.Any(r => r.RoomId == room && r.OwnerInstanceId == "game-a"
                && r.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(warm, "The room's route never became visible.");
        // The face's cache lags the snapshot by one watch round-trip; hold the version still
        // so the first push resolves a route instead of falling to the durable leg.
        for (var stable = 0; stable < 2; stable++)
        {
            var before = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            await Task.Delay(600);
            var after = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            Assert.Equal(before, after);
        }

        using var http = await HttpClientAsync(environment);
        var pushed = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"mig-host-a","eventId":"mig-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":10}""");
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        var receipt = Assert.Single(ReadReceipts(await pushed.Content.ReadAsStringAsync()));
        Assert.Equal("DurableAccepted", receipt.GetProperty("disposition").GetString());
        Assert.False(receipt.GetProperty("duplicate").GetBoolean());
        Assert.Equal(room, await RoomForAsync(environment, "mig-gift-1"));

        // The room's authority moves to host B — a claim under a different instance, one
        // epoch ahead, with an endpoint the directory can resolve. Host B holds no such
        // room, so its runtime refuses every push for the new epoch, and the store's fence
        // refuses the old epoch inside host A: neither instance accepts the event durably,
        // whatever the face's cache still says.
        var moved = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = room,
            OwnerInstanceId = "game-b", LeaseSeconds = 30
        });
        Assert.Equal(epoch + 1, moved.Epoch);
        var observed = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            return snapshot.Rooms.Any(r => r.RoomId == room && r.OwnerInstanceId == "game-b"
                && r.Epoch == epoch + 1 && r.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(observed, "The control plane did not publish the moved-on owner.");

        // The face retries its bounded budget — StaleOwner answers refresh the directory and
        // re-push the same event id — and when no instance can accept the event durably, the
        // webhook still ends in the persistent inbox: one attributed row under the platform's
        // dedup key, never a drop.
        var refused = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"mig-host-a","eventId":"mig-gift-2","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":20}""");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*),count(*) FILTER (WHERE disposition='Attributed')
                FROM input.event WHERE source_dedup_key='mig-gift-2'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        // The platform's own retry of the first callback — the lost-success-reply posture —
        // replays the stored outcome as a duplicate wherever it lands: still one row, one
        // room, one effect, and the new authority never takes a second write.
        var retried = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"mig-host-a","eventId":"mig-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":10}""");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var replay = Assert.Single(ReadReceipts(await retried.Content.ReadAsStringAsync()));
        Assert.True(replay.GetProperty("duplicate").GetBoolean());
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*),count(distinct attributed_room_id) FROM input.event
                WHERE event_id='mig-gift-1' OR source_dedup_key='mig-gift-1'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }
    }

    private static void AssertDurable(HttpResponseMessage response, string eventId)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"The push was not accepted: {response.StatusCode}");
        var receipt = Assert.Single(ReadReceipts(response.Content.ReadAsStringAsync()
            .GetAwaiter().GetResult()));
        Assert.Equal("DurableAccepted", receipt.GetProperty("disposition").GetString());
        Assert.Equal(eventId, receipt.GetProperty("eventId").GetString());
    }

    private static async Task<string?> RoomForAsync(
        RedesignM0HostTests.HostEnvironment environment, string eventId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT attributed_room_id FROM input.event WHERE event_id=@event", connection);
        command.Parameters.AddWithValue("event", eventId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static List<JsonElement> ReadReceipts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("receipts").EnumerateArray()
            .Select(receipt => receipt.Clone()).ToList();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, int ingressPort,
        string platform, string appId, string body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://localhost:{ingressPort}/ingress/{platform}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(PlatformIngressEndpoint.AppIdHeader, appId);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await http.SendAsync(request);
    }

    private static async Task<HttpClient> HttpClientAsync(
        RedesignM0HostTests.HostEnvironment environment)
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

    private static Dictionary<string, string> GatewayEnvironment(
        int dataPort, int ingressPort, string controlEndpoint, int reportPort) => new()
    {
        ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
        ["CHORUS_DEV_PLATFORM_ENDPOINT"] = "http://127.0.0.1:9/",
        ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(CultureInfo.InvariantCulture),
        ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
        ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{reportPort}",
        ["CHORUS_REPORT_INTERVAL_MS"] = "500",
        // One real platform keeps the verifier router mounted; AllowAll adds the development
        // verifier plus the simulator mapper — the leg a development fleet can drive home.
        ["Ingress__Platforms__0"] = "douyin",
        ["Ingress__AllowAll"] = "true",
        ["Ingress__Douyin__AppSecret"] = "route-douyin-secret"
    };

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
        RedesignM0HostTests.HostEnvironment environment, RoutingService.RoutingClient routing)
    {
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-route"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-route" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-b"
                && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");
    }

    private static async Task<(long Epoch, string HostSession)> StartRoomAsync(
        RedesignM0HostTests.HostEnvironment environment,
        Chorus.Rpc.ControlService.ControlServiceClient control, Chorus.Rpc.RoundStore.RoundStoreClient rounds,
        string roomId, string ownerInstance, int gamePort,
        X509Certificate2 controlCertificate,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string operationId,
        int durationTicks = 120)
    {
        var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = ownerInstance, LeaseSeconds = 30
        });
        var started = await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = ownerInstance, Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = operationId, TenantId = "tenant-a",
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
        });
        Assert.Equal("Preparing", started.Status);
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var prepared = await new Chorus.Rpc.GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new Chorus.Rpc.PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = ownerInstance,
                Epoch = claimed.Epoch, OperationId = operationId + "-prepare",
                DurationTicks = durationTicks, RandomSeed = 777, TickMs = 25, PackageHash = "pkg-1",
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
            });
        Assert.Equal("Running", prepared.Status);
        return (claimed.Epoch, host.SessionId);
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

    private static async Task<bool> WaitForRoutingWarmAsync(RoutingService.RoutingClient routing)
    {
        // The route the gateway's face must observe: both live keys name a streamer, and both
        // rooms are owned by an instance whose endpoint is served.
        var complete = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var hasStreamA = snapshot.Streamers.Any(streamer => streamer.Platform == "simulator"
                && streamer.CanonicalLiveSessionKey == "gw-host-a");
            var hasStreamB = snapshot.Streamers.Any(streamer => streamer.Platform == "simulator"
                && streamer.CanonicalLiveSessionKey == "gw-host-b");
            var roomA = snapshot.Rooms.FirstOrDefault(room => room.RoomId == "room-route-a");
            var roomB = snapshot.Rooms.FirstOrDefault(room => room.RoomId == "room-route-b");
            return hasStreamA && hasStreamB
                && roomA is not null && roomA.Endpoint.Length > 0
                && roomB is not null && roomB.Endpoint.Length > 0;
        }, TimeSpan.FromSeconds(30));
        if (!complete) return false;
        // The watch answers a version change with a full pull; hold the version still across a
        // couple of reads so the face's cache converges before the first push.
        for (var stable = 0; stable < 2; stable++)
        {
            var before = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            await Task.Delay(600);
            var after = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            if (before != after) return false;
        }
        return true;
    }
}
