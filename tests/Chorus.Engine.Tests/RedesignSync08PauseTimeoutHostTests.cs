using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Platform;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-08 acceptance (暂停超时终结的宿主腿): a paused round that exceeded its declared
/// MaxPause is terminated by the module's freeze rule through the full end-operation ladder —
/// the same ordered chain a natural end walks (FIX-END-15), never a bypass of the platform's
/// closing confirmation. The room is brought up paused at a pre-scored frozen head through the
/// data plane (the shipped package's own initial state is the Prepare snapshot; a committed
/// tick-1 checkpoint carries the scored state the freeze rule reads), the pause is aged past
/// the shipped MaxPause, and the host's FinishPausedRoom commits the terminal result and flips
/// the room Paused→Finishing — never Settled, the ladder owns the release. The evidence: the
/// barrier confirms before any platform call, the platform closes before the release, the
/// release stands at the Settled room, the ladder reaches Done, the result sits at the frozen
/// frame with the module's ranking, and the grants stand exactly once.
/// </summary>
public sealed class RedesignSync08PauseTimeoutHostTests
{
    /// <summary>The shipped package's canonical offline policy (FIX-SYNC-04): the room's
    /// declaration must match it verbatim or the recovery refuses — MaxPause=120s, Ready=1.</summary>
    private const string OfflinePolicy = "ContinueToEnd|30|120|1|Buffer";

    private const string OwnerInstance = "game-sync08-timeout";
    private const string Room = "room-sync08-timeout";

    /// <summary>Two participants carry an end task apiece under the round's single operation
    /// id: the platform hears one wire call per mount and applies the close exactly once.</summary>
    private const int Participants = 2;

    /// <summary>The ladder's rank of one sampled phase; 0 for a phase not yet standing.
    /// DeliveryStopped is transient — the coordinator may cross it inside one pass.</summary>
    private static int PhaseRank(string phase) => phase switch
    {
        "ResultFrozen" => 1, "ClosingInput" => 2, "StopDeliveryIssued" => 3, "DeliveryStopped" => 4,
        "PlatformTaskAuthorized" => 5, "PlatformClosed" => 6, "Released" => 7, "Destroyed" => 8,
        "Done" => 9, _ => 0
    };

    /// <summary>One tick of the ordered evidence: the ladder's phase, the room's status, and
    /// how many platform wire calls the end operation has made so far.</summary>
    private sealed record Sample(int Index, string Phase, string RoomStatus, int PlatformCalls);

