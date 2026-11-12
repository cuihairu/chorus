using Chorus.Control;
using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Process telemetry storage and the statistics face (FIX-OBS-07): a report's per-process
/// samples land beside the disk rows under the same sample, the stats store answers the
/// latest sample per node in the window (its disk rows, sample count), the latest process
/// reading per supervised process with the derived `processes_running` (a zero-process
/// sample answers 0, not absence), and outside-the-window or non-PostgreSQL stores answer
/// empty rather than failing.
/// </summary>
public sealed class RedesignObs07TelemetryStoreTests
{
    [Fact]
    public async Task ProcessRowsLandBesideTheDiskRowsUnderOneSample()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);

        var accepted = await store.ReportMachineTelemetryAsync(
            "node-a", "agent-1", DateTime.UtcNow, 1,
            null, null, null,
            [new RedesignControlStore.MachineDiskMount("/", 1000, 400, 40.0)],
            [
                new RedesignControlStore.MachineProcessSample("slot-1", "gateway", 12.5, 2048L * 1024, 150),
                new RedesignControlStore.MachineProcessSample("slot-2", "game-server", 0, 4096, 60),
            ], CancellationToken.None);

        Assert.True(accepted);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT s.process_instance_id, s.role, s.cpu_percent, s.memory_used_bytes, s.uptime_seconds,
                s.machine_sample_id
            FROM ops.process_sample s
            JOIN ops.machine_sample m ON m.id = s.machine_sample_id
            WHERE m.node_id = 'node-a'
            ORDER BY s.process_instance_id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("slot-1", reader.GetString(0));
        Assert.Equal("gateway", reader.GetString(1));
        Assert.Equal(12.5, reader.GetDouble(2), 6);
        Assert.Equal(2048L * 1024, reader.GetInt64(3));
        Assert.Equal(150L, reader.GetInt64(4));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("slot-2", reader.GetString(0));
        Assert.Equal("game-server", reader.GetString(1));
        Assert.False(await reader.ReadAsync()); // exactly two rows under the one sample
    }

    [Fact]
    public async Task MachineStatsAnswerTheLatestSamplePerNodeWithItsSampleCount()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);
        await store.ReportMachineTelemetryAsync("node-a", "agent-1", DateTime.UtcNow, 1,
            10.0, null, null, [new("/", 100, 10, 10.0)], [], CancellationToken.None);
        await store.ReportMachineTelemetryAsync("node-a", "agent-1", DateTime.UtcNow, 2,
            42.5, (Total: 1024, Used: 512, Percent: 50.0), (L1: 1.5, L5: 1.25, L15: 1.0),
            [new("/data", 2000, 500, 25.0)],
            [new("slot-1", "gateway", 12.5, 2048L * 1024, 150)], CancellationToken.None);

        var stats = new TelemetryStore(database.ConnectionString, ControlDatabaseProvider.PostgreSql);
        var machines = stats.MachineStats("node-a", 60);

        var node = Assert.Single(machines);
        Assert.Equal("node-a", node.NodeId);
        Assert.Equal(2, node.ReportSequence); // the latest sample, not the first
        Assert.Equal(42.5, node.CpuPercent.Value, 6);
        Assert.Equal(50.0, node.Memory!.UsedPercent, 6);
        Assert.Equal(1.5, node.Load!.L1, 6);
        Assert.Equal("/data", Assert.Single(node.Disk).Mount); // only the latest sample's disk rows
        Assert.Equal(2, node.Samples); // both samples count in the window
    }

    [Fact]
    public async Task ProcessStatsAnswerTheLatestReadingPerProcess()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);
        await store.ReportMachineTelemetryAsync("node-a", "agent-1", DateTime.UtcNow, 1,
            null, null, null, [],
            [
                new("slot-1", "gateway", 1.0, 1024, 10),
                new("slot-2", "gateway", 2.0, 2048, 20),
            ], CancellationToken.None);
        // The second sample loses slot-2 (exited): the latest reading per process is slot-1
        // only, and processes_running follows the latest sample, not the union.
        await store.ReportMachineTelemetryAsync("node-a", "agent-1", DateTime.UtcNow, 2,
            null, null, null, [],
            [new("slot-1", "gateway", 12.5, 2048L * 1024, 150)], CancellationToken.None);

        var stats = new TelemetryStore(database.ConnectionString, ControlDatabaseProvider.PostgreSql);
        var nodes = stats.ProcessStats("node-a", 60);

        var node = Assert.Single(nodes);
        Assert.Equal("node-a", node.NodeId);
        Assert.Equal(1, node.ProcessesRunning);
        var entry = Assert.Single(node.Processes);
        Assert.Equal("slot-1", entry.ProcessInstanceId);
        Assert.Equal(12.5, entry.CpuPercent, 6);
        Assert.Equal(150, entry.UptimeSeconds);
    }

    [Fact]
    public async Task AZeroProcessSampleAnswersRunningZeroNotAbsence()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);
        await store.ReportMachineTelemetryAsync("node-a", "agent-1", DateTime.UtcNow, 1,
            null, null, null, [], [], CancellationToken.None);

        var stats = new TelemetryStore(database.ConnectionString, ControlDatabaseProvider.PostgreSql);
        var node = Assert.Single(stats.ProcessStats("node-a", 60));
        Assert.Equal(0, node.ProcessesRunning);
        Assert.Empty(node.Processes);
    }

    [Fact]
    public async Task OutsideTheWindowAnswersEmpty()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignControlStore(database.ConnectionString);
        await store.RegisterNodeAsync("node-a", "agent-1", "{}", CancellationToken.None);
        await store.ReportMachineTelemetryAsync("node-a", "agent-1",
            DateTime.UtcNow.AddHours(-2), 1, null, null, null, [],
            [new("slot-1", "gateway", 1.0, 1024, 10)], CancellationToken.None);

        var stats = new TelemetryStore(database.ConnectionString, ControlDatabaseProvider.PostgreSql);
        Assert.Empty(stats.MachineStats("node-a", 60));
        Assert.Empty(stats.ProcessStats("node-a", 60));
    }

    [Fact]
    public void ANonPostgreSqlStoreAnswersEmptyWithoutTouchingTheServer()
    {
        // Sqlite development has no ops tables: no telemetry is a stat, not an error.
        var stats = new TelemetryStore("data/control.db", ControlDatabaseProvider.Sqlite);
        Assert.Empty(stats.MachineStats(null, 60));
        Assert.Empty(stats.ProcessStats(null, 60));
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs07_telemetry_" + Guid.NewGuid().ToString("N");
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
