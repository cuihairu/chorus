using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Npgsql;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M2 single-node dev baseline (AT13 distribution half, opt-in). One node, one GameServer,
/// one DataServer: 100 rooms released across one common start line (FIX-PERF-03 deferred
/// activation) at 20 Hz plus one hot room fed a planned 500 inputs/s. The measurement discards
/// a fixed warmup (the first <see cref="WarmupTicks"/> ticks) and samples the fixed tick
/// window that follows, using the data plane's own frame-commit clock. The input generator
/// keeps an honest plan: it records what the schedule called for, what it actually emitted,
/// and the backlog — a sustained shortfall fails the run instead of quietly re-targeting the
/// achieved rate. The report is honest dev-machine data, not a production capacity claim.
/// Gated by CHORUS_RUN_M2_CAPACITY=1; a run that happened is then held against the explicit
/// acceptance config (<c>capacity-acceptance.json</c>, FIX-PERF-04) — the target architecture
/// (100 rooms / 20 Hz), the completion-latency budget and the input plan can each fail the
/// verdict; a disabled run reports NOT RUN and never counts as a pass.
/// </summary>
public sealed class RedesignM2CapacityBenchmarkTests(ITestOutputHelper output)
{
    private const int RoomCount = 100;
    private const int DurationTicks = 100;
    private const int TickMs = 50;
    private const int HotEventsPerSecond = 500;
    private const int WarmupTicks = 20;

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task HundredRoomsFromACommonStartLineReportSingleNodeDistributions()
    {
        if (Environment.GetEnvironmentVariable("CHORUS_RUN_M2_CAPACITY") != "1")
        {
            var notRun = CapacityAcceptance.NotRun(CapacityAcceptance.LoadConfig(),
                "set CHORUS_RUN_M2_CAPACITY=1 to opt in — this run does not count as a capacity pass",
                DateTime.UtcNow, DateTime.UtcNow);
            output.WriteLine(notRun.Describe());
            return;
        }
        var acceptanceStarted = DateTime.UtcNow;
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server",
            new Dictionary<string, string> { ["CHORUS_METRICS_DUMP_SECONDS"] = "5" });
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_METRICS_DUMP_SECONDS"] = "5"
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var routing = new RoundInput.RoundInputClient(gameChannel);
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var ingest = new PlatformIngest.PlatformIngestClient(gatewayChannel);
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);

        // Mount one streamer per room; the hot room is room 0 — under a common start line it
        // is guaranteed to be Running when feeding starts (no ordering tricks needed).
        var rooms = new List<Room>();
        for (var index = 0; index < RoomCount; index++)
            rooms.Add(await MountAsync(environment, simulator, index));

        // One owner claim per room at the lease ceiling (architecture 4.2), renewed by a single
        // background loop so a work-bound room cannot outlive its fence under load (the M2-6
        // activity lesson); a lost epoch is a real failure.
        var owners = new List<(string EntityId, long Epoch)>();
        for (var index = 0; index < rooms.Count; index++)
        {
            var roomId = $"room-cap-bench-{index}";
            owners.Add((roomId, (await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = roomId, OwnerInstanceId = "game-1", LeaseSeconds = 60
            })).Epoch));
        }
        var renewStop = new CancellationTokenSource();
        var renewer = RenewOwnersAsync(control, owners, renewStop.Token);

        // Admit and prepare the whole fleet with deferred activation: every room is held —
        // round Preparing, actor not ticking — until the common release.
        for (var index = 0; index < rooms.Count; index++)
        {
            var room = rooms[index];
            var roomId = owners[index].EntityId;
            await rounds.StartRoundAsync(new StartRoundRequest
            {
                RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owners[index].Epoch, GameId = "game-a",
                ModuleVersion = "1.0.0", ConfigHash = "cfg-bench", PackageHash = "pkg-bench",
                OperationId = $"start-cap-{index}",
                Participants =
                {
                    new RoundParticipantProto { BindingId = room.BindingId, ParticipantId = room.StreamerId, Role = "Host" }
                }
            });
            var prepared = await gameHost.PrepareRoomAsync(new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owners[index].Epoch,
                OperationId = $"run-cap-{index}", DurationTicks = DurationTicks, RandomSeed = index,
                TickMs = TickMs, DeferredActivation = true,
                Participants =
                {
                    new GameParticipantProto { BindingId = room.BindingId, ParticipantId = room.StreamerId, Faction = "red" }
                }
            });
            Assert.Equal("Prepared", prepared.Status);
        }

        // The fleet is held: every round still Preparing and not one frame committed, long
        // after the sequential prepares have finished.
        Assert.Equal((long)RoomCount, await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM game.room WHERE room_id LIKE 'room-cap-bench-%' AND status='Preparing'"));
        Assert.Equal(0L, await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM game.frame WHERE room_id LIKE 'room-cap-bench-%'"));

        // The common start line: the whole fleet is released in one burst, and the run clock
        // starts at the end of the burst for every room alike.
        var lineEnd = DateTime.UtcNow;
        var releases = await Task.WhenAll(Enumerable.Range(0, RoomCount).Select(index =>
            gameHost.ActivateRoomAsync(new ActivateRoomRequest
            {
                RoomId = owners[index].EntityId, GameId = "game-a", OwnerInstanceId = "game-1",
                Epoch = owners[index].Epoch, OperationId = $"release-cap-{index}"
            }).ResponseAsync));
        Assert.All(releases, release => Assert.Equal("Running", release.Status));
        var burstMs = (DateTime.UtcNow - lineEnd).TotalMilliseconds;

        // The planned input generator runs from the line until the fleet has finished.
        var hotRoomId = owners[0].EntityId;
        var hotStop = new CancellationTokenSource();
        var hotFeederTask = FeedHotRoomAsync(ingest, routing, 0, HotEventsPerSecond, hotStop.Token);
        var walStart = await CurrentWalInsertLsnAsync(environment);
        var cpuStart = CpuSeconds(environment);

        // Every room runs its 100 ticks from the common line; completion is observed on a
        // 100 ms poll.
        var completed = new Dictionary<string, DateTime>();
        var deadline = DateTime.UtcNow.AddSeconds(240);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            while (completed.Count < RoomCount && DateTime.UtcNow < deadline)
            {
                await using var progress = new NpgsqlCommand(
                    "SELECT room_id,max(tick_id) FROM game.frame WHERE room_id LIKE 'room-cap-bench-%' GROUP BY room_id",
                    connection);
                await using var reader = await progress.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var roomId = reader.GetString(0);
                    if (reader.GetInt64(1) == DurationTicks && !completed.ContainsKey(roomId))
                        completed[roomId] = DateTime.UtcNow;
                }
                await Task.Delay(100);
            }
        }
        renewStop.Cancel();
        await renewer;
        hotStop.Cancel();
        var hotEvents = await hotFeederTask;
        var frameCount = await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM game.frame WHERE room_id LIKE 'room-cap-bench-%'");
        var hotRouted = await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM input.delivery WHERE room_id='" + hotRoomId + "'");
        var dispositions = await DispositionsAsync(environment.DatabaseConnectionString);
        var walBytes = await WalBytesSinceAsync(environment, walStart);
        var cpu = CpuSeconds(environment)
            .ToDictionary(entry => entry.Key, entry => entry.Value - cpuStart.GetValueOrDefault(entry.Key));

        Assert.Equal(RoomCount, completed.Count);
        Assert.Equal((long)RoomCount * DurationTicks, frameCount);
        // FIX-PERF-05: the rewards leg of the consistency guard — every room's final frame
        // lands exactly one result row, and grants land for exactly the rooms whose final
        // ranking contains a positive score (only the fed room scores here); a storage
        // change may not disturb either.
        Assert.Equal((long)RoomCount, await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(*) FROM game.result WHERE room_id LIKE 'room-cap-bench-%'"));
        var scoringRooms = await CountAsync(environment.DatabaseConnectionString, """
            SELECT count(*) FROM game.result
            WHERE room_id LIKE 'room-cap-bench-%'
              AND EXISTS (SELECT 1 FROM jsonb_array_elements(result->'ranking') entry
                          WHERE (entry->>'score')::bigint > 0)
            """);
        var grantRooms = await CountAsync(environment.DatabaseConnectionString,
            "SELECT count(DISTINCT room_id) FROM settlement.grant WHERE room_id LIKE 'room-cap-bench-%'");
        Assert.Equal(scoringRooms, grantRooms);
        Assert.True(grantRooms > 0, "the fed room's final score must produce its planned grants");
        Assert.True(hotRouted > 0,
            $"the hot room received no routed inputs (fed {hotEvents.Emitted}, " +
            $"dispositions {string.Join(",", dispositions)}" +
            (hotEvents.FirstError is null ? "" : $", first error {hotEvents.FirstError.Message}") + ").");
        Assert.True(hotEvents.FirstError is null,
            "the hot-room feed hit a server error after " + hotEvents.Emitted + " events " +
            "(dispositions " + string.Join(",", dispositions) + "): "
            + (hotEvents.FirstError?.Message ?? "no error recorded"));

        // Commit-clock measurement: the first frame of every room, and per room the span of
        // the fixed sampling window (warmup discarded), straight off the frame.commit
        // receipts' committed_at.
        var sampled = new List<(DateTime First, DateTime SampleStart, DateTime SampleEnd)>();
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("""
                SELECT min(committed_at),
                       min(committed_at) FILTER (WHERE (result_json->>'Tick')::bigint > @warm),
                       max(committed_at) FILTER (WHERE (result_json->>'Tick')::bigint <= @last)
                FROM infra.operation_receipt
                WHERE operation_kind='frame.commit' AND tenant_id='tenant-a'
                  AND target_id LIKE 'room-cap-bench-%'
                GROUP BY target_id
                """, connection);
            query.Parameters.AddWithValue("warm", (long)WarmupTicks);
            query.Parameters.AddWithValue("last", (long)DurationTicks);
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sampled.Add((reader.GetDateTime(0), reader.GetDateTime(1), reader.GetDateTime(2)));
        }
        Assert.Equal(RoomCount, sampled.Count);
        var firstSpread = (sampled.Max(item => item.First) - sampled.Min(item => item.First)).TotalMilliseconds;
        // The common active window: the tick range where all 100 rooms were concurrently
        // committing inside the sampling window.
        var windowStart = sampled.Max(item => item.SampleStart);
        var windowEnd = sampled.Min(item => item.SampleEnd);
        var commonMs = (windowEnd - windowStart).TotalMilliseconds;

        var durations = completed.Values
            .Select(finished => (finished - lineEnd).TotalMilliseconds)
            .OrderBy(ms => ms).ToList();
        var p50 = durations[(int)(durations.Count * 0.50)];
        var p95 = durations[(int)(durations.Count * 0.95)];
        var p99 = durations[^1];
        var wallSeconds = (completed.Values.Max() - lineEnd).TotalSeconds;
        var feedSeconds = (hotEvents.EndedUtc - hotEvents.StartedUtc).TotalSeconds;
        var dispositionText = string.Join(",", dispositions.Select(entry => $"{entry.Disposition}={entry.Count}"))
            + (hotEvents.FirstError is null ? "" : $"; first error: {hotEvents.FirstError.Message}");

        output.WriteLine("""
            M2 single-node dev baseline (NOT a production capacity claim; single dev machine):
              rooms={0}  ticks={1}@TickMs={2}  hot room={3} events/s (durable, planned)
              common start line: release burst {16:0} ms, first-frame spread {17:0} ms
              sampling window: ticks {18}..{19} (warmup {18} ticks discarded), common active window {20:0} ms
              frames committed: {4} (expected {5})
              hot room inputs: planned {21} emitted {6} backlog {22} ({23:0}/s achieved over {24:0.0}s)
              hot room dispositions: {15}
              per-room completion from the line: p50={7:0}ms  p95={8:0}ms  p99={9:0}ms  (100ms poll granularity)
              observed frame rate: {10:0} frames/s over {11:0.0}s
              CPU game-server: {12:0.0}s  data-server: {13:0.0}s
              WAL inserted: {14:0.0} MB
            """,
            RoomCount, DurationTicks, TickMs, HotEventsPerSecond, frameCount,
            (long)RoomCount * DurationTicks, hotEvents.Emitted, p50, p95, p99,
            frameCount / Math.Max(wallSeconds, 0.001), wallSeconds,
            cpu.GetValueOrDefault("Chorus.GameServer"), cpu.GetValueOrDefault("Chorus.DataServer"),
            walBytes / (1024.0 * 1024.0), dispositionText, burstMs, firstSpread,
            WarmupTicks, DurationTicks, commonMs,
            hotEvents.Planned, hotEvents.Backlog,
            hotEvents.Emitted / Math.Max(feedSeconds, 0.001), feedSeconds);

        // Phase percentiles straight out of the hosts' periodic meter dumps (the FIX-PERF-05
        // measurement channel): whole-run distributions per phase — inbox pull, apply,
        // serialize, commit, whole tick on the game server; connection open, transaction body,
        // commit on the data server — so the bottleneck attribution ships with every report.
        foreach (var (process, instance) in environment.HostProcesses)
        {
            if (!instance.Contains("GameServer") && !instance.Contains("DataServer")) continue;
            var metricLines = environment.HostOutput(process)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("metrics name=", StringComparison.Ordinal))
                .ToList();
            if (metricLines.Count == 0) continue;
            var firstInstrument = metricLines[0]["metrics name=".Length..].Split(' ')[0];
            var lastBlockStart = metricLines.FindLastIndex(
                line => line.StartsWith($"metrics name={firstInstrument} ", StringComparison.Ordinal));
            output.WriteLine($"  phase percentiles ({instance}, whole run):");
            foreach (var line in metricLines[lastBlockStart..])
                output.WriteLine("    " + line);
        }

        // The verdict comes after the evidence: the explicit acceptance config's budgets
        // (target architecture 100 rooms / 20 Hz, tick and completion latency, the input plan)
        // can each fail the run; a disabled run reported NOT RUN never counts as a pass.
        var measurement = new CapacityMeasurement(
            Rooms: RoomCount, TicksPerRoom: DurationTicks,
            FramesCommitted: frameCount, FramesExpected: (long)RoomCount * DurationTicks,
            AggregateFrameRate: frameCount / Math.Max(wallSeconds, 0.001),
            PerRoomCompletionP95Ms: p95, CommonWindowMs: commonMs,
            PlannedInputs: hotEvents.Planned, EmittedInputs: hotEvents.Emitted,
            BacklogInputs: hotEvents.Backlog, InputFirstError: hotEvents.FirstError?.Message);
        var report = CapacityAcceptance.Evaluate(measurement, CapacityAcceptance.LoadConfig(),
            acceptanceStarted, DateTime.UtcNow);
        output.WriteLine(report.Describe());
        Assert.Equal(CapacityVerdict.Passed, report.Verdict);
    }

    private sealed record Room(string StreamerId, string SessionId, string BindingId);

    /// <summary>
    /// Same-owner re-claims keep the epoch and extend the lease (architecture 4.2). One
    /// hundred leases at the 60s ceiling cannot ride a sequential loop under load — a full
    /// renewal cycle must stay far below the lease or the fence expires a room mid-round —
    /// so the claims run concurrently on a short cadence. A failed claim or a moved epoch
    /// fails the benchmark; only the ending cancellation is swallowed.
    /// </summary>
    private static async Task RenewOwnersAsync(ControlService.ControlServiceClient control,
        IReadOnlyList<(string EntityId, long Epoch)> owners, CancellationToken cancellationToken)
    {
        async Task RenewOneAsync((string EntityId, long Epoch) owner)
        {
            var renewed = await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = owner.EntityId, OwnerInstanceId = "game-1", LeaseSeconds = 60
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
        catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.Cancelled) { }
    }

    private static async Task<Room> MountAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentPlatformSimulator simulator, int index)
    {
        var openId = $"cap-{index}";
        var liveKey = $"live-cap-bench-{index}";
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

    private sealed record HotFeed(long Planned, long Emitted, long Backlog, DateTime StartedUtc,
        DateTime EndedUtc, Exception? FirstError);

    /// <summary>
    /// A rate-planned input generator: every cadence it computes what a perfect target-rate
    /// clock owes by now and delivers up to the batch cap of the deficit — transient stalls
    /// catch up, a sustained shortfall accumulates as backlog and fails the benchmark at the
    /// end. The plan is never re-targeted to whatever was achieved.
    /// </summary>
    private static async Task<HotFeed> FeedHotRoomAsync(PlatformIngest.PlatformIngestClient ingest,
        RoundInput.RoundInputClient routing, int hotRoomIndex, int perSecond,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        const int cadenceMs = 50;
        const int maxBatch = 100;
        long emitted = 0;
        long batchIndex = 0;
        long sequence = 0;
        Exception? firstError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var before = DateTime.UtcNow;
                var planned = (long)Math.Floor(perSecond * (before - started).TotalMilliseconds / 1000.0);
                var due = (int)Math.Min(maxBatch, Math.Max(0, planned - emitted));
                if (due > 0)
                {
                    var batch = new IngestBatchRequest
                    {
                        Platform = "simulator", AppId = "app-1", OperationId = $"cap-hot-batch-{batchIndex}"
                    };
                    for (var i = 0; i < due; i++)
                        batch.Events.Add(new IngestEvent
                        {
                            SourceDedupKey = $"cap-hot-{sequence++}",
                            Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
                            CanonicalRoomKey = $"live-cap-bench-{hotRoomIndex}",
                            PayloadJson = """{"kind":"gift","amount":1}"""
                        });
                    await ingest.IngestBatchAsync(batch, cancellationToken: cancellationToken);
                    await routing.RouteEventsAsync(new RouteEventsRequest
                    {
                        Platform = "simulator", AppId = "app-1", OperationId = $"cap-hot-route-{batchIndex}",
                        MaxEvents = 200
                    }, cancellationToken: cancellationToken);
                    emitted += due;
                    batchIndex++;
                }
                var spent = (DateTime.UtcNow - before).TotalMilliseconds;
                await Task.Delay(Math.Max(0, (int)(cadenceMs - spent)), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Grpc.Core.RpcException error)
        {
            // Cancellation races an in-flight call; a real refusal is a benchmark failure
            // and surfaces through the assert message.
            if (error.StatusCode != Grpc.Core.StatusCode.Cancelled)
                firstError ??= error;
        }
        var ended = DateTime.UtcNow;
        var finalPlanned = (long)Math.Floor(perSecond * (ended - started).TotalMilliseconds / 1000.0);
        return new HotFeed(finalPlanned, emitted, Math.Max(0, finalPlanned - emitted), started, ended, firstError);
    }

    private static async Task<List<(string Disposition, long Count)>> DispositionsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT disposition,count(*) FROM input.event
            WHERE source_dedup_key LIKE 'cap-hot-%' GROUP BY disposition
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<(string, long)>();
        while (await reader.ReadAsync())
            result.Add((reader.GetString(0), reader.GetInt64(1)));
        return result;
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> CurrentWalInsertLsnAsync(RedesignM0HostTests.HostEnvironment environment)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_current_wal_insert_lsn()::text", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> WalBytesSinceAsync(RedesignM0HostTests.HostEnvironment environment, string lsn)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(), @lsn::pg_lsn)", connection);
        command.Parameters.AddWithValue("lsn", lsn);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static IReadOnlyDictionary<string, double> CpuSeconds(RedesignM0HostTests.HostEnvironment environment) =>
        environment.HostProcesses
            .GroupBy(entry => entry.Instance)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Process.TotalProcessorTime.TotalSeconds));
}
