using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Coordinator terms (M3-1, architecture 7.1/10.1): one holder per partition, terms only
/// increase, a lost lease refuses renewal visibly, and an expired lease grants even the same
/// holder a new term — a partitioned coordinator never quietly keeps serving.
/// </summary>
public sealed class RedesignCoordinatorStoreTests
{
    [Fact]
    public async Task TermsAreMonotonicAcrossHoldersAndReleases()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);

        Assert.Null(await coordinator.GetTermAsync("match/main"));

        var first = await coordinator.ClaimTermAsync("match/main", "control-1", 30);
        Assert.Equal(1, first.Term);
        Assert.Equal("control-1", first.HolderInstanceId);

        // The same holder re-claiming a live lease keeps the term and extends the lease.
        var reclaim = await coordinator.ClaimTermAsync("match/main", "control-1", 30);
        Assert.Equal(1, reclaim.Term);
        Assert.True(reclaim.LeaseUntilUtc >= first.LeaseUntilUtc);
        var renewed = await coordinator.RenewTermAsync("match/main", "control-1", 30);
        Assert.Equal(1, renewed.Term);

        // A different holder takes the partition even while the lease is live: new term.
        var takeover = await coordinator.ClaimTermAsync("match/main", "control-2", 30);
        Assert.Equal(2, takeover.Term);
        Assert.Equal("control-2", takeover.HolderInstanceId);

        // The displaced holder cannot renew or release.
        await Assert.ThrowsAsync<RedesignTermLostException>(
            () => coordinator.RenewTermAsync("match/main", "control-1", 30));
        await Assert.ThrowsAsync<RedesignTermLostException>(
            () => coordinator.ReleaseTermAsync("match/main", "control-1"));

        // Release parks the lease in the past; the next claim — by anyone — starts a new term.
        await coordinator.ReleaseTermAsync("match/main", "control-2");
        var released = await coordinator.GetTermAsync("match/main");
        Assert.NotNull(released);
        Assert.True(released!.LeaseUntilUtc <= DateTime.UtcNow);
        var next = await coordinator.ClaimTermAsync("match/main", "control-1", 30);
        Assert.Equal(3, next.Term);

        // Invalid leases refuse.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.ClaimTermAsync("match/main", "control-1", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.ClaimTermAsync("match/main", "control-1", 61));

        // Partitions are independent.
        var other = await coordinator.ClaimTermAsync("match/other", "control-1", 30);
        Assert.Equal(1, other.Term);
    }

    [Fact]
    public async Task AnExpiredLeaseGrantsANewTermEvenToTheSameHolder()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);

        var first = await coordinator.ClaimTermAsync("match/main", "control-1", 1);
        Assert.Equal(1, first.Term);
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // The lease expired: the same holder re-claiming starts a new term, not a silent
        // extension — anything the old term still tries to commit must be refusable.
        var reclaimer = await coordinator.ClaimTermAsync("match/main", "control-1", 30);
        Assert.Equal(2, reclaimer.Term);
        await Assert.ThrowsAsync<RedesignTermLostException>(
            () => coordinator.RenewTermAsync("match/missing", "control-1", 30));

        var status = await coordinator.GetTermAsync("match/main");
        Assert.NotNull(status);
        Assert.Equal(2, status!.Term);
        Assert.Equal("control-1", status.HolderInstanceId);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m3_terms_" + Guid.NewGuid().ToString("N");
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
