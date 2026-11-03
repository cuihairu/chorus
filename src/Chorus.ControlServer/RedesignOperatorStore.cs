using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Chorus.ControlServer;

/// <summary>
/// Operator accounts (FIX-GM-06, architecture 12.1): the only place a human GM identity
/// exists. A bearer token is registered here (stored as a SHA-256 digest, never the secret),
/// and a request is authenticated by hashing the presented token and resolving the matching
/// enabled operator. The operator identity is distinct from every process certificate — the
/// certificate authenticates a process, this store authenticates a person.
/// </summary>
public sealed class RedesignOperatorStore(string connectionString)
{
    /// <summary>Registers or re-registers an operator. The token is digested before it is
    /// stored; re-registering under the same id rotates the credential and scope. Returns the
    /// identity as it now stands.</summary>
    public async Task<OperatorIdentity> RegisterOperatorAsync(string operatorId, string role,
        IReadOnlyList<string> tenants, IReadOnlyList<string> games, string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(games);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.operator(operator_id,role,tenants,games,token_hash,enabled,updated_at)
            VALUES (@id,@role,@tenants,@games,@hash,true,clock_timestamp())
            ON CONFLICT(operator_id) DO UPDATE
            SET role=excluded.role, tenants=excluded.tenants, games=excluded.games,
                token_hash=excluded.token_hash, enabled=true, updated_at=clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("id", operatorId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("tenants", tenants.ToArray());
        command.Parameters.AddWithValue("games", games.ToArray());
        command.Parameters.AddWithValue("hash", Digest(token));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new OperatorIdentity(operatorId, role, tenants, games);
    }

    /// <summary>Authenticates a presented bearer token. Returns the operator identity, or null
    /// when the token is unknown or the operator is disabled.</summary>
    public async Task<OperatorIdentity?> ResolveOperatorAsync(string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT operator_id,role,tenants,games FROM control.operator
            WHERE token_hash=@hash AND enabled
            """, connection);
        command.Parameters.AddWithValue("hash", Digest(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new OperatorIdentity(reader.GetString(0), reader.GetString(1),
            reader.GetFieldValue<string[]>(2), reader.GetFieldValue<string[]>(3));
    }

    public async Task<OperatorIdentity?> GetOperatorAsync(string operatorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT operator_id,role,tenants,games FROM control.operator WHERE operator_id=@id
            """, connection);
        command.Parameters.AddWithValue("id", operatorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new OperatorIdentity(reader.GetString(0), reader.GetString(1),
            reader.GetFieldValue<string[]>(2), reader.GetFieldValue<string[]>(3));
    }

    /// <summary>Enables or disables an operator. False when the id is unknown.</summary>
    public async Task<bool> SetOperatorEnabledAsync(string operatorId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.operator SET enabled=@enabled, updated_at=clock_timestamp()
            WHERE operator_id=@id
            """, connection);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("id", operatorId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static byte[] Digest(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
