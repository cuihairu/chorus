using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;
using Npgsql;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

public sealed class ControlPostgresCapacityBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Capacity")]
    public async Task RecordPostgres100RoomCapacityBaseline()
    {
        if (Environment.GetEnvironmentVariable("CHORUS_RUN_CONTROL_CAPACITY") != "1")
        {
            output.WriteLine("Capacity benchmark not run; set CHORUS_RUN_CONTROL_CAPACITY=1 to opt in.");
            return;
        }

        using var testDatabase = new PostgresTestDatabase();
        var maxPoolSize = int.TryParse(Environment.GetEnvironmentVariable("CHORUS_CONTROL_CAPACITY_POOL_SIZE"), out var configuredPool)
            ? Math.Clamp(configuredPool, 20, 500) : 100;
        var postgresConnection = new NpgsqlConnectionStringBuilder(testDatabase.ConnectionString) { Pooling = true, MaxPoolSize = maxPoolSize }.ConnectionString;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("CHORUS_CONTROL_CAPACITY_SECONDS"), out var configured)
            ? Math.Clamp(configured, 5, 120) : 10;
        var prefix = "pg-capacity-" + Guid.NewGuid().ToString("N");
        var store = new RoomControlStore(new ControlDatabase(postgresConnection, ControlDatabaseProvider.PostgreSql),
            new FrozenTimeProvider(DateTimeOffset.UtcNow));
        var workerId = prefix + "-worker";
        var workerInstance = prefix + "-boot";
        store.ReportNode(new(workerId, workerInstance, prefix + "-machine", "worker", new("127.0.0.1", 39999), 100));
        var rooms = Enumerable.Range(0, 100).Select(i => store.Assign(new($"{prefix}-{i:D3}"))).ToArray();
        var owners = rooms.ToDictionary(r => r.Id, r => new OwnerRequest(workerId, workerInstance, r.Epoch));
        var statePayload = new string('x', 12 * 1024);
        var checkpointLatencies = new ConcurrentBag<double>();
        var eventLatencies = new ConcurrentBag<double>();
        var failures = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var checkpointCount = 0;
        var eventCount = 0;
        var walBefore = await ReadWalBytes(postgresConnection);
        using var stop = new ManualResetEvent(false);
        using var start = new ManualResetEvent(false);
        using var ready = new CountdownEvent(rooms.Length + 20);
        var threads = new List<Thread>(rooms.Length + 20);

        foreach (var (room, index) in rooms.Select((room, index) => (room, index)))
        {
            var tick = 0L;
            threads.Add(StartPeriodicThread(66, index * 66 / rooms.Length, start, ready, stop, () =>
            {
                var prepared = store.Prepare(room.Id, owners[room.Id]);
                var started = Stopwatch.GetTimestamp();
                var nextTick = tick + 1;
                var state = JsonSerializer.SerializeToElement(new { tick = nextTick, payload = statePayload });
                if (!store.Commit(room.Id, new(workerId, workerInstance, room.Epoch, tick, nextTick, 1, state,
                        prepared.Inbox!.Select(RoomControlStore.EventKey).ToArray())))
                    throw new InvalidOperationException("Checkpoint commit returned false.");
                tick = nextTick;
                checkpointLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Interlocked.Increment(ref checkpointCount);
            }, failures));

        }

        var eventSequence = new int[20];
        for (var producer = 0; producer < eventSequence.Length; producer++)
        {
            var producerId = producer;
            threads.Add(StartPeriodicThread(20, producer, start, ready, stop, () =>
            {
                var roomIndex = Interlocked.Increment(ref eventSequence[producerId]) % rooms.Length;
                var room = rooms[roomIndex];
                var started = Stopwatch.GetTimestamp();
                var accepted = store.AppendEvent(new AudienceEvent($"{producerId}-{eventSequence[producerId]}", "capacity", room.Id,
                    $"viewer-{producerId}", AudienceEventKind.Like, 1));
                if (!accepted) throw new InvalidOperationException("Generated capacity event was deduplicated.");
                eventLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Interlocked.Increment(ref eventCount);
            }, failures));
        }
        store.ReportNode(new(workerId, workerInstance, prefix + "-machine", "worker", new("127.0.0.1", 39999), 100));

        foreach (var thread in threads) thread.Start();
        ready.Wait();
        var measured = Stopwatch.StartNew();
        start.Set();
        stop.WaitOne(TimeSpan.FromSeconds(seconds));
        measured.Stop();
        stop.Set();
        foreach (var thread in threads) thread.Join();
        var walAfter = await ReadWalBytes(postgresConnection);
        var checkpointSamples = checkpointLatencies.Order().ToArray();
        var eventSamples = eventLatencies.Order().ToArray();
        double P(double[] samples, double percentile) => samples.Length == 0 ? double.NaN
            : samples[Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)];
        var checkpointRate = checkpointCount / measured.Elapsed.TotalSeconds;
        var eventRate = eventCount / measured.Elapsed.TotalSeconds;
        var meetsLocalLatencyTargets = P(checkpointSamples, .95) <= 20 && P(checkpointSamples, .99) <= 40 && P(checkpointSamples, .999) <= 60;
        var capacityGatePassed = checkpointRate >= 1515 && eventRate >= 1000 && meetsLocalLatencyTargets && failures.IsEmpty;
        var failureKinds = failures.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}").ToArray();
        output.WriteLine($"rooms=100 poolSize={maxPoolSize} durationSeconds={measured.Elapsed.TotalSeconds:F2} checkpoints={checkpointCount} checkpointsPerSecond={checkpointRate:F1} " +
                         $"checkpointMs[p50={P(checkpointSamples, .50):F2},p95={P(checkpointSamples, .95):F2},p99={P(checkpointSamples, .99):F2},p999={P(checkpointSamples, .999):F2}] " +
                         $"events={eventCount} eventsPerSecond={eventRate:F1} eventMs[p95={P(eventSamples, .95):F2},p99={P(eventSamples, .99):F2}] " +
                         $"failedOperations={failures.Values.Sum()} failureKinds=[{string.Join(',', failureKinds)}] walBytesDelta={walAfter - walBefore} " +
                         $"localLatencyTargetsMet={meetsLocalLatencyTargets} capacityGatePassed={capacityGatePassed}");
        Assert.True(checkpointCount > 0 && eventCount > 0, "Benchmark did not commit both workload types.");
    }

    private static Thread StartPeriodicThread(int periodMs, int phaseMs, ManualResetEvent start, CountdownEvent ready, ManualResetEvent stop,
        Action operation, ConcurrentDictionary<string, int> failures) => new(() =>
    {
        ready.Signal();
        start.WaitOne();
        var periodTicks = (long)(periodMs / 1000d * Stopwatch.Frequency);
        var next = Stopwatch.GetTimestamp() + (long)(phaseMs / 1000d * Stopwatch.Frequency);
        while (WaitUntil(next, stop))
        {
            try { operation(); }
            catch (Exception ex) { RecordFailure(failures, ex); }
            next += periodTicks;
            var now = Stopwatch.GetTimestamp();
            if (next < now) next = now;
        }
    }) { IsBackground = true, Name = "control-capacity-worker" };

    private static bool WaitUntil(long deadline, ManualResetEvent stop)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return !stop.WaitOne(0);
        var waitMs = Math.Max(1, (int)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency));
        return !stop.WaitOne(waitMs);
    }

    private static async Task<long> ReadWalBytes(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT wal_bytes FROM pg_stat_wal", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void RecordFailure(ConcurrentDictionary<string, int> failures, Exception error)
    {
        var kind = error switch
        {
            PostgresException postgres => postgres.SqlState,
            ControlConflictException conflict => "ControlConflict:" + conflict.Message,
            _ => error.GetType().Name
        };
        failures.AddOrUpdate(kind, 1, (_, count) => count + 1);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
