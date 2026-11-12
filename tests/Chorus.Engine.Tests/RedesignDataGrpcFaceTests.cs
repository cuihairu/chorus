using System.Text.Json;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Protocol;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The player data face driven as plain method calls. Every write is one receipt transaction
/// — the effect and its receipt commit together or not at all — and the face fences callers
/// twice: by role per operation kind, and by the request fingerprint, so a replayed operation
/// id cannot smuggle different facts past the receipt.
/// </summary>
public sealed class RedesignDataGrpcFaceTests
{
    private const string Instance = "ss-1";
    private const string DataJson = """{"level":3,"name":"ada"}""";

    [Fact]
    public async Task ReadyAnswersForTheDataCallers()
    {
        await using var rig = await DataRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("streamer-server", Instance));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheDataPlane()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("data-server", "data-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task PatchProfileCreatesTheProfileAtVersionOne()
    {
        await using var rig = await DataRig.CreateAsync();
        var reply = await rig.Service.PatchProfile(Patch(), rig.Context("streamer-server", Instance));
        Assert.True(reply.Found);
        Assert.False(reply.Duplicate);
        Assert.Equal(1, reply.Version);
        Assert.Equal(DataJson, reply.DataJson);
    }

    [Fact]
    public async Task PatchProfileAnswersDuplicateForAnExactReplay()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.PatchProfile(Patch(), streamer);
        var reply = await rig.Service.PatchProfile(Patch(), streamer);
        Assert.True(reply.Duplicate);
        Assert.Equal(1, reply.Version);
    }

