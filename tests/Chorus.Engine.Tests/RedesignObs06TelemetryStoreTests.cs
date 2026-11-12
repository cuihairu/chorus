using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine telemetry store (FIX-OBS-06): an accepted snapshot lands as one append-only
/// sample row with per-mount disk children, a stale agent's report is refused rather than
/// written under a displaced identity, and a non-positive report sequence is rejected.
/// </summary>
public sealed class RedesignObs06TelemetryStoreTests
{
    [Fact]
    public async Task AcceptedSnapshotLandsWithItsDiskChildren()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);

        var accepted = await store.ReportMachineTelemetryAsync(
            "node-a", "agent-1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1,
            42.5, (Total: 1024, Used: 512, Percent: 50.0), (L1: 1.5, L5: 1.25, L15: 1.0),
            [
                new RedesignControlStore.MachineDiskMount("/", 1000, 400, 40.0),
                new RedesignControlStore.MachineDiskMount("/data", 2000, 500, 25.0),
            ], [], CancellationToken.None);

        Assert.True(accepted);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var sample = new NpgsqlCommand("""
            SELECT node_id, agent_instance_id, cpu_percent, memory_total_bytes, memory_used_bytes,
                memory_used_percent, load_1m, load_5m, load_15m
            FROM ops.machine_sample
            """, connection))
        await using (var reader = await sample.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("node-a", reader.GetString(0));
            Assert.Equal("agent-1", reader.GetString(1));
            Assert.Equal(42.5, reader.GetDouble(2), 6);
            Assert.Equal(1024L, reader.GetInt64(3));
            Assert.Equal(512L, reader.GetInt64(4));
            Assert.Equal(50.0, reader.GetDouble(5), 6);
            Assert.Equal(1.5, reader.GetDouble(6), 6);
            Assert.Equal(1.25, reader.GetDouble(7), 6);
            Assert.Equal(1.0, reader.GetDouble(8), 6);
            Assert.False(await reader.ReadAsync()); // exactly one row
        }
        await using (var disk = new NpgsqlCommand("""
            SELECT mount, total_bytes, used_bytes, used_percent
            FROM ops.machine_disk_sample ORDER BY mount
            """, connection))
        await using (var reader = await disk.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("/", reader.GetString(0));
            Assert.Equal(1000L, reader.GetInt64(1));
            Assert.Equal(400L, reader.GetInt64(2));
            Assert.Equal(40.0, reader.GetDouble(3), 6);
            Assert.True(await reader.ReadAsync());
            Assert.Equal("/data", reader.GetString(0));
            Assert.Equal(25.0, reader.GetDouble(3), 6);
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task StaleAgentIsRefusedRatherThanWritten()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);

        // A different agent instance id speaks for node-a: the report is refused, not written.
        var accepted = await store.ReportMachineTelemetryAsync(
            "node-a", "agent-other", DateTime.UtcNow, 1,
            null, null, null, [], [], CancellationToken.None);

        Assert.False(accepted);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM ops.machine_sample", connection);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NonPositiveReportSequenceIsRejected()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ReportMachineTelemetryAsync("node-a", "agent-1", DateTime.UtcNow, 0,
                null, null, null, [], [], CancellationToken.None));
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs06_telemetry_" + Guid.NewGuid().ToString("N");
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
