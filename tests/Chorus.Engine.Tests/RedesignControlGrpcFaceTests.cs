using Chorus.Data;
using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The process/ownership face driven as plain method calls: the certificate fences (agent vs
/// self-report, tenant scopes) run against issued process certificates, and the control store
/// runs against a real database — so the tests pin the two-reporter trust split, the
/// versioned-claim arithmetic and the error mapping at the face the node agents call.
/// </summary>
public sealed class RedesignControlGrpcFaceTests
{
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task ReadyAnswersForTheProcessRoles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        foreach (var role in new[] { "node-agent", "game-server", "streamer-gateway", "control-server" })
        {
            var reply = await rig.Service.Ready(new Empty(), rig.Context("@cluster", role, role));
            Assert.True(reply.Ready);
            Assert.Equal("4", reply.SchemaVersion);
        }
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheFace()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Context("@cluster", "platform-gateway", "pg-1")),
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
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task RegisterNodeAcceptsTheSupervisingAgent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var reply = await rig.Service.RegisterNode(new RegisterNodeRequest
        {
            NodeId = "node-a", AgentInstanceId = "agent-1", CapabilitiesJson = "{}"
        }, rig.Context("@cluster", "node-agent", "agent-1"));
        Assert.True(reply.Accepted);
        Assert.Equal("agent-1", reply.AgentInstanceId);
        Assert.Equal(0, reply.ReportSequence);
    }

    [Fact]
    public async Task RegisterNodeRefusesAMismatchedAgentIdentity()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.RegisterNode(new RegisterNodeRequest
        {
            NodeId = "node-a", AgentInstanceId = "agent-2", CapabilitiesJson = "{}"
        }, rig.Context("@cluster", "node-agent", "agent-1")),
            StatusCode.PermissionDenied, "Agent identity differs from certificate.");
    }

    [Fact]
    public async Task ReportProcessByTheAgentIsAccepted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var reply = await rig.Service.ReportProcess(BootstrapReport(), rig.Context("@cluster", "node-agent", "agent-1"));
        Assert.True(reply.Accepted);
        Assert.Equal("Ready", reply.Readiness);
        Assert.Equal("Recovered", reply.EntityRecovery);
    }

    [Fact]
    public async Task ReportProcessRefusesAMalformedBootstrapSlot()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var report = BootstrapReport();
        report.SlotId = "not-a-slot";
        await Rejects(() => rig.Service.ReportProcess(report, rig.Context("@cluster", "node-agent", "agent-1")),
            StatusCode.InvalidArgument, "Invalid bootstrap slot report.");
    }

    [Fact]
    public async Task ReportProcessByTheProcessItselfIsAccepted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var report = new ReportProcessRequest
        {
            NodeId = "node-a", AgentInstanceId = "gs-9", SlotId = "node-a/game-server",
            ProcessInstanceId = "gs-9", Role = "game-server", ReportSequence = 1,
            Lifecycle = "Running", Readiness = "Ready", Health = "Healthy",
            Admission = "Admitted", EntityRecovery = "Recovered", StartedUnixMs = NowMs
        };
        var reply = await rig.Service.ReportProcess(report, rig.Context("@cluster", "game-server", "gs-9"));
        Assert.True(reply.Accepted);
    }

    [Fact]
    public async Task ASelfReportThatDisagreesWithTheCertificateIsInvalid()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var report = new ReportProcessRequest
        {
            NodeId = "node-a", AgentInstanceId = "gs-9", SlotId = "node-a/game-server",
            ProcessInstanceId = "gs-other", Role = "game-server", ReportSequence = 1,
            Lifecycle = "Running", Readiness = "Ready", Health = "Healthy",
            Admission = "Admitted", EntityRecovery = "Recovered", StartedUnixMs = NowMs
        };
        await Rejects(() => rig.Service.ReportProcess(report, rig.Context("@cluster", "game-server", "gs-9")),
            StatusCode.InvalidArgument, "Invalid self slot report.");
    }

    [Fact]
    public async Task ReportProcessCarriesTheRoomsProgress()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var report = BootstrapReport();
        report.RoomProgress.Add(new RoomProgressProto
        {
            RoomId = "room-1", Epoch = 1, State = "Running", LastTick = 42, LastCommittedUnixMs = NowMs
        });
        var reply = await rig.Service.ReportProcess(report, rig.Context("@cluster", "node-agent", "agent-1"));
        Assert.True(reply.Accepted);
        var recorded = await QueryScalar(database.ConnectionString,
            "SELECT count(*) FROM control.room_progress WHERE process_instance_id='gs-1' AND room_id='room-1'");
        Assert.Equal(1, recorded);
    }

    [Fact]
    public async Task MachineTelemetryByTheAgentIsAccepted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var reply = await rig.Service.ReportMachineTelemetry(Telemetry(), rig.Context("@cluster", "node-agent", "agent-1"));
        Assert.True(reply.Accepted);
    }

    [Fact]
    public async Task MachineTelemetryAfterAnAgentTakeoverIsRefusedByTheStore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        // The node changes hands: the old agent's telemetry is refused, not silently trusted.
        await rig.Service.RegisterNode(new RegisterNodeRequest
        {
            NodeId = "node-a", AgentInstanceId = "agent-2", CapabilitiesJson = "{}"
        }, rig.Context("@cluster", "node-agent", "agent-2"));
        var reply = await rig.Service.ReportMachineTelemetry(Telemetry(), rig.Context("@cluster", "node-agent", "agent-1"));
        Assert.False(reply.Accepted);
    }

    [Fact]
    public async Task MachineTelemetryRefusesAnInvalidSample()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await RegisterAgent(rig);
        var telemetry = Telemetry();
        telemetry.SampledAtUnixMs = 0;
        await Rejects(() => rig.Service.ReportMachineTelemetry(telemetry, rig.Context("@cluster", "node-agent", "agent-1")),
            StatusCode.InvalidArgument, "Invalid machine telemetry report.");
    }

    [Fact]
    public async Task AppCountersByTheProcessItselfAreAccepted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var report = new AppCounterReport
        {
            NodeId = "node-a", ProcessInstanceId = "gs-9", ReportedAtUnixMs = NowMs
        };
        report.Counters.Add(new AppCounterProto { Family = "edges_total", Value = 3 });
        var reply = await rig.Service.ReportAppCounters(report, rig.Context("@cluster", "game-server", "gs-9"));
        Assert.True(reply.Accepted);
    }

    [Fact]
    public async Task AppCountersRefusedWhenTheIdentityDisagrees()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var report = new AppCounterReport
        {
            NodeId = "node-a", ProcessInstanceId = "gs-other", ReportedAtUnixMs = NowMs
        };
        report.Counters.Add(new AppCounterProto { Family = "edges_total", Value = 3 });
        await Rejects(() => rig.Service.ReportAppCounters(report, rig.Context("@cluster", "game-server", "gs-9")),
            StatusCode.InvalidArgument, "Invalid app counter report.");
    }

    [Fact]
    public async Task ClaimRoomOwnershipIsAcceptedAndVersioned()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var claim = await ClaimRoom(rig, "control-1");
        Assert.True(claim.Found);
        Assert.Equal(1, claim.Epoch);
        Assert.Equal("Active", claim.Status);
        // The same holder re-claiming a live lease keeps the epoch; a new holder bumps it.
        var reclaim = await ClaimRoom(rig, "control-1");
        Assert.Equal(1, reclaim.Epoch);
        var takeover = await ClaimRoom(rig, "control-2");
        Assert.Equal(2, takeover.Epoch);
        Assert.Equal("control-2", takeover.OwnerInstanceId);
    }

    [Fact]
    public async Task AnIncompleteOwnershipRequestIsInvalid()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "room", EntityId = "room-1", OwnerInstanceId = "control-1",
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = ""
        }, rig.Context("@cluster", "control-server", "control-1")),
            StatusCode.InvalidArgument, "Ownership request is incomplete.");
    }

    [Fact]
    public async Task AStreamerServerCannotClaimARoom()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "room", EntityId = "room-1", OwnerInstanceId = "streamer-1",
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "A streamer server owns streamer entities only.");
    }

    [Fact]
    public async Task AStreamerServerClaimsOnlyItself()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "streamer", EntityId = "streamer-x", OwnerInstanceId = "other",
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "A streamer server can only claim ownership for itself.");
    }

    [Fact]
    public async Task AStreamerClaimNamesTheActingSession()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "streamer", EntityId = "streamer-1", OwnerInstanceId = "streamer-1",
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.InvalidArgument, "A streamer ownership claim names the acting session and its version.");
    }

    [Fact]
    public async Task ACurrentStreamerClaimIsAdmittedAndAStaleOneRefuses()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var (streamerId, sessionId, _) = await MountAsync(database.ConnectionString, "mount-open-1");
        var current = await rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "streamer", EntityId = streamerId, OwnerInstanceId = "streamer-1",
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a",
            SessionId = sessionId, SessionVersion = 1
        }, rig.Context("tenant-a", "streamer-server", "streamer-1"));
        Assert.True(current.Found);
        Assert.Equal(1, current.Epoch);
        // A displaced holder's late claim names a session version that moved on: refused, and
        // routing stays with the admitted holder.
        await Rejects(() => rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "streamer", EntityId = streamerId, OwnerInstanceId = "streamer-2",
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a",
            SessionId = sessionId, SessionVersion = 99
        }, rig.Context("tenant-a", "streamer-server", "streamer-2")),
            StatusCode.FailedPrecondition,
            $"The claim names session {sessionId} (version 99), which is not the streamer's current session (version 1); routing stays with the current holder.");
        var held = await rig.Service.GetOwnership(new OwnershipLookup
        {
            Kind = "streamer", EntityId = streamerId, TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("@cluster", "control-server", "control-1"));
        Assert.True(held.Found);
        Assert.Equal("streamer-1", held.OwnerInstanceId);
    }

    [Fact]
    public async Task GetOwnershipAnswersTheClaimedRoom()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var claim = await ClaimRoom(rig, "control-1");
        var found = await rig.Service.GetOwnership(new OwnershipLookup
        {
            Kind = "room", EntityId = "room-1", TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("@cluster", "control-server", "control-1"));
        Assert.True(found.Found);
        Assert.Equal(claim.Epoch, found.Epoch);
        Assert.Equal("control-1", found.OwnerInstanceId);
        var unknown = await rig.Service.GetOwnership(new OwnershipLookup
        {
            Kind = "room", EntityId = "room-none", TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("@cluster", "control-server", "control-1"));
        Assert.False(unknown.Found);
    }

    [Fact]
    public async Task GetOwnershipIsTenantFenced()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.GetOwnership(new OwnershipLookup
        {
            Kind = "room", EntityId = "room-1", TenantId = "tenant-b", GameId = "game-a"
        }, rig.Context("tenant-a", "streamer-server", "streamer-1")),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    private static async Task<OwnershipReply> ClaimRoom(Rig rig, string owner) =>
        await rig.Service.ClaimOwnership(new OwnershipRequest
        {
            Kind = "room", EntityId = "room-1", OwnerInstanceId = owner,
            LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a"
        }, rig.Context("@cluster", "control-server", "control-1"));

    private static ReportProcessRequest BootstrapReport() => new()
    {
        NodeId = "node-a", AgentInstanceId = "agent-1", SlotId = "node-a/game-server",
        ProcessInstanceId = "gs-1", Role = "game-server", ReportSequence = 1,
        Lifecycle = "Running", Readiness = "Ready", Health = "Healthy",
        Admission = "Admitted", EntityRecovery = "Recovered", StartedUnixMs = NowMs
    };

    private static MachineTelemetryRequest Telemetry() => new()
    {
        NodeId = "node-a", AgentInstanceId = "agent-1", SampledAtUnixMs = NowMs, ReportSequence = 1,
        CpuPresent = true, CpuPercent = 12.5
    };

    private static async Task RegisterAgent(Rig rig) =>
        await rig.Service.RegisterNode(new RegisterNodeRequest
        {
            NodeId = "node-a", AgentInstanceId = "agent-1", CapabilitiesJson = "{}"
        }, rig.Context("@cluster", "node-agent", "agent-1"));

    /// <summary>Mounts a streamer the way the gateways do: identity, live session, login
    /// session, gameplay binding — the same shape the handler-host tests use.</summary>
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

    private static async Task<long> QueryScalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    private sealed record Rig(ControlGrpcService Service, TestProcessCertificates Certificates)
    {
        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var store = new RedesignControlStore(connectionString);
        return new Rig(new ControlGrpcService(store, certificates.Trusted), certificates);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_control_face_" + Guid.NewGuid().ToString("N");
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
