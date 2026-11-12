using System.Text;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Control;
using Chorus.Data;
using Chorus.PlatformGateway;
using Chorus.Protocol.Models;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using static Chorus.Rpc.RoomEvents;

namespace Chorus.Engine.Tests;

/// <summary>
/// Platform raw ingress stats (FIX-OBS-10): every disposition the webhook face answers
/// bumps exactly one counter family, named by the dictionary metrics (FIX-OBS-01), and the
/// snapshot the reporter carries names those same families. The read face sums the fresh
/// reporters' latest rows — a reporter that stopped reporting drops out of the sums, since
/// its cumulative values describe a process that no longer exists — and reads the raw-input
/// counter durably from the data plane's input.event by platform. A non-PostgreSQL control
/// database answers zeros and empty: no ingress traffic is a stat, not an error.
/// </summary>
public sealed class RedesignObs10IngressStatsTests
{
    private static string Body(string eventId) => $$"""
        {"sessionId":"live-a","eventId":"{{eventId}}","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":10}
        """;

    private static void AssertOnly(IngressCounters counters, string family, long count)
    {
        var snapshot = counters.Snapshot().ToDictionary(entry => entry.Family, entry => entry.Value);
        Assert.Equal(count, snapshot[family]);
        Assert.All(snapshot.Where(pair => pair.Key != family), pair => Assert.Equal(0, pair.Value));
    }

    [Fact]
    public async Task ADurablePushAcceptCountsAsAccepted()
    {
        var counters = new IngressCounters();
        var response = await PostAsync(new ScriptedIngest(), counters, Body("obs10-accept"),
            roomEvents: new ScriptedRoomEvents(Reply("DurableAccepted")),
            directory: ScriptedDirectory.OneTarget());

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        AssertOnly(counters, IngressStatsStore.AcceptedFamily, 1);
    }

    [Fact]
    public async Task ADuplicatePushAnswerCountsAsDuplicate()
    {
        // The lost-success posture: the first push committed nothing observable, the retry
        // of the same event id answers Duplicate — one webhook, one duplicate counted.
        var counters = new IngressCounters();
        var response = await PostAsync(new ScriptedIngest(), counters, Body("obs10-dup"),
            roomEvents: new ScriptedRoomEvents(Reply("OutcomeUnknown"), Reply("Duplicate")),
            directory: ScriptedDirectory.OneTarget());

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        AssertOnly(counters, IngressStatsStore.DuplicatesFamily, 1);
    }

    [Fact]
    public async Task AnUnreachableOwnerFallsToTheInboxAndCountsAccepted()
    {
        var counters = new IngressCounters();
        var response = await PostAsync(new ScriptedIngest(), counters, Body("obs10-inbox"),
            roomEvents: new ScriptedRoomEvents(Unreachable(), Unreachable(), Unreachable()),
            directory: ScriptedDirectory.OneTarget());

        // The durable attribution is as much an acceptance as the push leg's own.
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        AssertOnly(counters, IngressStatsStore.AcceptedFamily, 1);
    }

    [Fact]
    public async Task AnIngestDuplicateReceiptCountsAsDuplicate()
    {
        var counters = new IngressCounters();
        var response = await PostAsync(new ScriptedIngest(duplicate: true), counters, Body("obs10-inbox-dup"));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        AssertOnly(counters, IngressStatsStore.DuplicatesFamily, 1);
    }

    [Fact]
    public async Task AnUnverifiableCallbackCountsAsUnverified()
    {
        var counters = new IngressCounters();
        var response = await PostAsync(new ScriptedIngest(), counters, Body("obs10-sig"),
            verifier: new NeverVerify());

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        AssertOnly(counters, IngressStatsStore.UnverifiedFamily, 1);
    }

    [Fact]
    public async Task AnUnmappableBodyCountsAsUnmapped()
    {
        var counters = new IngressCounters();
        var response = await PostAsync(new ScriptedIngest(), counters, Body("obs10-map"),
            mappers: [new NeverMap()]);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        AssertOnly(counters, IngressStatsStore.UnmappedFamily, 1);
    }

