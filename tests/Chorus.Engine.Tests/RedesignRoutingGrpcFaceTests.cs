using Chorus.Data;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The routing face driven as plain method calls: the route-source report verifies the mount's
/// live-session join in a real database (an unverified or late report establishes nothing),
/// the snapshot carries what the register put there, and the watch notices a version change.
/// </summary>
public sealed class RedesignRoutingGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForTheRoutingRoles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var reply = await rig.Service.Ready(new Empty(), rig.Context("@cluster", "game-server", "gs-1"));
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
    public async Task AnEmptyPlaneAnswersAnEmptyVersionedSnapshot()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var snapshot = await rig.Service.GetRoutingSnapshot(new Empty(),
            rig.Context("@cluster", "node-agent", "agent-1"));
        Assert.Empty(snapshot.Rooms);
        Assert.Empty(snapshot.Streamers);
        Assert.Empty(snapshot.Instances);
    }

    [Fact]
    public async Task AVerifiedMountEstablishesItsRouteSource()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, sessionId, bindingId) = await MountAsync(database.ConnectionString, "route-open-1");
        var reply = await rig.Service.RegisterRouteSource(new RegisterRouteSourceRequest
        {
            TenantId = "tenant-a", GameId = "game-a", StreamerId = streamerId,
            BindingId = bindingId, BindingGeneration = 1
        }, rig.Context("tenant-a", "streamer-server", "streamer-1"));
        Assert.Equal("simulator", reply.Platform);
        Assert.False(string.IsNullOrEmpty(reply.CanonicalLiveSessionKey));
        Assert.Equal(1, reply.BindingGeneration);
        Assert.False(reply.Duplicate);

        var snapshot = await rig.Service.GetRoutingSnapshot(new Empty(),
            rig.Context("@cluster", "node-agent", "agent-1"));
        var route = Assert.Single(snapshot.Streamers);
        Assert.Equal(streamerId, route.StreamerId);
        Assert.Equal(bindingId, route.BindingId);
        Assert.Equal("simulator", route.Platform);
        Assert.Equal("tenant-a", route.TenantId);
    }

    [Fact]
    public async Task AReplayOfTheRecordedRouteSourceIsADuplicate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, _, bindingId) = await MountAsync(database.ConnectionString, "route-open-2");
        await rig.Service.RegisterRouteSource(RouteReport(streamerId, bindingId),
            rig.Context("tenant-a", "streamer-server", "streamer-1"));
        var reply = await rig.Service.RegisterRouteSource(RouteReport(streamerId, bindingId),
            rig.Context("tenant-a", "streamer-server", "streamer-1"));
        Assert.True(reply.Duplicate);
        Assert.Equal("simulator", reply.Platform);
    }

    [Fact]
    public async Task AReportNamingAGenerationTheMountDoesNotHoldIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, _, bindingId) = await MountAsync(database.ConnectionString, "route-open-3");
        // The report claims generation 3 while the mount the binding holds is at generation 1:
        // a stale or ahead claim establishes nothing. The route source registration is what the
        // fence protects — the snapshot's streamer routes come from the mounts themselves.
        await Rejects(() => rig.Service.RegisterRouteSource(RouteReport(streamerId, bindingId, 3),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.FailedPrecondition,
            $"The binding {bindingId} holds generation 1, not 3; the report is late or ahead of the mount.");

        var verified = await rig.Service.RegisterRouteSource(RouteReport(streamerId, bindingId),
            rig.Context("tenant-a", "streamer-server", "streamer-1"));
        Assert.False(verified.Duplicate);
    }

    [Fact]
    public async Task AReportForAnUnverifiedBindingEstablishesNothing()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.RegisterRouteSource(RouteReport("streamer-none", "binding-none"),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.FailedPrecondition,
            "The binding binding-none does not exist; no route source is established.");
    }

    [Fact]
    public async Task AnIncompleteRouteSourceReportIsInvalid()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.RegisterRouteSource(new RegisterRouteSourceRequest
        {
            TenantId = "tenant-a", GameId = "game-a", StreamerId = "", BindingId = "binding-1",
            BindingGeneration = 1
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "The route-source report is incomplete.");
    }

    [Fact]
    public async Task ANonPositiveBindingGenerationIsInvalid()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.RegisterRouteSource(new RegisterRouteSourceRequest
        {
            TenantId = "tenant-a", GameId = "game-a", StreamerId = "streamer-1", BindingId = "binding-1",
            BindingGeneration = 0
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "The binding generation must be a positive fact.");
    }

    [Fact]
    public async Task ConfirmRoutingVersionIsKeyedPerGatewayInstance()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var reply = await rig.Service.ConfirmRoutingVersion(new ConfirmRoutingVersionRequest { Version = 7 },
            rig.Context("@cluster", "streamer-gateway", "edge-1"));
        Assert.Equal(7, reply.Version);
        Assert.True(reply.Confirmed);
    }

    [Fact]
    public async Task ConfirmRoutingVersionRefusesARoleOutsideTheConfirmers()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ConfirmRoutingVersion(new ConfirmRoutingVersionRequest { Version = 7 },
                rig.Context("@cluster", "node-agent", "agent-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task TheWatchNoticesAVersionChange()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, _, bindingId) = await MountAsync(database.ConnectionString, "route-open-4");
        await rig.Service.RegisterRouteSource(RouteReport(streamerId, bindingId),
            rig.Context("tenant-a", "streamer-server", "streamer-1"));
        var snapshot = await rig.Service.GetRoutingSnapshot(new Empty(),
            rig.Context("@cluster", "node-agent", "agent-1"));

        using var cancellation = new CancellationTokenSource();
        var stream = new CaptureStream();
        var watch = rig.Service.WatchRouting(new WatchRoutingRequest { KnownVersion = snapshot.Version - 1 },
            stream, rig.Certificates.Context(
                rig.Certificates.Issue("@cluster", "streamer-gateway", "edge-1"), cancellation.Token));
        try
        {
            // The notice names the version the snapshot actually carries — a content hash,
            // which may be negative, so the value is compared, not sign-checked.
            await WaitForAsync(() => Task.FromResult(stream.Written.Count > 0), watch);
            Assert.Equal(snapshot.Version, stream.Written[0].Version);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
    }

    private static RegisterRouteSourceRequest RouteReport(string streamerId, string bindingId,
        long bindingGeneration = 1) => new()
    {
        TenantId = "tenant-a", GameId = "game-a", StreamerId = streamerId,
        BindingId = bindingId, BindingGeneration = bindingGeneration
    };

    /// <summary>Mounts a streamer the way the gateways do: identity, live session, login
    /// session, gameplay binding — the shape the route-source join verifies.</summary>
    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        string connectionString, string openId)
    {
        var identity = new RedesignIdentityStore(connectionString);
        var bindings = new RedesignBindingStore(connectionString);
        var streamer = await identity.ResolveIdentityAsync("tenant-a", "simulator", "app-1", openId);
        var live = await bindings.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "platform-room", "live-key-" + openId);
        var session = await identity.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var mount = await bindings.StartGameplayAsync("tenant-a", live, session.SessionId,
            "game-a", "mount-" + openId);
        return (streamer.StreamerId, session.SessionId, mount.BindingId);
    }

    /// <summary>Polls until the condition holds; if the watched call faulted instead, its own
    /// exception is the real answer and is rethrown rather than a timeout.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition, Task? faultSource = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                if (faultSource is { IsFaulted: true }) throw faultSource.Exception!;
                throw new TimeoutException("The watch never wrote.");
            }
            await Task.Delay(50);
        }
    }

    private sealed class CaptureStream : IServerStreamWriter<RoutingNotice>
    {
        public List<RoutingNotice> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(RoutingNotice message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }

        /// <summary>The watch writes with the call's token; a capture that carries it is
        /// recorded the same instant — the write never waits on cancellation.</summary>
        public Task WriteAsync(RoutingNotice message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    private sealed record Rig(RoutingGrpcService Service, TestProcessCertificates Certificates)
    {
        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var store = new RedesignControlStore(connectionString);
        return new Rig(new RoutingGrpcService(store, certificates.Trusted), certificates);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_routing_face_" + Guid.NewGuid().ToString("N");
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
