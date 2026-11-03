using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.Platform;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-08 end to end against real hosts: a stably identified simulated gift enters the
/// platform's public ingress face, routes through the control plane to its own room, is
/// consumed by a committed tick, and — under the host's configured input translation — scores
/// its recipient's faction. The receipt (DurableAccepted) and the effect (the module's score
/// delta in the committed frame and the client delta) are distinct surfaces; the gift's id,
/// the frame tick and the effect serialize through the database and the subscribed client.
/// A verbatim retry answers Duplicate with exactly one application, and a gift pushed after
/// the result freezes never changes it or enters another round.
/// </summary>
public sealed class RedesignInput08GiftEffectHostTests
{
    private const string GiftBody =
        """{"sessionId":"in08-host-a","eventId":"in08-gift-1","kind":"Gift","giftId":"g-08-1","giftCount":1,"giftValue":100}""";

    [Fact]
    public async Task AGiftWebhookScoresItsFactionOnACommittedTickAndRetriesApplyOnce()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (gamePort, ingressPort) = await StartClusterAsync(environment, "in08-a", "game-in08-a");

        var simulator = new DevelopmentPlatformSimulator(SimulatorKey);
        var host = await MountAsync(environment, simulator, "in08-host-a", "in08-host-a");
        var guest = await MountAsync(environment, simulator, "in08-guest-a", "in08-guest-a");
        const string roomId = "room-in08-a";
        var (epoch, _) = await StartRoomAsync(environment, gamePort, roomId, "game-in08-a", host, guest,
            "in08-start-a", durationTicks: 800);

        var warm = await WaitForRoutingWarmAsync(environment, roomId, "in08-host-a", "in08-guest-a");
        Assert.True(warm, "The gateway's routing snapshot never carried the room route.");

        using var watch = new GiftWatch(environment, gamePort, roomId, epoch);
        await Task.Delay(500);

        // The accept receipt: DurableAccepted on the webhook response. The gift is durably
        // received — that is not the effect, which lands only at the frame commit.
        using var http = await HttpClientAsync(environment);
        var accepted = await PostAsync(http, ingressPort, GiftBody);
        await AssertDurableAsync(accepted, "in08-gift-1");

        // The committed tick applied it: the client delta that carries the gift's id also
        // carries the score effect — red (the host faction) +100 — and the database's frame
        // for that same tick records both the input reference and the same effect.
        var applied = await WaitForAsync(() => Task.FromResult(watch.SawScore("red", 100)),
            TimeSpan.FromSeconds(60));
        Assert.True(applied, "The gift never produced its score effect." +
            $"\nEffects: {string.Join(";", watch.ScoreEffects)}");
        var frame = await FrameOfAsync(environment, roomId, "in08-gift-1");
        Assert.NotNull(frame);
        // The committed frame's effects column records the same score the client delta did:
        // the gift, its frame tick and its effect serialize through both surfaces.
        Assert.Equal(frame!.Value.Tick, watch.TickOf("in08-gift-1"));
        using (var effects = JsonDocument.Parse(frame.Value.Effects))
        {
            var score = effects.RootElement.EnumerateArray()
                .Single(entry => entry.GetProperty("Kind").GetString() == "score")
                .GetProperty("PayloadJson").GetString()!;
            using var payload = JsonDocument.Parse(score);
            Assert.Equal("red", payload.RootElement.GetProperty("faction").GetString());
            Assert.Equal(100, payload.RootElement.GetProperty("delta").GetInt64());
        }

