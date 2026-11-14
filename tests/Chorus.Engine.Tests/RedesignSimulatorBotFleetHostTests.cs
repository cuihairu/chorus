using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Platform;
using Chorus.SimulatorPlatform;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The BOTS-1 acceptance fact: a two-streamer bot fleet rides only real faces — the
/// launch chain mounts each simulated streamer (credential → login → gameplay binding),
/// the scripts' audience events go out through the platform ingress face (verifier, mapper,
/// durable inbox, gateway routing), the room consumes them into committed ticks and a
/// frozen result, and the settlement ladder grants the faction scores. Nothing is asserted
/// from the posters' word: the reconciliation library reads the data plane (deliveries,
/// result, grants) and holds it to what the scripts swore they would send — sent N must
/// mean delivered N per kind, consumed must equal delivered, and the faction scores and
/// grant deltas must equal the pulse-derived sums exactly.
/// </summary>
public sealed class RedesignSimulatorBotFleetHostTests
{
    private static readonly BotScript RedScript = BotScript.Parse("""
        [script]
        name = "bots-red"
        duration = 5.0
        seed = 11

        [streamer]
        openId = "bots-open-red"
        liveKey = "bots-live-red"

        [[segment]]
        from = 0.0
        to = 5.0
        commentPerSecond = 2.0
        likePerSecond = 1.0

        [[pulse]]
        at = 1.0
        kind = "Gift"
        count = 3
        giftId = "g-red"
        giftValue = 40

        [[pulse]]
        at = 3.0
        kind = "Comment"
        count = 4
        text = "go red"
        """);

    private static readonly BotScript BlueScript = BotScript.Parse("""
        [script]
        name = "bots-blue"
        duration = 5.0
        seed = 12

        [streamer]
        openId = "bots-open-blue"
        liveKey = "bots-live-blue"

        [[segment]]
        from = 0.0
        to = 5.0
        commentPerSecond = 1.0

        [[pulse]]
        at = 1.5
        kind = "Gift"
        count = 2
        giftId = "g-blue"
        giftValue = 25

        [[pulse]]
        at = 2.0
        kind = "Comment"
        count = 2
        text = "go blue"
        """);

    [Fact]
    public async Task ABotFleetRidesIngressIntoRoomResultAndSettlementAndReconcilesClean()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (gamePort, ingressPort) = await StartClusterAsync(environment, "bots-1", "game-bots-1");

        var simulator = new DevelopmentPlatformSimulator(SimulatorKey);
        var red = await MountAsync(environment, simulator, RedScript);
        var blue = await MountAsync(environment, simulator, BlueScript);
        const string roomId = "room-bots-1";
        await StartRoomAsync(environment, gamePort, roomId, "game-bots-1",
            red.Handle!, blue.Handle!, "bots-start-1", durationTicks: 1200);
        Assert.True(
            await WaitForRoutingWarmAsync(environment, roomId, RedScript.LiveKey, BlueScript.LiveKey),
            "The gateway's routing snapshot never carried the room route.");

        // The fleet plays both scripts at once; the event ids draw from one shared
        // sequence because they are the gateway's dedup key.
        using var http = await HttpClientAsync(environment);
        var sequence = 0L;
        Func<long> nextId = () => Interlocked.Increment(ref sequence);
        var posterRed = new AudienceEventPoster(http, $"https://localhost:{ingressPort}",
            RedScript.LiveKey, "bots-red-", nextId);
        var posterBlue = new AudienceEventPoster(http, $"https://localhost:{ingressPort}",
            BlueScript.LiveKey, "bots-blue-", nextId);
        var runnerRed = new BotFleetRunner(RedScript, posterRed);
        var runnerBlue = new BotFleetRunner(BlueScript, posterBlue);
        await Task.WhenAll(runnerRed.RunAsync(), runnerBlue.RunAsync());
        Assert.True(runnerRed.Completed && runnerBlue.Completed);
        Assert.Equal(RedScript.Schedule.Count,
            posterRed.Snapshot().SentByKind.Values.Sum());
        Assert.Equal(BlueScript.Schedule.Count,
            posterBlue.Snapshot().SentByKind.Values.Sum());

        // The round froze with exactly the pulse-derived scores: red 3×40, blue 2×25.
        var result = await ResultAsync(environment, roomId);
        using (var document = JsonDocument.Parse(result))
        {
            Assert.Equal(120L, ScoreOf(document, "red"));
            Assert.Equal(50L, ScoreOf(document, "blue"));
        }

