using Npgsql;

namespace Chorus.Data.Redesign;

/// <summary>Explicit migration step; business process startup must not run DDL.</summary>
public static class OperationReceiptMigration
{
    public static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Open the migration connection before applying schema changes.");

        await using var stream = typeof(OperationReceiptMigration).Assembly.GetManifestResourceStream(
            "Chorus.Data.Redesign.0001_operation_receipt.sql")
            ?? throw new InvalidOperationException("Operation receipt migration resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
