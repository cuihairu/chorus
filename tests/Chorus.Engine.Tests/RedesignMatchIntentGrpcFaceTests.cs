using Chorus.Data;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The match-intent entry driven as plain method calls: the submit and its cancel mirror run
/// their gates (role, tenant, completeness, generation, and the route-source registration) at
/// the face the streamer server calls. The queue forward is reached only by a registered
/// mount — in this rig the data channel points nowhere, so a passed gate surfaces as the
/// queue's unreachable answer, which is exactly the boundary under test.
/// </summary>
public sealed class RedesignMatchIntentGrpcFaceTests
{
    private const string DeadEndpoint = "https://127.0.0.1:9";

    [Fact]
    public async Task TheSubmitGateRefusesARoleOutsideTheSubmitter()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(), rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task AnAbsentCertificateIsUnauthenticated()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task AForeignCertificateIsUnauthenticated()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task TheTenantFenceAppliesToTheSubmit()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(tenantId: "tenant-b"),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task AnIncompleteIntentIsInvalid()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(sessionId: ""),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "The match intent is incomplete.");
    }

    [Fact]
    public async Task ANonPositiveBindingGenerationIsInvalid()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(bindingGeneration: 0),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "The binding generation must be a positive fact.");
    }

    [Fact]
    public async Task AnUnregisteredMountCannotSubmit()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(bindingId: "binding-none"),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.FailedPrecondition,
            "The binding binding-none is not a registered route source for tenant-a/game-a; the mount establishes no match intent.");
    }

    [Fact]
    public async Task ARegisteredMountPassesTheSubmitGateAndReachesTheQueue()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, _, bindingId) = await MountAsync(database.ConnectionString, "intent-open-1");
        await rig.Store.RegisterRouteSourceAsync("tenant-a", "game-a", streamerId, bindingId, 1);
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            rig.Service.SubmitMatchIntent(Intent(bindingId: bindingId),
                rig.Context("tenant-a", "streamer-server", "streamer-1")));
        // The gate passed; the forward fails on the rig's unreachable queue — the face's own
        // contract (gate, then forward) is what this pins.
        Assert.Equal(StatusCode.Unavailable, error.Status.StatusCode);
    }

    [Fact]
    public async Task ASupersededGenerationCannotSubmit()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, _, bindingId) = await MountAsync(database.ConnectionString, "intent-open-2");
        await rig.Store.RegisterRouteSourceAsync("tenant-a", "game-a", streamerId, bindingId, 1);
        await Rejects(() => rig.Service.SubmitMatchIntent(Intent(bindingId: bindingId, bindingGeneration: 9),
                rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.FailedPrecondition,
            $"The route source for {streamerId} holds generation 1, not 9; the intent names a superseded mount.");
    }

    [Fact]
    public async Task TheCancelGateMirrorsTheSubmitFences()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.CancelMatchIntent(new CancelMatchIntentRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = "binding-none",
            BindingGeneration = 1, OperationId = "op-1"
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.FailedPrecondition,
            "The binding binding-none is not a registered route source for tenant-a/game-a; the mount establishes no match intent.");
    }

    [Fact]
    public async Task ARegisteredMountPassesTheCancelGateAndReachesTheQueue()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, _, bindingId) = await MountAsync(database.ConnectionString, "intent-open-3");
        await rig.Store.RegisterRouteSourceAsync("tenant-a", "game-a", streamerId, bindingId, 1);
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            rig.Service.CancelMatchIntent(new CancelMatchIntentRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = bindingId,
                BindingGeneration = 1, OperationId = "op-1"
            }, rig.Context("tenant-a", "streamer-server", "streamer-1")));
        Assert.Equal(StatusCode.Unavailable, error.Status.StatusCode);
    }

    private static SubmitMatchIntentRequest Intent(
        string tenantId = "tenant-a", string bindingId = "binding-none",
        long bindingGeneration = 1, string sessionId = "session-fake") => new()
    {
        TenantId = tenantId, GameId = "game-a", BindingId = bindingId,
        BindingGeneration = bindingGeneration, SessionId = sessionId,
        PartitionKey = "tenant-a/game-a/-/default", OperationId = "intent-op-1", ExpiresUnixMs = 0
    };

    /// <summary>Mounts a streamer the way the gateways do — the shape the route-source
    /// registration verifies.</summary>
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

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    private sealed record Rig(
        MatchIntentGrpcService Service, TestProcessCertificates Certificates,
        RedesignControlStore Store)
    {
        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var store = new RedesignControlStore(connectionString);
        var directory = new ClusterDirectory(certificates.Issue("@cluster", "control-server", "control-1"),
            certificates.Root, certificates.NodeAuthority, "cluster-a", DeadEndpoint, []);
        return new Rig(new MatchIntentGrpcService(store, directory, certificates.Trusted),
            certificates, store);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_intent_face_" + Guid.NewGuid().ToString("N");
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
}
