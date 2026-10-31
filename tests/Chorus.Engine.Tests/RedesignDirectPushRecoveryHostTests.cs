using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-07: push-mode recovery across a takeover. The owning host dies between a
/// confirmed push's durable accept and its consuming frame commit — the exact window a
/// kill leaves to chance — and the stall governor's migration hands the room to the other
/// host, whose recovery pull is the consumption authority: every confirmed push ends
/// Consumed and referenced by exactly one frame wherever it was consumed, the already
/// consumed never apply again, and the gateway keeps pushing into the new owner through
/// the same webhook face. The counts of accepted, deduplicated, consumed and still-pending
/// deliveries are reported per event. No input enters except through the ingress face.
/// </summary>
public sealed class RedesignDirectPushRecoveryHostTests
{
    private readonly ITestOutputHelper _output;

    public RedesignDirectPushRecoveryHostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ATakeoverRecoversUnconsumedPushesAndContinuesGatewayDelivery()
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
                // The deadline sweep stays out of the way: the stall governor (FIX-TAKE-07)
                // is the takeover under test — it notices the frozen head, migrates the room
                // and recovers it on the other host in one pass.
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "1000",
                ["CHORUS_STALL_SECONDS"] = "5",
                // The fresh window closes well before the room completes, so the governor
                // cannot churn the finished round afterwards; while the owner lives the
                // lease stays wide the way the push fence needs under CI starvation.
                ["CHORUS_FRESH_SECONDS"] = "15",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
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
            instanceId: "gateway-rcv");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rcv");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-rcv");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);

        await RegisterHostsAsync(environment, routing, "game-a", "game-b");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "rcv-open-host", "rcv-host-a");
        var guest = await MountAsync(environment, simulator, "rcv-open-guest", "rcv-guest-a");

        const string roomId = "room-rcv-a";
        var (epoch, hostSession) = await StartRoomAsync(environment, control, rounds, roomId,
            "game-a", gamePortA, controlCertificate, host, guest, "rcv-start", durationTicks: 600);

        var warm = await WaitForRoutingWarmAsync(routing, ["rcv-host-a"], [roomId],
            "game-a", $"https://localhost:{gamePortA}");
        Assert.True(warm, "The gateway's routing snapshot never carried the live route.");
        using var http = await HttpClientAsync(environment);

        // The old consumed push: confirmed, committed into a frame, out of the race for good.
        await AssertDurableAsync(await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"rcv-host-a","eventId":"rcv-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}"""),
            "rcv-gift-1");
        Assert.True(await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await DeliveryStateAsync(connection, "rcv-gift-1") == "Consumed"
                && await FrameContainsAsync(connection, roomId, "rcv-gift-1");
        }, TimeSpan.FromSeconds(30)), "The first push never reached a committed frame.");

        // Two more confirmed pushes, then the kill at once: the accept's durable commit is
        // done, the consuming frame commit is not — which side of the commit each push dies
        // on is the kill's to decide, and the recovery must land either side exactly once.
        await AssertDurableAsync(await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"rcv-host-a","eventId":"rcv-gift-2a","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":100}"""),
            "rcv-gift-2a");
        await AssertDurableAsync(await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"rcv-host-a","eventId":"rcv-gift-2b","kind":"Gift","giftId":"g3","giftCount":1,"giftValue":100}"""),
            "rcv-gift-2b");
        // The tree-walk kill spends hundreds of milliseconds before the signal leaves — the
        // accept-to-commit window a push dies inside is far narrower than that.
        environment.KillAtOnce("game-a");
        await Task.Delay(500);
        long deathHead;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            deathHead = await MaxTickAsync(connection, roomId);
        }
        _output.WriteLine($"FIX-INPUT-07: owner game-a killed at head tick {deathHead} (epoch {epoch}).");

        // The stall governor migrates the frozen room to game-b under a new epoch and
        // recovers it there; the head moving past the death head is the recovery's own
        // proof that the new owner is committing frames.
        var migrated = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT owner_process_instance_id,epoch FROM control.ownership WHERE kind='room' AND entity_id=@room",
                connection);
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync();
            return await reader.ReadAsync() && reader.GetString(0) == "game-b"
                && reader.GetInt64(1) == epoch + 1;
        }, TimeSpan.FromSeconds(40));
        Assert.True(migrated, "The stall governor never migrated the dead owner's room.");
        var resumed = await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await MaxTickAsync(connection, roomId) > deathHead;
        }, TimeSpan.FromSeconds(40));
        Assert.True(resumed, "The takeover host never committed past the dead owner's head.");

        // The gateway's directory catches up to the new owner, then the same webhook face
        // keeps delivering: a fresh push lands on game-b and consumes there.
        var rewarm = await WaitForRoutingWarmAsync(routing, ["rcv-host-a"], [roomId],
            "game-b", $"https://localhost:{gamePortB}");
        Assert.True(rewarm, "The gateway's routing snapshot never carried the takeover route.");
        await AssertDurableAsync(await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"rcv-host-a","eventId":"rcv-gift-3","kind":"Gift","giftId":"g4","giftCount":1,"giftValue":100}"""),
            "rcv-gift-3");
        Assert.True(await WaitForAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            return await DeliveryStateAsync(connection, "rcv-gift-3") == "Consumed";
        }, TimeSpan.FromSeconds(30)), "The post-takeover push was never consumed.");

        // The platform's own retry posture over the same face: an already consumed push and
        // a takeover-recovered push replay verbatim — both must answer Duplicate and apply
        // nothing on top of whichever leg already consumed them.
        await AssertDuplicateAsync(await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"rcv-host-a","eventId":"rcv-gift-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":100}"""),
            "rcv-gift-1");
        await AssertDuplicateAsync(await PostAsync(http, ingressPort, "simulator", "app-1",
            """{"sessionId":"rcv-host-a","eventId":"rcv-gift-2b","kind":"Gift","giftId":"g3","giftCount":1,"giftValue":100}"""),
            "rcv-gift-2b");

        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new Chorus.Rpc.GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(90));
        Assert.True(finished, "The takeover host did not finish the room.");

        // The verdict, per event: one durable delivery each, consumed exactly once by
        // exactly one frame, on whichever side of the death the consumption fell.
        string[] accepted = ["rcv-gift-1", "rcv-gift-2a", "rcv-gift-2b", "rcv-gift-3"];
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            foreach (var eventId in accepted)
            {
                var summary = await DeliverySummaryAsync(connection, eventId);
                Assert.Equal(1L, summary.Rows);
                Assert.Equal(1L, summary.Consumed);
                Assert.Equal(0L, summary.Pending);
                var tick = await FirstFrameTickAsync(connection, roomId, eventId);
                Assert.NotNull(tick);
                Assert.Equal(1L, await FrameReferenceCountAsync(connection, roomId, eventId));
                var branch = tick <= deathHead ? "consumed before the kill on game-a"
                    : "recovered by the takeover on game-b";
                _output.WriteLine(
                    $"FIX-INPUT-07: {eventId} consumed at tick {tick} — {branch}.");
                if (eventId == "rcv-gift-1")
                    Assert.True(tick <= deathHead, "The pre-kill confirmed push moved frames after death.");
                if (eventId == "rcv-gift-3")
                    Assert.True(tick > deathHead, "The post-takeover push consumed on the dead owner.");
            }
            var backlog = await RoomBacklogAsync(connection, roomId);
            Assert.Equal(0L, backlog);
            _output.WriteLine(
                $"FIX-INPUT-07 report: accepted={accepted.Length}, deduplicated=2, " +
                $"consumed={accepted.Length}, pending-backlog={backlog}; death head {deathHead}, " +
                $"takeover epoch {epoch + 1}, host session {hostSession}.");
        }
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
        ["Ingress__Douyin__AppSecret"] = "rcv-douyin-secret"
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
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-rcv"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new Chorus.Rpc.ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new Chorus.Rpc.RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-rcv" });
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

    private static async Task<bool> WaitForRoutingWarmAsync(RoutingService.RoutingClient routing,
        string[] liveKeys, string[] roomIds, string ownerInstance, string ownerEndpoint)
    {
        // The route the gateway's face must observe: every live key names a streamer, and
        // every room is owned by the named instance serving that endpoint.
        var complete = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamers = liveKeys.All(key => snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == key));
            var rooms = roomIds.All(id => snapshot.Rooms.FirstOrDefault(room => room.RoomId == id)
                is { OwnerInstanceId: var owner, Endpoint: var endpoint }
                && owner == ownerInstance && endpoint == ownerEndpoint);
            return streamers && rooms;
        }, TimeSpan.FromSeconds(30));
        if (!complete) return false;
        // The watch answers a version change with a full pull; hold the version still across a
        // couple of reads so the face's cache converges before the next push.
        for (var stable = 0; stable < 2; stable++)
        {
            var before = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            await Task.Delay(600);
            var after = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
            if (before != after) return false;
        }
        return true;
    }

    private static async Task<long> MaxTickAsync(NpgsqlConnection connection, string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(max(tick_id),0) FROM game.frame WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> DeliveryStateAsync(NpgsqlConnection connection, string eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT state FROM input.delivery WHERE event_id=@event", connection);
        command.Parameters.AddWithValue("event", eventId);
        return await command.ExecuteScalarAsync() as string;
    }

    private sealed record DeliverySummary(long Rows, long Consumed, long Pending);

    private static async Task<DeliverySummary> DeliverySummaryAsync(NpgsqlConnection connection, string eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*),count(*) FILTER (WHERE state='Consumed')," +
            "count(*) FILTER (WHERE state='Pending') FROM input.delivery WHERE event_id=@event", connection);
        command.Parameters.AddWithValue("event", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DeliverySummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> RoomBacklogAsync(NpgsqlConnection connection, string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM input.delivery WHERE consumer_scope=@scope AND state='Pending'", connection);
        command.Parameters.AddWithValue("scope", "room:" + roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

    private static async Task<long?> FirstFrameTickAsync(NpgsqlConnection connection, string roomId, string eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT min(tick_id) FROM game.frame WHERE room_id=@room AND input_refs::text LIKE @pattern",
            connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("pattern", "%\"" + eventId + "\"%");
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<long> FrameReferenceCountAsync(NpgsqlConnection connection, string roomId,
        string eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM game.frame WHERE room_id=@room AND input_refs::text LIKE @pattern",
            connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("pattern", "%\"" + eventId + "\"%");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

    private static async Task AssertDuplicateAsync(HttpResponseMessage response, string eventId)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"The replayed callback was not accepted: {response.StatusCode} " +
            $"{await response.Content.ReadAsStringAsync()}");
        var receipt = Assert.Single(ReadReceipts(await response.Content.ReadAsStringAsync()));
        Assert.Equal("Duplicate", receipt.GetProperty("disposition").GetString());
        Assert.True(receipt.GetProperty("duplicate").GetBoolean());
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
