using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-15: the end sequence's full-chain order under crashes. A real game host runs
/// each round to its natural end while the fact samples the ladder — the ordered evidence
/// — and crashes the control plane at one injection point per fact: while the round still
/// runs (before the gateway's confirmation exists at all), after the confirmation
/// authorized the platform call, while the platform's success answer was lost to a hang,
/// and after the finalize released the round with the game host already gone. Every
/// injection recovers into the same order — the barrier confirms before any platform
/// call, the platform closes before the release, the release before the ladder's end —
/// the platform's apply stands behind exactly one operation id (one wire call per mount,
/// applied once), each granted player is paid by exactly one ledger line, and the
/// replayed finalize answers from its receipt without moving anything.
/// </summary>
public sealed class RedesignEnd15ChainOrderHostTests
{
    private readonly ITestOutputHelper _output;

    public RedesignEnd15ChainOrderHostTests(ITestOutputHelper output) => _output = output;

    /// <summary>Each fact's round fields two participants, so two mounts carry an end
    /// task apiece under the round's single operation id: the platform hears one wire
    /// call per mount and applies the close exactly once (idempotent by operation id).
    /// </summary>
    private const int Participants = 2;

    /// <summary>The ladder's rank of one sampled phase; 0 for a phase not yet standing.
    /// DeliveryStopped is transient — the coordinator may cross it inside one pass — so
    /// the confirm evidence accepts it and everything above it.</summary>
    private static int PhaseRank(string phase) => phase switch
    {
        "ResultFrozen" => 1, "ClosingInput" => 2, "StopDeliveryIssued" => 3, "DeliveryStopped" => 4,
        "PlatformTaskAuthorized" => 5, "PlatformClosed" => 6, "Released" => 7, "Destroyed" => 8,
        "Done" => 9, _ => 0
    };

