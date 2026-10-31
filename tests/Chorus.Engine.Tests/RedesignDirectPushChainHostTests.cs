using System.Collections.Concurrent;
using System.Globalization;
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
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-06 end to end: the whole direct-push chain against real hosts — a simulator
/// webhook into the gateway's ingress face, routed through the control plane's snapshot to
/// the owning game host's room-event RPC, seated in the RoomActor, consumed by a frame
/// commit, and published to a subscribed client as a delta that carries the event id. Two
/// rooms on two different hosts each receive only their own events. A host killed after a
/// confirmed push — on either side of the frame commit — never loses the input: the durable
/// delivery is the truth, consumed or pending, and nothing in these tests calls RouteEvents
/// or the pull inbox; every input enters through the webhook face alone.
/// </summary>
public sealed class RedesignDirectPushChainHostTests
{
    [Fact]
    public async Task AWebhookReachesItsOwnRoomFrameAndClientDeltaThroughTheGateway()
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
                // Nothing here waits for a lease to lapse, and the pushes must find the lease
                // alive however starved the runner — a lapsed lease answers a correct
                // StaleOwner that says nothing about the chain. The tight 6s/3s recipe
                // starves on 2-core CI.
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15"
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
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, ingressPort, controlEndpoint, gatewayPort),
            instanceId: "gateway-chain");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-chain");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-chain");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "game-a", "game-b");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        var hostA = await MountAsync(environment, simulator, "chain-open-a", "chain-host-a");
        var guestA = await MountAsync(environment, simulator, "chain-open-guest-a", "chain-guest-a");
        var hostB = await MountAsync(environment, simulator, "chain-open-b", "chain-host-b");
        var guestB = await MountAsync(environment, simulator, "chain-open-guest-b", "chain-guest-b");

        const string roomA = "room-chain-a";
        const string roomB = "room-chain-b";
        var (epochA, _) = await StartRoomAsync(environment, control, rounds, roomA,
            "game-a", gamePortA, controlCertificate, hostA, guestA, "chain-start-a", durationTicks: 800);
        var (epochB, _) = await StartRoomAsync(environment, control, rounds, roomB,
            "game-b", gamePortB, controlCertificate, hostB, guestB, "chain-start-b", durationTicks: 800);

        var warm = await WaitForRoutingWarmAsync(routing,
            ["chain-host-a", "chain-host-b"], [roomA, roomB]);
        Assert.True(warm, "The gateway's routing snapshot never carried the two routes.");

        // The clients subscribe before any push: a delta only reaches an attached subscriber,
        // and the whole point of this leg is the client-visible input.
        using var watchA = new RoomWatch(environment, gamePortA, roomA, epochA, "chain-watch-a");
        using var watchB = new RoomWatch(environment, gamePortB, roomB, epochB, "chain-watch-b");
        await Task.Delay(500);

        // One webhook per room through the same public face — the platform's own retry is the
        // replay at the end. Nothing here touches RouteEvents or the pull inbox.
        using var http = await HttpClientAsync(environment);
        var pushedA = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"chain-host-a","eventId":"chain-gift-a1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        await AssertDurableAsync(pushedA, "chain-gift-a1");
        var pushedB = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"chain-host-b","eventId":"chain-gift-b1","kind":"Gift","giftId":"g2","giftCount":2,"giftValue":50}""");
        await AssertDurableAsync(pushedB, "chain-gift-b1");

        // Each room's own client saw its own event in a committed delta — and never the other
        // room's: InputEventIds come from that room's own frame commits alone.
        var delivered = await WaitForAsync(
            () => Task.FromResult(watchA.Saw("chain-gift-a1") && watchB.Saw("chain-gift-b1")),
            TimeSpan.FromSeconds(30));
        Assert.True(delivered, "The pushed events never reached their rooms' client deltas. " +
            $"A saw {string.Join(",", watchA.InputIds)}; B saw {string.Join(",", watchB.InputIds)}; " +
            $"notes A={string.Join(",", watchA.Notes)} B={string.Join(",", watchB.Notes)}.");
        Assert.False(watchA.Saw("chain-gift-b1"), "Room A's stream carried room B's event.");
        Assert.False(watchB.Saw("chain-gift-a1"), "Room B's stream carried room A's event.");

        // The data plane agrees: one delivery per event, consumed by a frame commit, each in
        // its own room's inbox, and each event attributed to exactly its own room.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("""
                SELECT event_id,count(*),count(*) FILTER (WHERE state='Consumed'),min(room_id)
                FROM input.delivery WHERE event_id IN ('chain-gift-a1','chain-gift-b1')
                GROUP BY event_id
                """, connection))
            await using (var reader = await deliveries.ExecuteReaderAsync())
            {
                var rows = 0;
                while (await reader.ReadAsync())
                {
                    rows++;
                    Assert.Equal(1L, reader.GetInt64(1));
                    Assert.Equal(1L, reader.GetInt64(2));
                    Assert.Equal(reader.GetString(0) == "chain-gift-a1" ? roomA : roomB,
                        reader.GetString(3));
                }
                Assert.Equal(2, rows);
            }
            Assert.Equal(roomA, await RoomOfAsync(connection, "chain-gift-a1"));
            Assert.Equal(roomB, await RoomOfAsync(connection, "chain-gift-b1"));
            Assert.True(await FrameContainsAsync(connection, roomA, "chain-gift-a1"));
            Assert.True(await FrameContainsAsync(connection, roomB, "chain-gift-b1"));
        }

        // The platform retries callback A verbatim: the same event id answers Duplicate
        // through the whole chain and nothing lands a second time.
        var replay = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"chain-host-a","eventId":"chain-gift-a1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        Assert.True(replay.StatusCode == HttpStatusCode.OK,
            $"The retried callback was not accepted: {replay.StatusCode}");
        var receipt = Assert.Single(ReadReceipts(await replay.Content.ReadAsStringAsync()));
        Assert.Equal("Duplicate", receipt.GetProperty("disposition").GetString());
        Assert.True(receipt.GetProperty("duplicate").GetBoolean());

        // Both rooms run to a clean finish with their results published — the pushed inputs
        // never broke a tick (a broken room ends its streams with a resync instead).
        var finished = await WaitForAsync(
            () => Task.FromResult(watchA.ResultJson is not null && watchB.ResultJson is not null),
            TimeSpan.FromSeconds(90));
        Assert.True(finished, "The rooms did not publish their results to their clients. " +
            $"A notes={string.Join(",", watchA.Notes)}; B notes={string.Join(",", watchB.Notes)}.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var rows = new NpgsqlCommand(
                "SELECT event_id,count(*) FROM input.delivery " +
                "WHERE event_id IN ('chain-gift-a1','chain-gift-b1') GROUP BY event_id", connection);
            await using var reader = await rows.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                Assert.Equal(1L, reader.GetInt64(1)); // the replay added no second delivery
        }
    }

    [Fact]
    public async Task AKilledHostLosesNoConfirmedPushOnEitherSideOfTheCommit()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                // No reclaim happens in this fact — the host dies and stays dead — so the
                // wide lease only keeps the pushes fenced correctly under CI starvation.
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15"
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
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            GatewayEnvironment(environment.DataPort, ingressPort, controlEndpoint, gatewayPort),
            instanceId: "gateway-kill");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-kill");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-kill");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "game-a");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        var host = await MountAsync(environment, simulator, "kill-open-a", "kill-host-a");
        var guest = await MountAsync(environment, simulator, "kill-open-guest-a", "kill-guest-a");
        const string room = "room-kill-a";
        var (epoch, _) = await StartRoomAsync(environment, control, rounds, room,
            "game-a", gamePort, controlCertificate, host, guest, "kill-start-a", durationTicks: 400);

        var warm = await WaitForRoutingWarmAsync(routing, ["kill-host-a"], [room]);
        Assert.True(warm, "The gateway's routing snapshot never carried the route.");
        using var http = await HttpClientAsync(environment);

        // The first push is confirmed and deliberately allowed to commit: killed after the
        // commit, its evidence must all survive the crash.
        var committed = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"kill-host-a","eventId":"kill-post-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}""");
        await AssertDurableAsync(committed, "kill-post-1");
        var consumed = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await DeliveryStateAsync(connection, "kill-post-1") == "Consumed" &&
                await FrameContainsAsync(connection, room, "kill-post-1");
        }, TimeSpan.FromSeconds(30));
        Assert.True(consumed, "The first push was never consumed by a frame commit.");

        // The second push is confirmed and killed for: the kill races the tick that would
        // consume it, and the assertion below holds on either side of that race — the input
        // is durable either way, which is the whole contract of a confirmed push.
        var pending = await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"kill-host-a","eventId":"kill-pre-2","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":100}""");
        await AssertDurableAsync(pending, "kill-pre-2");
        environment.Kill("game-a");
        await Task.Delay(1000); // let the crash settle; every row below was committed already

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            // Past the commit: consumed delivery, frame reference and attribution all intact.
            Assert.Equal("Consumed", await DeliveryStateAsync(connection, "kill-post-1"));
            Assert.True(await FrameContainsAsync(connection, room, "kill-post-1"));
            Assert.Equal(room, await RoomOfAsync(connection, "kill-post-1"));
            // Before the commit (or racing it): exactly one durable row whose state the crash
            // left wherever the race ended it — Pending is a recoverable input (FIX-INPUT-07
            // proves the recovery consumes it), Consumed must come with its frame evidence.
            var state = await DeliveryStateAsync(connection, "kill-pre-2");
            Assert.True(state is "Pending" or "Consumed",
                $"The confirmed push was neither pending nor consumed after the crash: {state}.");
            if (state == "Consumed")
                Assert.True(await FrameContainsAsync(connection, room, "kill-pre-2"));
            Assert.Equal(room, await RoomOfAsync(connection, "kill-pre-2"));
        }
    }

    /// <summary>A subscribed client as the test's eyes: every committed delta's input ids in
    /// arrival order, plus the terminal result and any non-delta message for diagnostics.</summary>
    private sealed class RoomWatch : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly Grpc.Net.Client.GrpcChannel _channel;
        private readonly AsyncServerStreamingCall<Chorus.Rpc.SyncMessage> _stream;
        private readonly Task _reading;
        private string? _resultJson;

        public ConcurrentQueue<string> InputIds { get; } = new();
        public ConcurrentQueue<string> Notes { get; } = new();
        public string? ResultJson => Volatile.Read(ref _resultJson);

        public RoomWatch(RedesignM0HostTests.HostEnvironment environment, int gamePort,
            string roomId, long epoch, string syncId)
        {
            _certificate = environment.Certificate("tenant-a", "streamer-server", syncId);
            _channel = environment.Channel(gamePort, _certificate, "game-server");
            _stream = new Chorus.Rpc.RoundSync.RoundSyncClient(_channel).Subscribe(
                new Chorus.Rpc.SubscribeRequest
                {
                    RoomId = roomId, GameId = "game-a", Epoch = epoch,
                    LastAppliedTick = 0, LastAppliedStateVersion = 1
                }, deadline: DateTime.UtcNow.Add(TimeSpan.FromSeconds(120)));
            _reading = Task.Run(async () =>
            {
                await foreach (var message in _stream.ResponseStream.ReadAllAsync())
                    switch (message.KindCase)
                    {
                        case Chorus.Rpc.SyncMessage.KindOneofCase.Delta:
                            foreach (var id in message.Delta.InputEventIds) InputIds.Enqueue(id);
                            break;
                        case Chorus.Rpc.SyncMessage.KindOneofCase.RoundResult:
                            Volatile.Write(ref _resultJson, message.RoundResult.ResultJson);
                            break;
                        default:
                            Notes.Enqueue(message.KindCase.ToString());
                            break;
                    }
            });
        }

        public bool Saw(string eventId) => InputIds.Contains(eventId);

        public void Dispose()
        {
            _stream.Dispose();
            _channel.Dispose();
            _certificate.Dispose();
            _reading.Dispose();
        }
    }

    private static async Task<string?> DeliveryStateAsync(NpgsqlConnection connection, string eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT state FROM input.delivery WHERE event_id=@event", connection);
        command.Parameters.AddWithValue("event", eventId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> RoomOfAsync(NpgsqlConnection connection, string eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT attributed_room_id FROM input.event WHERE event_id=@event", connection);
        command.Parameters.AddWithValue("event", eventId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<bool> FrameContainsAsync(
        NpgsqlConnection connection, string roomId, string eventId)
    {
        await using var frame = new NpgsqlCommand(
            "SELECT input_refs FROM game.frame WHERE room_id=@room ORDER BY tick_id", connection);
        frame.Parameters.AddWithValue("room", roomId);
        await using var reader = await frame.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (reader.GetString(0).Contains("\"" + eventId + "\"", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static List<JsonElement> ReadReceipts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("receipts").EnumerateArray()
            .Select(receipt => receipt.Clone()).ToList();
    }

    private static async Task AssertDurableAsync(HttpResponseMessage response, string eventId)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"The push was not accepted: {response.StatusCode}");
        var receipt = Assert.Single(ReadReceipts(await response.Content.ReadAsStringAsync()));
        Assert.Equal("DurableAccepted", receipt.GetProperty("disposition").GetString());
        Assert.Equal(eventId, receipt.GetProperty("eventId").GetString());
    }

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

    private static async Task<bool> WaitForRoutingWarmAsync(RoutingService.RoutingClient routing,
        string[] liveKeys, string[] roomIds)
    {
        // The route the gateway's face must observe: every live key names a streamer, and
        // every room is owned by an instance whose endpoint is served.
        var complete = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamers = liveKeys.All(key => snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == key));
            var rooms = roomIds.All(id => snapshot.Rooms.FirstOrDefault(room => room.RoomId == id)
                is { Endpoint.Length: > 0 });
            return streamers && rooms;
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
        ["Ingress__Douyin__AppSecret"] = "chain-douyin-secret"
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
        RedesignM0HostTests.HostEnvironment environment, RoutingService.RoutingClient routing,
        params string[] instanceIds)
    {
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-chain"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-chain" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            return instanceIds.All(id => snapshot.Instances.Any(instance =>
                instance.ProcessInstanceId == id && instance.Endpoint.Length > 0));
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
            ModuleVersion = "1.1.0", ConfigHash = "cfg-1-v2", PackageHash = "pkg-1-v2",
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
                DurationTicks = durationTicks, RandomSeed = 777, TickMs = 25, PackageHash = "pkg-1-v2",
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
}