        // Settlement grants land right behind the result; wait for both players' rows,
        // then hold the whole ledger to what the fleet swore it sent.
        var ledger = await WaitForAsync(async () =>
        {
            var snapshot = await BotReconciliation.ReadRoomAsync(
                environment.DatabaseConnectionString, roomId);
            return snapshot.GrantDeltas.Count >= 2 ? snapshot : null;
        }, TimeSpan.FromSeconds(90));
        Assert.NotNull(ledger);

        var expectation = BotReconciliation.Expect(
        [
            (red.Handle!, "red", RedScript),
            (blue.Handle!, "blue", BlueScript),
        ]);
        var report = BotReconciliation.Deviations(expectation, ledger!,
            [posterRed.Snapshot(), posterBlue.Snapshot()]);
        Assert.True(report.IsClean,
            $"The fleet did not reconcile:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", report.Deviations));
    }

    // ---- cluster ----------------------------------------------------------------------

    private static byte[] SimulatorKey { get; } = RandomNumberGenerator.GetBytes(32);
    private static byte[] AccessKey { get; } = RandomNumberGenerator.GetBytes(32);

    private static async Task<(int GamePort, int IngressPort)> StartClusterAsync(
        RedesignM0HostTests.HostEnvironment environment, string suffix, string gameId)
    {
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var ingressPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"{gameId}=https://localhost:{gamePort}",
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
                ["CHORUS_TENANT_APPLICATIONS"] = "{\"tenant-a\":{\"app-1\":\"" + gameId + "\"}}"
            });
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500",
                // The host maps platform deliveries to module commands — the bots ride
                // the same translation the public ingress serves.
                ["CHORUS_INPUT_TRANSLATION"] = "audience-event"
            },
            clientTenant: "tenant-a", instanceId: gameId);
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
                ["Ingress__Douyin__AppSecret"] = "bots-douyin-secret"
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

    // ---- fleet mounting and the room the bots play into -------------------------------

    private static async Task<BotStreamer> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        BotScript script)
    {
        using var certificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-" + script.OpenId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new Chorus.Rpc.StreamerSession.StreamerSessionClient(channel);
        var streamer = new BotStreamer(simulator, session, "app-1", "game-bots-1",
            script.OpenId, script.LiveKey);
        await streamer.MountAsync();
        return streamer;
    }

    private static async Task<long> StartRoomAsync(
        RedesignM0HostTests.HostEnvironment environment, int gamePort, string roomId, string gameId,
        BotStreamerHandle host, BotStreamerHandle guest, string operationId, int durationTicks)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-bots");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new Chorus.Rpc.ControlService.ControlServiceClient(controlChannel);
        var claimed = await control.ClaimOwnershipAsync(new Chorus.Rpc.OwnershipRequest
        {
            TenantId = "tenant-a", GameId = gameId, Kind = "room", EntityId = roomId,
            OwnerInstanceId = gameId, LeaseSeconds = 30
        });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-bots");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new Chorus.Rpc.RoundStore.RoundStoreClient(gameChannel);
        var started = await rounds.StartRoundAsync(new Chorus.Rpc.StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = gameId, Epoch = claimed.Epoch, GameId = gameId,
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
                RoomId = roomId, GameId = gameId, OwnerInstanceId = gameId,
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
        return claimed.Epoch;
    }

    private static async Task<bool> WaitForRoutingWarmAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId, params string[] liveKeys)
    {
        using var certificate = environment.Certificate("@cluster", "control-server", "routing-bots");
        using var channel = environment.Channel(environment.ControlPort, certificate, "control-server");
        var routing = new RoutingService.RoutingClient(channel);
        return await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
            var streamers = liveKeys.All(key => snapshot.Streamers.Any(streamer =>
                streamer.Platform == "simulator" && streamer.CanonicalLiveSessionKey == key));
            var room = snapshot.Rooms.FirstOrDefault(room => room.RoomId == roomId);
            if (!(streamers && room is { Endpoint.Length: > 0 })) return false;
            // A settled snapshot: the route stands and the version held still for two
            // 600 ms windows (the same settled-snapshot rule the ingress facts use).
            for (var stable = 0; stable < 2; stable++)
            {
                var before = (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version;
                await Task.Delay(600);
                if (before != (await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty())).Version)
                    return false;
            }
            return true;
        }, TimeSpan.FromSeconds(30));
    }

    // ---- the reconciliation's eyes -----------------------------------------------------

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

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var candidate = await probe();
            if (candidate is not null) return candidate;
            await Task.Delay(200);
        }
        return await probe();
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
}
