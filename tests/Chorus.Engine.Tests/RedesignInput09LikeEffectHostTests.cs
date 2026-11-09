using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.Platform;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-09 end to end against real hosts: a declared platform capability — a like — enters
/// the platform's public ingress, routes to its own room, is consumed by a committed tick under
/// the host's configured translation, and accumulates into its recipient faction's count. The
/// receipt (DurableAccepted) and the effect (the module's like delta in the committed frame and
/// the client delta) are distinct surfaces; the report reads clicks, never callbacks; the
/// threshold behavior fires the configured number of times; a verbatim retry answers Duplicate
/// with exactly one application; and the result's likes section reconciles the applied total
/// against what the platform accepted.
/// </summary>
public sealed class RedesignInput09LikeEffectHostTests
{
    private const string HostLiveKey = "in09-host-a";
    private const string GuestLiveKey = "in09-guest-a";

    [Fact]
    public async Task ALikeWebhookAccumulatesItsClicksIntoTheFactionCountAndReconciles()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (gamePort, ingressPort) = await StartClusterAsync(environment, "in09-a", "game-in09-a");

        var simulator = new DevelopmentPlatformSimulator(SimulatorKey);
        var host = await MountAsync(environment, simulator, "in09-host-a", HostLiveKey);
        var guest = await MountAsync(environment, simulator, "in09-guest-a", GuestLiveKey);
        const string roomId = "room-in09-a";
        var (epoch, _) = await StartRoomAsync(environment, gamePort, roomId, "game-in09-a", host, guest,
            "in09-start-a", durationTicks: 400);

        var warm = await WaitForRoutingWarmAsync(environment, roomId, HostLiveKey, GuestLiveKey);
        Assert.True(warm, "The gateway's routing snapshot never carried the room route.");

        using var watch = new LikeWatch(environment, gamePort, roomId, epoch);
        await Task.Delay(500);

        using var http = await HttpClientAsync(environment);

        // The accept receipt: DurableAccepted on each webhook response — durably received, that
        // is not the effect, which lands only at the frame commit.
        var merged = await PostAsync(http, ingressPort,
            LikeBody("in09-like-1", HostLiveKey, "5")); // one callback standing for five clicks
        await AssertDurableAsync(merged, "in09-like-1");
        var single = await PostAsync(http, ingressPort,
            LikeBody("in09-like-2", HostLiveKey)); // one physical click
        await AssertDurableAsync(single, "in09-like-2");

        // The committed ticks applied both: the client deltas that carry the like ids also
        // carry the like effects — six clicks in total for red (the host faction), merged per
        // tick window — and the database's frames for those ticks record the same references.
        var applied = await WaitForAsync(() => Task.FromResult(watch.LikeTotal(Red) == 6),
            TimeSpan.FromSeconds(60));
        Assert.True(applied, "The likes never produced their like effect." +
            $"\nLike effects: {string.Join(";", watch.LikeEffects)}");
        var frameOne = await FrameOfAsync(environment, roomId, "in09-like-1");
        var frameTwo = await FrameOfAsync(environment, roomId, "in09-like-2");
        Assert.NotNull(frameOne);
        Assert.NotNull(frameTwo);

