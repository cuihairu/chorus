using Chorus.Data;
using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The coordinator term face driven as plain method calls: the certificate fence runs against
/// issued process certificates, and the term store runs against a real database — so the tests
/// pin the boundary's error mapping (lost terms refuse visibly, out-of-range leases refuse as
/// invalid) at the exact face a partitioned coordinator calls.
/// </summary>
public sealed class RedesignCoordinatorGrpcFaceTests
{
    [Fact]
    public async Task ReadyAnswersForTheControlRole()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var reply = await rig.Service.Ready(new Empty(), rig.Context());
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheFace()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(
                rig.Certificates.Issue("@cluster", "node-agent", "agent-1"))),
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
    public async Task ClaimTermAcquiresTermOneAndNamesTheHolder()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var term = await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        Assert.True(term.Found);
        Assert.Equal("control-1", term.HolderInstanceId);
        Assert.Equal(1, term.Term);
        Assert.True(term.LeaseUntilUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000);
    }

    [Fact]
    public async Task AReclaimByTheHolderKeepsTheTerm()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        var reclaim = await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        Assert.Equal(1, reclaim.Term);
    }

    [Fact]
    public async Task AClaimByAnotherHolderStartsTheNextTerm()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        var takeover = await rig.Service.ClaimTerm(TermRequest("match/main", "control-2"), rig.Context());
        Assert.Equal(2, takeover.Term);
        Assert.Equal("control-2", takeover.HolderInstanceId);
    }

    [Fact]
    public async Task RenewTermByTheHolderExtendsTheLease()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        var renewed = await rig.Service.RenewTerm(TermRequest("match/main", "control-1"), rig.Context());
        Assert.True(renewed.Found);
        Assert.Equal(1, renewed.Term);
    }

    [Fact]
    public async Task RenewTermByADisplacedHolderRefusesVisibly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-2"), rig.Context());
        await Rejects(() => rig.Service.RenewTerm(TermRequest("match/main", "control-1"), rig.Context()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task ReleaseTermParksTheLeaseInThePast()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        await rig.Service.ReleaseTerm(TermRequest("match/main", "control-1"), rig.Context());
        var term = await rig.Service.GetTerm(new GetTermRequest { PartitionKey = "match/main" }, rig.Context());
        Assert.True(term.Found);
        Assert.True(term.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ReleaseByANonHolderRefusesVisibly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await rig.Service.ClaimTerm(TermRequest("match/main", "control-1"), rig.Context());
        await Rejects(() => rig.Service.ReleaseTerm(TermRequest("match/main", "control-2"), rig.Context()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task GetTermWithoutARowAnswersNotFound()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        var term = await rig.Service.GetTerm(new GetTermRequest { PartitionKey = "match/none" }, rig.Context());
        Assert.False(term.Found);
    }

    [Fact]
    public async Task AnOutOfRangeLeaseIsInvalidArgument()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ClaimTerm(TermRequest("match/main", "control-1", leaseSeconds: 0), rig.Context()),
            StatusCode.InvalidArgument,
            "Specified argument was out of the range of valid values. (Parameter 'leaseSeconds')");
    }

    [Fact]
    public async Task AnUnnamedPartitionIsInvalidArgument()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var rig = New(database.ConnectionString);
        await Rejects(() => rig.Service.ClaimTerm(TermRequest("", "control-1"), rig.Context()),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'partitionKey')");
    }

    private static TermRequest TermRequest(string partitionKey, string holderInstanceId,
        int leaseSeconds = 30) => new()
    {
        PartitionKey = partitionKey, HolderInstanceId = holderInstanceId, LeaseSeconds = leaseSeconds
    };

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    private sealed record Rig(CoordinatorGrpcService Service, TestProcessCertificates Certificates)
    {
        public TestServerCallContext Context() =>
            Certificates.Context(Certificates.Issue("@cluster", "control-server", "control-1"));
    }

    private static Rig New(string connectionString)
    {
        var certificates = new TestProcessCertificates();
        var store = new RedesignCoordinatorStore(connectionString);
        return new Rig(new CoordinatorGrpcService(store, certificates.Trusted), certificates);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_terms_face_" + Guid.NewGuid().ToString("N");
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
