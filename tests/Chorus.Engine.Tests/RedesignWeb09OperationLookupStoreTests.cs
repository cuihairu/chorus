using Chorus.ControlServer;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The control-plane half of the operation result query (FIX-WEB-09): the audit row a
/// request id stands under, with the evidence the status answer hands back as its note.
/// A room command's status comes from the data plane (FIX-GM-07); every control-plane
/// command's status comes from here.
/// </summary>
public sealed class RedesignWeb09OperationLookupStoreTests
{
    [Fact]
    public async Task ALookupReturnsTheAuditedRowWithItsEvidence()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignAdminStore(database.ConnectionString);
        await store.WriteAuditAsync("web09-audit-1", "gm-alice", "incident web09",
            "partition test/web09", "SuspendMatchQueue",
            """{"partition":"test/web09","suspended":true}""");

        var row = await store.LookupOperationAsync("web09-audit-1");
        Assert.NotNull(row);
        Assert.Equal("web09-audit-1", row!.RequestId);
        Assert.Equal("gm-alice", row.Operator);
        Assert.Equal("incident web09", row.Reason);
        Assert.Equal("partition test/web09", row.Scope);
        Assert.Equal("SuspendMatchQueue", row.Command);
        Assert.Contains("test/web09", row.DetailJson);
    }

    [Fact]
    public async Task AnUnknownRequestIdHasNoAuditedOperation()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignAdminStore(database.ConnectionString);
        Assert.Null(await store.LookupOperationAsync("web09-none"));
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_web09_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
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
            await Chorus.Data.RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
