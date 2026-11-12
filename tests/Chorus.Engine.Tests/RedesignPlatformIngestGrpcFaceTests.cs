using System.Text.Json;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The durable platform intake face driven as plain method calls. The gateway presents a
/// verified webhook batch and every reply reflects a committed batch — events, dedup keys and
/// the operation receipt land in one transaction before the ack leaves the host. The face adds
/// the single-role fence: only a platform gateway presents webhooks, and exact retries replay
/// as duplicates while a changed batch under the same operation id conflicts.
/// </summary>
public sealed class RedesignPlatformIngestGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForThePlatformGateway()
    {
        await using var rig = await IngestRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("platform-gateway", "gw-1"));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await IngestRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await IngestRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesAnyRoleButThePlatformGateway()
    {
        await using var rig = await IngestRig.CreateAsync();
        // Webhooks arrive only from the gateway that verified them; the control plane polls
        // readiness elsewhere and never presents a batch.
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("control-server", "control-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("data-server", "data-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task IngestBatchAnswersForACommittedBatch()
    {
        await using var rig = await IngestRig.CreateAsync();
        var reply = await rig.Service.IngestBatch(Batch(), rig.Context("platform-gateway", "gw-1"));
        Assert.False(reply.Duplicate);
        Assert.Single(reply.Receipts);
        Assert.Equal("evt-1", reply.Receipts[0].SourceDedupKey);
        Assert.Equal(RedesignInputEventStore.PendingAttribution, reply.Receipts[0].Disposition);
    }

    [Fact]
    public async Task IngestBatchAnswersDuplicateOnAnExactRetry()
    {
        await using var rig = await IngestRig.CreateAsync();
        var gateway = rig.Context("platform-gateway", "gw-1");
        await rig.Service.IngestBatch(Batch(), gateway);
        var replay = await rig.Service.IngestBatch(Batch(), gateway);
        Assert.True(replay.Duplicate);
        Assert.Single(replay.Receipts);
        Assert.True(replay.Receipts[0].Duplicate);
    }

    [Fact]
    public async Task IngestBatchRefusesAChangedBatchUnderTheSameOperationId()
    {
        await using var rig = await IngestRig.CreateAsync();
        var gateway = rig.Context("platform-gateway", "gw-1");
        await rig.Service.IngestBatch(Batch(), gateway);
        // The operation id is already spent: a retry cannot change what it acked.
        await rig.Rejects(() => rig.Service.IngestBatch(Batch(dedupKey: "evt-other"), gateway),
            StatusCode.InvalidArgument, "The operation was retried with a different request.");
    }

    [Fact]
    public async Task IngestBatchRefusesABatchWithNoEvents()
    {
        await using var rig = await IngestRig.CreateAsync();
        // Argument-exception messages carry the parameter name the runtime appends.
        await rig.Rejects(() => rig.Service.IngestBatch(Batch(events: []),
                rig.Context("platform-gateway", "gw-1")),
            StatusCode.InvalidArgument, "The batch must contain at least one event. (Parameter 'batch')");
    }

    [Fact]
    public async Task IngestBatchRefusesAnUnknownDurability()
    {
        await using var rig = await IngestRig.CreateAsync();
        await rig.Rejects(() => rig.Service.IngestBatch(
                Batch(durability: "Forever"), rig.Context("platform-gateway", "gw-1")),
            StatusCode.InvalidArgument,
            $"Durability must be {RedesignInputEventStore.Durable} or {RedesignInputEventStore.BestEffort}. "
            + "(Parameter 'batch')");
    }

    [Fact]
    public async Task IngestBatchRefusesAnUnparseablePayload()
    {
        await using var rig = await IngestRig.CreateAsync();
        // A contract gap, recorded rather than smoothed over: the face maps argument refusals
        // to InvalidArgument but not JSON parsing failures, so an unparseable payload leaves
        // the face as the parser's own exception. The observable fact is what matters — the
        // batch was refused, so nothing of it reached the durable inbox.
        await Assert.ThrowsAnyAsync<JsonException>(() => rig.Service.IngestBatch(
            Batch(payloadJson: "not json"), rig.Context("platform-gateway", "gw-1")));
        var reply = await rig.Service.GetPendingInbox(new PendingInboxRequest { Platform = "simulator" },
            rig.Context("platform-gateway", "gw-1"));
        Assert.Empty(reply.Events);
    }

    [Fact]
    public async Task GetPendingInboxListsTheUnattributedDurableEvents()
    {
        await using var rig = await IngestRig.CreateAsync();
        var gateway = rig.Context("platform-gateway", "gw-1");
        await rig.Service.IngestBatch(Batch(), gateway);
        var reply = await rig.Service.GetPendingInbox(new PendingInboxRequest { Platform = "simulator" }, gateway);
        Assert.Single(reply.Events);
        Assert.Equal("evt-1", reply.Events[0].SourceDedupKey);
        Assert.Equal("room-1", reply.Events[0].CanonicalRoomKey);
    }

    [Fact]
    public async Task GetPendingInboxAnswersEmptyWithoutAnInbox()
    {
        await using var rig = await IngestRig.CreateAsync();
        var reply = await rig.Service.GetPendingInbox(new PendingInboxRequest { Platform = "simulator" },
            rig.Context("platform-gateway", "gw-1"));
        Assert.Empty(reply.Events);
    }

    [Fact]
    public async Task ReconcileInboxAttributesNothingWithoutABinding()
    {
        await using var rig = await IngestRig.CreateAsync();
        // No binding is active on the room key, so reconciliation has nothing to attach to —
        // the pending event stays in the inbox for a later pass.
        await rig.Service.IngestBatch(Batch(), rig.Context("platform-gateway", "gw-1"));
        var reply = await rig.Service.ReconcileInbox(new ReconcileInboxRequest
        {
            Platform = "simulator", CanonicalRoomKey = "room-1"
        }, rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(0, reply.Attributed);
    }

    private static IngestBatchRequest Batch(
        string dedupKey = "evt-1", string durability = RedesignInputEventStore.Durable,
        string payloadJson = """{"type":"gift","amount":1}""",
        IReadOnlyList<IngestEvent>? events = null)
    {
        var request = new IngestBatchRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "op-ingest-1"
        };
        request.Events.AddRange(events ?? [new IngestEvent
        {
            SourceDedupKey = dedupKey, Durability = durability,
            CanonicalRoomKey = "room-1", PayloadJson = payloadJson
        }]);
        return request;
    }

    private sealed record IngestRig(PlatformIngestGrpcService Service,
        TestProcessCertificates Certificates, TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<IngestRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var store = new RedesignInputEventStore(database.ConnectionString);
            return new IngestRig(new PlatformIngestGrpcService(store, certificates.Trusted),
                certificates, database);
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

        public async Task Rejects(Func<Task> call, StatusCode code, string? detail)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            if (detail is not null) Assert.Equal(detail, error.Status.Detail);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_ingest_face_" + Guid.NewGuid().ToString("N");
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
            // DROP ... WITH (FORCE) collects the database's backends under one lock and
            // checks each pid's privileges after releasing it: a backend that exits in
            // between can have its OS pid reused by another role's connection under a
            // co-tenant fork storm, and the privilege check then lands on that stranger
            // and fails with 42501. The exiting backend is really gone within
            // milliseconds, so a short bounded retry settles it.
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var drop = new NpgsqlCommand(
                        $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
                    await drop.ExecuteNonQueryAsync();
                    return;
                }
                catch (PostgresException error) when (error.SqlState == "42501" && attempt < 5)
                {
                    await Task.Delay(100 * attempt);
                }
            }
        }
    }
}
