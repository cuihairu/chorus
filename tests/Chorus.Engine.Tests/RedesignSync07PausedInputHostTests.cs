using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-07 acceptance (暂停期间的输入处置, authoritative-sync.md:52): a paused room's
/// incoming platform event, delivered through the real gateway webhook face, lands durably in
/// the room's inbox under the paused-input policy the room declared — bounded and observable,
/// never silently dropped. The ingest leg only attributes; the gateway's routing trigger
/// (FIX-SYNC-07) retires the attributed event into the store's paused-input gate, so a paused
/// room's Buffer policy yields a Pending delivery at the durable accept order with a frozen
/// watermark, a full buffer answers the overflow disposition without moving the cursor, and
/// Compensate/Reject record their terminal dispositions instead of a delivery. The mechanism
/// (the policy branch and the cap) is store-level in
/// <see cref="RedesignRoundInputStoreTests"/>; this is the full chain through the webhook.
/// </summary>
public sealed class RedesignSync07PausedInputHostTests
{
    [Fact]
    public async Task AWebhookIntoAPausedBufferRoomLandsAPendingDeliveryThroughTheGate()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await StartServersAsync(environment, simulatorKey, accessKey, ingressPort, gatewayPort);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var clients = await ClientsAsync(environment);
        var (control, rounds) = (clients.Control, clients.Rounds);
        var room = await BringUpPausedRoomAsync(environment, control, rounds, simulator,
            "room-sync-07-a", "PauseAtBoundary|30|120|1|Buffer");

