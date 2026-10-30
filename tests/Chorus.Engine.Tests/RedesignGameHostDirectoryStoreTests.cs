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

    [Fact]
    public async Task ADrainSurvivesOptimisticSelfReportsAndTheHostStaysListed()
    {
        // FIX-SCHED-02: the admin's Draining is the admission ratchet — a drained host that
        // keeps reporting cannot un-drain itself — and the drained host stays in the feed,
        // because its existing rooms still need the control plane to reach them.
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        var admin = new RedesignAdminStore(database.ConnectionString);
        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        var started = DateTime.UtcNow;
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server",
            "game-server", "game-dir-drain", 1, "Running", "Ready", "Healthy", "Accepting",
            "Steady", started, endpoint: "https://localhost:1001");

        Assert.Equal("Draining", await admin.DrainProcessAsync("game-dir-drain"));

        // The host's own reports keep coming and keep saying Running/Accepting: the report
        // is accepted (the process is alive and its evidence refreshes) but its admission
        // word loses to the ratchet.
        var reported = await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server",
            "game-server", "game-dir-drain", 2, "Running", "Ready", "Healthy", "Accepting",
            "Steady", started, endpoint: "https://localhost:1001");
        Assert.True(reported.Accepted);
        var entry = Assert.Single(await control.ListGameHostReportsAsync(60));
        Assert.Equal("game-dir-drain", entry.InstanceId);
        Assert.Equal("Ready", entry.Readiness);
        Assert.Equal("Draining", entry.Admission);
        // The duplicate-drain reply speaks the durable fact: admission, not the lifecycle
        // the host's own report flipped back.
        Assert.Equal("Draining", await admin.ReadProcessAdmissionAsync("game-dir-drain"));
    }

    [Fact]
    public async Task ADrainedOwnersRoomsKeepTheirLeasesRenewed()
    {
        // The other half of FIX-SCHED-02: drain stops NEW rooms, never the leases of the
        // rooms the host already holds — while a drained owner keeps reporting, the renewal
        // registrar still extends its room leases; silence, not drain, is what lets a lease
        // run out.
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        var admin = new RedesignAdminStore(database.ConnectionString);
        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        var started = DateTime.UtcNow;
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server",
            "game-server", "game-lease-1", 1, "Running", "Ready", "Healthy", "Accepting",
            "Steady", started, endpoint: "https://localhost:1001");
        var claimed = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room",
            "room-lease-a", "game-lease-1", 15);
        Assert.Equal("Active", claimed.Status);

        Assert.Equal("Draining", await admin.DrainProcessAsync("game-lease-1"));
        // The drained host keeps reporting — its rooms are still live on it — and between
        // the drain and its next report the row genuinely says lifecycle Draining: the
        // registrar must treat that owner as alive and renew anyway.
        Assert.Equal(1, await control.RenewLiveOwnershipsAsync(15, 15));
        var after = await control.GetOwnershipAsync("tenant-a", "game-a", "room", "room-lease-a");
        Assert.Equal("Active", after!.Status);
        Assert.True(after.LeaseUntilUtc > DateTime.UtcNow.AddSeconds(10),
            "The drained owner's room lease was not pushed a full lease ahead.");

        // The counterproof: a silent owner (no fresh report) loses its renewal — the lease
        // runs out and the entity becomes takeable. Drain was never the trigger; silence is.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var backdate = new NpgsqlCommand(
                "UPDATE control.process SET observed_at=clock_timestamp()-interval '120 seconds'",
                connection);
            await backdate.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await control.RenewLiveOwnershipsAsync(15, 15));
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
            await Chorus.Data.RedesignSchemaMigrator.ApplyAsync(migrate);
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
