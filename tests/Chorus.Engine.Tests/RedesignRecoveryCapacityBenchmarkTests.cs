using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// The mass-recovery baseline (FIX-PERF-08, opt-in): one hundred rooms run on one game host,
/// the host dies, and the whole fleet recovers simultaneously on the replacement through the
/// real takeover path (transfer fence, then takeover recovery). The phases are measured where
/// they happen: detection is the control-plane fencing span from the kill to each room's
/// ownership moving Recovering to the replacement (the benchmark drives the reassignment;
/// evidence of an independent node's power loss remains FIX-ENV-01's acceptance), queue, read,
/// replay and activate come straight out of the recovery host's periodic meter dumps, and the
/// peak host memory is sampled off the process RSS across the whole recovery window. The
/// recovery host runs a bounded concurrency gate (FIX-PERF-08): at most the configured number
/// of recoveries' heavy work runs at once, the rest wait, and a recovery past the queue is
/// refused explicitly — so the burst can queue or refuse, never fake a success or OOM the
/// host. Gated by CHORUS_RUN_RECOVERY_CAPACITY=1; a run that happened is then held against
/// the pre-declared SLO (<c>recovery-acceptance.json</c>) — every phase percentile, the peak
/// memory and the recovered-room floor can fail the verdict; a disabled run reports NOT RUN
/// and never counts as a pass. Honest dev-machine data, not a production capacity claim.
/// </summary>
public sealed class RedesignRecoveryCapacityBenchmarkTests(ITestOutputHelper output)
{
    private const int RoomCount = 100;
    // The fleet is killed once every room has committed past the SLO's recovery depth
    // (RecoveryTicks), so the head at the kill is well below the round end: a recovered
    // room must still have room to tick past the head it died at. If the round ended at
    // RecoveryTicks itself, rooms already Finished at the kill could never grow again and
    // the "tick past the head" assertion would fail on finished rounds, not on recovery.
    private const int DurationTicks = 400;
    private const int RecoveryTicks = 120;
    private const int TickMs = 50;

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task HundredRoomsRecoverSimultaneouslyAndReportPhaseDistributions()
    {
        var config = RecoveryAcceptance.LoadConfig();
        if (Environment.GetEnvironmentVariable("CHORUS_RUN_RECOVERY_CAPACITY") != "1")
        {
            var notRun = RecoveryAcceptance.NotRun(config,
                "set CHORUS_RUN_RECOVERY_CAPACITY=1 to opt in — this run does not count as a recovery pass",
                DateTime.UtcNow, DateTime.UtcNow);
            output.WriteLine(notRun.Describe());
            return;
        }
        // The workload shape must match the declared SLO exactly, or the budgets judge a
        // different run than the one declared.
        Assert.Equal(RoomCount, config.Rooms);
        Assert.Equal(RecoveryTicks, config.RecoveryTicksPerRoom);
        Assert.Equal(TickMs, config.TickMs);
        var acceptanceStarted = DateTime.UtcNow;

        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // Both sweeps stay out of the way: the benchmark drives the fence and the recovery
        // itself, so a timer firing mid-measurement would blur the phase spans.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                // The benchmark drives the fence and the recovery itself. The background
                // sweeps stay far beyond the run window: a late sweep racing the fleet would
                // bump an epoch a second time and turn a recovered room into a stale-epoch
                // refusal, which is governance noise, not recovery evidence.
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "600000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "600000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "30"
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
        // The recovery host carries the measurement channel and the concurrency gate: the
        // whole benchmark is what its gate and meters observe.
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#2",
                new Dictionary<string, string>
                {
                    ["CHORUS_GAME_MAX_CONCURRENT_RECOVERIES"] =
                        config.ConcurrentRecoveries.ToString(CultureInfo.InvariantCulture),
                    ["CHORUS_GAME_MAX_QUEUED_RECOVERIES"] =
                        config.QueuedRecoveries.ToString(CultureInfo.InvariantCulture),
                    ["CHORUS_METRICS_DUMP_SECONDS"] = "2"
                }),
            clientTenant: "tenant-a", instanceId: "game-b");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-rec-cap");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-rec-cap");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        using var hostChannelA = environment.Channel(gamePortA, controlCertificate, "game-server");
        var gameHostA = new GameHost.GameHostClient(hostChannelA);
        var gameB = environment.HostProcesses.Single(entry => entry.Instance == "game-b").Process;

        await RegisterHostsAsync(environment, controlCertificate, "rec-cap");

        // One streamer, one claim, one round, one prepared room per index: the fleet holds at
        // Preparing under deferred activation and starts from one common line, so every room
        // is at the same tick depth when the host dies.
        var rooms = new List<Room>();
        for (var index = 0; index < RoomCount; index++)
            rooms.Add(await MountAsync(environment, simulator, index));
        var owners = new List<(string EntityId, long Epoch)>();
        for (var index = 0; index < RoomCount; index++)
        {
            var roomId = RoomId(index);
            owners.Add((roomId, (await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = roomId, OwnerInstanceId = "game-a", LeaseSeconds = 60
            })).Epoch));
        }
        var renewStop = new CancellationTokenSource();
        var renewer = RenewOwnersAsync(control, owners, renewStop.Token);
        for (var index = 0; index < RoomCount; index++)
        {
            var room = rooms[index];
            await rounds.StartRoundAsync(new StartRoundRequest
            {
                RoomId = owners[index].EntityId, OwnerInstanceId = "game-a",
                Epoch = owners[index].Epoch, GameId = "game-a",
                ModuleVersion = GameModuleIdentity.ModuleVersion, ConfigHash = GameModuleIdentity.ConfigHash,
                PackageHash = GameModuleIdentity.PackageHash,
                OperationId = $"start-rec-cap-{index}", TenantId = "tenant-a",
                Participants =
                {
                    new RoundParticipantProto
                    {
                        BindingId = room.BindingId, ParticipantId = room.StreamerId, Role = "Host"
                    }
                }
            });
            var prepared = await gameHostA.PrepareRoomAsync(new PrepareRoomRequest
            {
                RoomId = owners[index].EntityId, GameId = "game-a", OwnerInstanceId = "game-a",
                Epoch = owners[index].Epoch, OperationId = $"run-rec-cap-{index}",
                DurationTicks = DurationTicks, RandomSeed = index, TickMs = TickMs,
                DeferredActivation = true,
                Participants =
                {
                    new GameParticipantProto
                    {
                        BindingId = room.BindingId, ParticipantId = room.StreamerId, Faction = "red"
                    }
                }
            });
            Assert.Equal("Prepared", prepared.Status);
        }
        var releases = await Task.WhenAll(Enumerable.Range(0, RoomCount).Select(index =>
            gameHostA.ActivateRoomAsync(new ActivateRoomRequest
            {
                RoomId = owners[index].EntityId, GameId = "game-a", OwnerInstanceId = "game-a",
                Epoch = owners[index].Epoch, OperationId = $"release-rec-cap-{index}"
            }).ResponseAsync));
        Assert.All(releases, release => Assert.Equal("Running", release.Status));

        // Every room commits past the SLO's tick depth, and each room's head at the kill is
        // the floor its recovery must land on — a recovery that went backwards would fail it.
        var reached = await WaitForAsync(async () => await CountHeadsAtOrAboveAsync(
                environment, RecoveryTicks) == RoomCount, TimeSpan.FromSeconds(120));
        Assert.True(reached, $"Only {await CountHeadsAtOrAboveAsync(environment, RecoveryTicks)} rooms " +
                             $"reached {RecoveryTicks} committed ticks before the kill.");

        // The host dies. Detection is measured from here: the span until the control plane has
        // fenced each room's ownership Recovering to the replacement.
        renewStop.Cancel();
        try { await renewer; }
        catch (OperationCanceledException) { }
        var killedAt = DateTime.UtcNow;
        environment.Kill("game-a");
        var detected = new List<double>();
        var transfers = await Task.WhenAll(Enumerable.Range(0, RoomCount).Select(async index =>
        {
            var moved = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = owners[index].EntityId, ToInstanceId = "game-b",
                LeaseSeconds = 15, OperationId = $"rec-cap-{index}"
            });
            lock (detected) detected.Add((DateTime.UtcNow - killedAt).TotalMilliseconds);
            return moved;
        }));
        Assert.All(transfers, moved => Assert.Equal("Recovering", moved.Status));
        Assert.Equal(owners[0].Epoch + 1, transfers[0].Epoch);
        // The dead host's last commits — including anything in flight at the kill — have all
        // landed by now: this is the floor every recovery must land on and tick past.
        var headsAtKill = await ReadHeadsAsync(environment);
        Assert.Equal(RoomCount, headsAtKill.Count);

        // The whole fleet recovers simultaneously through the real takeover path. The RSS
        // sampler runs across the whole window — the queue drain, the replays, and the ramp of
        // one hundred recovered rooms starting to tick — because that is the memory the
        // recovery target must actually survive.
        var samplerStop = new CancellationTokenSource();
        var sampler = PeakRssKbAsync(gameB, samplerStop.Token);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, RoomCount).Select(async index =>
            await RecoverOneAsync(takeover, owners[index].EntityId)));
        // Hold the window open past the last reply: the fleet of recovered rooms settles into
        // its running steady state inside the sampled span.
        await Task.Delay(TimeSpan.FromSeconds(3));
        samplerStop.Cancel();
        var peakRssMb = await sampler / 1024.0;

        var recoveredRooms = outcomes.Count(entry => entry.Recovered);
        var explicitRejections = outcomes.Count(entry => entry.Refused);
        var firstFailure = outcomes.FirstOrDefault(entry => !entry.Recovered)?.Error;
        var failures = outcomes.Where(entry => !entry.Recovered)
            .Select(entry => $"{entry.Error} ({(entry.Refused ? "refused" : "failed")}, {entry.TotalMs:0} ms)")
            .ToList();
        if (recoveredRooms != RoomCount)
        {
            // A recovery that did not land says who fenced or refused it: the control plane's
            // governance log lines (stall migration, sweep reassignment) name the racer.
            var controlHost = environment.HostProcesses.Single(
                entry => entry.Instance == "Chorus.ControlServer").Process;
            var dataHost = environment.HostProcesses.Single(
                entry => entry.Instance == "Chorus.DataServer").Process;
            foreach (var (name, process) in new[] { ("control", controlHost), ("data", dataHost), ("game-b", gameB) })
            {
                output.WriteLine($"--- {name} output tail ---");
                foreach (var line in environment.HostOutput(process)
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(line => line.TrimEnd('\r')).TakeLast(40))
                    output.WriteLine("  " + line);
            }
            // The refused room's own operation row carries the recovery's note (the reason the
            // activation or the head pin was refused) — the one witness the reply drops.
            await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT room_id, phase, last_error, last_tick FROM control.recovery_operation " +
                    "WHERE room_id LIKE 'room-rec-cap-%' AND phase <> 'Activated'", connection);
                await using var reader = await command.ExecuteReaderAsync();
                output.WriteLine("--- unfinished recovery operations ---");
                while (await reader.ReadAsync())
                    output.WriteLine(
                        $"  {reader.GetString(0)} phase={reader.GetString(1)} " +
                        $"tick={reader.GetInt64(3)} " +
                        $"error={(reader.IsDBNull(2) ? "(none)" : reader.GetString(2))}");
            }
        }
        Assert.True(recoveredRooms == RoomCount,
            $"Only {recoveredRooms} rooms recovered; first failure: {firstFailure}; " +
            $"failures: {string.Join("; ", failures)}");
        // No fake success: the control plane's own answer is not enough — every room's
        // ownership must read Active on the replacement, every operation must be recorded
        // Activated, and every recovered room must be ticking past the head it died at.
        Assert.Equal(RoomCount, await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM control.ownership WHERE kind='room' AND owner_process_instance_id='game-b' " +
            "AND status='Active' AND entity_id LIKE 'room-rec-cap-%'"));
        Assert.Equal(RoomCount, await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM control.recovery_operation WHERE phase='Activated' " +
            "AND room_id LIKE 'room-rec-cap-%'"));
        Assert.False(gameB.HasExited, "The recovery host died during the mass recovery.");
        var ticking = await WaitForAsync(async () =>
        {
            var heads = await ReadHeadsAsync(environment);
            return heads.Count == RoomCount
                && heads.All(pair => pair.Value > headsAtKill.GetValueOrDefault(pair.Key));
        }, TimeSpan.FromSeconds(30));
        Assert.True(ticking, "The recovered rooms never ticked past the heads they died at.");

        // The phase distributions come out of the recovery host's meter dump: the last block
        // for each instrument carries the whole-run samples.
        var gameBOutput = environment.HostOutput(gameB);
        await WaitForAsync(() =>
        {
            gameBOutput = environment.HostOutput(gameB);
            return Task.FromResult(
                MetricSample(gameBOutput, "room.recovery.queue") is { Count: >= RoomCount });
        }, TimeSpan.FromSeconds(15));
        var queue = MetricSample(gameBOutput, "room.recovery.queue");
        var read = MetricSample(gameBOutput, "room.recovery.read");
        var replay = MetricSample(gameBOutput, "room.recovery.replay");
        var activate = MetricSample(gameBOutput, "room.recovery.activate");
        Assert.NotNull(queue);
        Assert.True(queue.Value.Count >= RoomCount,
            $"the dump carries {queue.Value.Count} queue samples for {RoomCount} recoveries");
        var totals = outcomes.Select(entry => entry.TotalMs).OrderBy(ms => ms).ToList();

        output.WriteLine("""
            mass-recovery baseline (NOT a production capacity claim; single dev machine):
              fleet: {0} rooms x {1} committed ticks @ {2} ms, recovered on one replacement host
              gate: {3} concurrent recoveries, {4} queued; {5} rooms recovered, {6} refused
              detection (kill -> fenced to the replacement, per room): p50={7:0} ms  p95={8:0} ms  max={9:0} ms
              total takeover recovery (dispatch -> reply, per room): p50={10:0} ms  p95={11:0} ms  max={12:0} ms
              recovery host peak RSS over the window: {13:0} MB
            """,
            RoomCount, RecoveryTicks, TickMs, config.ConcurrentRecoveries, config.QueuedRecoveries,
            recoveredRooms, explicitRejections,
            Percentile(detected, 0.50), Percentile(detected, 0.95), detected.Max(),
            Percentile(totals, 0.50), Percentile(totals, 0.95), totals[^1],
            peakRssMb);
        output.WriteLine("  phase percentiles (game-b, whole run):");
        foreach (var name in new[] { "room.recovery.queue", "room.recovery.read",
                     "room.recovery.replay", "room.recovery.activate" })
            foreach (var line in gameBOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(line => line.TrimEnd('\r'))
                         .Where(line => line.StartsWith($"metrics name={name} ", StringComparison.Ordinal))
                         .TakeLast(1))
                output.WriteLine("    " + line);

        // The verdict comes after the evidence: the pre-declared SLO's budgets — every phase
        // percentile, the peak memory, the recovered-room floor — can each fail the run.
        var measurement = new RecoveryMeasurement(
            Rooms: RoomCount,
            RecoveredRooms: recoveredRooms,
            ExplicitRejections: explicitRejections,
            DetectP95Ms: Percentile(detected, 0.95),
            QueueP95Ms: queue!.Value.P95Ms,
            ReadP95Ms: read?.P95Ms ?? 0,
            ReplayP95Ms: replay?.P95Ms ?? 0,
            ActivateP95Ms: activate?.P95Ms ?? 0,
            TotalP95Ms: Percentile(totals, 0.95),
            PeakMemoryMb: peakRssMb);
        var report = RecoveryAcceptance.Evaluate(measurement, config,
            acceptanceStarted, DateTime.UtcNow);
        output.WriteLine(report.Describe());
        Assert.Equal(RecoveryVerdict.Passed, report.Verdict);
    }

    [Fact]
    public async Task InvalidRecoveryGateConfigurationFailsClosedAtStartup()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // FIX-PERF-08: a mistyped gate bound is a configuration error — the host refuses to
        // start, it does not silently disable the recovery bound and admit an unbounded burst.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
                new Dictionary<string, string>
                {
                    ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                    ["CHORUS_GAME_MAX_CONCURRENT_RECOVERIES"] = "not-a-number"
                }, clientTenant: "tenant-a"));
        Assert.Contains("must be a positive integer", error.Message);
    }

    private static string RoomId(int index) => $"room-rec-cap-{index}";

    private sealed record Room(string StreamerId, string SessionId, string BindingId);

    private sealed record RecoveryOutcome(bool Recovered, bool Refused, string? Error, double TotalMs);

    /// <summary>One takeover recovery: a refused recovery (ResourceExhausted from the gate)
    /// is an explicit, honest refusal; any other failure names its error for the report.</summary>
    private static async Task<RecoveryOutcome> RecoverOneAsync(Takeover.TakeoverClient takeover,
        string roomId)
    {
        var started = DateTime.UtcNow;
        try
        {
            var reply = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
                ToInstanceId = "game-b", LeaseSeconds = 15, TickMs = TickMs
            });
            return new RecoveryOutcome(reply.Recovered, false,
                reply.Recovered ? null : $"{reply.Status}@{reply.LastTick} (epoch {reply.Epoch})",
                (DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.ResourceExhausted)
        {
            return new RecoveryOutcome(false, true, error.Status.Detail,
                (DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (Exception error)
        {
            return new RecoveryOutcome(false, false, error.Message,
                (DateTime.UtcNow - started).TotalMilliseconds);
        }
    }

    private static Dictionary<string, string> GameEnvironment(
        int dataPort, string controlEndpoint, int gamePort, string slotId,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
            ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
            ["CHORUS_SLOT_ID"] = slotId,
            ["CHORUS_REPORT_INTERVAL_MS"] = "500"
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
            environment[key] = value;
        return environment;
    }

    /// <summary>Registers the node, then waits until both game hosts self-report routes.</summary>
    private static async Task RegisterHostsAsync(
        RedesignM0HostTests.HostEnvironment environment,
        X509Certificate2 controlCertificate, string openPrefix)
    {
        using var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-" + openPrefix);
        using var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server");
        await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
            new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-" + openPrefix });
        using var routingChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var routing = new Chorus.Redesign.Rpc.Routing.RoutingClient(routingChannel);
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                   && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-b"
                   && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");
    }

    /// <summary>
    /// Same-owner re-claims keep the epoch and extend the lease (architecture 4.2). One
    /// hundred leases at the 60s ceiling cannot ride a sequential loop under load, so the
    /// claims run concurrently on a short cadence; only the ending cancellation is swallowed.
    /// </summary>
    private static async Task RenewOwnersAsync(ControlService.ControlServiceClient control,
        IReadOnlyList<(string EntityId, long Epoch)> owners, CancellationToken cancellationToken)
    {
        async Task RenewOneAsync((string EntityId, long Epoch) owner)
        {
            var renewed = await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = owner.EntityId, OwnerInstanceId = "game-a", LeaseSeconds = 60
            }, cancellationToken: cancellationToken);
            if (renewed.Epoch != owner.Epoch)
                throw new InvalidOperationException(
                    $"the room {owner.EntityId} lease epoch moved from {owner.Epoch} to {renewed.Epoch}.");
        }

        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                await Task.WhenAll(owners.Select(RenewOneAsync));
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled) { }
    }

    private static async Task<Room> MountAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentPlatformSimulator simulator, int index)
    {
        var openId = $"rec-cap-{index}";
        var liveKey = $"live-rec-cap-{index}";
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
        return new Room(login.StreamerId, login.SessionId, mounted.BindingId);
    }

    /// <summary>The peak VmRSS of the recovery host, sampled across the recovery window; a
    /// sampler that cannot read must never fail the run.</summary>
    private static async Task<long> PeakRssKbAsync(Process process, CancellationToken cancellationToken)
    {
        long peak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(
                             $"/proc/{process.Id}/status", cancellationToken))
                    if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                        peak = Math.Max(peak, long.Parse(
                            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1],
                            CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        return peak;
    }

    /// <summary>The last dump line of one instrument: the sample count and the p95 the SLO
    /// judges, straight from the recovery host's whole-run percentile dump.</summary>
    private static (int Count, double P95Ms)? MetricSample(string hostOutput, string name)
    {
        string? last = null;
        foreach (var line in hostOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith($"metrics name={name} ", StringComparison.Ordinal)) last = trimmed;
        }
        if (last is null) return null;
        var count = Regex.Match(last, "count=([0-9]+)");
        var p95 = Regex.Match(last, "p95=([0-9.]+)ms");
        if (!count.Success || !p95.Success) return null;
        return (int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(p95.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static double Percentile(List<double> samples, double fraction)
    {
        var ordered = samples.OrderBy(value => value).ToList();
        return ordered[Math.Min(ordered.Count - 1, (int)Math.Floor(ordered.Count * fraction))];
    }

    private static async Task<long> CountHeadsAtOrAboveAsync(
        RedesignM0HostTests.HostEnvironment environment, long floor)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM (SELECT room_id FROM game.frame WHERE room_id LIKE 'room-rec-cap-%' " +
            "GROUP BY room_id HAVING max(tick_id) >= @floor) heads", connection);
        command.Parameters.AddWithValue("floor", floor);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<Dictionary<string, long>> ReadHeadsAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT room_id,COALESCE(max(tick_id),0) FROM game.frame WHERE room_id LIKE 'room-rec-cap-%' " +
            "GROUP BY room_id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var heads = new Dictionary<string, long>();
        while (await reader.ReadAsync())
            heads[reader.GetString(0)] = reader.GetInt64(1);
        return heads;
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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
