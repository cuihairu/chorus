using Chorus.Control;
using Chorus.Data;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-OBS-13's window over the ops sample history: a family's windowed delta is the sum
/// of its reporters' per-step increments inside the window — seeded with each reporter's
/// latest pre-window row so a straddling step counts once, and a drop between steps (a
/// restart) counts the post-restart value from zero. Composite families roll up under
/// their base name; a reporter that stopped mid-window still counts its in-window steps;
/// minutes without samples stay absent from the series; retention pruning drops what is
/// older than the cutoff across the ops sample tables; a non-PostgreSQL control database
/// answers empty.
/// </summary>
public sealed class RedesignObs13OpsWindowTests
{
    private static readonly string Accepted = Rpc.OpsMetrics.IngressAcceptedTotal.Name;

    [Fact]
    public async Task WindowDeltasCountOnlyStepsInsideTheWindowAndSurviveRestarts()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        // A: a continuous reporter straddling the window start — only the in-window step counts.
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 100, now.AddMinutes(-70));
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 105, now.AddMinutes(-5));
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 112, now.AddMinutes(-4));
        // B: rows only before the window — nothing to count.
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-b", Accepted, 40, now.AddMinutes(-70));
        // C: first-ever rows inside the window — counts from zero.
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-c", Accepted, 7, now.AddMinutes(-4));
        // D: a restart — the cumulative value fell between steps; the post-restart value counts from zero.
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-d", Accepted, 50, now.AddMinutes(-70));
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-d", Accepted, 30, now.AddMinutes(-4));

        var stats = StatsStore(temp.ConnectionString).Stats(60);
        Assert.Equal(60, stats.WindowMinutes);
        Assert.Single(stats.Families);
        Assert.Equal(Accepted, stats.Families[0].Family);
        Assert.Equal(12 + 7 + 30, stats.Families[0].Delta);
    }

    [Fact]
    public async Task CompositeFamiliesAggregateUnderTheirBaseNames()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var execute = $"{Rpc.OpsMetrics.PlatformApiCallsTotal.Name}:simulator.execute";
        var status = $"{Rpc.OpsMetrics.PlatformApiCallsTotal.Name}:simulator.status";
        var bucket = $"{Rpc.OpsMetrics.PlatformApiLatencyMs.Name}:simulator.execute:le=50";
        var inf = $"{Rpc.OpsMetrics.PlatformApiLatencyMs.Name}:simulator.execute:le=+Inf";
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", execute, 5, now.AddMinutes(-4));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", status, 3, now.AddMinutes(-4));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", bucket, 2, now.AddMinutes(-4));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", inf, 4, now.AddMinutes(-4));

        var stats = StatsStore(temp.ConnectionString).Stats(60);
        var families = stats.Families.ToDictionary(f => f.Family, f => f.Delta);
        Assert.Equal(8, families[Rpc.OpsMetrics.PlatformApiCallsTotal.Name]);
        Assert.Equal(6, families[Rpc.OpsMetrics.PlatformApiLatencyMs.Name]);
        Assert.Equal([Rpc.OpsMetrics.PlatformApiCallsTotal.Name, Rpc.OpsMetrics.PlatformApiLatencyMs.Name],
            stats.Families.Select(f => f.Family).ToList());
    }

    [Fact]
    public async Task SeriesPointsBucketByMinuteAndAbsentMinutesStayAbsent()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        // Anchored inside one minute bucket, so the first two rows share a bucket whatever
        // the wall clock's phase within the minute.
        var early = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 60 * 60)
            .AddMinutes(-5).AddSeconds(10);
        var late = now.AddMinutes(-2);
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 10, early);
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 25, early.AddSeconds(20));
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 31, late);

        var stats = StatsStore(temp.ConnectionString).Stats(60);
        var series = Assert.Single(stats.Series);
        Assert.Equal(Accepted, series.Family);
        var points = series.Points;
        Assert.Equal(2, points.Count);
        Assert.Equal(early.ToUnixTimeSeconds() / 60 * 60, points[0].MinuteUnix);
        // The reporter has no pre-window baseline, so its first sample counts from zero —
        // the same rule a mid-window restart follows.
        Assert.Equal(25, points[0].Delta);
        Assert.Equal(late.ToUnixTimeSeconds() / 60 * 60, points[1].MinuteUnix);
        Assert.Equal(6, points[1].Delta);
        Assert.True(points[1].MinuteUnix - points[0].MinuteUnix > 60, "The two sampled minutes are distinct buckets.");
    }

    [Fact]
    public async Task AReporterThatStoppedMidWindowStillCountsItsInWindowSteps()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        // The live totals drop a reporter that stopped 15s ago; the window counts what its
        // steps inside the window actually added, whenever it stopped after them.
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-gone", Accepted, 3, now.AddMinutes(-50));
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-gone", Accepted, 9, now.AddMinutes(-45));

        var stats = StatsStore(temp.ConnectionString).Stats(60);
        Assert.Equal(9, Assert.Single(stats.Families).Delta);
    }

    [Fact]
    public async Task PruneSamplesDropsRowsOlderThanTheCutoffAcrossTheOpsTables()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 1, now.AddDays(-8));
        await SeedCounterAsync(temp.ConnectionString, "n1", "rep-a", Accepted, 2, now.AddHours(-1));
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var sampledAt in new[] { now.AddDays(-8), now.AddHours(-1) })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO ops.machine_sample(node_id, agent_instance_id, sampled_at, report_sequence)
                    VALUES (@node, @agent, @at, 1)
                    """, connection);
                insert.Parameters.AddWithValue("node", "n1");
                insert.Parameters.AddWithValue("agent", "agent-1");
                insert.Parameters.AddWithValue("at", sampledAt.UtcDateTime);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var removed = StatsStore(temp.ConnectionString).PruneSamples(now.AddDays(-7));
        Assert.Equal(2, removed);
        Assert.Equal(1, await CountAsync(temp.ConnectionString, "ops.app_counter_sample"));
        Assert.Equal(1, await CountAsync(temp.ConnectionString, "ops.machine_sample"));
    }

    [Fact]
    public void ASqliteControlDatabaseAnswersEmptyAndPrunesNothing()
    {
        var store = new OpsWindowStore("data/obs13-sqlite.db", ControlDatabaseProvider.Sqlite);
        var stats = store.Stats(60);
        Assert.Empty(stats.Families);
        Assert.Empty(stats.Series);
        Assert.Equal(0, store.PruneSamples(DateTimeOffset.UtcNow));
    }

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)count.ExecuteScalar()!;
    }

    private static OpsWindowStore StatsStore(string connectionString) =>
        new(connectionString, ControlDatabaseProvider.PostgreSql);

    private static async Task SeedCounterAsync(string connectionString, string node, string instance,
        string family, long value, DateTimeOffset reportedAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO ops.app_counter_sample(node_id, process_instance_id, family, value, reported_at)
            VALUES (@node, @instance, @family, @value, @reported)
            """, connection);
        insert.Parameters.AddWithValue("node", node);
        insert.Parameters.AddWithValue("instance", instance);
        insert.Parameters.AddWithValue("family", family);
        insert.Parameters.AddWithValue("value", value);
        insert.Parameters.AddWithValue("reported", reportedAt.UtcDateTime);
        await insert.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs13_window_" + Guid.NewGuid().ToString("N");
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
