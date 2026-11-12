using Chorus.Control;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-OBS-15's threshold words: the alert answer is computed on read from the statistics
/// faces — a machine sample over the resource limits breaches, a node quiet past the
/// quiet threshold is judged quiet and its stale readings are not judged, an ingress
/// refusal family's windowed delta breaches, failing or backlogged outbox effects breach
/// the durable link words, a platform API endpoint with failures breaches, and a
/// non-PostgreSQL control database answers no breaches at all.
/// </summary>
public sealed class RedesignObs15OpsAlertsTests
{
    private static readonly OpsAlertThresholds Defaults = new(90, 90, 5, 1, 0, 1000, 1);

    [Fact]
    public async Task AMachineSampleAboveTheResourceThresholdsBreaches()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedMachineAsync(temp.ConnectionString, "n1", now.AddMinutes(-1), cpuPercent: 95.5, memoryPercent: 91.0);

        var alerts = AlertsStore(temp.ConnectionString, Defaults).Active();
        Assert.Equal(
            ["machine-cpu", "machine-memory"],
            alerts.Select(a => a.Kind).ToList());
        var cpu = Assert.Single(alerts, a => a.Kind == "machine-cpu");
        Assert.Equal("n1", cpu.Subject);
        Assert.Equal(95.5, cpu.Observed);
        Assert.Equal(90, cpu.Threshold);
        Assert.Equal(90, Assert.Single(alerts, a => a.Kind == "machine-memory").Threshold);
    }

    [Fact]
    public async Task AQuietNodeIsJudgedQuietAndItsStaleReadingsAreNot()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        // The last sample sits past the quiet threshold carrying a breaching cpu — the
        // quiet word fires and the stale reading is not judged as a live breach.
        await SeedMachineAsync(temp.ConnectionString, "n1", now.AddMinutes(-10), cpuPercent: 99.0, memoryPercent: 10.0);

        var alerts = AlertsStore(temp.ConnectionString, Defaults).Active();
        var quiet = Assert.Single(alerts);
        Assert.Equal("machine-quiet", quiet.Kind);
        Assert.Equal("n1", quiet.Subject);
        Assert.InRange(quiet.Observed, 9.9, 11);
        Assert.Equal(5, quiet.Threshold);
    }

    [Fact]
    public async Task AHealthyFleetAnswersNoBreaches()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedMachineAsync(temp.ConnectionString, "n1", now.AddMinutes(-1), cpuPercent: 50, memoryPercent: 50);
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", IngressStatsStore.AcceptedFamily, 10, now.AddMinutes(-4));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", $"{PlatformApiStatsStore.CallsFamily}:simulator.execute", 4, now.AddSeconds(-2));
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id, kind, payload, status, attempt_count)
                VALUES ('e1', 'k', '{}', 'Done', 1)
                """, connection);
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Empty(AlertsStore(temp.ConnectionString, Defaults).Active());
    }

    [Fact]
    public async Task AnIngressRefusalDeltaInTheWindowBreaches()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        // duplicates straddles into a +3 window delta; rate limited nets to zero across
        // the window (its baseline row lets the first in-window step cancel) and is not
        // a breach — a baseline-less first row would count from zero (OBS-13 rule).
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", IngressStatsStore.DuplicatesFamily, 5, now.AddMinutes(-70));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", IngressStatsStore.DuplicatesFamily, 8, now.AddMinutes(-4));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", IngressStatsStore.RateLimitedFamily, 1, now.AddMinutes(-70));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", IngressStatsStore.RateLimitedFamily, 1, now.AddMinutes(-30));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", IngressStatsStore.RateLimitedFamily, 1, now.AddMinutes(-4));

        var alerts = AlertsStore(temp.ConnectionString, Defaults).Active();
        var rejection = Assert.Single(alerts);
        Assert.Equal("ingress-rejections", rejection.Kind);
        Assert.Equal("duplicates", rejection.Subject);
        Assert.Equal(3, rejection.Observed);
        Assert.Equal(1, rejection.Threshold);
    }

    [Fact]
    public async Task FailingOutboxEffectsAndABacklogBreachesTheLink()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        await using (var connection = new NpgsqlConnection(temp.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id, kind, payload, status, attempt_count)
                VALUES ('e1', 'k', '{}', 'Done', 1), ('e2', 'k', '{}', 'Pending', 2), ('e3', 'k', '{}', 'Pending', 3)
                """, connection);
            await insert.ExecuteNonQueryAsync();
        }

        // Failed is derived (attempts minus done = 6 - 1 = 5); the pending floor rides low
        // in this test so the backlog word answers alongside the failure word.
        var thresholds = Defaults with { EffectsPending = 1 };
        var alerts = AlertsStore(temp.ConnectionString, thresholds).Active();
        Assert.Equal(
            ["effects-failed", "effects-pending"],
            alerts.Select(a => a.Kind).ToList());
        Assert.Equal(5, Assert.Single(alerts, a => a.Kind == "effects-failed").Observed);
        Assert.Equal(2, Assert.Single(alerts, a => a.Kind == "effects-pending").Observed);
    }

    [Fact]
    public async Task APlatformApiEndpointWithFailuresBreaches()
    {
        await using var temp = await TemporaryDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var execute = ":simulator.execute";
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", PlatformApiStatsStore.CallsFamily + execute, 10, now.AddSeconds(-2));
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", PlatformApiStatsStore.FailuresFamily + execute, 2, now.AddSeconds(-2));
        // A clean endpoint stays silent.
        await SeedCounterAsync(temp.ConnectionString, "n1", "gw-a", PlatformApiStatsStore.CallsFamily + ":simulator.status", 3, now.AddSeconds(-2));

        var alerts = AlertsStore(temp.ConnectionString, Defaults).Active();
        var failure = Assert.Single(alerts);
        Assert.Equal("platform-api-failures", failure.Kind);
        Assert.Equal("simulator.execute", failure.Subject);
        Assert.Equal(2, failure.Observed);
        Assert.Equal(1, failure.Threshold);
    }

    [Fact]
    public void ASqliteControlDatabaseAnswersNoBreaches()
    {
        var store = new OpsAlertsStore(
            new TelemetryStore("data/obs15-sqlite.db", ControlDatabaseProvider.Sqlite),
            new OpsWindowStore("data/obs15-sqlite.db", ControlDatabaseProvider.Sqlite),
            new DeliveryStatsStore("data/obs15-sqlite.db", ControlDatabaseProvider.Sqlite),
            new PlatformApiStatsStore("data/obs15-sqlite.db", ControlDatabaseProvider.Sqlite),
            Defaults);
        Assert.Empty(store.Active());
    }

    private static OpsAlertsStore AlertsStore(string connectionString, OpsAlertThresholds thresholds) => new(
        new TelemetryStore(connectionString, ControlDatabaseProvider.PostgreSql),
        new OpsWindowStore(connectionString, ControlDatabaseProvider.PostgreSql),
        new DeliveryStatsStore(connectionString, ControlDatabaseProvider.PostgreSql),
        new PlatformApiStatsStore(connectionString, ControlDatabaseProvider.PostgreSql),
        thresholds);

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

    private static async Task SeedMachineAsync(string connectionString, string node, DateTimeOffset sampledAt,
        double cpuPercent, double memoryPercent)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO ops.machine_sample(node_id, agent_instance_id, sampled_at, report_sequence,
                cpu_percent, memory_total_bytes, memory_used_bytes, memory_used_percent)
            VALUES (@node, @agent, @at, 1, @cpu, 16000000000, 0, @memory)
            """, connection);
        insert.Parameters.AddWithValue("node", node);
        insert.Parameters.AddWithValue("agent", "agent-1");
        insert.Parameters.AddWithValue("at", sampledAt.UtcDateTime);
        insert.Parameters.AddWithValue("cpu", cpuPercent);
        insert.Parameters.AddWithValue("memory", memoryPercent);
        await insert.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_obs15_alerts_" + Guid.NewGuid().ToString("N");
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
            await using var migrated = new NpgsqlConnection(ConnectionString);
            await migrated.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(migrated);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