        // The platform retries the first callback verbatim: Duplicate through the chain, one
        // delivery row, one input reference — the six clicks applied exactly once.
        var replay = await PostAsync(http, ingressPort, LikeBody("in09-like-1", HostLiveKey, "5"));
        var receipt = Assert.Single(ReadReceipts(await replay.Content.ReadAsStringAsync()));
        Assert.Equal("Duplicate", receipt.GetProperty("disposition").GetString());
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM input.delivery WHERE event_id='in09-like-1'"));
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM game.frame WHERE room_id=@room AND input_refs::text LIKE '%\"in09-like-1\"%'",
                ("room", roomId)));
        }

        // The round ran to a clean result: the applied likes section reconciles with what the
        // platform accepted — six clicks for red, none for blue — below the shipped threshold of
        // 20, so no bonus and the ranking is untouched by the like rule.
        var result = await ResultAsync(environment, roomId);
        using (var document = JsonDocument.Parse(result))
        {
            Assert.Equal(6L, LikeOf(document, Red));
            Assert.Equal(0L, LikeOf(document, Blue));
            Assert.Equal(0L, ScoreOf(document, Red));
        }
    }

    [Fact]
    public async Task ALikeBatchThresholdBehaviorFiresTheConfiguredNumberOfTimes()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (gamePort, ingressPort) = await StartClusterAsync(environment, "in09-b", "game-in09-b");

        var simulator = new DevelopmentPlatformSimulator(SimulatorKey);
        var host = await MountAsync(environment, simulator, "in09-host-b", "in09-host-b");
        var guest = await MountAsync(environment, simulator, "in09-guest-b", "in09-guest-b");
        const string roomId = "room-in09-b";
        var (epoch, _) = await StartRoomAsync(environment, gamePort, roomId, "game-in09-b", host, guest,
            "in09-start-b", durationTicks: 400);
        Assert.True(await WaitForRoutingWarmAsync(environment, roomId, "in09-host-b", "in09-guest-b"),
            "The gateway's routing snapshot never carried the room route.");

        using var watch = new LikeWatch(environment, gamePort, roomId, epoch);
        await Task.Delay(500);
        using var http = await HttpClientAsync(environment);

        // One callback carrying forty clicks (the reducer's merged batch): at the shipped
        // threshold of 20 it fires exactly two bursts — floor(40 / 20) = 2 — and adds the
        // shipped bonus twice. The threshold behavior counts clicks, not callbacks.
        var batch = await PostAsync(http, ingressPort, LikeBody("in09-batch-1", "in09-host-b", "40"));
        await AssertDurableAsync(batch, "in09-batch-1");

        var applied = await WaitForAsync(() => Task.FromResult(watch.BurstCount >= 2),
            TimeSpan.FromSeconds(60));
        Assert.True(applied, "The like batch never reached its two threshold bursts." +
            $"\nBurst effects: {string.Join(";", watch.BurstEffects)}");
        Assert.True(await WaitForAsync(() => Task.FromResult(watch.LikeTotal(Red) == 40),
            TimeSpan.FromSeconds(60)), "The batch never reported forty clicks.");
        Assert.Equal(2, watch.BurstCount);
        Assert.True(await WaitForAsync(() => Task.FromResult(watch.SawScore(Red, 100)),
            TimeSpan.FromSeconds(60)), "The two bursts never added the bonus twice.");

        var result = await ResultAsync(environment, roomId);
        using (var document = JsonDocument.Parse(result))
        {
            Assert.Equal(40L, LikeOf(document, Red));
            Assert.Equal(100L, ScoreOf(document, Red)); // 2 bursts × the shipped bonus of 50
        }
    }

    // ---- cluster ----------------------------------------------------------------------

    private const string Red = GameStartConfig.RedFaction;
    private const string Blue = GameStartConfig.BlueFaction;

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
                ["CHORUS_GAME_ENDPOINTS"] = $"game-in09=https://localhost:{gamePort}",
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
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-in09"}}"""
            });
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500",
                // FIX-INPUT-08/09: the host maps platform deliveries to module commands — the
                // translator takes the gift and the like commands, and passes everything else.
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
                ["Ingress__Douyin__AppSecret"] = "in09-douyin-secret"
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
            AccessCredential = login.AccessCredential, GameId = "game-in09", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }

    private static async Task<(long Epoch, string HostSession)> StartRoomAsync(
        RedesignM0HostTests.HostEnvironment environment, int gamePort, string roomId, string gameInstance,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string operationId,
        int durationTicks)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-in09");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-in09", Kind = "room", EntityId = roomId,
            OwnerInstanceId = gameInstance, LeaseSeconds = 30
        });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-in09");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);
        var started = await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = gameInstance, Epoch = claimed.Epoch, GameId = "game-in09",
            ModuleVersion = "1.2.0", ConfigHash = "cfg-1-v3", PackageHash = "pkg-1-v3",
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
                RoomId = roomId, GameId = "game-in09", OwnerInstanceId = gameInstance,
                Epoch = claimed.Epoch, OperationId = operationId + "-prepare",
                DurationTicks = durationTicks, RandomSeed = 777, TickMs = 25, PackageHash = "pkg-1-v3",
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

    private static async Task<bool> WaitForRoutingWarmAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId, params string[] liveKeys)
    {
        using var certificate = environment.Certificate("@cluster", "control-server", "routing-in09");
        using var channel = environment.Channel(environment.ControlPort, certificate, "control-server");
        var routing = new RoutingService.RoutingClient(channel);
        var complete = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamers = liveKeys.All(key => snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == key));
            var room = snapshot.Rooms.FirstOrDefault(room => room.RoomId == roomId);
            if (!(streamers && room is { Endpoint.Length: > 0 })) return false;
            // A settled snapshot: the route stands AND the version held still for two
            // 600 ms windows. A legitimate late bump — a gateway's readiness word
            // flipping as its probe first answers, a session rotation — restarts the
            // hunt inside the same 30 s budget instead of failing the whole wait.
            for (var stable = 0; stable < 2; stable++)
            {
                var before = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
                await Task.Delay(600);
                if (before != (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version) return false;
            }
            return true;
        }, TimeSpan.FromSeconds(30));
        return complete;
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

    private static string LikeBody(string eventId, string liveKey, string? text = null) => string.IsNullOrEmpty(text)
        ? $$"""{"sessionId":"{{liveKey}}","eventId":"{{eventId}}","kind":"Like","likeId":"l-1","likeCount":1}"""
        : $$"""{"sessionId":"{{liveKey}}","eventId":"{{eventId}}","kind":"Like","likeId":"l-1","likeCount":1,"text":"{{text}}"}""";

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

    private static long LikeOf(JsonDocument result, string faction) =>
        result.RootElement.GetProperty("likes").EnumerateArray()
            .Single(entry => entry.GetProperty("faction").GetString() == faction)
            .GetProperty("count").GetInt64();

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

    /// <summary>A subscribed client as the test's eyes: every committed delta's input ids, its
    /// like/score/burst effects, and the terminal result.</summary>
    private sealed class LikeWatch : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly Grpc.Net.Client.GrpcChannel _channel;
        private readonly AsyncServerStreamingCall<Chorus.Rpc.SyncMessage> _stream;
        private readonly Task _reading;

        public ConcurrentQueue<string> LikeEffects { get; } = new();
        public ConcurrentQueue<string> BurstEffects { get; } = new();
        public int BurstCount => Volatile.Read(ref _burstCount);
        private int _burstCount;
        private readonly ConcurrentDictionary<string, long> _likeTotal = new(StringComparer.Ordinal);

        public LikeWatch(RedesignM0HostTests.HostEnvironment environment, int gamePort,
            string roomId, long epoch)
        {
            _certificate = environment.Certificate("tenant-a", "streamer-server", "watch-in09");
            _channel = environment.Channel(gamePort, _certificate, "game-server");
            _stream = new Chorus.Rpc.RoundSync.RoundSyncClient(_channel).Subscribe(
                new Chorus.Rpc.SubscribeRequest
                {
                    RoomId = roomId, GameId = "game-in09", Epoch = epoch,
                    LastAppliedTick = 0, LastAppliedStateVersion = 1
                }, deadline: DateTime.UtcNow.Add(TimeSpan.FromSeconds(120)));
            _reading = Task.Run(async () =>
            {
                await foreach (var message in _stream.ResponseStream.ReadAllAsync())
                    switch (message.KindCase)
                    {
                        case Chorus.Rpc.SyncMessage.KindOneofCase.Delta:
                            foreach (var effect in message.Delta.Effects)
                                switch (effect.Kind)
                                {
                                    case "like":
                                        LikeEffects.Enqueue(effect.PayloadJson);
                                        using (var document = JsonDocument.Parse(effect.PayloadJson))
                                        {
                                            var faction = document.RootElement.GetProperty("faction").GetString()!;
                                            _likeTotal.AddOrUpdate(faction,
                                                document.RootElement.GetProperty("count").GetInt64(),
                                                (_, old) => old + document.RootElement.GetProperty("count").GetInt64());
                                        }
                                        break;
                                    case "like-burst":
                                        BurstEffects.Enqueue(effect.PayloadJson);
                                        Interlocked.Increment(ref _burstCount);
                                        break;
                                }
                            break;
                        default:
                            break;
                    }
            });
        }

        /// <summary>The cumulative clicks this room applied for a faction across all deltas —
        /// robust to whether the two likes merged into one tick window or split across two.</summary>
        public long LikeTotal(string faction) => _likeTotal.GetValueOrDefault(faction);

        public bool SawScore(string faction, long total) => BurstEffects.Any(effect =>
        {
            using var document = JsonDocument.Parse(effect);
            return document.RootElement.GetProperty("faction").GetString() == faction
                && document.RootElement.GetProperty("total").GetInt64() == total;
        });

        public void Dispose()
        {
            _stream.Dispose();
            _channel.Dispose();
            _certificate.Dispose();
        }
    }
}
