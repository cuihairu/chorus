using Chorus.ControlServer;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>The store half of the dynamic game-host directory (FIX-SCHED-01): a restarted
/// incarnation takes its process row over — the new endpoint replaces the dead one — and the
/// directory feed lists exactly the game-server processes whose reports are still valid.</summary>
public sealed class RedesignGameHostDirectoryStoreTests
{
    [Fact]
    public async Task ARestartedIncarnationTakesItsRowOver()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        var firstStart = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        var live = await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server",
            "game-server", "game-dir-1", 7, "Running", "Ready", "Healthy", "Open", "Steady",
            firstStart, endpoint: "https://localhost:1001");
        Assert.True(live.Accepted);
        var restarted = await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server",
            "game-server", "game-dir-1", 1, "Running", "Ready", "Healthy", "Open", "Steady",
            firstStart + TimeSpan.FromMinutes(5), endpoint: "https://localhost:2002");
        // A restart under the same instance id resets the sequence but carries a later
        // started_at: the newer incarnation wins its own row — sequence, address and
        // incarnation clock all move to the live process.
        Assert.True(restarted.Accepted);
        var replayOfTheDead = await control.ReportProcessAsync("node-a", "agent-1",
            "node-a/game-server", "game-server", "game-dir-1", 99, "Exited", "NotReady",
            "Failed", "Draining", "NotStarted", firstStart, endpoint: "https://localhost:1001");
        Assert.False(replayOfTheDead.Accepted);
        var staleSequence = await control.ReportProcessAsync("node-a", "agent-1",
            "node-a/game-server", "game-server", "game-dir-1", 1, "Running", "Ready", "Healthy",
            "Open", "Steady", firstStart + TimeSpan.FromMinutes(5),
            endpoint: "https://localhost:2002");
        Assert.False(staleSequence.Accepted);
        var reports = await control.ListGameHostReportsAsync(60);
        var entry = Assert.Single(reports, report => report.InstanceId == "game-dir-1");
        Assert.Equal("https://localhost:2002", entry.Endpoint);
    }

    [Fact]
    public async Task TheFeedListsOnlyValidGameHostReports()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        var started = DateTime.UtcNow;
        // A live game host with an endpoint: directory material.
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server",
            "game-server", "game-dir-live", 1, "Running", "Ready", "Healthy", "Open", "Steady",
            started, endpoint: "https://localhost:1001");
        // Same shape, but the process has exited: not assignable.
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server#2",
            "game-server", "game-dir-exited", 1, "Exited", "NotReady", "Failed", "Draining",
            "NotStarted", started, endpoint: "https://localhost:2002");
        // A non-game role never enters the game-host directory.
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/streamer-server",
            "streamer-server", "streamer-dir-1", 1, "Running", "Ready", "Healthy", "Open",
            "Steady", started, endpoint: "https://localhost:3003");
        // A game process without an endpoint (an agent-bootstrap report): nothing to dial.
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server#3",
            "game-server", "game-dir-no-endpoint", 1, "Running", "Preparing", "Healthy", "Open",
            "Steady", started);
        var reports = await control.ListGameHostReportsAsync(60);
        var entry = Assert.Single(reports);
        Assert.Equal("game-dir-live", entry.InstanceId);
        Assert.Equal("https://localhost:1001", entry.Endpoint);
        Assert.Equal("Ready", entry.Readiness);
        Assert.Equal("Open", entry.Admission);

        // Staleness is the gate: once observed_at falls outside the window the host leaves
        // the feed — a dead process's last report stops counting as evidence.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var backdate = new NpgsqlCommand(
                "UPDATE control.process SET observed_at=clock_timestamp()-interval '120 seconds'",
                connection);
            await backdate.ExecuteNonQueryAsync();
        }
        Assert.Empty(await control.ListGameHostReportsAsync(60));
    }

    private static async Task<TemporaryDatabase> CreateDatabaseAsync()
    {
        var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
        var database = new TemporaryDatabase(source);
        await database.InitializeAsync();
        return database;
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_dir_store_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var builder = new NpgsqlConnectionStringBuilder(_admin.ConnectionString)
            { Database = _name, Pooling = false };
            ConnectionString = builder.ConnectionString;
            await using var migrate = new NpgsqlConnection(builder.ConnectionString);
            await migrate.OpenAsync();
            await Chorus.Data.Redesign.RedesignSchemaMigrator.ApplyAsync(migrate);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
