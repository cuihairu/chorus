using System.Data;
using System.Text.Json;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.Data.Redesign;

public sealed record OperationReceipt(string ResultJson, bool Duplicate);

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException()
        : base("The operation ID has already been used for a different canonical request.") { }
}

/// <summary>
/// Commits a receipt with a business write in one PostgreSQL transaction. The callback must use
/// the supplied connection and transaction for every durable effect; it must not commit itself.
/// </summary>
public sealed class PostgresOperationReceiptStore(string connectionString)
{
    public async Task<OperationReceipt> ExecuteAsync(
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<string>> apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(apply);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (var claim = new NpgsqlCommand("""
            INSERT INTO infra.operation_receipt
                (tenant_id, game_id, operation_kind, target_id, operation_id,
                 canonical_version, request_hash, result_json)
            VALUES (@tenant, @game, @kind, @target, @operation, @version, @hash, 'null'::jsonb)
            ON CONFLICT DO NOTHING
            RETURNING 1
            """, connection, transaction))
        {
            BindIdentity(claim, identity);
            claim.Parameters.AddWithValue("version", fingerprint.CanonicalVersion);
            claim.Parameters.AddWithValue("hash", fingerprint.Sha256Hex);
            if (await claim.ExecuteScalarAsync(cancellationToken) is null)
            {
                var existing = await ReadAsync(connection, transaction, identity, cancellationToken)
                    ?? throw new InvalidOperationException("A conflicting receipt disappeared during the transaction.");
                if (existing.Version != fingerprint.CanonicalVersion ||
                    !StringComparer.Ordinal.Equals(existing.Hash, fingerprint.Sha256Hex))
                    throw new IdempotencyConflictException();
                return new OperationReceipt(existing.ResultJson, Duplicate: true);
            }
        }

        var resultJson = await apply(connection, transaction, cancellationToken);
        if (string.IsNullOrWhiteSpace(resultJson))
            throw new ArgumentException("The business write must return a JSON receipt.", nameof(apply));
        using (JsonDocument.Parse(resultJson)) { }

        string storedResult;
        await using (var complete = new NpgsqlCommand("""
            UPDATE infra.operation_receipt
            SET result_json = @result::jsonb, committed_at = clock_timestamp()
            WHERE tenant_id = @tenant AND game_id = @game AND operation_kind = @kind
              AND target_id = @target AND operation_id = @operation
            RETURNING result_json::text
            """, connection, transaction))
        {
            BindIdentity(complete, identity);
            complete.Parameters.AddWithValue("result", resultJson);
            storedResult = await complete.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidOperationException("The claimed receipt was not updated.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new OperationReceipt(storedResult, Duplicate: false);
    }

    /// <summary>Query after a lost commit response before deciding whether to retry.</summary>
    public async Task<OperationReceipt?> GetAsync(
        OperationIdentity identity, OperationFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fingerprint);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var existing = await ReadAsync(connection, null, identity, cancellationToken);
        if (existing is null) return null;
        if (existing.Value.Version != fingerprint.CanonicalVersion ||
            !StringComparer.Ordinal.Equals(existing.Value.Hash, fingerprint.Sha256Hex))
            throw new IdempotencyConflictException();
        return new OperationReceipt(existing.Value.ResultJson, Duplicate: true);
    }

    private static async Task<(int Version, string Hash, string ResultJson)?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, OperationIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT canonical_version, request_hash, result_json::text
            FROM infra.operation_receipt
            WHERE tenant_id = @tenant AND game_id = @game AND operation_kind = @kind
              AND target_id = @target AND operation_id = @operation
            """, connection, transaction);
        BindIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt32(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static void BindIdentity(NpgsqlCommand command, OperationIdentity identity)
    {
        command.Parameters.AddWithValue("tenant", identity.TenantId);
        command.Parameters.AddWithValue("game", identity.GameId);
        command.Parameters.AddWithValue("kind", identity.OperationKind);
        command.Parameters.AddWithValue("target", identity.TargetId);
        command.Parameters.AddWithValue("operation", identity.OperationId);
    }
}
