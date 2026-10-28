using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Npgsql;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M2 single-node dev baseline (AT13 distribution half, opt-in). One node, one GameServer,
/// one DataServer: 100 concurrent rooms at 20 Hz plus one hot room fed 500 inputs/s. The report
/// is honest dev-machine data — a single-node development baseline, not a production capacity
/// claim; the full capacity model belongs to M4. Gated by CHORUS_RUN_M2_CAPACITY=1; the
/// assertions check the run happened (frame count, routed inputs), never a latency threshold.
/// </summary>
public sealed class RedesignM2CapacityBenchmarkTests(ITestOutputHelper output)
{
    private const int RoomCount = 100;
    private const int DurationTicks = 100;
    private const int TickMs = 50;
    private const int HotEventsPerSecond = 500;

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task HundredRoomsWithAHotRoomReportSingleNodeDistributions()
    {
        if (Environment.GetEnvironmentVariable("CHORUS_RUN_M2_CAPACITY") != "1")
        {
            output.WriteLine("M2 capacity benchmark not run; set CHORUS_RUN_M2_CAPACITY=1 to opt in.");
            return;
        }
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
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
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

        // Mount one streamer per room. The last-prepared room is the hot room (see below).
        var rooms = new List<Room>();
        for (var index = 0; index < RoomCount; index++)
            rooms.Add(await MountAsync(environment, simulator, index));

        // One owner claim per room at the lease ceiling (architecture 4.2), renewed by a single
        // background loop so a work-bound room cannot outlive its fence under load (the M2-6
        // activity lesson); a lost epoch is a real failure.
        var owners = new List<(string EntityId, OwnershipReply Owner)>();
        for (var index = 0; index < rooms.Count; index++)
        {
            var roomId = $"room-cap-bench-{index}";
            owners.Add((roomId, await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = roomId, OwnerInstanceId = "game-1", LeaseSeconds = 60
            })));
        }
        var renewStop = new CancellationTokenSource();
        var renewer = RenewOwnersAsync(control, owners, renewStop.Token);

        var starts = new Dictionary<string, DateTime>();
        for (var index = 0; index < rooms.Count; index++)
        {
            var room = rooms[index];
            var roomId = $"room-cap-bench-{index}";
            var owner = owners[index].Owner;
            await rounds.StartRoundAsync(new StartRoundRequest
            {
                RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
                ModuleVersion = "1.0.0", ConfigHash = "cfg-bench", PackageHash = "pkg-bench",
                OperationId = $"start-cap-{index}",
                Participants =
                {
                    new RoundParticipantProto { BindingId = room.BindingId, ParticipantId = room.StreamerId, Role = "Host" }
                }
            });
            await gameHost.PrepareRoomAsync(new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
                OperationId = $"run-cap-{index}", DurationTicks = DurationTicks, RandomSeed = index,
                TickMs = TickMs,
                Participants =
                {
                    new GameParticipantProto { BindingId = room.BindingId, ParticipantId = room.StreamerId, Faction = "red" }
                }
            });
            starts[roomId] = DateTime.UtcNow;
        }

        // The hot room is the LAST prepared room: it is still Running when feeding starts.
        // (The first-prepared room finishes during setup; events to a terminal round land
        // OutOfRound — the honest terminal disposition — and route to nothing.)
        var hotRoomId = $"room-cap-bench-{RoomCount - 1}";
        var hotStop = new CancellationTokenSource();
        var hotFeederTask = FeedHotRoomAsync(ingest, routing, HotEventsPerSecond, hotStop.Token);
        var walStart = await CurrentWalInsertLsnAsync(environment);
        var cpuStart = CpuSeconds(environment);

        // Every room runs its 100 ticks; completion is observed on a 100 ms poll.
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
        Assert.True(hotRouted > 0,
            $"the hot room received no routed inputs (fed {hotEvents.Delivered}, " +
            $"dispositions {string.Join(",", dispositions)}" +
            (hotEvents.FirstError is null ? "" : $", first error {hotEvents.FirstError.Message}") + ").");
        Assert.True(hotEvents.FirstError is null,
            "the hot-room feed hit a server error after " + hotEvents.Delivered + " events " +
            "(dispositions " + string.Join(",", dispositions) + "): "
            + (hotEvents.FirstError?.Message ?? "no error recorded"));

        var durations = starts
            .Where(entry => completed.TryGetValue(entry.Key, out _))
            .Select(entry => (completed[entry.Key] - entry.Value).TotalMilliseconds)
            .OrderBy(ms => ms).ToList();
        var p50 = durations[(int)(durations.Count * 0.50)];
        var p95 = durations[(int)(durations.Count * 0.95)];
        var p99 = durations[^1];
        var wallSeconds = (completed.Values.Max() - starts.Values.Min()).TotalSeconds;
        var feedSeconds = (hotEvents.EndedUtc - hotEvents.StartedUtc).TotalSeconds;
        var dispositionText = string.Join(",", dispositions.Select(entry => $"{entry.Disposition}={entry.Count}"))
            + (hotEvents.FirstError is null ? "" : $"; first error: {hotEvents.FirstError.Message}");

        output.WriteLine("""
            M2 single-node dev baseline (NOT a production capacity claim; single dev machine):
              rooms={0}  ticks={1}@TickMs={2}  hot room={3} events/s (durable)
              frames committed: {4} (expected {5})
              hot room inputs: fed {6} routed {7} (achieved {13:0} events/s over {14:0.0}s, target {3}/s)
              hot room dispositions: {18}
              per-room completion: p50={8:0}ms  p95={9:0}ms  p99={10:0}ms  (100ms poll granularity)
              observed frame rate: {11:0} frames/s over {12:0.0}s
              CPU game-server: {15:0.0}s  data-server: {16:0.0}s
              WAL inserted: {17:0.0} MB
            """,
            RoomCount, DurationTicks, TickMs, HotEventsPerSecond, frameCount,
            (long)RoomCount * DurationTicks, hotEvents.Delivered, hotRouted, p50, p95, p99,
            frameCount / Math.Max(wallSeconds, 0.001), wallSeconds,
            hotEvents.Delivered / Math.Max(feedSeconds, 0.001), feedSeconds,
            cpu.GetValueOrDefault("Chorus.GameServer"), cpu.GetValueOrDefault("Chorus.DataServer"),
            walBytes / (1024.0 * 1024.0), dispositionText);
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
        IReadOnlyList<(string EntityId, OwnershipReply Owner)> owners, CancellationToken cancellationToken)
    {
        async Task RenewOneAsync((string EntityId, OwnershipReply Owner) owner)
        {
            var renewed = await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room",
                EntityId = owner.EntityId, OwnerInstanceId = "game-1", LeaseSeconds = 60
            }, cancellationToken: cancellationToken);
            if (renewed.Epoch != owner.Owner.Epoch)
                throw new InvalidOperationException(
                    $"the room {owner.EntityId} lease epoch moved from {owner.Owner.Epoch} to {renewed.Epoch}.");
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

    private sealed record HotFeed(int Delivered, DateTime StartedUtc, DateTime EndedUtc, Exception? FirstError);

    private static async Task<HotFeed> FeedHotRoomAsync(PlatformIngest.PlatformIngestClient ingest,
        RoundInput.RoundInputClient routing, int perSecond, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var delivered = 0;
        var counter = 0;
        var batchIndex = 0;
        Exception? firstError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = new IngestBatchRequest
                {
                    Platform = "simulator", AppId = "app-1", OperationId = $"cap-hot-batch-{batchIndex}"
                };
                for (var i = 0; i < perSecond / 20; i++)
                {
                    batch.Events.Add(new IngestEvent
                    {
                        SourceDedupKey = $"cap-hot-{counter++}",
                        Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
                        CanonicalRoomKey = $"live-cap-bench-{RoomCount - 1}",
                        PayloadJson = """{"kind":"gift","amount":1}"""
                    });
                }
                await ingest.IngestBatchAsync(batch, cancellationToken: cancellationToken);
                await routing.RouteEventsAsync(new RouteEventsRequest
                {
                    Platform = "simulator", AppId = "app-1", OperationId = $"cap-hot-route-{batchIndex}",
                    MaxEvents = 100
                }, cancellationToken: cancellationToken);
                delivered += batch.Events.Count;
                batchIndex++;
                await Task.Delay(50, cancellationToken);
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
        return new HotFeed(delivered, started, DateTime.UtcNow, firstError);
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
