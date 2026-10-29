using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Chorus.Data.Redesign;

/// <summary>Applies target-architecture PostgreSQL migrations explicitly, before service startup.</summary>
public static class RedesignSchemaMigrator
{
    private static readonly (int Version, string Resource)[] Steps =
    [
        (1, "Chorus.Data.Redesign.0001_operation_receipt.sql"),
        (2, "Chorus.Data.Redesign.0002_core_schema.sql"),
        (3, "Chorus.Data.Redesign.0003_ledger_target_scope.sql"),
        (4, "Chorus.Data.Redesign.0004_ownership_scope.sql"),
        (5, "Chorus.Data.Redesign.0005_session_connection.sql"),
        (6, "Chorus.Data.Redesign.0006_live_session_key.sql"),
        (7, "Chorus.Data.Redesign.0007_input_event_attribution.sql"),
        (8, "Chorus.Data.Redesign.0008_platform_task_coordination.sql"),
        (9, "Chorus.Data.Redesign.0009_round_generation.sql"),
        (10, "Chorus.Data.Redesign.0010_settlement_manifest_leaderboard.sql"),
        (11, "Chorus.Data.Redesign.0011_retention_capacity.sql"),
        (12, "Chorus.Data.Redesign.0012_match_ticket_audit.sql"),
        (13, "Chorus.Data.Redesign.0013_match_reservation_scope.sql"),
        (14, "Chorus.Data.Redesign.0014_match_ticket_streamer.sql"),
        (15, "Chorus.Data.Redesign.0015_process_endpoint.sql"),
        (16, "Chorus.Data.Redesign.0016_deployment_plan.sql"),
        (17, "Chorus.Data.Redesign.0017_admin_gate.sql"),
        (18, "Chorus.Data.Redesign.0018_recovery_operation.sql")
    ];

    public static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Open the migration connection before applying schema changes.");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var prepare = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(785245019403117211);
            CREATE SCHEMA IF NOT EXISTS infra;
            CREATE TABLE IF NOT EXISTS infra.schema_migration (
                version integer PRIMARY KEY,
                checksum char(64) NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            """, connection, transaction))
            await prepare.ExecuteNonQueryAsync(cancellationToken);

        foreach (var (version, resource) in Steps)
        {
            await using var stream = typeof(RedesignSchemaMigrator).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource {resource} is missing.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

            await using var check = new NpgsqlCommand(
                "SELECT checksum FROM infra.schema_migration WHERE version = @version", connection, transaction);
            check.Parameters.AddWithValue("version", version);
            var existing = await check.ExecuteScalarAsync(cancellationToken) as string;
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing, checksum))
                    throw new InvalidOperationException($"Migration {version} checksum differs from the applied schema.");
                continue;
            }

            await using (var apply = new NpgsqlCommand(sql, connection, transaction))
                await apply.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand(
                "INSERT INTO infra.schema_migration(version, checksum) VALUES (@version, @checksum)", connection, transaction);
            record.Parameters.AddWithValue("version", version);
            record.Parameters.AddWithValue("checksum", checksum);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task VerifyAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Open the database connection before checking schema compatibility.");

        foreach (var (version, resource) in Steps)
        {
            await using var stream = typeof(RedesignSchemaMigrator).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource {resource} is missing.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            await using var command = new NpgsqlCommand(
                "SELECT checksum FROM infra.schema_migration WHERE version = @version", connection);
            command.Parameters.AddWithValue("version", version);
            if (!StringComparer.Ordinal.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, checksum))
                throw new InvalidOperationException($"Required redesign migration {version} is missing or changed.");
        }

        await using var newer = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM infra.schema_migration WHERE version > @latest)", connection);
        newer.Parameters.AddWithValue("latest", Steps[^1].Version);
        if (await newer.ExecuteScalarAsync(cancellationToken) is true)
            throw new InvalidOperationException("Database schema is newer than this service build.");
    }
}