    [Fact]
    public async Task PatchProfileBumpsTheVersionOnTheUpdatePath()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.PatchProfile(Patch(), streamer);
        // A different canonical request is a different operation: it carries its own
        // operation id, since the first id is spent on the create.
        var reply = await rig.Service.PatchProfile(
            Patch(expectedVersion: 1, dataJson: """{"level":4}""", operationId: "op-data-2"),
            streamer);
        Assert.Equal(2, reply.Version);
        // The stored jsonb reads back normalized, so the round-trip is compared as JSON,
        // not as the exact text that was sent.
        var stored = JsonDocument.Parse(reply.DataJson).RootElement;
        Assert.Equal(4, stored.GetProperty("level").GetInt32());
    }

    [Fact]
    public async Task PatchProfileRefusesAnEpochTheProfileDoesNotCarry()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.PatchProfile(Patch(), streamer);
        // A distinct canonical request spends its own operation id: reusing the create's
        // id would answer IdempotencyConflict before the fence is reached.
        await rig.Rejects(() => rig.Service.PatchProfile(
                Patch(expectedVersion: 9, operationId: "op-data-2"), streamer),
            StatusCode.Aborted, "Profile version conflict.");
    }

    [Fact]
    public async Task PatchProfileRefusesAnInvalidSchemaVersion()
    {
        await using var rig = await DataRig.CreateAsync();
        // A contract gap, recorded rather than smoothed over: the face maps version and
        // fingerprint refusals but not argument refusals, so the store's argument
        // exception leaves the face unmapped.
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Service.PatchProfile(Patch(schemaVersion: 0), rig.Context("streamer-server", Instance)));
        Assert.Contains("Invalid profile operation.", error.Message);
    }

    [Fact]
    public async Task PatchProfileRefusesAKeyOfAnotherOperationKind()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        var key = Patch().Key;
        key.OperationKind = "asset.change";
        var request = Patch();
        request.Key = key;
        await rig.Rejects(() => rig.Service.PatchProfile(request, streamer),
            StatusCode.InvalidArgument, "Operation key is invalid.");
    }

    [Fact]
    public async Task PatchProfileRefusesACallerActingForAnotherTenant()
    {
        await using var rig = await DataRig.CreateAsync();
        var tenant = rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "streamer-server", Instance));
        // A tenant-scoped certificate may act for its own tenant only: a key claiming
        // another tenant is refused before the store is reached.
        await rig.Rejects(() => rig.Service.PatchProfile(Patch(tenantId: "tenant-b"), tenant),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task GetProfileAnswersTheStoredProfile()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.PatchProfile(Patch(), streamer);
        var reply = await rig.Service.GetProfile(Profile(), streamer);
        Assert.True(reply.Found);
        Assert.Equal(1, reply.Version);
        // jsonb reads back normalized, so the profile is compared as JSON, not as the
        // exact text that was stored.
        var stored = JsonDocument.Parse(reply.DataJson).RootElement;
        Assert.Equal(3, stored.GetProperty("level").GetInt32());
        Assert.Equal("ada", stored.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetProfileAnswersNotFoundWithoutAProfile()
    {
        await using var rig = await DataRig.CreateAsync();
        var reply = await rig.Service.GetProfile(Profile(), rig.Context("streamer-server", Instance));
        Assert.False(reply.Found);
    }

    [Fact]
    public async Task ChangeAssetAppliesTheDeltaAndLedgersIt()
    {
        await using var rig = await DataRig.CreateAsync();
        var reply = await rig.Service.ChangeAsset(Change(delta: 5), rig.Context("streamer-server", Instance));
        Assert.False(reply.Duplicate);
        Assert.Equal(5, reply.Balance);
        Assert.Equal(2, reply.Version);
    }

    [Fact]
    public async Task ChangeAssetAnswersDuplicateForAnExactReplay()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.ChangeAsset(Change(delta: 5), streamer);
        var reply = await rig.Service.ChangeAsset(Change(delta: 5), streamer);
        Assert.True(reply.Duplicate);
        Assert.Equal(5, reply.Balance);
    }

    [Fact]
    public async Task ChangeAssetRefusesAnOverdraw()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.ChangeAsset(Change(delta: 5), streamer);
        // The balance constraint lives in the store's update, not in a face check: a delta
        // that would push the balance negative refuses visibly and the ledger stays clean.
        // The overdraw is a distinct canonical request, so it spends its own operation id.
        await rig.Rejects(() => rig.Service.ChangeAsset(
                Change(delta: -10, operationId: "op-asset-2"), streamer),
            StatusCode.Aborted, "Asset balance constraint.");
        var balance = await rig.Service.GetAsset(Asset(), streamer);
        Assert.Equal(5, balance.Balance);
    }

    [Fact]
    public async Task ChangeAssetRefusesAReplayedOperationIdWithDifferentFacts()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.ChangeAsset(Change(delta: 5), streamer);
        await rig.Rejects(() => rig.Service.ChangeAsset(Change(delta: 7), streamer),
            StatusCode.AlreadyExists, "Operation ID conflict.");
    }

    [Fact]
    public async Task ChangeAssetRefusesAnEmptyReason()
    {
        await using var rig = await DataRig.CreateAsync();
        // Same contract gap as the profile patch: the store's argument exception is not
        // mapped by the face.
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Service.ChangeAsset(Change(delta: 1, reason: "  "),
                rig.Context("streamer-server", Instance)));
        Assert.Contains("Invalid asset operation.", error.Message);
    }

    [Fact]
    public async Task GetAssetAnswersZeroForAnAccountThatNeverMoved()
    {
        await using var rig = await DataRig.CreateAsync();
        var reply = await rig.Service.GetAsset(Asset(), rig.Context("streamer-server", Instance));
        Assert.Equal(0, reply.Balance);
        Assert.Equal(0, reply.Version);
    }

    [Fact]
    public async Task GetOperationAnswersTheCommittedReceipt()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.PatchProfile(Patch(), streamer);
        var reply = await rig.Service.GetOperation(Key("profile.patch", "player-1",
            JsonSerializer.SerializeToUtf8Bytes(new { ExpectedVersion = (long)0, SchemaVersion = 1, DataJson = DataJson })),
            streamer);
        Assert.True(reply.Found);
        Assert.True(reply.Duplicate);
        Assert.Contains("level", reply.ResultJson);
    }

    [Fact]
    public async Task GetOperationRefusesAKindTheRoleCannotQuery()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        var key = Key("fenced.probe", "room-1", [1]);
        await rig.Rejects(() => rig.Service.GetOperation(key, streamer),
            StatusCode.PermissionDenied, "Role cannot query this operation kind.");
    }

    [Fact]
    public async Task PatchProfileRefusesAKeyWhoseFingerprintDisagrees()
    {
        await using var rig = await DataRig.CreateAsync();
        var request = Patch();
        request.Key.RequestHashHex = new string('0', 64);
        // The write path recomputes the fingerprint from the request body and refuses a
        // key that claims another hash, before the receipt store is reached.
        await rig.Rejects(() => rig.Service.PatchProfile(request, rig.Context("streamer-server", Instance)),
            StatusCode.InvalidArgument, "Request fingerprint differs.");
    }

    [Fact]
    public async Task GetOperationRefusesAReceiptQueriedUnderADifferentFingerprint()
    {
        await using var rig = await DataRig.CreateAsync();
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.PatchProfile(Patch(), streamer);
        // A query carries only the stored hash, so a hash that disagrees with the
        // committed receipt is answered by the receipt store itself — and the query face,
        // unlike the writers, does not map that conflict (recorded contract gap).
        var key = Key("profile.patch", "player-1", [1]);
        var error = await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => rig.Service.GetOperation(key, streamer));
        Assert.Equal("The operation ID has already been used for a different canonical request.",
            error.Message);
    }

    [Fact]
    public async Task GetOperationAnswersNotFoundForAnUnspentKey()
    {
        await using var rig = await DataRig.CreateAsync();
        var key = Key("profile.patch", "player-1", [1], operationId: "op-data-2");
        var reply = await rig.Service.GetOperation(key, rig.Context("streamer-server", Instance));
        Assert.False(reply.Found);
    }

    [Fact]
    public async Task CommitFencedProbeAppliesUnderALiveOwnership()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.SeedOwnershipAsync();
        var reply = await rig.Service.CommitFencedProbe(
            Probe("gs-1"), rig.Context("game-server", "gs-1"));
        Assert.False(reply.Duplicate);
        Assert.Equal("effect-1", reply.EffectId);
    }

    [Fact]
    public async Task CommitFencedProbeRefusesAnOwnerTheCertificateDisavows()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.SeedOwnershipAsync();
        // The fence is the caller's own certificate: a probe naming another holder is
        // refused before the store is reached.
        await rig.Rejects(() => rig.Service.CommitFencedProbe(
                Probe("gs-1"), rig.Context("game-server", "gs-other")),
            StatusCode.PermissionDenied, "Owner instance differs from certificate.");
    }

    [Fact]
    public async Task CommitFencedProbeRefusesAProbeWithoutALiveOwnership()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.Rejects(() => rig.Service.CommitFencedProbe(
                Probe("gs-1"), rig.Context("game-server", "gs-1")),
            StatusCode.FailedPrecondition, "Stale owner.");
    }

    [Fact]
    public async Task GetOperationAnswersAFencedProbeForTheGameServer()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.SeedOwnershipAsync();
        await rig.Service.CommitFencedProbe(Probe("gs-1"), rig.Context("game-server", "gs-1"));
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            OwnerKind = "room", EntityId = "room-1", OwnerInstanceId = "gs-1",
            Epoch = (long)3, EffectId = "effect-1"
        });
        var reply = await rig.Service.GetOperation(
            Key("fenced.probe", "room-1", canonical), rig.Context("game-server", "gs-1"));
        Assert.True(reply.Found);
    }

    [Fact]
    public async Task PatchProfileRefusesAKeyWhoseKindDisagrees()
    {
        await using var rig = await DataRig.CreateAsync();
        var request = Patch();
        request.Key.OperationKind = "asset.change";
        await rig.Rejects(() => rig.Service.PatchProfile(request, rig.Context("streamer-server", Instance)),
            StatusCode.InvalidArgument, "Operation key is invalid.");
    }

    [Fact]
    public async Task GetOperationRefusesABlankOperationId()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.Rejects(() => rig.Service.GetOperation(
                Key("profile.patch", "player-1", Array.Empty<byte>(), operationId: ""),
                rig.Context("streamer-server", Instance)),
            StatusCode.InvalidArgument, "Operation key is invalid.");
    }

    [Fact]
    public async Task GetOperationRefusesATenantCallerNamingAnotherTenant()
    {
        await using var rig = await DataRig.CreateAsync();
        var context = rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "streamer-server", Instance));
        await rig.Rejects(() => rig.Service.GetOperation(
                Key("profile.patch", "player-1", Array.Empty<byte>(), tenantId: "tenant-b"), context),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task ChangeAssetRefusesASpentOperationIdUnderADifferentShape()
    {
        await using var rig = await DataRig.CreateAsync();
        await rig.Service.ChangeAsset(Change(100, operationId: "op-asset-2"),
            rig.Context("streamer-server", Instance));

        await rig.Rejects(() => rig.Service.ChangeAsset(Change(200, operationId: "op-asset-2"),
                rig.Context("streamer-server", Instance)),
            StatusCode.AlreadyExists, "Operation ID conflict.");
    }

    private static PatchProfileRequest Patch(
        long expectedVersion = 0, int schemaVersion = 1, string dataJson = DataJson,
        string tenantId = "tenant-a", string operationId = "op-data-1")
    {
        var hash = JsonSerializer.SerializeToUtf8Bytes(
            new { ExpectedVersion = expectedVersion, SchemaVersion = schemaVersion, DataJson = dataJson });
        var request = new PatchProfileRequest
        {
            Key = Key("profile.patch", "player-1", hash, tenantId, operationId),
            ExpectedVersion = expectedVersion, SchemaVersion = schemaVersion, DataJson = dataJson
        };
        return request;
    }

    private static ProfileRequest Profile() => new()
        { TenantId = "tenant-a", GameId = "game-a", PlayerId = "player-1" };

    private static ChangeAssetRequest Change(long delta, string reason = "quest reward",
        string operationId = "op-data-1")
    {
        var hash = JsonSerializer.SerializeToUtf8Bytes(
            new { AssetId = "coin", Delta = delta, Reason = reason });
        return new ChangeAssetRequest
        {
            Key = Key("asset.change", "player-1", hash, operationId: operationId),
            AssetId = "coin", Delta = delta, Reason = reason
        };
    }

    private static AssetRequest Asset() => new()
        { TenantId = "tenant-a", GameId = "game-a", PlayerId = "player-1", AssetId = "coin" };

    private static FencedProbeRequest Probe(string ownerInstanceId)
    {
        var hash = JsonSerializer.SerializeToUtf8Bytes(new
        {
            OwnerKind = "room", EntityId = "room-1", OwnerInstanceId = ownerInstanceId,
            Epoch = (long)3, EffectId = "effect-1"
        });
        return new FencedProbeRequest
        {
            // The probe's target is the owned entity itself: the store refuses a key whose
            // target names anything else.
            Key = Key("fenced.probe", "room-1", hash), OwnerKind = "room", EntityId = "room-1",
            OwnerInstanceId = ownerInstanceId, Epoch = 3, EffectId = "effect-1"
        };
    }

    private static OperationKey Key(string kind, string targetId, byte[] canonical,
        string tenantId = "tenant-a", string operationId = "op-data-1") => new()
    {
        TenantId = tenantId, GameId = "game-a", OperationKind = kind, TargetId = targetId,
        OperationId = operationId, CanonicalVersion = 1,
        RequestHashHex = OperationFingerprint.FromCanonicalRequest(1, canonical).Sha256Hex
    };

    private sealed record DataRig(DataGrpcService Service,
        TestProcessCertificates Certificates, TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<DataRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var store = new RedesignPlayerStore(database.ConnectionString);
            return new DataRig(new DataGrpcService(store, certificates.Trusted),
                certificates, database);
        }

        /// <summary>Mounts the live ownership row a fenced probe spends: the fence reads it
        /// under a row lock, so the probe needs the real holder with an unexpired lease.</summary>
        public async Task SeedOwnershipAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.ownership(kind, entity_id, owner_process_instance_id, epoch,
                    lease_until, status, version, tenant_id, game_id)
                VALUES ('room', 'room-1', 'gs-1', 3,
                    clock_timestamp() + interval '5 minutes', 'Active', 1, 'tenant-a', 'game-a')
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

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
        private readonly string _name = "chorus_data_face_" + Guid.NewGuid().ToString("N");
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