    [Fact]
    public async Task AnOversizedBodyCountsAsTooLarge()
    {
        var counters = new IngressCounters();
        var oversized = new string('x', PlatformIngressEndpoint.MaxBodyBytes + 1);

        var response = await PostAsync(new ScriptedIngest(), counters, oversized);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Status);
        AssertOnly(counters, IngressStatsStore.TooLargeFamily, 1);
    }

    [Fact]
    public async Task ACommitThatNeverAnswersCountsAsTimedOut()
    {
        var counters = new IngressCounters();
        var response = await PostAsync(
            new ScriptedIngest(failure: new RpcException(new Status(StatusCode.Unavailable, "down"))),
            counters, Body("obs10-timeout"));

        // The one answer that never acknowledges a lost event: nothing was committed, the
        // platform's retry must be a fresh delivery, and the face counts the timeout.
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        AssertOnly(counters, IngressStatsStore.TimedOutFamily, 1);
    }

    [Fact]
    public void TheSnapshotNamesTheDictionaryFamiliesAndCarriesTheUnwiredFamily()
    {
        // The seven families are the dictionary's names, in its order — the read face's
        // constants pin the same strings, so reporter, store and API never drift apart.
        Assert.Equal(
        [
            OpsMetrics.IngressAcceptedTotal.Name,
            OpsMetrics.IngressDuplicatesTotal.Name,
            OpsMetrics.IngressUnverifiedTotal.Name,
            OpsMetrics.IngressUnmappedTotal.Name,
            OpsMetrics.IngressRateLimitedTotal.Name,
            OpsMetrics.IngressTooLargeTotal.Name,
            OpsMetrics.IngressTimedOutTotal.Name,
        ], new IngressCounters().Snapshot().Select(entry => entry.Family).ToArray());
        Assert.Equal(OpsMetrics.IngressRateLimitedTotal.Name, IngressStatsStore.RateLimitedFamily);
        Assert.Equal(OpsMetrics.IngressAcceptedTotal.Name, IngressStatsStore.AcceptedFamily);
        Assert.Equal(OpsMetrics.IngressDuplicatesTotal.Name, IngressStatsStore.DuplicatesFamily);
        Assert.Equal(OpsMetrics.IngressUnverifiedTotal.Name, IngressStatsStore.UnverifiedFamily);
        Assert.Equal(OpsMetrics.IngressUnmappedTotal.Name, IngressStatsStore.UnmappedFamily);
        Assert.Equal(OpsMetrics.IngressTooLargeTotal.Name, IngressStatsStore.TooLargeFamily);
        Assert.Equal(OpsMetrics.IngressTimedOutTotal.Name, IngressStatsStore.TimedOutFamily);
    }

    [Fact]
    public async Task FreshReportersSumIntoTheTotalsAndListTheirCounters()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var store = StatsStore(temp.ConnectionString);
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-1),
            ("node-1", "inst-1", IngressStatsStore.AcceptedFamily, 3),
            ("node-1", "inst-1", IngressStatsStore.DuplicatesFamily, 1),
            ("node-2", "inst-2", IngressStatsStore.AcceptedFamily, 4),
            // A family the totals do not know (a later OBS family rides the same table).
            ("node-2", "inst-2", "chorus_delivery_unknown_total", 9));

        var stats = store.Stats();

        Assert.Equal(7, stats.Accepted);
        Assert.Equal(1, stats.Duplicates);
        Assert.Equal(0, stats.RateLimited);
        Assert.Equal([("node-1", "inst-1"), ("node-2", "inst-2")],
            stats.Gateways.Select(g => (g.NodeId, g.ProcessInstanceId)).ToList());
        Assert.Equal(9, stats.Gateways[1].Counters["chorus_delivery_unknown_total"]);
    }

    [Fact]
    public async Task AReporterThatStoppedReportingDropsOutOfTheAnswer()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-1),
            ("node-1", "inst-1", IngressStatsStore.AcceptedFamily, 3));
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-60),
            ("node-gone", "inst-gone", IngressStatsStore.AcceptedFamily, 100));

        var stats = StatsStore(temp.ConnectionString).Stats();

        // A dead reporter's cumulative values describe a process that no longer exists —
        // the sums answer the living only.
        Assert.Equal(3, stats.Accepted);
        Assert.Equal([("node-1", "inst-1")],
            stats.Gateways.Select(g => (g.NodeId, g.ProcessInstanceId)).ToList());
    }

    [Fact]
    public async Task OnlyEachReportersLatestRowsAnswer()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-3),
            ("node-1", "inst-1", IngressStatsStore.AcceptedFamily, 5));
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-1),
            ("node-1", "inst-1", IngressStatsStore.AcceptedFamily, 9));

        Assert.Equal(9, StatsStore(temp.ConnectionString).Stats().Accepted);
    }

    [Fact]
    public async Task InputEventsBySourceCountsTheDurableRawInput()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var (id, platform, dedup) in new[]
            {
                ("e-1", "bilibili", "k-1"), ("e-2", "bilibili", "k-2"), ("e-3", "douyin", "k-3"),
            })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO input.event(event_id, platform, app_id, source_dedup_key,
                        payload_hash, disposition, payload)
                    VALUES (@id, @platform, 'app-1', @dedup, '\x00', 'Durable', '{}'::jsonb)
                    """, connection);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("platform", platform);
                insert.Parameters.AddWithValue("dedup", dedup);
                await insert.ExecuteNonQueryAsync();
            }
        }

        Assert.Equal(
            [new IngressSourceCount("bilibili", 2), new IngressSourceCount("douyin", 1)],
            StatsStore(temp.ConnectionString).Stats().InputEventsBySource);
    }

    [Fact]
    public void ASqliteControlDatabaseAnswersZerosAndEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "chorus_obs10_" + Guid.NewGuid().ToString("N"), "control.db");
        var stats = new IngressStatsStore(path, ControlDatabaseProvider.Sqlite).Stats();

        Assert.Equal(0, stats.Accepted);
        Assert.Equal(0, stats.TimedOut);
        Assert.Empty(stats.Gateways);
        Assert.Empty(stats.InputEventsBySource);
    }

    private static IngressStatsStore StatsStore(string connectionString) =>
        new(connectionString, ControlDatabaseProvider.PostgreSql);

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

    private static DeliverRoomEventReply Reply(string disposition) => new() { Disposition = disposition };

    private static RpcException Unreachable() =>
        new(new Status(StatusCode.Unavailable, "the owner endpoint refused the connection"));

    private sealed record FaceResponse(int Status);

    /// <summary>Drives one simulator webhook through the face with the scripted doubles:
    /// the backoffs are zero so the retry posture itself is what the outcomes script.</summary>
    private static async Task<FaceResponse> PostAsync(PlatformIngest.PlatformIngestClient ingest,
        IngressCounters counters, string body, ScriptedRoomEvents? roomEvents = null,
        ScriptedDirectory? directory = null, IPlatformEventVerifier? verifier = null,
        IPlatformEventMapper[]? mappers = null)
    {
        var endpoint = new PlatformIngressEndpoint(
            verifier ?? new AllowAllPlatformEventVerifier(),
            mappers ?? [new SimulatorPlatformEventMapper()], ingest,
            routing: roomEvents is null ? null : directory ?? ScriptedDirectory.OneTarget(),
            gameChannelFactory: roomEvents is null
                ? null
                : _ => Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1"),
            roomEventsFactory: roomEvents is null ? null : _ => roomEvents,
            pushRetryBackoffs: [TimeSpan.Zero, TimeSpan.Zero], counters: counters);
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IOptions<JsonOptions>>(
            Microsoft.Extensions.Options.Options.Create(new JsonOptions()));
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Headers[PlatformIngressEndpoint.AppIdHeader] = "app-1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await (await endpoint.HandleAsync("simulator", context)).ExecuteAsync(context);
        return new FaceResponse(context.Response.StatusCode);
    }

    private sealed class ScriptedRoomEvents(params object[] outcomes) : RoomEventsClient
    {
        private readonly Queue<object> _outcomes = new(outcomes);

        public override AsyncUnaryCall<DeliverRoomEventReply> DeliverEventAsync(
            DeliverRoomEventRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var outcome = _outcomes.Dequeue();
            if (outcome is RpcException error) throw error;
            var reply = (DeliverRoomEventReply)outcome;
            return new AsyncUnaryCall<DeliverRoomEventReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private sealed class ScriptedIngest(bool duplicate = false, RpcException? failure = null)
        : PlatformIngest.PlatformIngestClient
    {
        public override AsyncUnaryCall<IngestBatchReply> IngestBatchAsync(
            IngestBatchRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            if (failure is not null) throw failure;
            var reply = new IngestBatchReply();
            if (request.Events.Count > 0)
                reply.Receipts.Add(new EventReceipt
                {
                    SourceDedupKey = request.Events[0].SourceDedupKey,
                    Disposition = duplicate ? "Duplicate" : "Durable",
                    Duplicate = duplicate
                });
            return new AsyncUnaryCall<IngestBatchReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private sealed class ScriptedDirectory(
        GatewayRoutingMonitor.RoomRouteTarget target) : IRoomRoutingDirectory
    {
        public static ScriptedDirectory OneTarget() => new(
            new GatewayRoutingMonitor.RoomRouteTarget("room-obs10", "tenant-a", "game-a",
                "binding-a", "game-a", 1, "https://localhost:6001", 7));

        public bool TryResolve(string platform, string canonicalLiveKey,
            out GatewayRoutingMonitor.RoomRouteTarget routeTarget)
        {
            routeTarget = target;
            return true;
        }

        public Task<bool> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class NeverVerify : IPlatformEventVerifier
    {
        public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) =>
            false;
    }

    private sealed class NeverMap : IPlatformEventMapper
    {
        public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs) => null;
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs10_ingress_" + Guid.NewGuid().ToString("N");
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
}
