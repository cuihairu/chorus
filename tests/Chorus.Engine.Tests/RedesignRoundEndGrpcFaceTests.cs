using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The end-operation face driven as plain method calls: the game host's begin, the operator's
/// status read and the gateway's delivery-stop ack, against a real database — pinning the
/// idempotent replay, the one-operation-per-round refusal and the argument fences at the face
/// the hosts call.
/// </summary>
public sealed class RedesignRoundEndGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForTheGameAndControlRoles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        Assert.True((await rig.Service.Ready(new Empty(), rig.Context("@cluster", "game-server", "gs-1"))).Ready);
        Assert.True((await rig.Service.Ready(new Empty(), rig.Context("@cluster", "control-server", "control-1"))).Ready);
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheFace()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Context("@cluster", "node-agent", "agent-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
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
    public async Task BeginEndRecordsTheRoundEndOperation()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await RoundAsync(database.ConnectionString);
        var rig = New(database.ConnectionString);
        var reply = await rig.Service.BeginEnd(BeginRequest("op-1", "room-1"),
            rig.Context("@cluster", "game-server", "gs-1"));
        Assert.Equal("op-1", reply.EndOperationId);
        Assert.Equal(EndOperationPhases.ResultFrozen, reply.Phase);
        Assert.False(reply.Duplicate);

        var status = await rig.Service.GetEndStatus(new GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-1"
        }, rig.Context("@cluster", "control-server", "control-1"));
        Assert.True(status.Found);
        Assert.Equal("op-1", status.EndOperationId);
        Assert.Equal(EndOperationPhases.ResultFrozen, status.Phase);
        Assert.Equal(120, status.FinalTick);
        Assert.Equal(1, status.ResultVersion);
    }

    [Fact]
    public async Task BeginEndReplaysByIdempotentIdentity()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await RoundAsync(database.ConnectionString);
        var rig = New(database.ConnectionString);
        await rig.Service.BeginEnd(BeginRequest("op-1", "room-1"), rig.Context("@cluster", "game-server", "gs-1"));
        var reply = await rig.Service.BeginEnd(BeginRequest("op-1", "room-1"),
            rig.Context("@cluster", "game-server", "gs-1"));
        Assert.True(reply.Duplicate);
        Assert.Equal(EndOperationPhases.ResultFrozen, reply.Phase);
    }

    [Fact]
    public async Task ASecondBusinessIdForTheSameRoomRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await RoundAsync(database.ConnectionString);
        var rig = New(database.ConnectionString);
        await rig.Service.BeginEnd(BeginRequest("op-1", "room-1"), rig.Context("@cluster", "game-server", "gs-1"));
        await Rejects(() => rig.Service.BeginEnd(BeginRequest("op-2", "room-1"),
                rig.Context("@cluster", "game-server", "gs-1")),
            StatusCode.FailedPrecondition, "The room room-1 already has its end operation.");
    }

    [Fact]
    public async Task TheSameBusinessIdForAnotherRoomRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await RoundAsync(database.ConnectionString);
        var rig = New(database.ConnectionString);
        await rig.Service.BeginEnd(BeginRequest("op-1", "room-1"), rig.Context("@cluster", "game-server", "gs-1"));
        await Rejects(() => rig.Service.BeginEnd(BeginRequest("op-1", "room-2"),
                rig.Context("@cluster", "game-server", "gs-1")),
            StatusCode.FailedPrecondition, "The end operation op-1 belongs to the room room-1.");
    }

    [Fact]
    public async Task AnIncompleteBeginRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.BeginEnd(new BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "", EndOperationId = "op-1"
        }, rig.Context("@cluster", "game-server", "gs-1")),
            StatusCode.InvalidArgument, "The begin request is incomplete.");
    }

    [Fact]
    public async Task NegativeResultFactsRefuse()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.BeginEnd(new BeginEndRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-1", EndOperationId = "op-1",
            FinalTick = -1, ResultVersion = 0
        }, rig.Context("@cluster", "game-server", "gs-1")),
            StatusCode.InvalidArgument, "The begin's result facts must be non-negative.");
    }

    [Fact]
    public async Task GetEndStatusWithoutAnOperationAnswersNotFound()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var status = await rig.Service.GetEndStatus(new GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-none"
        }, rig.Context("@cluster", "control-server", "control-1"));
        Assert.False(status.Found);
    }

    [Fact]
    public async Task AnIncompleteStatusRequestRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.GetEndStatus(new GetEndStatusRequest
        {
            TenantId = "tenant-a", GameId = "", RoomId = "room-1"
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "The status request is incomplete.");
    }

    [Fact]
    public async Task AckDeliveryStoppedRefusesARoleOutsideTheConfirmers()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.AckDeliveryStopped(AckRequest(), rig.Context("tenant-a", "streamer-server", "s-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task AnIncompleteAckRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var ack = AckRequest();
        ack.EndOperationId = "";
        await Rejects(() => rig.Service.AckDeliveryStopped(ack, rig.Context("@cluster", "platform-gateway", "pg-1")),
            StatusCode.InvalidArgument, "The delivery-stop ack is incomplete.");
    }

    [Fact]
    public async Task AnAckWithoutItsRouteVersionRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var ack = AckRequest();
        ack.CloseRouteVersion = 0;
        await Rejects(() => rig.Service.AckDeliveryStopped(ack, rig.Context("@cluster", "platform-gateway", "pg-1")),
            StatusCode.InvalidArgument, "The delivery-stop ack must name its version and non-negative drain facts.");
    }

    private static BeginEndRequest BeginRequest(string endOperationId, string roomId) => new()
    {
        TenantId = "tenant-a", GameId = "game-a", RoomId = roomId, EndOperationId = endOperationId,
        RoundGeneration = 1, FinalTick = 120, ResultVersion = 1
    };

    private static AckDeliveryStoppedRequest AckRequest() => new()
    {
        TenantId = "tenant-a", GameId = "game-a", RoomId = "room-1", EndOperationId = "op-1",
        CloseRouteVersion = 4, CutoffSequence = 10, DrainedOutOfRound = 0, DrainedRouted = 2
    };

    /// <summary>Admits the round the begin ends: the lateness fence derives its generations
    /// from the mounts still seated in the room, so a round with no participants is a begin
    /// the store refuses to record.</summary>
    private static async Task RoundAsync(string connectionString)
    {
        var identity = new RedesignIdentityStore(connectionString);
        var bindings = new RedesignBindingStore(connectionString);
        var rooms = new RedesignRoomStore(connectionString);
        var control = new RedesignControlStore(connectionString);
        var streamer = await identity.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "roundend-open-1");
        var live = await bindings.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "platform-room", "live-key-roundend");
        var session = await identity.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindings.StartGameplayAsync("tenant-a", live, session.SessionId,
            "game-a", "mount-roundend");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1", "gs-1", 60);
        await rooms.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
            [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-1");
    }

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    private sealed record Rig(RoundEndGrpcService Service, TestProcessCertificates Certificates)
    {
        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var store = new RedesignControlStore(connectionString);
        return new Rig(new RoundEndGrpcService(store, certificates.Trusted), certificates);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_roundend_face_" + Guid.NewGuid().ToString("N");
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