        // The platform retries the callback verbatim: Duplicate through the chain, one
        // delivery row, one input reference — the effect applied exactly once.
        var replay = await PostAsync(http, ingressPort, GiftBody);
        var receipt = Assert.Single(ReadReceipts(await replay.Content.ReadAsStringAsync()));
        Assert.Equal("Duplicate", receipt.GetProperty("disposition").GetString());
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM input.delivery WHERE event_id='in08-gift-1'"));
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM game.frame WHERE room_id=@room AND input_refs::text LIKE '%\"in08-gift-1\"%'",
                ("room", roomId)));
        }

        // The round ran to a clean result: red scored the one gift, blue nothing.
        var result = await ResultAsync(environment, roomId);
        using (var document = JsonDocument.Parse(result))
        {
            Assert.Equal(100L, ScoreOf(document, "red"));
            Assert.Equal(0L, ScoreOf(document, "blue"));
        }
    }

    [Fact]
    public async Task ALateGiftNeverChangesTheFrozenResultOrEntersAnotherRound()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (gamePort, ingressPort) = await StartClusterAsync(environment, "in08-b", "game-in08-b");

        var simulator = new DevelopmentPlatformSimulator(SimulatorKey);
        var host = await MountAsync(environment, simulator, "in08-host-b", "in08-host-b");
        var guest = await MountAsync(environment, simulator, "in08-guest-b", "in08-guest-b");
        const string roomId = "room-in08-b";
        var (epoch, _) = await StartRoomAsync(environment, gamePort, roomId, "game-in08-b", host, guest,
            "in08-start-b", durationTicks: 400);
        Assert.True(await WaitForRoutingWarmAsync(environment, roomId, "in08-host-b", "in08-guest-b"),
            "The gateway's routing snapshot never carried the room route.");

        using var watch = new GiftWatch(environment, gamePort, roomId, epoch);
        await Task.Delay(500);
        using var http = await HttpClientAsync(environment);

        // A live gift applies; the round then freezes its result. The gift names this
        // cluster's own mounted session (in08-host-b) — the shared GiftBody const belongs to
        // fact 1's session and would sit unattributable in the gateway's cache.
        var live = await PostAsync(http, ingressPort,
            """{"sessionId":"in08-host-b","eventId":"in08-gift-1","kind":"Gift","giftId":"g-08-1","giftCount":1,"giftValue":100}""");
        await AssertDurableAsync(live, "in08-gift-1");
        Assert.True(await WaitForAsync(() => Task.FromResult(watch.SawScore("red", 100)),
            TimeSpan.FromSeconds(60)), "The live gift never scored its faction.");
        var frozen = await ResultAsync(environment, roomId);
        using (var document = JsonDocument.Parse(frozen))
            Assert.Equal(100L, ScoreOf(document, "red"));

        // A second gift after the freeze. Its landing disposition is timing's choice: while
        // the room is Finishing its route still resolves and the receiver refuses the push
        // permanently (a room in Finishing does not take input — a 4xx the platform must not
        // retry); once the route retires the push falls to the durable inbox and is held.
        // Neither landing may alter the frozen result, apply an effect, or open a second room.
        var late = await PostAsync(http, ingressPort,
            """{"sessionId":"in08-host-b","eventId":"in08-gift-late","kind":"Gift","giftId":"g-08-2","giftCount":1,"giftValue":50}""");
        var lateBody = await late.Content.ReadAsStringAsync();
        Assert.True(late.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable,
            $"The late push answered unexpectedly: {late.StatusCode} {lateBody}");
        await Task.Delay(2000); // let any route refresh and fallback settle

        var after = await ResultAsync(environment, roomId);
        Assert.Equal(frozen, after); // the result document is byte-identical — nothing moved
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            // The late gift was never consumed into a frame (it may sit pending/attributed).
            var consumed = await ScalarAsync(connection,
                "SELECT count(*) FROM input.delivery WHERE event_id='in08-gift-late' AND state='Consumed'");
            Assert.Equal(0L, consumed);
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT count(*) FROM game.frame WHERE room_id=@room AND input_refs::text LIKE '%\"in08-gift-late\"%'",
                ("room", roomId)));
            // One room for the game — the late gift built no second room.
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM game.room WHERE game_id='game-in08'"));
        }
        Assert.DoesNotContain(watch.ScoreEffects, effect => effect.Contains("50"));
    }

    // ---- cluster ----------------------------------------------------------------------

    private static byte[] SimulatorKey { get; } = RandomNumberGenerator.GetBytes(32);
    private static byte[] AccessKey { get; } = RandomNumberGenerator.GetBytes(32);

    private static async Task<(int GamePort, int IngressPort)> StartClusterAsync(
        RedesignM0HostTests.HostEnvironment environment, string suffix, string gameInstance)
    {
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-in08=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_ROUND_DURATION_TICKS"] = "40",
                ["CHORUS_ROUTE_CONFIRM_TIMEOUT_MS"] = "10000"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(SimulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(AccessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-in08"}}"""
            });
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500",
                // FIX-INPUT-08: the host maps platform deliveries to module commands.
                ["CHORUS_INPUT_TRANSLATION"] = "audience-event"
            },
            clientTenant: "tenant-a", instanceId: gameInstance);
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_DEV_PLATFORM_ENDPOINT"] = "http://127.0.0.1:9/",
                ["CHORUS_INGRESS_PORT"] = ingressPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gatewayPort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500",
                ["Ingress__Platforms__0"] = "douyin",
                ["Ingress__AllowAll"] = "true",
                ["Ingress__Douyin__AppSecret"] = "in08-douyin-secret"
            },
            instanceId: "gateway-" + suffix);
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-" + suffix))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-" + suffix });
        }
        return (gamePort, ingressPort);
    }

    // ---- helpers ----------------------------------------------------------------------

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
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
            AccessCredential = login.AccessCredential, GameId = "game-in08", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }

    private static async Task<(long Epoch, string HostSession)> StartRoomAsync(
        RedesignM0HostTests.HostEnvironment environment, int gamePort, string roomId, string gameInstance,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string operationId,
        int durationTicks)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-in08");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-in08", Kind = "room", EntityId = roomId,
            OwnerInstanceId = gameInstance, LeaseSeconds = 30
        });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-in08");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);
        var started = await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = gameInstance, Epoch = claimed.Epoch, GameId = "game-in08",
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
                RoomId = roomId, GameId = "game-in08", OwnerInstanceId = gameInstance,
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

    private static async Task<bool> WaitForRoutingWarmAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId, params string[] liveKeys)
    {
        using var certificate = environment.Certificate("@cluster", "control-server", "routing-in08");
        using var channel = environment.Channel(environment.ControlPort, certificate, "control-server");
        var routing = new RoutingService.RoutingClient(channel);
        var complete = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamers = liveKeys.All(key => snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == key));
            var room = snapshot.Rooms.FirstOrDefault(room => room.RoomId == roomId);
            return streamers && room is { Endpoint.Length: > 0 };
        }, TimeSpan.FromSeconds(30));
        if (!complete) return false;
        for (var stable = 0; stable < 2; stable++)
        {
            var before = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            await Task.Delay(600);
            if (before != (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version) return false;
        }
        return true;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, int ingressPort, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://localhost:{ingressPort}/ingress/simulator")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(Chorus.PlatformGateway.PlatformIngressEndpoint.AppIdHeader, "app-1");
        return await http.SendAsync(request);
    }

    private static async Task AssertDurableAsync(HttpResponseMessage response, string eventId)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"The push was not accepted: {response.StatusCode}");
        var receipt = Assert.Single(ReadReceipts(await response.Content.ReadAsStringAsync()));
        Assert.Equal("DurableAccepted", receipt.GetProperty("disposition").GetString());
        Assert.Equal(eventId, receipt.GetProperty("eventId").GetString());
    }

    private static List<JsonElement> ReadReceipts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("receipts").EnumerateArray()
            .Select(receipt => receipt.Clone()).ToList();
    }

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

    private static async Task<string> ResultAsync(RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        string? result = null;
        var ready = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            result = await ScalarAsync(connection,
                "SELECT result::text FROM game.result WHERE room_id=@room ORDER BY result_version DESC LIMIT 1",
                ("room", roomId)) as string;
            return result is not null;
        }, TimeSpan.FromSeconds(90));
        Assert.True(ready, "The room never committed its result.");
        return result!;
    }

    private static async Task<(long Tick, string Effects)?> FrameOfAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId, string eventId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT tick_id, effects::text FROM game.frame WHERE room_id=@room AND input_refs::text LIKE @ref " +
            "ORDER BY tick_id LIMIT 1", connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("ref", "%\"" + eventId + "\"%");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt64(0), reader.GetString(1));
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private static long ScoreOf(JsonDocument result, string faction) =>
        result.RootElement.GetProperty("ranking").EnumerateArray()
            .Single(entry => entry.GetProperty("faction").GetString() == faction)
            .GetProperty("score").GetInt64();

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

    /// <summary>A subscribed client as the test's eyes: every committed delta's input ids,
    /// its score effects, and the terminal result.</summary>
    private sealed class GiftWatch : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly Grpc.Net.Client.GrpcChannel _channel;
        private readonly AsyncServerStreamingCall<Chorus.Rpc.SyncMessage> _stream;
        private readonly Task _reading;

        public ConcurrentQueue<string> ScoreEffects { get; } = new();
        public ConcurrentDictionary<string, long> TickByEvent { get; } = new();
        private string? _resultJson;

        public GiftWatch(RedesignM0HostTests.HostEnvironment environment, int gamePort,
            string roomId, long epoch)
        {
            _certificate = environment.Certificate("tenant-a", "streamer-server", "watch-in08");
            _channel = environment.Channel(gamePort, _certificate, "game-server");
            _stream = new Chorus.Rpc.RoundSync.RoundSyncClient(_channel).Subscribe(
                new Chorus.Rpc.SubscribeRequest
                {
                    RoomId = roomId, GameId = "game-in08", Epoch = epoch,
                    LastAppliedTick = 0, LastAppliedStateVersion = 1
                }, deadline: DateTime.UtcNow.Add(TimeSpan.FromSeconds(120)));
            _reading = Task.Run(async () =>
            {
                await foreach (var message in _stream.ResponseStream.ReadAllAsync())
                    switch (message.KindCase)
                    {
                        case Chorus.Rpc.SyncMessage.KindOneofCase.Delta:
                            foreach (var id in message.Delta.InputEventIds)
                                TickByEvent[id] = message.Delta.Tick;
                            foreach (var effect in message.Delta.Effects)
                                if (effect.Kind == "score")
                                    ScoreEffects.Enqueue(effect.PayloadJson);
                            break;
                        case Chorus.Rpc.SyncMessage.KindOneofCase.RoundResult:
                            Volatile.Write(ref _resultJson, message.RoundResult.ResultJson);
                            break;
                        default:
                            break;
                    }
            });
        }

        public bool SawScore(string faction, long delta) => ScoreEffects.Any(effect =>
        {
            using var document = JsonDocument.Parse(effect);
            return document.RootElement.GetProperty("faction").GetString() == faction
                && document.RootElement.GetProperty("delta").GetInt64() == delta;
        });

        public long? TickOf(string eventId) => TickByEvent.TryGetValue(eventId, out var tick) ? tick : null;

        public void Dispose()
        {
            _stream.Dispose();
            _channel.Dispose();
            _certificate.Dispose();
            // The reading task ends with its stream; disposing it here would require the
            // stream to have ended first, which the final frame's timing does not promise.
        }
    }
}
