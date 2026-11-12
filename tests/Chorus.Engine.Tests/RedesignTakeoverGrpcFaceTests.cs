using Chorus.Data;
using Chorus.DataServer;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The takeover face driven as plain method calls: the entity handover verifies the live
/// lease in a real database (only an Active ownership transfers, a streamer handover lands
/// in one control-plane transaction), the room handover forwards past the gates to the data
/// plane, and the recovery reaches for the target game host the directory names — never a
/// second recovery implementation.
/// </summary>
public sealed class RedesignTakeoverGrpcFaceTests
{
    private const string DeadGameEndpoint = "https://127.0.0.1:9";

    [Fact]
    public async Task ReadyAnswersForTheControlServer()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var reply = await rig.Service.Ready(new Empty(), rig.Context("@cluster", "control-server", "control-1"));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheControlPlane()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Context("@cluster", "streamer-server", "streamer-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ATransferOfAnUnsupportedKindIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "guild", EntityId = "guild-1",
            ToInstanceId = "gs-2", LeaseSeconds = 30, OperationId = "op-1"
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "Takeover of kind 'guild' is not supported yet.");
    }

    [Fact]
    public async Task ARoomTransferWithoutItsOperationIdIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-1",
            ToInstanceId = "gs-2", LeaseSeconds = 30
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "A room transfer names the recovery operation it belongs to.");
    }

    [Fact]
    public async Task AnIncompleteTransferRequestIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        // A lease outside 1..60 is an incomplete fact, as is a missing entity.
        await Rejects(() => rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = "streamer-1",
            ToInstanceId = "streamer-b", LeaseSeconds = 90
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "The takeover request is incomplete.");
        await Rejects(() => rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = "",
            ToInstanceId = "streamer-b", LeaseSeconds = 30
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "The takeover request is incomplete.");
    }

    [Fact]
    public async Task AStreamerHandoverMovesTheLeaseInOneTransaction()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var streamerId = await SeedStreamerAsync(database.ConnectionString, "takeover-open-1");
        var reply = await rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = streamerId,
            ToInstanceId = "streamer-b", LeaseSeconds = 30
        }, rig.Context("@cluster", "control-server", "control-1"));
        Assert.Equal("streamer-b", reply.OwnerInstanceId);
        Assert.Equal(2, reply.Epoch);
        Assert.Equal("Recovering", reply.Status);
    }

    [Fact]
    public async Task AHandoverOfAMissingOwnershipIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = "streamer-none",
            ToInstanceId = "streamer-b", LeaseSeconds = 30
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.FailedPrecondition, "The entity ownership is not Active; it cannot transfer.");
    }

    [Fact]
    public async Task ARoomHandoverForwardsPastTheGatesToTheDataPlane()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        // The gates pass on a seeded Active room ownership; the transfer itself is the data
        // plane's transaction (both epochs move together), so the dead endpoint answers.
        await SeedOwnershipAsync(database.ConnectionString, "room", "room-take-1", "gs-1");
        await Rejects(() => rig.Service.TransferOwnership(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = "room-take-1",
            ToInstanceId = "gs-2", LeaseSeconds = 30, OperationId = "op-room-1"
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.Unavailable, null!);
    }

    [Fact]
    public async Task AnIncompleteTakeoverRoomRequestIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.TakeoverRoom(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-1",
            ToInstanceId = "gs-2", LeaseSeconds = 30, TickMs = 0
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "The takeover request is incomplete.");
    }

    [Fact]
    public async Task ATakeoverNamingAnUnknownTargetIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.TakeoverRoom(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-1",
            ToInstanceId = "gs-none", LeaseSeconds = 30, TickMs = 25
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.NotFound, "The takeover target 'gs-none' is not a known game host.");
    }

    [Fact]
    public async Task ARecoveryOfARoomWithNoOwnershipIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.TakeoverRoom(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-none",
            ToInstanceId = "gs-2", LeaseSeconds = 30, TickMs = 25
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.FailedPrecondition, "The room has no ownership row to recover.");
    }

    [Fact]
    public async Task ARecoveryNamingTheWrongHolderIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await SeedOwnershipAsync(database.ConnectionString, "room", "room-take-2", "gs-1");
        await Rejects(() => rig.Service.TakeoverRoom(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-take-2",
            ToInstanceId = "gs-2", LeaseSeconds = 30, TickMs = 25
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.FailedPrecondition, "The room is held by 'gs-1', not 'gs-2'.");
    }

    [Fact]
    public async Task ARecoveryReachesTheTargetHostPastTheGates()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        // The directory names gs-2 and the ownership row agrees; the recovery itself rides
        // the game host's face, so the dead endpoint answers — the fences all passed.
        await SeedOwnershipAsync(database.ConnectionString, "room", "room-take-3", "gs-2");
        await Rejects(() => rig.Service.TakeoverRoom(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-take-3",
            ToInstanceId = "gs-2", LeaseSeconds = 30, TickMs = 25
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.Unavailable, null!);
    }

    /// <summary>Seeds a streamer with an Active ownership the way the streamer servers hold
    /// one: a real login session, then the versioned claim that names it.</summary>
    private static async Task<string> SeedStreamerAsync(string connectionString, string openId)
    {
        var identity = new RedesignIdentityStore(connectionString);
        var streamer = await identity.ResolveIdentityAsync("tenant-a", "simulator", "app-1", openId);
        var session = await identity.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        await new RedesignControlStore(connectionString).ClaimOwnershipAsync("tenant-a", "game-a",
            "streamer", streamer.StreamerId, "streamer-a", 30,
            sessionId: session.SessionId, sessionVersion: 1);
        return streamer.StreamerId;
    }

    /// <summary>Seeds an ownership row directly — the recovery fences read the row's owner,
    /// epoch and status, which is all these facts need to hold.</summary>
    private static async Task SeedOwnershipAsync(string connectionString, string kind, string entityId,
        string ownerInstanceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.ownership(tenant_id,game_id,kind,entity_id,owner_process_instance_id,
                epoch,lease_until,status,version)
            VALUES ('tenant-a','game-a',@kind,@entity,@owner,1,clock_timestamp()+interval '60 second',
                'Active',1)
            """, connection);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("entity", entityId);
        command.Parameters.AddWithValue("owner", ownerInstanceId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        if (detail is not null) Assert.Equal(detail, error.Status.Detail);
    }

    private sealed record Rig(TakeoverGrpcService Service, TestProcessCertificates Certificates)
    {
        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var controlStore = new RedesignControlStore(connectionString);
        // The directory knows one game host on a dead endpoint: the recovery reaches for it
        // the moment its fences pass, without opening a listener.
        var directory = new ClusterDirectory(
            certificates.Issue("@cluster", "control-server", "control-1"), certificates.Root,
            certificates.NodeAuthority, "cluster-a", "https://127.0.0.1:9",
            ["gs-2=" + DeadGameEndpoint]);
        return new Rig(new TakeoverGrpcService(directory, controlStore, certificates.Trusted), certificates);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_takeover_face_" + Guid.NewGuid().ToString("N");
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
