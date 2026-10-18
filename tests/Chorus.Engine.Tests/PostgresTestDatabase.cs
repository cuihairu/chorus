using Npgsql;

namespace Chorus.Engine.Tests;

internal sealed class PostgresTestDatabase : IDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _schema;

    public PostgresTestDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to an isolated PostgreSQL test database.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        _adminConnectionString = builder.ConnectionString;
        _schema = "chorus_test_" + Guid.NewGuid().ToString("N");
        using var connection = new NpgsqlConnection(_adminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", connection);
        command.ExecuteNonQuery();
        builder.SearchPath = _schema;
        ConnectionString = builder.ConnectionString;
    }

    public string ConnectionString { get; }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_adminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        command.ExecuteNonQuery();
    }
}