    /// <summary>One tick of the ordered evidence: the ladder's phase, the room's status,
    /// and how many platform wire calls the end operation has made so far.</summary>
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
            if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                && !reader.IsDBNull(0) && !reader.IsDBNull(1))
            {
                phase = reader.GetString(0);
                status = reader.GetString(1);
            }
        }
        return new Sample(index, phase, status, platform.Calls(operationId));
    }

    private static async Task<Sample> PollUntilAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform, string roomId, string operationId,
        Func<Sample, bool> done, TimeSpan budget, List<Sample> samples,
        Func<Sample, Task>? onSample = null)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var sample = await SampleAsync(environment, platform, roomId, operationId, samples.Count);
            samples.Add(sample);
            if (onSample is not null) await onSample(sample);
            if (done(sample)) return sample;
            await Task.Delay(100);
        }
        return samples[^1];
    }

    private static string Dump(IReadOnlyList<Sample> samples) => string.Join("\n",
        samples.Select(sample => $"  #{sample.Index}: phase={sample.Phase} room={sample.RoomStatus} calls={sample.PlatformCalls}"));

    /// <summary>The order every injection must recover into: the barrier's confirmation
    /// (DeliveryStopped or later) at or before the first platform call, the release — the
    /// ladder's own Released phase, not the settle leg's room column, which the settle
    /// executor writes on its own before the finalize — no earlier than that call and
    /// with the platform's calls complete (exactly one wire call per mount, never a
    /// re-call from the crash) and the room settled behind it, and the ladder's Done no
    /// earlier than the release.</summary>
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
        // The directory ages a dead game host out on a long fuse (FIX-END-14's destroy
        // leg waits on the live set only): the after-finalize fact kills the game host
        // before the release, so the destroy broadcast must park on the unreachable —
        // but still live — directory entry first, and only a control crash plus the
        // fuse burning down finishes the ladder through the vacuous destroy. The fuse
        // must outlast a slow executor start (a loaded runner takes tens of seconds to
        // bring the gateway up) yet burn out well inside the final poll's budget.
        ["CHORUS_DIRECTORY_STALE_SECONDS"] = "90",
        ["CHORUS_DIRECTORY_REFRESH_MS"] = "1000"
    };

    private static async Task StartControlAsync(RedesignM0HostTests.HostEnvironment environment,
        Dictionary<string, string> controlEnvironment) =>
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", controlEnvironment);

    private static async Task StartGatewayAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform, int gatewayPort, string instanceId,
        Dictionary<string, string>? extra = null)
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
        if (extra is not null)
            foreach (var (key, value) in extra)
                gatewayEnvironment[key] = value;
        await environment.StartAsync("Chorus.PlatformGateway", gatewayPort, "platform-gateway",
            gatewayEnvironment, instanceId: instanceId);
    }

    private sealed record RoundFacts(long OwnerEpoch, (string StreamerId, string SessionId, string BindingId) Host,
        (string StreamerId, string SessionId, string BindingId) Guest);

    private static async Task<RoundFacts> OpenRoundAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string name, string roomId, int gamePort)
    {
        var host = await MountAsync(environment, simulator, name + "-host");
        var guest = await MountAsync(environment, simulator, name + "-guest");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-" + name);
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-" + name);
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(dataChannel);
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-" + name, LeaseSeconds = 60
        });
        Assert.Equal("Preparing", (await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-" + name, Epoch = owner.Epoch,
            GameId = "game-a", ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-" + name, TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        })).Status);
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        Assert.Equal("Running", (await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-" + name,
                Epoch = owner.Epoch, OperationId = "run-" + name, DurationTicks = 40, RandomSeed = 444,
                TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                    new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
                }
            })).Status);
        // The fighters boost — a duel scores only its fighters' boosts, so without these
        // the round ends goalless and plans no grants at all. The host out-boosts the
        // guest: the outcome, and with it the grant manifest, is deterministic.
        await BoostAsync(environment, host, roomId, owner.Epoch, "boost-" + name + "-host", 5);
        await BoostAsync(environment, guest, roomId, owner.Epoch, "boost-" + name + "-guest", 3);
        return new RoundFacts(owner.Epoch, host, guest);
    }

    private static async Task BoostAsync(RedesignM0HostTests.HostEnvironment environment,
        (string StreamerId, string SessionId, string BindingId) player, string roomId, long epoch,
        string commandId, int level)
    {
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + commandId);
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        await new RoundInput.RoundInputClient(channel).AcceptClientInputAsync(new ClientInputRequest
        {
            SessionId = player.SessionId, RoomId = roomId, RoomEpoch = epoch, GameId = "game-a",
            CommandId = commandId, PayloadJson = $$"""{"action":"boost","level":{{level}}}"""
        });
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

    /// <summary>The settlement every chain must reach exactly once: every granted player
    /// carries exactly one ledger line for the round's grant, no grant operation paid
    /// twice, the ownership stands at exactly the release's bump, the settle outbox effect
    /// stands, and the replayed finalize answers from its receipt changing nothing.</summary>
    private static async Task AssertSettlementAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentTaskPlatform platform, RoundFacts round, string name, string roomId)
    {
        var operationId = roomId + "/end";
        Assert.Equal(Participants, platform.Calls(operationId));
        Assert.Equal("end-round", platform.Kinds.GetValueOrDefault(operationId));
        long granted;
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var grants = new NpgsqlCommand(
                "SELECT count(*) FROM settlement.grant WHERE room_id=@room AND status='Granted'", connection);
            grants.Parameters.AddWithValue("room", roomId);
            granted = Convert.ToInt64(await grants.ExecuteScalarAsync());
        }
        Assert.Equal(Participants, granted);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var lines = new NpgsqlCommand(
                "SELECT count(*), count(DISTINCT operation_id) FROM player.ledger WHERE operation_id LIKE @prefix",
                connection);
            lines.Parameters.AddWithValue("prefix", roomId + "/grant/%");
            await using var reader = await lines.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var (total, distinct) = (reader.GetInt64(0), reader.GetInt64(1));
            Assert.Equal(Participants, distinct);
            Assert.Equal(Participants, total); // exactly one ledger line per granted player
        }
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var records = new NpgsqlCommand("""
                SELECT (SELECT status||':'||epoch FROM control.ownership
                          WHERE kind='room' AND entity_id=@room),
                       (SELECT kind FROM infra.outbox WHERE effect_id=@settle)
                """, connection);
            records.Parameters.AddWithValue("room", roomId);
            records.Parameters.AddWithValue("settle", roomId + "/0/settle");
            await using var reader = await records.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal($"Released:{round.OwnerEpoch + 1}", reader.GetString(0));
            Assert.Equal("round.settled", reader.GetString(1));
        }
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-" + name);
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var replay = await new Settlement.SettlementClient(dataChannel).FinalizeRoundAsync(
            new FinalizeRoundRequest
            {
                RoomId = roomId, GameId = "game-a", TenantId = "tenant-a", OperationId = operationId
            }, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.True(replay.Duplicate);
        Assert.Equal("Settled", replay.Status);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var epoch = new NpgsqlCommand(
                "SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id=@room", connection);
            epoch.Parameters.AddWithValue("room", roomId);
            await using var reader = await epoch.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Released", reader.GetString(0));
            Assert.Equal(round.OwnerEpoch + 1, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task AKillBeforeTheRoundEndsRecoversIntoTheOrderedChain()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = DevelopmentTaskPlatform.Start(
            () => RedesignM0HostTests.HostEnvironment.FreePort());
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var gatewayPort = RedesignM0HostTests.HostEnvironment.FreePort();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var controlEnvironment = ControlEnvironment(environment.DataPort, gamePort);
        // This fact's kill lands while the round still runs, so the host's round-end
        // report may hit the dead plane and exhaust its bounded in-process retries — the
        // designed net for exactly that is the discovery sweep (a control plane that was
        // down at the moment), which it enables here on a short fuse. The other facts'
        // kills land after the report already succeeded; they keep discovery lazy.
        controlEnvironment["CHORUS_END_DISCOVERY_INTERVAL_MS"] = "1000";
        controlEnvironment["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "2";
        await StartControlAsync(environment, controlEnvironment);
        await StartDataStreamerGameAsync(environment, gamePort, simulatorKey, accessKey, "e15a");
        await RegisterNodeAsync(environment, "e15a");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        const string roomId = "room-e15-a";
        var round = await OpenRoundAsync(environment, simulator, "e15a", roomId, gamePort);
        // The crash lands while the round still runs — before the end sequence, and with
        // it every confirmation, exists at all. The data plane carries the round's end on
        // its own while the control plane is down, and the restarted coordinator must
        // pick the standing row up — the host's own report if its retries outlasted the
        // restart, the discovery net if they did not — and walk the whole chain in order.
        environment.Kill("Chorus.ControlServer");
        await StartControlAsync(environment, controlEnvironment);
        await StartGatewayAsync(environment, platform, gatewayPort, "gateway-e15a");
        var samples = new List<Sample>();
        var final = await PollUntilAsync(environment, platform, roomId, roomId + "/end",
            sample => sample.Phase == "Done" && sample.RoomStatus == "Settled",
            TimeSpan.FromSeconds(150), samples);
        Assert.Equal("Done", final.Phase);
        AssertChainOrder(samples, "kill-before-the-end");
        await AssertSettlementAsync(environment, platform, round, "e15a", roomId);
    }

    [Fact]
    public async Task AKillAfterTheConfirmationRecoversIntoTheOrderedChain()
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
        await StartDataStreamerGameAsync(environment, gamePort, simulatorKey, accessKey, "e15b");
        await RegisterNodeAsync(environment, "e15b");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        const string roomId = "room-e15-b";
        var round = await OpenRoundAsync(environment, simulator, "e15b", roomId, gamePort);
        await StartGatewayAsync(environment, platform, gatewayPort, "gateway-e15b");
        var samples = new List<Sample>();
        var killed = false;
        var killPhase = "";
        // The authorization dwell is bounded below by one coordinator interval — the
        // phase only leaves PlatformTaskAuthorized on a later pass — and the sampler's
        // cadence is finer than that interval, so the kill is guaranteed its window.
        await PollUntilAsync(environment, platform, roomId, roomId + "/end",
            sample => sample.Phase == "Done" && sample.RoomStatus == "Settled",
            TimeSpan.FromSeconds(150), samples,
            async sample =>
            {
                if (killed || sample.Phase != "PlatformTaskAuthorized") return;
                killed = true;
                killPhase = sample.Phase;
                environment.Kill("Chorus.ControlServer");
                await StartControlAsync(environment, controlEnvironment);
            });
        Assert.True(killed, "The kill never found its window after the confirmation.\n" + Dump(samples));
        Assert.True(PhaseRank(killPhase) >= 5,
            "The kill landed before the barrier authorized the platform call.\n" + Dump(samples));
        AssertChainOrder(samples, "kill-after-confirm");
        await AssertSettlementAsync(environment, platform, round, "e15b", roomId);
    }

    [Fact]
    public async Task ALostPlatformSuccessAnswerResolvesThroughTheOperationId()
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
        await StartDataStreamerGameAsync(environment, gamePort, simulatorKey, accessKey, "e15c");
        await RegisterNodeAsync(environment, "e15c");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        const string roomId = "room-e15-c";
        var round = await OpenRoundAsync(environment, simulator, "e15c", roomId, gamePort);
        // The platform applies the close but its success answer never arrives: the
        // executors park their tasks at OutcomeUnknown and only the operation id's
        // official record can resolve them — a control crash in that window must not
        // add a single wire call.
        platform.ScriptOperation(roomId + "/end", new DevelopmentTaskPlatform.Script
        {
            Apply = true, HangAnswer = TimeSpan.FromSeconds(60)
        });
        await StartGatewayAsync(environment, platform, gatewayPort, "gateway-e15c",
            new Dictionary<string, string>
            {
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "5",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "3"
            });
        var samples = new List<Sample>();
        var parked = false;
        string phaseAtPark = "";
        var parkDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < parkDeadline && !parked)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM live.task WHERE barrier_end_operation_id=@end),
                           (SELECT count(*) FILTER (WHERE status='OutcomeUnknown')
                              FROM live.task WHERE barrier_end_operation_id=@end),
                           (SELECT phase FROM control.end_operation WHERE room_id=@room)
                    """, connection);
                command.Parameters.AddWithValue("end", roomId + "/end");
                command.Parameters.AddWithValue("room", roomId);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(2))
                {
                    phaseAtPark = reader.GetString(2);
                    if (reader.GetInt64(0) == Participants && reader.GetInt64(1) == Participants
                        && phaseAtPark == "PlatformTaskAuthorized")
                        parked = true;
                }
            }
            samples.Add(await SampleAsync(environment, platform, roomId, roomId + "/end", samples.Count));
            await Task.Delay(100);
        }
        Assert.True(parked, $"The lost answer never parked every task unknown (phase: {phaseAtPark}).\n{Dump(samples)}");
        environment.Kill("Chorus.ControlServer");
        await StartControlAsync(environment, controlEnvironment);
        var final = await PollUntilAsync(environment, platform, roomId, roomId + "/end",
            sample => sample.Phase == "Done" && sample.RoomStatus == "Settled",
            TimeSpan.FromSeconds(150), samples);
        Assert.Equal("Done", final.Phase);
        AssertChainOrder(samples, "lost-answer");
        Assert.True(platform.StatusQueries(roomId + "/end") >= 1,
            "The resolution never asked the platform for its official record.");
        await AssertSettlementAsync(environment, platform, round, "e15c", roomId);
    }

    [Fact]
    public async Task AKillAfterTheFinalizeFinishesTheDestroyWithoutTheGameHost()
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
        await StartDataStreamerGameAsync(environment, gamePort, simulatorKey, accessKey, "e15d");
        await RegisterNodeAsync(environment, "e15d");
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        const string roomId = "room-e15-d";
        var round = await OpenRoundAsync(environment, simulator, "e15d", roomId, gamePort);
        // Park the ladder at the authorization with no executor; the round's result is
        // already committed, so the game host may die now — the entity it holds is a
        // finished tail. The executor then releases the round; the destroy broadcast
        // finds only the dead host and parks, and a control crash in that window must
        // still finish the ladder once the directory ages the host out.
        var parked = false;
        string phaseAtPark = "";
        var parkDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < parkDeadline && !parked)
        {
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM live.task WHERE barrier_end_operation_id=@end),
                           (SELECT count(*) FILTER (WHERE status='Pending')
                              FROM live.task WHERE barrier_end_operation_id=@end),
                           (SELECT phase FROM control.end_operation WHERE room_id=@room)
                    """, connection);
                command.Parameters.AddWithValue("end", roomId + "/end");
                command.Parameters.AddWithValue("room", roomId);
                if (await command.ExecuteReaderAsync() is { } reader && await reader.ReadAsync()
                    && !reader.IsDBNull(2))
                {
                    phaseAtPark = reader.GetString(2);
                    if (reader.GetInt64(0) == Participants && reader.GetInt64(1) == Participants
                        && phaseAtPark == "PlatformTaskAuthorized")
                        parked = true;
                }
            }
            await Task.Delay(150);
        }
        Assert.True(parked, $"The end tasks never parked authorized (phase: {phaseAtPark}).");
        environment.Kill("game-e15d");
        await StartGatewayAsync(environment, platform, gatewayPort, "gateway-e15d");
        var samples = new List<Sample>();
        var released = await PollUntilAsync(environment, platform, roomId, roomId + "/end",
            sample => PhaseRank(sample.Phase) >= 7, TimeSpan.FromSeconds(120), samples);
        await Task.Delay(2000);
        var stillHeld = await SampleAsync(environment, platform, roomId, roomId + "/end", samples.Count);
        samples.Add(stillHeld);
        Assert.True(PhaseRank(released.Phase) >= 7, $"The release never landed.\n{Dump(samples)}");
        Assert.True(stillHeld.Phase == "Released",
            $"The destroy did not park on the dead host — it reached {stillHeld.Phase}.\n{Dump(samples)}");
        environment.Kill("Chorus.ControlServer");
        await StartControlAsync(environment, controlEnvironment);
        var final = await PollUntilAsync(environment, platform, roomId, roomId + "/end",
            sample => sample.Phase == "Done" && sample.RoomStatus == "Settled",
            TimeSpan.FromSeconds(150), samples);
        Assert.Equal("Done", final.Phase);
        AssertChainOrder(samples, "kill-after-finalize");
        await AssertSettlementAsync(environment, platform, round, "e15d", roomId);
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
}