    private static async Task<Sample> SampleAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform, string roomId, string operationId, int index)
    {
        var phase = "";
        var status = "";
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT (SELECT phase FROM control.end_operation WHERE room_id=@room),
                       (SELECT status FROM game.room WHERE room_id=@room)
                """, connection);
            command.Parameters.AddWithValue("room", roomId);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0) && !reader.IsDBNull(1))
            {
                phase = reader.GetString(0);
                status = reader.GetString(1);
            }
        }
        return new Sample(index, phase, status, platform.Calls(operationId));
    }

    private static async Task<Sample> PollUntilAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform, string roomId, string operationId,
        Func<Sample, bool> done, TimeSpan budget, List<Sample> samples)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var sample = await SampleAsync(environment, platform, roomId, operationId, samples.Count);
            samples.Add(sample);
            if (done(sample)) return sample;
            await Task.Delay(100);
        }
        return samples[^1];
    }

    private static string Dump(IReadOnlyList<Sample> samples) => string.Join("\n",
        samples.Select(sample => $"  #{sample.Index}: phase={sample.Phase} room={sample.RoomStatus} calls={sample.PlatformCalls}"));

    /// <summary>The order the pause-timeout termination must recover into, identical to a
    /// natural end: the barrier's confirmation (DeliveryStopped or later) at or before the
    /// first platform call, the release no earlier than that call and with the platform's
    /// calls complete (one wire call per mount), the room settled behind the release, and
    /// the ladder's Done no earlier than the release.</summary>
    private static void AssertChainOrder(List<Sample> samples, string label)
    {
        Assert.True(samples.Count > 0, $"{label}: nothing was ever sampled.");
        var confirm = samples.FindIndex(sample => PhaseRank(sample.Phase) >= 4);
        var call = samples.FindIndex(sample => sample.PlatformCalls >= 1);
        var release = samples.FindIndex(sample => PhaseRank(sample.Phase) >= 7);
        var done = samples.FindIndex(sample => sample.Phase == "Done");
        Assert.True(confirm >= 0, $"{label}: the delivery-stop barrier never confirmed.\n{Dump(samples)}");
        Assert.True(call >= 0, $"{label}: the platform call never landed.\n{Dump(samples)}");
        Assert.True(call >= confirm, $"{label}: a platform call preceded the barrier's confirmation.\n{Dump(samples)}");
        Assert.True(release >= 0, $"{label}: the round was never released.\n{Dump(samples)}");
        Assert.True(release >= call, $"{label}: the release preceded the platform call.\n{Dump(samples)}");
        var atRelease = samples[release];
        Assert.Equal("Settled", atRelease.RoomStatus);
        Assert.Equal(Participants, atRelease.PlatformCalls);
        Assert.True(done >= 0, $"{label}: the ladder never finished.\n{Dump(samples)}");
        Assert.True(done >= release, $"{label}: the ladder finished before the release.\n{Dump(samples)}");
    }

    private static Dictionary<string, string> ControlEnvironment(int dataPort, int gamePort) => new()
    {
        ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{dataPort}",
        ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
        ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
        ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
        ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
        ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
        ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15",
        ["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "600000",
        ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
        ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
        ["CHORUS_END_BARRIER_STALE_SECONDS"] = "5",
        ["CHORUS_DIRECTORY_STALE_SECONDS"] = "90",
        ["CHORUS_DIRECTORY_REFRESH_MS"] = "1000",
        // FIX-SYNC-08: the resume/timeout coordinator's own leg (discovery + owner resolve +
        // RPC) is the thin driver the store tests cover; this fact drives the host RPCs
        // directly for determinism, so the coordinator's race is parked far out.
        ["CHORUS_PAUSE_RESUME_INTERVAL_MS"] = "600000"
    };

    private static async Task StartControlAsync(RedesignM0HostTests.HostEnvironment environment,
        Dictionary<string, string> controlEnvironment) =>
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", controlEnvironment);

    private static async Task StartGatewayAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform, int gatewayPort, string instanceId)
    {
        var gatewayEnvironment = new Dictionary<string, string>(
            RedesignEndOperationHostTests.GatewayEnvironment(environment.DataPort,
                $"https://localhost:{environment.ControlPort}", gatewayPort))
        {
            ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platform.Url,
            ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
            ["CHORUS_TASK_LEASE_SECONDS"] = "60",
            ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
            ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
        };
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            gatewayEnvironment, instanceId: instanceId);
    }

    private static async Task StartDataStreamerGameAsync(
        RedesignM0HostTests.HostEnvironment environment, int gamePort, byte[] simulatorKey,
        byte[] accessKey, string name)
    {
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-" + name);
    }

    private static async Task RegisterNodeAsync(RedesignM0HostTests.HostEnvironment environment, string name)
    {
        using var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-" + name);
        using var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server");
        await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
            new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-" + name });
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
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
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }

    [Fact]
    public async Task APauseExceedingMaxPauseFinishesByTheFrozenRuleThroughTheOrderedLadder()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var controlEnvironment = ControlEnvironment(environment.DataPort, gamePort);
        await StartControlAsync(environment, controlEnvironment);
        await StartDataStreamerGameAsync(environment, gamePort, simulatorKey, accessKey, "sync08-timeout");
        await RegisterNodeAsync(environment, "sync08-timeout");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        // Real mounts through the streamer server: the end ladder closes these bindings on
        // the platform, so the mounts carry real platform credentials.
        var host = await MountAsync(environment, simulator, "sync08-timeout-host");
        var guest = await MountAsync(environment, simulator, "sync08-timeout-guest");

        // Data-plane bring-up under the shipped package's identity: the room is mounted and
        // opened with the module's own initial state as the Prepare snapshot (the only payload
        // a real host can replay), then one committed tick at the checkpoint carrying the
        // pre-scored frozen state the freeze rule reads.
        var roomStore = new RedesignRoomStore(environment.DatabaseConnectionString);
        var frameStore = new RedesignFrameStore(environment.DatabaseConnectionString);
        var controlStore = new RedesignControlStore(environment.DatabaseConnectionString);
        var claim = await controlStore.ClaimOwnershipAsync("tenant-a", "game-a", "room", Room,
            OwnerInstance, 60);
        var package = RedesignModuleCatalog.Default;
        var module = package.Load(package.ManifestAt(25));
        var factions = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [host.StreamerId] = GameStartConfig.RedFaction,
            [guest.StreamerId] = GameStartConfig.BlueFaction
        };
        var initial = module.CreateInitialState(new GameStartConfig(800, 444,
            [new GameParticipant(host.StreamerId, GameStartConfig.RedFaction),
             new GameParticipant(guest.StreamerId, GameStartConfig.BlueFaction)]));
        await roomStore.StartRoundAsync("tenant-a", "game-a", Room, OwnerInstance, claim.Epoch,
            [new RoundParticipant(host.BindingId, host.StreamerId, "Host"),
             new RoundParticipant(guest.BindingId, guest.StreamerId, "Guest")],
            "1.0.0", "cfg-1", "pkg-1", "start-sync08-timeout");
        await roomStore.PrepareRoundAsync("tenant-a", "game-a", Room, OwnerInstance, claim.Epoch,
            module.Serialize(initial), module.ComputeStateHash(initial), 1, "prepare-sync08-timeout",
            tickMs: 25, recoveryMode: "DeterministicReplay", offlinePolicy: OfflinePolicy);
        await roomStore.ActivateRoundAsync("tenant-a", "game-a", Room, OwnerInstance, claim.Epoch,
            "activate-sync08-timeout");

        // The tick-1 checkpoint carries the pre-scored frozen state — the head the timeout
        // terminates from. The frame's state hash is the scored state's, so an honest replay
        // of the tick-0 snapshot refuses (a no-input tick from the initial state would not
        // produce these scores) and the newest surviving snapshot — this checkpoint — is the
        // head the freeze rule reads.
        var scored = new ExampleGameState(1, 444, 800, 0, factions,
            new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [GameStartConfig.RedFaction] = 50,
                [GameStartConfig.BlueFaction] = 30
            }, Finished: false);
        await frameStore.CommitFrameAsync("tenant-a", "game-a", Room, new FrameCommit(
            CommitId: "commit-sync08-timeout-tick1", OwnerInstanceId: OwnerInstance,
            Epoch: claim.Epoch, PrevTick: 0, PrevStateVersion: 1,
            InputEventIds: [], StateHash: module.ComputeStateHash(scored),
            Effects: [], ModuleVersion: "1.0.0", ConfigHash: "cfg-1",
            SnapshotPayload: module.Serialize(scored), SnapshotHash: module.ComputeStateHash(scored),
            SnapshotSchemaVersion: scored.SchemaVersion));

        var paused = await roomStore.PauseRoundAsync("tenant-a", "game-a", Room, OwnerInstance,
            claim.Epoch, "all control sessions offline past the declared grace",
            "pause-sync08-timeout", 1, 2);
        Assert.Equal("Paused", paused.Status);

        // Age the pause past the shipped MaxPause (120 s) — the timeout discovery's window.
        await AgePauseAsync(environment, 200);

        // The gateway must stand before the ladder authorizes the platform tasks.
        await StartGatewayAsync(environment, platform, gatewayPort, "gateway-sync08-timeout");

        // The timeout leg drives the host directly (the coordinator's own discovery + owner
        // resolve is the store-tested thin driver; its interval is parked far out so it
        // cannot race this fact).
        using var controlCertificate = environment.Certificate("@cluster", "control-server",
            "control-sync08-timeout");
        using var gameChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var finished = await new GameHost.GameHostClient(gameChannel).FinishPausedRoomAsync(
            new FinishPausedRoomRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = Room,
                OwnerInstanceId = OwnerInstance, Epoch = claim.Epoch,
                Reason = "pause exceeded MaxPauseSeconds"
            }, deadline: DateTime.UtcNow.AddSeconds(30));
        Assert.Equal("Finishing", finished.Status);
        Assert.Equal(1, finished.FinalTick);
        Assert.Equal(1, finished.ResultVersion);

        // The room is Finishing immediately — the pause-timeout never reports the round
        // settled itself; the end-operation ladder owns the release.
        Assert.Equal("Finishing", await RoomStatusAsync(environment));

        var samples = new List<Sample>();
        var final = await PollUntilAsync(environment, platform, Room, Room + "/end",
            sample => sample.Phase == "Done" && sample.RoomStatus == "Settled",
            TimeSpan.FromSeconds(150), samples);
        Assert.Equal("Done", final.Phase);
        AssertChainOrder(samples, "pause-timeout");
        await AssertFrozenSettlementAsync(environment, platform, host, guest, Room);
    }

    /// <summary>The frozen settlement the chain must reach exactly once: the result sits at
    /// the frozen frame (tick 1, result_version 1) with the module's frozen ranking — red 50,
    /// blue 30 — and each scored participant is paid by exactly one ledger line.</summary>
    private static async Task AssertFrozenSettlementAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform,
        (string StreamerId, string SessionId, string BindingId) host,
        (string StreamerId, string SessionId, string BindingId) guest, string roomId)
    {
        var operationId = roomId + "/end";
        Assert.Equal(Participants, platform.Calls(operationId));
        Assert.Equal("end-round", platform.Kinds.GetValueOrDefault(operationId));
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var result = new NpgsqlCommand("""
                SELECT result_version, final_frame,
                       result->'ranking'->0->>'faction', result->'ranking'->0->>'score',
                       result->'ranking'->1->>'faction', result->'ranking'->1->>'score'
                FROM game.result WHERE room_id=@room
                """, connection);
            result.Parameters.AddWithValue("room", roomId);
            await using var reader = await result.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0)); // result_version
            Assert.Equal(1, reader.GetInt64(1)); // final_frame at the frozen tick
            Assert.Equal("red", reader.GetString(2));
            Assert.Equal("50", reader.GetString(3));
            Assert.Equal("blue", reader.GetString(4));
            Assert.Equal("30", reader.GetString(5));
        }
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var grants = new NpgsqlCommand(
                "SELECT player_id, delta FROM settlement.grant WHERE room_id=@room AND status='Granted' ORDER BY player_id",
                connection);
            grants.Parameters.AddWithValue("room", roomId);
            await using var reader = await grants.ExecuteReaderAsync();
            var lines = new List<(string Player, long Delta)>();
            while (await reader.ReadAsync()) lines.Add((reader.GetString(0), reader.GetInt64(1)));
            Assert.Equal(2, lines.Count);
            Assert.Contains((host.StreamerId, 50L), lines);
            Assert.Contains((guest.StreamerId, 30L), lines);
        }
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand(
                "SELECT count(*), count(DISTINCT operation_id) FROM player.ledger WHERE operation_id LIKE @prefix",
                connection);
            ledger.Parameters.AddWithValue("prefix", roomId + "/grant/%");
            await using var reader = await ledger.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var (total, distinct) = (reader.GetInt64(0), reader.GetInt64(1));
            Assert.Equal(Participants, distinct);
            Assert.Equal(Participants, total); // exactly one ledger line per granted player
        }
    }

    private static async Task AgePauseAsync(RedesignM0HostTests.HostEnvironment environment, int seconds)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE game.room SET paused_at=clock_timestamp() - @age * interval '1 second' WHERE room_id=@room",
            connection);
        command.Parameters.AddWithValue("age", seconds);
        command.Parameters.AddWithValue("room", Room);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> RoomStatusAsync(RedesignM0HostTests.HostEnvironment environment)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM game.room WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", Room);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
