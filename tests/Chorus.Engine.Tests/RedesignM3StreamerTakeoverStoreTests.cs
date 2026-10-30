using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Streamer-entity ownership handover (M3-7b, architecture 10.3): the transfer transaction moves
/// the holder and bumps the epoch under the named-from guard, parks the row Recovering so the
/// renewal registrar leaves it alone, and the new holder's own claim confirms it — same holder
/// with a live lease keeps the epoch and returns the row Active.
/// </summary>
public sealed class RedesignM3StreamerTakeoverStoreTests
{
    [Fact]
    public async Task TransferBumpsEpochUnderTheNamedHolderAndTheNewClaimConfirms()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        var claimed = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-a", 15);
        Assert.Equal(1, claimed.Epoch);
        Assert.Equal("Active", claimed.Status);

        // A stale command names the wrong holder; the entity that changed hands stays put.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.TransferOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-1", "streamer-b", "streamer-b", 15));
        // A scope that does not match the row is a visible refusal, not a silent second entity.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.TransferOwnershipAsync(
            "tenant-a", "game-b", "streamer", "streamer-open-1", "streamer-a", "streamer-b", 15));

        var moved = await control.TransferOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-a", "streamer-b", 15);
        Assert.Equal("streamer-b", moved.OwnerInstanceId);
        Assert.Equal(2, moved.Epoch);
        Assert.Equal("Recovering", moved.Status);

        // A second transfer before the new holder confirms refuses: the row is not Active.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.TransferOwnershipAsync(
            "tenant-a", "game-a", "streamer", "streamer-open-1", "streamer-b", "streamer-c", 15));

        // The new holder's claim — same holder, live lease — keeps the epoch and confirms Active.
        var confirmed = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-b", 15);
        Assert.Equal("streamer-b", confirmed.OwnerInstanceId);
        Assert.Equal(2, confirmed.Epoch);
        Assert.Equal("Active", confirmed.Status);

        // A late claim from the displaced holder steals visibly: the epoch moves again, because
        // the session domain — not this row — decides who may speak for a streamer entity.
        var stolen = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-1", "streamer-a", 15);
        Assert.Equal("streamer-a", stolen.OwnerInstanceId);
        Assert.Equal(3, stolen.Epoch);
        Assert.Equal("Active", stolen.Status);
    }

    [Fact]
    public async Task RecoveringOwnershipIsNotRenewedEvenWhileTheHolderReportsLive()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/streamer-server", "streamer-server",
            "streamer-a", 1, "Running", "Ready", "Healthy", "Accepting", "Steady", DateTime.UtcNow,
            "https://localhost:7502");
        var active = await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-1",
            "streamer-a", 6);
        await control.ClaimOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-2",
            "streamer-a", 6);
        var moved = await control.TransferOwnershipAsync("tenant-a", "game-a", "streamer",
            "streamer-open-2", "streamer-a", "streamer-b", 6);

        // The registrar renews only Active ownerships: the Recovering row keeps the lease the
        // handover wrote, so an uncompleted takeover expires on its own and becomes takeable.
        var renewed = await control.RenewLiveOwnershipsAsync(6, 15);
        var open1 = await control.GetOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-1");
        var open2 = await control.GetOwnershipAsync("tenant-a", "game-a", "streamer", "streamer-open-2");
        Assert.True(renewed >= 1);
        Assert.True(open1!.LeaseUntilUtc > active.LeaseUntilUtc);
        Assert.Equal(moved.LeaseUntilUtc, open2!.LeaseUntilUtc);
        Assert.Equal("Recovering", open2.Status);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m3_streamer_" + Guid.NewGuid().ToString("N");
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