        using var http = await HttpClientAsync(environment);
        var response = await PostAsync(http, ingressPort, "simulator", "app-1",
            WebhookBody(room.LiveKey, "sync07-gift-a1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDurable(await response.Content.ReadAsStringAsync(), "sync07-gift-a1");

        // The paused room's buffer accepted the event: a Pending delivery at the durable
        // accept order, the event routed with the room attributed, the watermark untouched
        // (nothing consumed), and no frame — the buffer is durable, not played.
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using (var delivery = new NpgsqlCommand("""
            SELECT d.state,d.sequence,c.consumed_contiguous_sequence
            FROM input.delivery d
            JOIN input.cursor c ON c.consumer_scope=d.consumer_scope
            WHERE d.consumer_scope='room:room-sync-07-a'
            """, connection))
        await using (var reader = await delivery.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "The paused room got no delivery.");
            Assert.Equal("Pending", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
        }
        await using (var events = new NpgsqlCommand("""
            SELECT count(*) FROM input.event
            WHERE disposition='Routed' AND attributed_room_id='room-sync-07-a'
            """, connection))
            Assert.Equal(1L, Convert.ToInt64(await events.ExecuteScalarAsync()));
        await using (var frames = new NpgsqlCommand(
            "SELECT count(*) FROM game.frame WHERE room_id='room-sync-07-a'", connection))
            Assert.Equal(0L, Convert.ToInt64(await frames.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AWebhookPastTheBufferCapAnswersOverflowVisiblyWithoutADelivery()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await StartServersAsync(environment, simulatorKey, accessKey, ingressPort, gatewayPort);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var clients = await ClientsAsync(environment);
        var (control, rounds) = (clients.Control, clients.Rounds);
        var room = await BringUpPausedRoomAsync(environment, control, rounds, simulator,
            "room-sync-07-b", "PauseAtBoundary|30|120|1|Buffer");

        // Pre-fill the bounded buffer to the cap through the data plane (the same store gate
        // the webhook trigger drives), then one webhook past the cap. The data plane answers
        // platform ingest and routing to the gateway's own process role.
        using var gameCertificate = environment.Certificate("@cluster", "platform-gateway", "ingest-sync07-b");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var ingest = new PlatformIngest.PlatformIngestClient(gameChannel);
        var input = new RoundInput.RoundInputClient(gameChannel);
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "prefill-b" };
        for (var i = 1; i <= 500; i++)
            batch.Events.Add(new IngestEvent
            {
                SourceDedupKey = $"pre-gift-{i}", Durability = "Durable",
                CanonicalRoomKey = room.LiveKey,
                PayloadJson = $"{{\"kind\":\"gift\",\"giftId\":\"g{i}\",\"giftCount\":1,\"giftValue\":{i}}}"
            });
        await ingest.IngestBatchAsync(batch, deadline: DateTime.UtcNow.AddSeconds(10));
        var prefilled = await input.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "prefill-route-b", MaxEvents = 500
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.Equal(500, prefilled.Events.Count);
        Assert.All(prefilled.Events, item => Assert.Equal("Routed", item.Disposition));

        // The 501st event, arriving through the real webhook, overflows visibly: the room is
        // attributed, the overflow disposition is recorded, no delivery is written and the
        // cursor stays at 501 — no accepted event is dropped, none is silent.
        using var http = await HttpClientAsync(environment);
        var response = await PostAsync(http, ingressPort, "simulator", "app-1",
            WebhookBody(room.LiveKey, "sync07-gift-b501"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDurable(await response.Content.ReadAsStringAsync(), "sync07-gift-b501");

        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using (var overflow = new NpgsqlCommand("""
            SELECT disposition,attributed_room_id FROM input.event WHERE source_dedup_key='sync07-gift-b501'
            """, connection))
        await using (var reader = await overflow.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "The overflow event vanished.");
            Assert.Equal("PausedBufferFull", reader.GetString(0));
            Assert.Equal("room-sync-07-b", reader.GetString(1));
        }
        await using (var deliveries = new NpgsqlCommand(
            "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-sync-07-b'", connection))
            Assert.Equal(500L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
        await using (var cursor = new NpgsqlCommand("""
            SELECT next_delivery_sequence FROM input.cursor WHERE consumer_scope='room:room-sync-07-b'
            """, connection))
            Assert.Equal(501L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task APausedCompensateAndRejectRoomAnswerThroughTheRealFace()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await StartServersAsync(environment, simulatorKey, accessKey, ingressPort, gatewayPort);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var clients = await ClientsAsync(environment);
        var (control, rounds) = (clients.Control, clients.Rounds);
        var compensate = await BringUpPausedRoomAsync(environment, control, rounds, simulator,
            "room-sync-07-c", "PauseAtBoundary|30|120|1|Compensate");
        var reject = await BringUpPausedRoomAsync(environment, control, rounds, simulator,
            "room-sync-07-d", "PauseAtBoundary|30|120|1|Reject");

        using var http = await HttpClientAsync(environment);
        var accepted = await PostAsync(http, ingressPort, "simulator", "app-1",
            WebhookBody(compensate.LiveKey, "sync07-gift-c1"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var refused = await PostAsync(http, ingressPort, "simulator", "app-1",
            WebhookBody(reject.LiveKey, "sync07-gift-d1"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);

        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using (var marked = new NpgsqlCommand("""
            SELECT disposition,attributed_room_id FROM input.event
            WHERE source_dedup_key IN ('sync07-gift-c1','sync07-gift-d1')
            ORDER BY source_dedup_key
            """, connection))
        {
            var rows = new List<(string Disposition, string Room)>();
            await using (var reader = await marked.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    rows.Add((reader.GetString(0), reader.GetString(1)));
            Assert.Equal([("Compensated", "room-sync-07-c"), ("PausedRejected", "room-sync-07-d")], rows);
        }
        await using (var deliveries = new NpgsqlCommand("SELECT count(*) FROM input.delivery", connection))
            Assert.Equal(0L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
    }

    private sealed record PausedRoom(long Epoch, string LiveKey, string RoomId);

    private static async Task<RedesignM0HostTests.HostEnvironment> StartServersAsync(
        RedesignM0HostTests.HostEnvironment environment, byte[] simulatorKey, byte[] accessKey,
        int ingressPort, int gatewayPort)
    {
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });
        // No control endpoint on the gateway: the webhook face keeps its durable attribution
        // leg (no routing to resolve), and the FIX-SYNC-07 trigger retires the attributed
        // event into the paused-input gate — the leg this test drives.
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = "http://127.0.0.1:9/",
                ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(CultureInfo.InvariantCulture),
                ["Ingress__Platforms__0"] = "douyin",
                ["Ingress__AllowAll"] = "true",
                ["Ingress__Douyin__AppSecret"] = "sync07-douyin-secret"
            }, instanceId: "gateway-sync07");
        return environment;
    }

    private sealed record TestClients(
        Chorus.Rpc.ControlService.ControlServiceClient Control,
        Chorus.Rpc.RoundStore.RoundStoreClient Rounds,
        X509Certificate2 ControlCertificate,
        Grpc.Net.Client.GrpcChannel ControlChannel,
        X509Certificate2 GameCertificate,
        Grpc.Net.Client.GrpcChannel GameChannel) : IDisposable
    {
        public void Dispose()
        {
            ControlChannel.Dispose();
            ControlCertificate.Dispose();
            GameChannel.Dispose();
            GameCertificate.Dispose();
        }
    }

    private static async Task<TestClients> ClientsAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        var controlCertificate = environment.Certificate("@cluster", "control-server", "control-sync07");
        var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-sync07");
        var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        return new TestClients(
            new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel),
            new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel),
            controlCertificate, controlChannel, gameCertificate, gameChannel);
    }

    private static async Task<PausedRoom> BringUpPausedRoomAsync(
        RedesignM0HostTests.HostEnvironment environment,
        Chorus.Rpc.ControlService.ControlServiceClient control,
        Chorus.Rpc.RoundStore.RoundStoreClient rounds,
        DevelopmentPlatformSimulator simulator, string roomId, string offlinePolicy)
    {
        var operation = "sync07-" + roomId;
        var host = await MountAsync(environment, simulator, operation + "-host", "live-" + roomId);
        var guest = await MountAsync(environment, simulator, operation + "-guest", "live-" + roomId + "-g");
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = operation + "-start",
            Participants =
            {
                new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        await rounds.PrepareRoundAsync(new PrepareRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            OperationId = operation + "-prepare", TickMs = 25, RecoveryMode = "DeterministicReplay",
            OfflinePolicy = offlinePolicy,
            SnapshotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64)),
            SnapshotHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            SnapshotSchemaVersion = 1
        });
        await rounds.ActivateRoundAsync(new ActivateRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            OperationId = operation + "-activate"
        });
        var paused = await rounds.PauseRoundAsync(new PauseRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            Reason = "all control sessions offline past the declared grace",
            OperationId = operation + "-pause", Tick = 0, StateVersion = 1
        });
        Assert.Equal("Paused", paused.Status);
        return new PausedRoom(owner.Epoch, "live-" + roomId, roomId);
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
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }

    private static string WebhookBody(string sessionId, string eventId) =>
        $"{{\"sessionId\":\"{sessionId}\",\"eventId\":\"{eventId}\",\"kind\":\"Gift\",\"giftId\":\"g1\",\"giftCount\":1,\"giftValue\":100}}";

    private static void AssertDurable(string json, string eventId)
    {
        using var document = JsonDocument.Parse(json);
        var receipt = Assert.Single(document.RootElement.GetProperty("receipts").EnumerateArray());
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
}
