using System.Text;
using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignReceiptStoreTests
{
    [Fact]
    public async Task ConcurrentRetry_AppliesEffectOnceAndReturnsOriginalReceipt()
    {
        using var database = new IsolatedReceiptDatabase();
        await database.MigrateAsync();
        var store = new PostgresOperationReceiptStore(database.ConnectionString);
        var identity = NewIdentity();
        var fingerprint = Fingerprint("grant:10");
        var attempts = Enumerable.Range(0, 12).Select(_ => store.ExecuteAsync(
            identity, fingerprint, async (connection, transaction, cancellationToken) =>
            {
                await using var command = new NpgsqlCommand("INSERT INTO test_effect (id) VALUES ('effect-1')", connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return """{"balance":10}""";
            }));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => !result.Duplicate);
        Assert.All(results, result => Assert.Equal(results[0].ResultJson, result.ResultJson));
        using (var json = JsonDocument.Parse(results[0].ResultJson))
            Assert.Equal(10, json.RootElement.GetProperty("balance").GetInt32());
        Assert.Equal(1, await database.EffectCountAsync());
        Assert.True((await new PostgresOperationReceiptStore(database.ConnectionString)
            .GetAsync(identity, fingerprint))?.Duplicate);
    }

    [Fact]
    public async Task ChangedRequest_ConflictsWithoutApplyingAnotherEffect()
    {
        using var database = new IsolatedReceiptDatabase();
        await database.MigrateAsync();
        var store = new PostgresOperationReceiptStore(database.ConnectionString);
        var identity = NewIdentity();
        await store.ExecuteAsync(identity, Fingerprint("grant:10"), InsertEffect);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.ExecuteAsync(identity, Fingerprint("grant:11"), InsertEffect));
        Assert.Equal(1, await database.EffectCountAsync());
    }

    [Fact]
    public async Task FailedEffect_RollsBackClaimAndAllowsSameOperationToRetry()
    {
        using var database = new IsolatedReceiptDatabase();
        await database.MigrateAsync();
        var store = new PostgresOperationReceiptStore(database.ConnectionString);
        var identity = NewIdentity();
        var fingerprint = Fingerprint("grant:10");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync(identity, fingerprint,
            async (connection, transaction, cancellationToken) =>
            {
                await InsertEffect(connection, transaction, cancellationToken);
                throw new InvalidOperationException("fail after write");
            }));
        Assert.Null(await store.GetAsync(identity, fingerprint));
        Assert.Equal(0, await database.EffectCountAsync());

        var receipt = await store.ExecuteAsync(identity, fingerprint, InsertEffect);
        Assert.False(receipt.Duplicate);
        Assert.Equal(1, await database.EffectCountAsync());
    }

    private static OperationIdentity NewIdentity() => new("tenant-1", "game-1", "grant", "player-1", "operation-1");
    private static OperationFingerprint Fingerprint(string request) =>
        OperationFingerprint.FromCanonicalRequest(1, Encoding.UTF8.GetBytes(request));

    private static async Task<string> InsertEffect(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO test_effect (id) VALUES ('effect-1')", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return """{"balance":10}""";
    }

    private sealed class IsolatedReceiptDatabase : IDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName = "chorus_receipt_test_" + Guid.NewGuid().ToString("N");

        public IsolatedReceiptDatabase()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB for receipt integration tests.");
            var builder = new NpgsqlConnectionStringBuilder(source) { Pooling = false };
            _adminConnectionString = builder.ConnectionString;
            using (var admin = new NpgsqlConnection(_adminConnectionString))
            {
                admin.Open();
                using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                create.ExecuteNonQuery();
            }
            builder.Database = _databaseName;
            ConnectionString = builder.ConnectionString;
        }

        public string ConnectionString { get; }

        public async Task MigrateAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await OperationReceiptMigration.ApplyAsync(connection);
            await using var command = new NpgsqlCommand("CREATE TABLE test_effect (id text PRIMARY KEY)", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> EffectCountAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM test_effect", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public void Dispose()
        {
            using var admin = new NpgsqlConnection(_adminConnectionString);
            admin.Open();
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            drop.ExecuteNonQuery();
        }
    }
}
