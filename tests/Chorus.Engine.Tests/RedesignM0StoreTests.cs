using System.Text;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Protocol;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignM0StoreTests
{
    [Fact]
    public async Task ProfileAssetAndReceipt_UseDatabaseVersionsAndIdempotentLedger()
    {
        await using var database = await CreateDatabaseAsync();
        var data = new RedesignPlayerStore(database.ConnectionString);
        var profileIdentity = new OperationIdentity("tenant-a", "game-a", "profile.patch", "player-a", "profile-1");
        var profileFingerprint = Fingerprint("profile-v1");
        var (profile, duplicate) = await data.PatchProfileAsync(profileIdentity, profileFingerprint, 0, 1, """{"name":"A"}""");
        Assert.False(duplicate);
        Assert.Equal(1, profile.Version);
        await Assert.ThrowsAsync<RedesignVersionConflictException>(() => data.PatchProfileAsync(
            new("tenant-a", "game-a", "profile.patch", "player-a", "profile-2"),
            Fingerprint("profile-v2"), 0, 1, """{"name":"B"}"""));

        var assetIdentity = new OperationIdentity("tenant-a", "game-a", "asset.change", "player-a", "grant-1");
        var assetFingerprint = Fingerprint("grant-ten");
        var (first, firstDuplicate) = await data.ChangeAssetAsync(assetIdentity, assetFingerprint, "coins", 10, "test");
        Assert.False(firstDuplicate);
        Assert.Equal(10, first.Balance);
        var original = await data.GetOperationAsync(assetIdentity, assetFingerprint);
        Assert.NotNull(original);
        var (retried, retryDuplicate) = await new RedesignPlayerStore(database.ConnectionString)
            .ChangeAssetAsync(assetIdentity, assetFingerprint, "coins", 10, "test");
        Assert.True(retryDuplicate);
        Assert.Equal(first, retried);
        Assert.Equal(10, (await data.GetAssetAsync("tenant-a", "game-a", "player-a", "coins")).Balance);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM player.ledger", connection);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task OwnershipTakeover_FencesOldDataWriteAndKeepsOnlyCommittedEffect()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        var data = new RedesignPlayerStore(database.ConnectionString);
        var first = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-a", "game-1", 15);
        Assert.Equal(1, first.Epoch);
        var oldOperation = new OperationIdentity("tenant-a", "game-a", "fenced.probe", "room-a", "tick-1");
        await data.CommitFencedProbeAsync(oldOperation, Fingerprint("tick-1"), "room", "room-a", "game-1", 1, "effect-1");
        var second = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-a", "game-2", 15);
        Assert.Equal(2, second.Epoch);
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => data.CommitFencedProbeAsync(
            new("tenant-a", "game-a", "fenced.probe", "room-a", "tick-2"),
            Fingerprint("tick-2"), "room", "room-a", "game-1", 1, "effect-2"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => data.CommitFencedProbeAsync(
            new("tenant-b", "game-a", "fenced.probe", "room-a", "tick-3"),
            Fingerprint("tick-3"), "room", "room-a", "game-2", 2, "effect-3"));
        Assert.Equal(1, await database.CountAsync("infra.outbox"));
    }

    [Fact]
    public async Task AgentRestart_RejectsOldAndOutOfOrderReports_WithoutConflatingRecovery()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        var started = DateTime.UtcNow;
        var ready = await control.ReportProcessAsync("node-a", "agent-1", "node-a/data-server", "data-server",
            "data-1", 1, "Running", "Ready", "Healthy", "Accepting", "RecoveryFailed", started);
        Assert.True(ready.Accepted);
        Assert.Equal("Ready", ready.Readiness);
        Assert.Equal("RecoveryFailed", ready.EntityRecovery);
        var oldSequence = await control.ReportProcessAsync("node-a", "agent-1", "node-a/data-server", "data-server",
            "data-1", 1, "Exited", "NotReady", "Failed", "Draining", "NotStarted", started);
        Assert.False(oldSequence.Accepted);
        await control.RegisterNodeAsync("node-a", "agent-2", "{}");
        var oldAgent = await control.ReportProcessAsync("node-a", "agent-1", "node-a/data-server", "data-server",
            "data-1", 2, "Exited", "NotReady", "Failed", "Draining", "NotStarted", started);
        Assert.False(oldAgent.Accepted);
        Assert.Equal(1, await database.CountAsync("control.process"));
    }

    private static OperationFingerprint Fingerprint(string value) =>
        OperationFingerprint.FromCanonicalRequest(1, Encoding.UTF8.GetBytes(value));

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
        private readonly string _name = "chorus_m0_store_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var database = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = database.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async Task<int> CountAsync(string table)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
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
