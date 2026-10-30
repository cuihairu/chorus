using System.Text.Json;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Protocol;
using Npgsql;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-PERF-02, the two experiments the acceptance asks for: starving the connection pool
/// pays at the connection open while the transaction body stays fast; blocking a statement
/// on a row lock pays inside the body while the open stays fast. Two different instruments
/// light up, so a slow store call never has to be reported as one blurred "database slow".
/// A private meter keeps parallel suites out of the samples.
/// </summary>
public sealed class RedesignDataDiagnosticsTests
{
    private const string TestMeter = "Chorus.Data.Tests";

    /// <summary>How long each experiment holds its victim resource.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(400);

    private readonly ITestOutputHelper _output;

    public RedesignDataDiagnosticsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PoolSaturationPaysAtTheConnectionOpenNotTheTransactionBody()
    {
        using var metrics = new DataMetrics(TestMeter);
        using var samples = new MeterSamples(TestMeter);
        var database = await TemporaryDatabase.CreateAsync();
        await using var _ = database;
        // A pool of exactly one connection: whoever holds it, the next open waits. Pooling
        // is explicit because the arrange's connection string was built with it off.
        var starved = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Pooling = true, MaxPoolSize = 1 }.ConnectionString;
        var receipts = new PostgresOperationReceiptStore(starved, metrics);

        for (var round = 1; round <= 3; round++)
        {
            await using var holder = new NpgsqlConnection(starved);
            await holder.OpenAsync(); // the pool's only slot
            var call = receipts.ExecuteAsync(
                new OperationIdentity("tenant-a", "game-a", "diag.pool", "target-1", $"op-{round}"),
                Fingerprint(),
                (_, _, _) => Task.FromResult("{}"));
            await Task.Delay(Hold);
            await holder.DisposeAsync(); // release the slot; the waiting open proceeds
            var receipt = await call;
            Assert.False(receipt.Duplicate);
        }

        var snap = samples.Snapshot();
        _output.WriteLine($"open {Describe(snap["data.conn.open"])} ms, " +
            $"work {Describe(snap["data.tx.work"])} ms, commit {Describe(snap["data.tx.commit"])} ms");
        Assert.Equal(3, snap["data.conn.open"].Length);
        Assert.Equal(3, snap["data.tx.work"].Length);
        Assert.Equal(3, snap["data.tx.commit"].Length);
        // Every open waited out the hold — structurally guaranteed, so per item.
        Assert.All(snap["data.conn.open"], value => Assert.True(value >= 300.0));
        // The body and commit stayed fast; the median tolerates a scheduler spike.
        Assert.True(Median(snap["data.tx.work"]) < 150.0, "the body median rose");
        Assert.True(Median(snap["data.tx.commit"]) < 150.0, "the commit median rose");
        Assert.Equal(0, samples.TaggedMeasurements);
    }

    [Fact]
    public async Task RowLockBlockingPaysInsideTheTransactionBodyNotTheOpen()
    {
        using var metrics = new DataMetrics(TestMeter);
        using var samples = new MeterSamples(TestMeter);
        var database = await TemporaryDatabase.CreateAsync();
        await using var _ = database;
        // The frame commit's first body statement takes the ownership row FOR UPDATE; a
        // holder transaction holding that lock parks the whole body behind it.
        var control = new RedesignControlStore(database.ConnectionString);
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-lock-1",
            "gs-1", 60);
        var frames = new RedesignFrameStore(database.ConnectionString, metrics: metrics);

        for (var round = 1; round <= 3; round++)
        {
            await using var holder = new NpgsqlConnection(database.ConnectionString);
            await holder.OpenAsync();
            await using var holderTx = await holder.BeginTransactionAsync();
            await using (var lockRow = new NpgsqlCommand("""
                SELECT 1 FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
                """, holder, holderTx))
            {
                lockRow.Parameters.AddWithValue("room", "room-lock-1");
                await lockRow.ExecuteScalarAsync();
            }
            var call = frames.CommitFrameAsync("tenant-a", "game-a", "room-lock-1",
                new FrameCommit($"commit-{round}", "gs-1", claim.Epoch, 0, 0, [], [], [],
                    "1.0.0", "cfg-1"));
            await Task.Delay(Hold);
            await holderTx.RollbackAsync(); // release; the commit then finds no game.room row
            await Assert.ThrowsAsync<FrameConflictException>(() => call);
        }

        var snap = samples.Snapshot();
        _output.WriteLine($"open {Describe(snap["data.conn.open"])} ms, " +
            $"work {Describe(snap["data.tx.work"])} ms");
        Assert.Equal(3, snap["data.conn.open"].Length);
        Assert.Equal(3, snap["data.tx.work"].Length);
        // Every body waited out the hold — structurally guaranteed, so per item.
        Assert.All(snap["data.tx.work"], value => Assert.True(value >= 300.0));
        // The opens stayed fast; the median tolerates a scheduler spike.
        Assert.True(Median(snap["data.conn.open"]) < 150.0, "the open median rose");
        // The blocked transactions never reached a commit.
        Assert.Equal(0, snap.TryGetValue("data.tx.commit", out var commits) ? commits.Length : 0);
        Assert.Equal(0, samples.TaggedMeasurements);
    }

    [Fact]
    public async Task AQuietOperationPaysEachPhaseExactlyOnce()
    {
        using var metrics = new DataMetrics(TestMeter);
        using var samples = new MeterSamples(TestMeter);
        var database = await TemporaryDatabase.CreateAsync();
        await using var _ = database;
        var receipts = new PostgresOperationReceiptStore(database.ConnectionString, metrics);

        var receipt = await receipts.ExecuteAsync(
            new OperationIdentity("tenant-a", "game-a", "diag.quiet", "target-1", "op-1"),
            Fingerprint(),
            (_, _, _) => Task.FromResult("{}"));

        Assert.False(receipt.Duplicate);
        var snap = samples.Snapshot();
        Assert.Equal(1, snap["data.conn.open"].Length);
        Assert.Equal(1, snap["data.tx.work"].Length);
        Assert.Equal(1, snap["data.tx.commit"].Length);
        Assert.Equal(0, samples.TaggedMeasurements);
    }

    private static OperationFingerprint Fingerprint() =>
        OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes("{}"));

    private static double Median(double[] values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static string Describe(double[] values) =>
        $"{values.Min():0.0}..{values.Max():0.0}";

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_diag_" + Guid.NewGuid().ToString("N");
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
