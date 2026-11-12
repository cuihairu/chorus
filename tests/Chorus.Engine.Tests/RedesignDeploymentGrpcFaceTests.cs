using Chorus.Data;
using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The deployment plan face driven as plain method calls: ApplyPlan is control-plane only,
/// the watch is the node agent's reconnection point and only ever streams its own node's
/// plans. The store runs against a real database; the watch runs against a capture stream
/// until the context's cancellation ends the poll loop.
/// </summary>
public sealed class RedesignDeploymentGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForTheAgentAndTheControlPlane()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        Assert.True((await rig.Service.Ready(new Empty(), rig.Context("node-agent", "agent-1"))).Ready);
        Assert.True((await rig.Service.Ready(new Empty(), rig.Context("control-server", "control-1"))).Ready);
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheFace()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Context("game-server", "gs-1")),
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
    public async Task ApplyPlanIssuesTheVersionedPlan()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterNodeAsync(rig);
        var plan = await rig.Service.ApplyPlan(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = "node-a/game-server", Role = "game-server",
            DesiredState = "Running", DesiredVersion = "1.2.0", PackageHash = "abc",
            PlanGeneration = 3, CommandId = "cmd-1", CommandExpiresUnixMs = 0
        }, rig.Context("control-server", "control-1"));
        Assert.Equal("node-a/game-server", plan.SlotId);
        Assert.Equal("node-a", plan.NodeId);
        Assert.Equal("game-server", plan.Role);
        Assert.Equal("Running", plan.DesiredState);
        Assert.Equal("1.2.0", plan.DesiredVersion);
        Assert.Equal("abc", plan.PackageHash);
        Assert.Equal(3, plan.PlanGeneration);
        Assert.Equal("cmd-1", plan.CommandId);
        Assert.Equal(0, plan.CommandExpiresUnixMs);
    }

    [Fact]
    public async Task ApplyPlanRefusesAnIncompletePlan()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ApplyPlan(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = "node-a/game-server", Role = "game-server",
            DesiredState = "Running", DesiredVersion = "", PackageHash = "abc", PlanGeneration = 3
        }, rig.Context("control-server", "control-1")),
            StatusCode.InvalidArgument, "The deployment plan is incomplete.");
    }

    [Fact]
    public async Task ApplyPlanIsControlPlaneOnly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ApplyPlan(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = "node-a/game-server", Role = "game-server",
            DesiredState = "Running", DesiredVersion = "1.2.0", PackageHash = "abc", PlanGeneration = 3
        }, rig.Context("node-agent", "agent-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task TheAgentWatchesItsOwnNodesPlans()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterNodeAsync(rig);
        await rig.Service.ApplyPlan(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = "node-a/game-server", Role = "game-server",
            DesiredState = "Running", DesiredVersion = "1.2.0", PackageHash = "abc", PlanGeneration = 3,
            CommandId = "cmd-1"
        }, rig.Context("control-server", "control-1"));

        using var cancellation = new CancellationTokenSource();
        var stream = new CaptureStream();
        var watch = rig.Service.WatchPlans(new WatchPlansRequest { NodeId = "node-a" }, stream,
            rig.Certificates.Context(rig.Certificates.Issue("@cluster", "node-agent", "agent-1"),
                cancellation.Token));
        try
        {
            // A reconnection re-emits the whole plan set: the watch is the agent's point of
            // return, so an applied plan must reach it without a fresh push.
            await WaitForAsync(() => Task.FromResult(stream.Written.Count > 0), watch);
            var plan = Assert.Single(stream.Written);
            Assert.Equal("node-a/game-server", plan.SlotId);
            Assert.Equal("node-a", plan.NodeId);
            Assert.Equal("game-server", plan.Role);
            Assert.Equal("Running", plan.DesiredState);
            Assert.Equal("1.2.0", plan.DesiredVersion);
            Assert.Equal(3, plan.PlanGeneration);
            Assert.Equal("cmd-1", plan.CommandId);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
    }

    [Fact]
    public async Task AnAgentOnlyWatchesItsOwnNode()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var stream = new CaptureStream();
        await Rejects(() => rig.Service.WatchPlans(new WatchPlansRequest { NodeId = "node-b" }, stream,
                rig.Context("node-agent", "agent-1")),
            StatusCode.PermissionDenied, "An agent only watches the plans of its own node.");
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

    private sealed class CaptureStream : IServerStreamWriter<DeploymentPlanProto>
    {
        public List<DeploymentPlanProto> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(DeploymentPlanProto message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }

        /// <summary>The watch writes with the call's token; a capture that carries it is
        /// recorded the same instant — the write never waits on cancellation.</summary>
        public Task WriteAsync(DeploymentPlanProto message, CancellationToken cancellationToken)
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

    private sealed record Rig(DeploymentGrpcService Service, TestProcessCertificates Certificates,
        RedesignControlStore Store)
    {
        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var store = new RedesignControlStore(connectionString);
        return new Rig(new DeploymentGrpcService(store, certificates.Trusted), certificates, store);
    }

    /// <summary>A plan's node is a foreign key into the cluster's node registry: the control
    /// plane plans a slot on a node it already knows about.</summary>
    private static async Task RegisterNodeAsync(Rig rig)
    {
        await rig.Store.RegisterNodeAsync("node-a", "agent-1", "{}");
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_deploy_face_" + Guid.NewGuid().ToString("N");
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
