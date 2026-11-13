using Chorus.Control;
using Chorus.Data;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using Xunit;
using static Chorus.Rpc.PlatformTask;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-OBS-12's outbound face in pins: the executor counts every platform API exchange —
/// calls, transport failures and cumulative latency buckets — under a vocabulary bounded
/// by construction ({platform}.{execute|status}); a platform answer of "not executed" is
/// a successful interaction, a thrown exchange is not. The control plane reads the fresh
/// reporters' composite families back apart per endpoint and sums the fresh gateways,
/// while a stopped gateway drops out entirely and a non-PostgreSQL control database
/// answers empty.
/// </summary>
public sealed class RedesignObs12PlatformApiStatsTests
{
    // ---- the counters' own arithmetic ----

    [Fact]
    public void ACallCountsItsLatencyUnderTheFirstBoundItMeets()
    {
        var counters = new PlatformApiCounters();
        counters.Observed("simulator.execute", failed: false, latencyMs: 37);
        counters.Observed("simulator.execute", failed: false, latencyMs: 6);
        var snapshot = Dictionary(counters.Snapshot());
        Assert.Equal(2, snapshot[Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.execute")]);
        Assert.Equal(0, snapshot[Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.execute")]);
        Assert.Equal(1, snapshot[Bucket("simulator.execute", 10)]);
        Assert.Equal(1, snapshot[Bucket("simulator.execute", 25)]);
        Assert.Equal(2, snapshot[Bucket("simulator.execute", 50)]);
        Assert.Equal(2, snapshot[Bucket("simulator.execute", 5000)]);
        Assert.Equal(2, snapshot[InfBucket("simulator.execute")]);
        // The latency families ride in bound order, +Inf last — the reader's contract.
        var latencyFamilies = counters.Snapshot()
            .Select(entry => entry.Family)
            .Where(family => family.StartsWith(Rpc.OpsMetrics.PlatformApiLatencyMs.Name, StringComparison.Ordinal))
            .ToList();
        Assert.Equal([.. Rpc.OpsMetrics.PlatformApiLatencyBoundsMs.Select(b => $"le={b}"), "le=+Inf"],
            latencyFamilies.Select(family => family[(family.LastIndexOf(":le=", StringComparison.Ordinal) + 1)..]).ToList());
    }

    [Fact]
    public void AFailedCallCountsAsACallAndALatencyAboveEveryBoundCountsOnlyAtInfinity()
    {
        var counters = new PlatformApiCounters();
        counters.Observed("simulator.execute", failed: true, latencyMs: 6400);
        var snapshot = Dictionary(counters.Snapshot());
        Assert.Equal(1, snapshot[Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.execute")]);
        Assert.Equal(1, snapshot[Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.execute")]);
        Assert.Equal(0, snapshot[Bucket("simulator.execute", 5000)]);
        Assert.Equal(1, snapshot[InfBucket("simulator.execute")]);
    }

    [Fact]
    public void TwoEndpointsStaySeparateUnderTheirDictionaryNames()
    {
        var counters = new PlatformApiCounters();
        counters.Observed("simulator.execute", failed: false, latencyMs: 12);
        counters.Observed("simulator.status", failed: true, latencyMs: 900);
        var snapshot = Dictionary(counters.Snapshot());
        Assert.Equal(1, snapshot[Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.execute")]);
        Assert.Equal(0, snapshot[Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.execute")]);
        Assert.Equal(1, snapshot[Bucket("simulator.execute", 25)]);
        Assert.Equal(1, snapshot[Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.status")]);
        Assert.Equal(1, snapshot[Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.status")]);
        Assert.Equal(1, snapshot[Bucket("simulator.status", 1000)]);
        Assert.Equal(1, snapshot[InfBucket("simulator.status")]);
        // Endpoints answer in ordinal order, the whole endpoint's block together.
        Assert.Equal([Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.execute"),
            Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.execute")],
            counters.Snapshot().Select(entry => entry.Family).Take(2).ToList());
    }

    // ---- the executor's own exchanges, counted ----

    [Fact]
    public async Task AReconciledCallBumpsTheExecuteEndpointAndAFaultingQueryBumpsTheStatusFailures()
    {
        var tasks = new ScriptedTasks(
            new TaskStateReply
            {
                TaskId = "task-exec", BindingId = "binding-1", OperationId = "op-exec",
                Status = "Pending", DesiredJson = """{"kind":"screen-link"}""",
                Platform = "simulator", AppId = "app-1"
            },
            new TaskStateReply
            {
                TaskId = "task-unknown", BindingId = "binding-1", OperationId = "op-unknown",
                Status = "OutcomeUnknown", DesiredJson = """{"kind":"end-round"}""",
                AttemptDeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(),
                Platform = "simulator", AppId = "app-1"
            });
        var adapter = new RecordingAdapter { QueryFaults = true, IdempotentReissue = true };
        var counters = new PlatformApiCounters();
        using var cancellation = new CancellationTokenSource();
        using var reconciler = new TaskReconciler(tasks, adapter, "gateway-obs12",
            intervalMs: 20, leaseSeconds: 30, deadlineSeconds: 30, callTimeoutSeconds: 1, counters);
        await reconciler.StartAsync(cancellation.Token);

        Assert.True(await WaitAsync(() => adapter.Calls.Contains("op-exec"), 5_000),
            "The pending task never ran.");
        Assert.True(await WaitAsync(() => adapter.Queries.Contains("op-unknown"), 5_000),
            "The unknown task was never queried.");
        await Task.Delay(200);

        var snapshot = Dictionary(counters.Snapshot());
        Assert.Equal(1, snapshot[Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.execute")]);
        Assert.Equal(0, snapshot[Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.execute")]);
        Assert.True(snapshot[Family(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, "simulator.status")] >= 1,
            "The faulting queries never counted as calls.");
        Assert.True(snapshot[Family(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, "simulator.status")] >= 1,
            "The faulting queries never counted as failures.");
        await cancellation.CancelAsync();
        await reconciler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ACounterWithNoObservationYetSnapsToEmpty()
    {
        // The reporter's contract: an empty snapshot is not a report — it stays silent
        // until this face has observed an exchange.
        Assert.Empty(new PlatformApiCounters().Snapshot());
    }

    // ---- the read side, against the ops table ----

    [Fact]
    public async Task FreshGatewaySamplesParseAndSumPerEndpoint()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        await SeedCountersAsync(temp.ConnectionString, DateTime.UtcNow,
            ("n1", "gw-a", Calls("simulator.execute"), 5),
            ("n1", "gw-a", Failures("simulator.execute"), 1),
            ("n1", "gw-a", LatencyBucket("simulator.execute", 50), 3),
            ("n1", "gw-a", LatencyBucket("simulator.execute", "+Inf"), 5),
            ("n1", "gw-a", Calls("simulator.status"), 2),
            ("n1", "gw-a", LatencyBucket("simulator.status", 10), 2),
            ("n1", "gw-a", LatencyBucket("simulator.status", "+Inf"), 2),
            // Families other stores own ride the same table; they never enter this answer.
            ("n1", "gw-a", "chorus_events_ingested_total", 100),
            ("n1", "gw-a", "chorus_ingress_accepted_total", 7),
            ("n1", "gw-b", Calls("simulator.execute"), 2),
            ("n1", "gw-b", LatencyBucket("simulator.execute", 50), 1),
            ("n1", "gw-b", LatencyBucket("simulator.execute", "+Inf"), 2));

        var stats = StatsStore(temp.ConnectionString).Stats();
        var endpoints = EndpointMap(stats.Endpoints);
        Assert.Equal(["simulator.execute", "simulator.status"], endpoints.Keys.ToList());

        var execute = endpoints["simulator.execute"];
        Assert.Equal(7, execute.Calls);
        Assert.Equal(1, execute.Failures);
        Assert.Equal([.. PlatformApiStatsStore.LatencyBoundsMs.Select(b => b.ToString()), PlatformApiStatsStore.InfBound],
            execute.Latency.Select(bucket => bucket.Le).ToList());
        Assert.Equal(0, execute.Latency[0].Count);
        Assert.Equal(4, execute.Latency.Single(bucket => bucket.Le == "50").Count);
        Assert.Equal(7, execute.Latency[^1].Count);

        var status = endpoints["simulator.status"];
        Assert.Equal(2, status.Calls);
        Assert.Equal(0, status.Failures);
        Assert.Equal(2, status.Latency.Single(bucket => bucket.Le == "10").Count);
        Assert.Equal(2, status.Latency[^1].Count);

        // Each gateway's own listing is parsed the same way, foreign families absent.
        Assert.Equal(2, stats.Gateways.Count);
        var first = EndpointMap(stats.Gateways[0].Endpoints);
        Assert.Equal(5, first["simulator.execute"].Calls);
        Assert.Equal(2, first["simulator.status"].Calls);
        Assert.DoesNotContain("chorus_events_ingested_total", first.Keys);
        var second = EndpointMap(stats.Gateways[1].Endpoints);
        Assert.Equal(2, second["simulator.execute"].Calls);
        Assert.DoesNotContain("simulator.status", second.Keys);
    }

    [Fact]
    public async Task OnlyEachGatewaysLatestRowsAnswer()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-2),
            ("n1", "gw-a", Calls("simulator.execute"), 1));
        await SeedCountersAsync(temp.ConnectionString, now,
            ("n1", "gw-a", Calls("simulator.execute"), 9));

        var stats = StatsStore(temp.ConnectionString).Stats();
        Assert.Single(stats.Endpoints);
        Assert.Equal(9, stats.Endpoints[0].Calls);
    }

    [Fact]
    public async Task AGatewayThatStoppedReportingDropsOutOfTheSums()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        await SeedCountersAsync(temp.ConnectionString, DateTime.UtcNow.AddSeconds(-60),
            ("n1", "gw-old", Calls("simulator.execute"), 40),
            ("n1", "gw-old", LatencyBucket("simulator.execute", "+Inf"), 40));
        await SeedCountersAsync(temp.ConnectionString, DateTime.UtcNow,
            ("n1", "gw-new", Calls("simulator.execute"), 3),
            ("n1", "gw-new", LatencyBucket("simulator.execute", "+Inf"), 3));

        var stats = StatsStore(temp.ConnectionString).Stats();
        Assert.Equal(3, stats.Endpoints[0].Calls);
        Assert.Equal(["n1/gw-new"], stats.Gateways.Select(g => $"{g.NodeId}/{g.ProcessInstanceId}").ToList());
    }

    [Fact]
    public void ASqliteControlDatabaseAnswersEmpty()
    {
        var stats = new PlatformApiStatsStore("data/obs12-sqlite.db", ControlDatabaseProvider.Sqlite).Stats();
        Assert.Empty(stats.Endpoints);
        Assert.Empty(stats.Gateways);
    }

    [Fact]
    public void TheStoreNamesAndBoundsPinTheDictionaryFamilies()
    {
        // This project reads the composite families without referencing Chorus.Rpc; the
        // pin is what keeps the two listings the same words.
        Assert.Equal(Rpc.OpsMetrics.PlatformApiCallsTotal.Name, PlatformApiStatsStore.CallsFamily);
        Assert.Equal(Rpc.OpsMetrics.PlatformApiFailuresTotal.Name, PlatformApiStatsStore.FailuresFamily);
        Assert.Equal(Rpc.OpsMetrics.PlatformApiLatencyMs.Name, PlatformApiStatsStore.LatencyFamily);
        Assert.Equal(Rpc.OpsMetrics.PlatformApiLatencyBoundsMs, PlatformApiStatsStore.LatencyBoundsMs);
    }

    private static string Calls(string endpoint) => $"{PlatformApiStatsStore.CallsFamily}:{endpoint}";
    private static string Failures(string endpoint) => $"{PlatformApiStatsStore.FailuresFamily}:{endpoint}";
    private static string LatencyBucket(string endpoint, object bound) =>
        $"{PlatformApiStatsStore.LatencyFamily}:{endpoint}:le={bound}";
    private static string Family(string metric, string endpoint) => $"{metric}:{endpoint}";
    private static string Bucket(string endpoint, long bound) =>
        $"{Rpc.OpsMetrics.PlatformApiLatencyMs.Name}:{endpoint}:le={bound}";
    private static string InfBucket(string endpoint) =>
        $"{Rpc.OpsMetrics.PlatformApiLatencyMs.Name}:{endpoint}:le=+Inf";

    private static Dictionary<string, long> Dictionary(IReadOnlyList<(string Family, long Value)> snapshot) =>
        snapshot.ToDictionary(entry => entry.Family, entry => entry.Value, StringComparer.Ordinal);

    private static Dictionary<string, PlatformApiEndpointStats> EndpointMap(
        IReadOnlyList<PlatformApiEndpointStats> endpoints) =>
        endpoints.ToDictionary(endpoint => endpoint.Endpoint, StringComparer.Ordinal);

    private static PlatformApiStatsStore StatsStore(string connectionString) =>
        new(connectionString, ControlDatabaseProvider.PostgreSql);

    private static async Task<bool> WaitAsync(Func<bool> probe, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (probe()) return true;
            await Task.Delay(20);
        }
        return probe();
    }

    private static async Task SeedCountersAsync(string connectionString, DateTime reportedAt,
        params (string Node, string Instance, string Family, long Value)[] rows)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var row in rows)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO ops.app_counter_sample(node_id, process_instance_id, family,
                    value, reported_at)
                VALUES (@node, @instance, @family, @value, @reported)
                """, connection);
            insert.Parameters.AddWithValue("node", row.Node);
            insert.Parameters.AddWithValue("instance", row.Instance);
            insert.Parameters.AddWithValue("family", row.Family);
            insert.Parameters.AddWithValue("value", row.Value);
            insert.Parameters.AddWithValue("reported", reportedAt);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs12_platform_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }

    /// <summary>The external platform as a recording: every call executes and is named by
    /// its operation id, with a query leg that cannot answer when scripted.</summary>
    private sealed class RecordingAdapter : IPlatformTaskAdapter
    {
        private readonly object _gate = new();
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public bool QueryFaults;
        public bool IdempotentReissue { get; set; } = true;

        public string Platform => "simulator";
        public bool ProductionReady => false;

        public Task<PlatformTaskOutcome> CallAsync(PlatformTaskCall call,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) Calls.Add(call.OperationId);
            return Task.FromResult(new PlatformTaskOutcome(Executed: true,
                """{"executed":true,"applied":true}"""));
        }

        public Task<PlatformTaskOutcome> QueryAsync(string operationId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) Queries.Add(operationId);
            if (QueryFaults)
                throw new HttpRequestException("The platform cannot answer for the operation.");
            return Task.FromResult(new PlatformTaskOutcome(Executed: false, null));
        }
    }

    /// <summary>The task face as a script: the listed tasks answer every listing until
    /// their branch completes them, and every protocol step is recorded so a step that
    /// should never happen is visible by name.</summary>
    private sealed class ScriptedTasks(params TaskStateReply[] listed) : PlatformTaskClient
    {
        public readonly object Gate = new();
        public readonly List<string> Leased = [];
        public readonly List<string> Attempted = [];
        public readonly List<string> Completed = [];
        public readonly List<string> Resolved = [];
        public bool BarrierAuthorized;

        public override AsyncUnaryCall<TaskListReply> ListReadyTasksAsync(TaskListRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            TaskListReply reply;
            lock (Gate)
            {
                reply = new TaskListReply();
                foreach (var task in listed)
                    if (!Completed.Contains(task.TaskId) && !Resolved.Contains(task.TaskId))
                        reply.Tasks.Add(task);
            }
            return Reply(reply);
        }

        public override AsyncUnaryCall<TaskBarrierReply> GetBarrierAuthorizationAsync(
            TaskBarrierRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) return Reply(new TaskBarrierReply
            {
                Authorized = BarrierAuthorized,
                Phase = BarrierAuthorized ? "PlatformTaskAuthorized" : "StopDeliveryIssued"
            });
        }

        public override AsyncUnaryCall<TaskStateReply> AcquireLeaseAsync(TaskLeaseRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Leased.Add(request.TaskId);
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "Pending" });
        }

        public override AsyncUnaryCall<TaskStateReply> BeginAttemptAsync(TaskAttemptRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Attempted.Add(request.TaskId);
            return Reply(new TaskStateReply
            {
                TaskId = request.TaskId, Status = "Executing",
                AttemptDeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds()
            });
        }

        public override AsyncUnaryCall<TaskStateReply> CompleteAttemptAsync(TaskCompleteRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Completed.Add(request.TaskId);
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "Completed" });
        }

        public override AsyncUnaryCall<TaskStateReply> ResolveOutcomeAsync(TaskResolveRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Resolved.Add(request.TaskId);
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "Completed" });
        }

        private static AsyncUnaryCall<T> Reply<T>(T value) => new(Task.FromResult(value),
            Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
            () => new Metadata(), () => { });
    }
}
