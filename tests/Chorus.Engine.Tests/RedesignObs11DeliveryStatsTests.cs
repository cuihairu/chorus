using Chorus.Control;
using Chorus.Data;
using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using System.Net;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Event delivery and effect chain stats (FIX-OBS-11): the session face's own counters
/// bump at the decisions the face makes — a seated hand or an inbox pull counts ingested,
/// a frame whose verdict landed counts its inputs applied — and the reporter names the
/// dictionary families. The read face sums the fresh reporters' latest rows into the
/// session totals, reads the delivery/effect pair from the data plane's own durable
/// delivery rows (a delivery row is the routing handing the event to its room's inbox;
/// the Consumed mark is the frame that applied the effect), reads the outbox dispatch
/// stages from infra.outbox by status, and derives the failed counter from the attempt
/// ledger (rows are never pruned, so every claim that was not the settling one failed).
/// The routing's terminal dispositions are not drops at the session face — the face never
/// saw them — so a table full of OutOfRound events answers the same session totals. A
/// non-PostgreSQL control database answers zeros and empty.
/// </summary>
public sealed class RedesignObs11DeliveryStatsTests
{
    // ---------------------------------------------------------------- session face

    [Fact]
    public async Task ASeatedHandCountsIngestedAndItsFrameCountsApplied()
    {
        var frames = new ScriptedFrameStore();
        await using var runtime = NewRuntime(frames);
        await runtime.PrepareRoomAsync(Prepare("room-obs11-hand"));

        var reply = await runtime.DeliverEventAsync(Deliver("room-obs11-hand", "obs11-hand-1"));

        Assert.Equal("DurableAccepted", reply.Disposition);
        Assert.Equal(1, Total(runtime.EventCounters, DeliveryStatsStore.IngestedFamily));
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount > 0)));
        Assert.True(await WaitForAsync(() => Task.FromResult(
            Total(runtime.EventCounters, DeliveryStatsStore.AppliedFamily) >= 1)));
        // The face never dropped anything and nothing merged: the unwired families answer
        // zero from the start, so a later merge or refusal lands on a stable API.
        Assert.Equal(0, Total(runtime.EventCounters, DeliveryStatsStore.DroppedFamily));
        Assert.Equal(0, Total(runtime.EventCounters, DeliveryStatsStore.MergedFamily));
    }

    [Fact]
    public async Task AnInboxPullCountsItsBatchIngestedAndApplied()
    {
        var frames = new ScriptedFrameStore(inboxOnce: 2);
        await using var runtime = NewRuntime(frames);
        await runtime.PrepareRoomAsync(Prepare("room-obs11-pull"));

        // The seats are empty, so the first tick pulls the scripted batch: both legs of
        // the chain answer the same two events — taken in, then applied at the commit.
        Assert.True(await WaitForAsync(() => Task.FromResult(
            Total(runtime.EventCounters, DeliveryStatsStore.IngestedFamily) >= 2)));
        Assert.True(await WaitForAsync(() => Task.FromResult(
            Total(runtime.EventCounters, DeliveryStatsStore.AppliedFamily) >= 2)));
    }

    [Fact]
    public void TheSnapshotNamesTheDictionaryFamiliesInTheDictionaryOrder()
    {
        var counters = new RoomEventCounters();
        counters.Dropped();
        Assert.Equal(
        [
            OpsMetrics.EventsIngestedTotal.Name,
            OpsMetrics.EventsAppliedTotal.Name,
            OpsMetrics.EventsMergedTotal.Name,
            OpsMetrics.EventsDroppedTotal.Name,
        ], counters.Snapshot().Select(entry => entry.Family).ToArray());
        Assert.Equal(1, counters.Snapshot()[3].Value);
        // The read face's constants pin the same strings, so reporter, store and API
        // never drift apart.
        Assert.Equal(OpsMetrics.EventsIngestedTotal.Name, DeliveryStatsStore.IngestedFamily);
        Assert.Equal(OpsMetrics.EventsAppliedTotal.Name, DeliveryStatsStore.AppliedFamily);
        Assert.Equal(OpsMetrics.EventsMergedTotal.Name, DeliveryStatsStore.MergedFamily);
        Assert.Equal(OpsMetrics.EventsDroppedTotal.Name, DeliveryStatsStore.DroppedFamily);
    }

    // ---------------------------------------------------------------- read face

    [Fact]
    public async Task FreshReportersSumIntoTheSessionTotals()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-1),
            ("node-1", "inst-1", DeliveryStatsStore.IngestedFamily, 5),
            ("node-1", "inst-1", DeliveryStatsStore.AppliedFamily, 4),
            ("node-2", "inst-2", DeliveryStatsStore.IngestedFamily, 3),
            // A family the totals do not know (OBS-12 rides the same table).
            ("node-2", "inst-2", "chorus_platform_api_unknown_total", 9));

        var stats = StatsStore(temp.ConnectionString).Stats();

        Assert.Equal(8, stats.Ingested);
        Assert.Equal(4, stats.Applied);
        Assert.Equal(0, stats.Merged);
        Assert.Equal(0, stats.Dropped);
        Assert.Equal([("node-1", "inst-1"), ("node-2", "inst-2")],
            stats.Reporters.Select(r => (r.NodeId, r.ProcessInstanceId)).ToList());
        Assert.Equal(9, stats.Reporters[1].Counters["chorus_platform_api_unknown_total"]);
    }

    [Fact]
    public async Task AReporterThatStoppedReportingDropsOutOfTheSums()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-1),
            ("node-1", "inst-1", DeliveryStatsStore.IngestedFamily, 5));
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-60),
            ("node-gone", "inst-gone", DeliveryStatsStore.IngestedFamily, 100));

        var stats = StatsStore(temp.ConnectionString).Stats();

        // A dead reporter's cumulative values describe a process that no longer exists —
        // the sums answer the living only.
        Assert.Equal(5, stats.Ingested);
        Assert.Equal([("node-1", "inst-1")],
            stats.Reporters.Select(r => (r.NodeId, r.ProcessInstanceId)).ToList());
    }

    [Fact]
    public async Task OnlyEachReportersLatestRowsAnswer()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-3),
            ("node-1", "inst-1", DeliveryStatsStore.AppliedFamily, 5));
        await SeedCountersAsync(temp.ConnectionString, now.AddSeconds(-1),
            ("node-1", "inst-1", DeliveryStatsStore.AppliedFamily, 9));

        Assert.Equal(9, StatsStore(temp.ConnectionString).Stats().Applied);
    }

    [Fact]
    public async Task DeliveredAndEffectReadTheDurableDeliveries()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand(
                "INSERT INTO input.cursor(consumer_scope) VALUES ('scope-obs11')",
                connection).ExecuteNonQueryAsync();
            // Two routed events hold delivery rows — one still pending, one consumed by a
            // frame — and two routing-terminal events hold none at all.
            foreach (var (id, disposition) in new[]
            {
                ("obs11-d-1", "Routed"), ("obs11-d-2", "Routed"),
                ("obs11-o-1", "OutOfRound"), ("obs11-o-2", "PausedRejected"),
            })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO input.event(event_id, platform, app_id, source_dedup_key,
                        payload_hash, disposition, payload)
                    VALUES (@id, 'simulator', 'app-1', @id, '\x00', @disposition, '{}'::jsonb)
                    """, connection);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("disposition", disposition);
                await insert.ExecuteNonQueryAsync();
            }
            foreach (var (id, state, sequence) in new[]
            {
                ("obs11-d-1", "Pending", 1L), ("obs11-d-2", "Consumed", 2L),
            })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO input.delivery(event_id, consumer_scope, route_version,
                        sequence, state)
                    VALUES (@id, 'scope-obs11', 7, @sequence, @state)
                    """, connection);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("sequence", sequence);
                insert.Parameters.AddWithValue("state", state);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var stats = StatsStore(temp.ConnectionString).Stats();

        Assert.Equal(2, stats.Delivered);
        Assert.Equal(1, stats.EffectsApplied);
        // The routing's terminal dispositions are not session-face drops — the face never
        // saw those events — so the session dropped count stays at what the reporters say.
        Assert.Equal(0, stats.Dropped);
    }

    [Fact]
    public async Task OutboxStagesCountByStatusAndFailedDerivesFromTheLedger()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var (effect, status, attempts) in new[]
            {
                ("obs11-done-1", "Done", 1), ("obs11-done-2", "Done", 3),
                ("obs11-pending-1", "Pending", 2),
                ("obs11-dispatching-1", "Dispatching", 1),
                ("obs11-stopped-1", "Stopped", 5),
            })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO infra.outbox(effect_id, kind, payload, status, attempt_count)
                    VALUES (@effect, 'round.started', '{}'::jsonb, @status, @attempts)
                    """, connection);
                insert.Parameters.AddWithValue("effect", effect);
                insert.Parameters.AddWithValue("status", status);
                insert.Parameters.AddWithValue("attempts", attempts);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var stats = StatsStore(temp.ConnectionString).Stats();

        Assert.Equal(1, stats.EffectsPending);
        Assert.Equal(1, stats.EffectsDispatching);
        Assert.Equal(2, stats.EffectsDone);
        // Twelve claims were ever taken, two of them settled their rows Done — the other
        // ten were failures (the in-flight claim reads as one until it settles, then its
        // answer moves the ledger either way).
        Assert.Equal(10, stats.EffectsFailed);
    }

    [Fact]
    public void ASqliteControlDatabaseAnswersZerosAndEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "chorus_obs11_" + Guid.NewGuid().ToString("N"), "control.db");
        var stats = new DeliveryStatsStore(path, ControlDatabaseProvider.Sqlite).Stats();

        Assert.Equal(0, stats.Ingested);
        Assert.Equal(0, stats.EffectsApplied);
        Assert.Equal(0, stats.EffectsFailed);
        Assert.Empty(stats.Reporters);
    }

    // ---------------------------------------------------------------- harness

    private static long Total(RoomEventCounters counters, string family) =>
        counters.Snapshot().First(entry => entry.Family == family).Value;

    private static DeliveryStatsStore StatsStore(string connectionString) =>
        new(connectionString, ControlDatabaseProvider.PostgreSql);

    private static RoomRuntime NewRuntime(ScriptedFrameStore frames)
    {
        // The durable accept rides the runtime's own RoundInput client, built on the data
        // channel; a scripted HTTP handler answers that one call so the face's decision
        // (seat or refuse) is what the fact observes, never the wire.
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1",
            new Grpc.Net.Client.GrpcChannelOptions { HttpHandler = new ScriptedDataHandler() });
        return new RoomRuntime(new ScriptedRoundStore(), frames, new ScriptedSettlement(),
            channel, inputTranslator: new AudienceEventModuleInputTranslator());
    }

    private static DeliverRoomEventRequest Deliver(string roomId, string eventId) => new()
    {
        TenantId = "tenant-a", GameId = "game-a", RoomId = roomId,
        OwnerInstanceId = "game-1", Epoch = 1, EventId = eventId,
        Platform = "simulator", AppId = "app-1",
        PayloadJson = """{"action":"boost","level":3}"""
    };

    private static PrepareRoomRequest Prepare(string roomId) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = $"op-{roomId}", DurationTicks = 200,
        RandomSeed = 777, TickMs = 25,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-r", BindingId = "binding-host-r", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-r", BindingId = "binding-guest-r", Faction = GameStartConfig.BlueFaction }
        }
    };

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(20);
        }
        return false;
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
        private readonly string _name = "chorus_obs11_delivery_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Preparing" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> SuspendRoundAsync(SuspendRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Suspended" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    /// <summary>The data channel's HTTP face: every RoundInput accept is answered with a
    /// fresh accept (framed reply, grpc-status trailer), so the hand decision is scripted
    /// without a server process.</summary>
    private sealed class ScriptedDataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.PathAndQuery.EndsWith("RoundInput/AcceptRoomEvent",
                    StringComparison.Ordinal))
                throw new InvalidOperationException($"The scripted data face refuses {request.RequestUri.PathAndQuery}.");
            var reply = new AcceptRoomEventReply
                { EventId = "obs11-accepted", Duplicate = false, Sequence = 1, RouteVersion = 7 };
            var message = reply.ToByteArray();
            var frame = new byte[5 + message.Length];
            frame[0] = 0;
            frame[1] = (byte)(message.Length >> 24);
            frame[2] = (byte)(message.Length >> 16);
            frame[3] = (byte)(message.Length >> 8);
            frame[4] = (byte)message.Length;
            message.CopyTo(frame, 5);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(frame),
                Version = new Version(2, 0)
            };
            response.Content.Headers.Add("content-type", "application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }
    }

    /// <summary>The frame store: plain commit successes; the inbox hands its scripted batch
    /// once (the recovery catch-up), then stays empty.</summary>
    private sealed class ScriptedFrameStore(int inboxOnce = 0) : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _commits;
        private int _inboxCalls;

        public int CommitCount { get { lock (_gate) return _commits; } }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var reply = new ReadInboxReply();
            lock (_gate)
                if (_inboxCalls++ == 0 && inboxOnce > 0)
                    for (var index = 0; index < inboxOnce; index++)
                        reply.Items.Add(new InboxItemProto
                        {
                            EventId = $"obs11-pull-{index}", Sequence = index + 1,
                            RouteVersion = 7, BindingId = "binding-host-r",
                            PayloadJson = """{"action":"boost","level":3}""",
                            Platform = "simulator", SourceDedupKey = $"obs11-pull-{index}"
                        });
            return new(Task.FromResult(reply), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits++;
            return new(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
