using System.Text.Json;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Protocol;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The retention face driven as plain method calls: the control plane releases a settled
/// round's frame prefix below its verified anchor, and every guard lives in the store — a
/// refusal reaches the caller as data, never as a quiet no. The face adds the tenant fence:
/// a cluster caller names the tenant it acts for, a tenant caller cannot act for another.
/// </summary>
public sealed class RedesignRetentionGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForTheControlPlane()
    {
        await using var rig = await RetentionRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("control-server", "control-1"));
        Assert.True(reply.Ready);
        Assert.Equal("1", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await RetentionRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await RetentionRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheControlPlane()
    {
        await using var rig = await RetentionRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("game-server", "gs-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task PruneRoundAnswersDuplicateForTheSameCanonicalRequest()
    {
        await using var rig = await RetentionRig.CreateAsync();
        // The receipt was written for this very canonical request, so the replay is the
        // same action: the duplicate answer stands and the retention guards never run.
        await rig.SeedReceiptAsync(overrideWindow: false);
        var reply = await rig.Service.PruneRound(Command(overrideWindow: false),
            rig.Context("control-server", "control-1"));
        Assert.True(reply.Duplicate);
        Assert.Equal("room-1", reply.RoomId);
        Assert.Equal(0, reply.KeepTick);
    }

    [Fact]
    public async Task PruneRoundRefusesARequestRetriedWithDifferentArguments()
    {
        await using var rig = await RetentionRig.CreateAsync();
        // The receipt was written under a different canonical request: the operation id is
        // already spent, and a retry cannot change its facts.
        await rig.SeedReceiptAsync(overrideWindow: false);
        await rig.Rejects(() => rig.Service.PruneRound(Command(overrideWindow: true),
                rig.Context("control-server", "control-1")),
            StatusCode.InvalidArgument, "The operation was retried with a different request.");
    }

    [Fact]
    public async Task PruneRoundReportsTheRoomsRefusalAsData()
    {
        await using var rig = await RetentionRig.CreateAsync();
        // A room the store has never seen: the guard's refusal is data, and the face maps it
        // to a precondition failure rather than swallowing it.
        await rig.Rejects(() => rig.Service.PruneRound(Command(operationId: "op-room-none"),
                rig.Context("control-server", "control-1")),
            StatusCode.FailedPrecondition, "The room does not exist.");
    }

    [Fact]
    public async Task PruneRoundRefusesARequestMissingItsFields()
    {
        await using var rig = await RetentionRig.CreateAsync();
        var control = rig.Context("control-server", "control-1");
        // The tenant field is the caller's own: a cluster caller that names none is refused by
        // the tenant gate, not by the store (see the dedicated fact), so it is not tested here.
        // Argument-exception messages carry the parameter name the runtime appends.
        await rig.Rejects(() => rig.Service.PruneRound(Command(gameId: ""), control),
            StatusCode.InvalidArgument, "'gameId' must not be empty. (Parameter 'gameId')");
        await rig.Rejects(() => rig.Service.PruneRound(Command(roomId: ""), control),
            StatusCode.InvalidArgument, "'roomId' must not be empty. (Parameter 'roomId')");
        await rig.Rejects(() => rig.Service.PruneRound(Command(operationId: ""), control),
            StatusCode.InvalidArgument, "'operationId' must not be empty. (Parameter 'operationId')");
    }

    [Fact]
    public async Task PruneRoundRefusesAClusterCallerThatNamesNoTenant()
    {
        await using var rig = await RetentionRig.CreateAsync();
        await rig.Rejects(() => rig.Service.PruneRound(Command(tenantId: ""),
                rig.Context("control-server", "control-1")),
            StatusCode.InvalidArgument, "A cluster caller must name the tenant it acts for.");
    }

    [Fact]
    public async Task PruneRoundRefusesACallerActingForAnotherTenant()
    {
        await using var rig = await RetentionRig.CreateAsync();
        var tenant = rig.Context("tenant-a", "control-server", "control-2");
        // A tenant-scoped control server names a tenant it does not hold: the gate closes
        // before the store is reached, so no cross-tenant reach-through exists.
        await rig.Rejects(() => rig.Service.PruneRound(Command(tenantId: "tenant-b"), tenant),
            StatusCode.PermissionDenied, "A tenant caller cannot act for another tenant.");
    }

    private static PruneRoundRequest Command(
        string tenantId = "tenant-a", string gameId = "game-a", string roomId = "room-1",
        string operationId = "op-prune-1", bool overrideWindow = false) => new()
        {
            TenantId = tenantId, GameId = gameId, RoomId = roomId,
            OperationId = operationId, OverrideRetentionWindow = overrideWindow
        };

    private sealed record RetentionRig(RetentionGrpcService Service,
        TestProcessCertificates Certificates, TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<RetentionRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var store = new RedesignRetentionStore(database.ConnectionString);
            return new RetentionRig(new RetentionGrpcService(store, certificates.Trusted),
                certificates, database);
        }

        /// <summary>Records a receipt the way the store does, for a canonical request the
        /// caller does present (or does not): both branches settle before the retention
        /// guards run, which is the point of seeding — the guards never need to fire.</summary>
        public async Task SeedReceiptAsync(bool overrideWindow)
        {
            var canonical = JsonSerializer.SerializeToUtf8Bytes(new
            {
                RoomId = "room-1", OverrideRetentionWindow = overrideWindow
            });
            var fingerprint = OperationFingerprint.FromCanonicalRequest(1, canonical);
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO infra.operation_receipt(tenant_id, game_id, operation_kind, target_id,
                    operation_id, canonical_version, request_hash, result_json)
                VALUES ('tenant-a', 'game-a', 'retention.prune', 'room-1', 'op-prune-1',
                    @version, @hash,
                    '{"RoomId":"room-1","KeepTick":0,"FramesDeleted":0,"SnapshotsDeleted":0,
                      "Duplicate":false}'::jsonb)
                """, connection);
            insert.Parameters.AddWithValue("version", fingerprint.CanonicalVersion);
            insert.Parameters.AddWithValue("hash", fingerprint.Sha256Hex);
            await insert.ExecuteNonQueryAsync();
        }

        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));

        /// <summary>The common caller: a cluster-scoped control server.</summary>
        public TestServerCallContext Context(string role, string instance) =>
            Context("@cluster", role, instance);

        public async Task Rejects(Func<Task> call, StatusCode code, string detail)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            Assert.Equal(detail, error.Status.Detail);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_retention_face_" + Guid.NewGuid().ToString("N");
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
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
