using Npgsql;

namespace Chorus.ControlServer;

public sealed record TermStatus(string HolderInstanceId, long Term, DateTime LeaseUntilUtc);

/// <summary>The term no longer belongs to the caller: the lease expired or another holder won.</summary>
public sealed class RedesignTermLostException : Exception;

/// <summary>
/// Match coordinator terms (architecture 7.1, 10.1). One holder per partition; a takeover or
/// an expired re-claim starts a new term, so a write carrying an old term is always refusable.
/// Terms are monotonically increasing — the same lease rules as ownership, without epochs.
/// </summary>
public sealed class RedesignCoordinatorStore(string connectionString)
{
    /// <summary>
    /// Acquire-or-takeover: the same holder re-claiming a live lease only extends it and keeps
    /// the term; any other transition — a different holder, or an expired lease — starts the
    /// next term.
    /// </summary>
    public async Task<TermStatus> ClaimTermAsync(string partitionKey, string holderInstanceId,
        int leaseSeconds, CancellationToken cancellationToken = default)
    {
        Require(partitionKey, nameof(partitionKey)); Require(holderInstanceId, nameof(holderInstanceId));
        CheckLease(leaseSeconds);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.coordinator(partition_key,holder_instance_id,term,lease_until)
            VALUES (@partition,@holder,1,clock_timestamp()+@lease*interval '1 second')
            ON CONFLICT(partition_key) DO UPDATE
            SET holder_instance_id=excluded.holder_instance_id,
                term=CASE WHEN control.coordinator.holder_instance_id=excluded.holder_instance_id
                       AND control.coordinator.lease_until>clock_timestamp()
                     THEN control.coordinator.term ELSE control.coordinator.term+1 END,
                lease_until=excluded.lease_until
            RETURNING holder_instance_id,term,lease_until
            """, connection);
        Bind(command, partitionKey, holderInstanceId, leaseSeconds);
        return await ReadTermAsync(command, cancellationToken);
    }

    /// <summary>
    /// Extends the lease of the current holder. A renewal that would silently re-acquire a lost
    /// partition hides a partition from the caller, so an expired or taken lease refuses here
    /// and the caller must decide whether to re-claim.
    /// </summary>
    public async Task<TermStatus> RenewTermAsync(string partitionKey, string holderInstanceId,
        int leaseSeconds, CancellationToken cancellationToken = default)
    {
        Require(partitionKey, nameof(partitionKey)); Require(holderInstanceId, nameof(holderInstanceId));
        CheckLease(leaseSeconds);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.coordinator
            SET lease_until=clock_timestamp()+@lease*interval '1 second'
            WHERE partition_key=@partition AND holder_instance_id=@holder
              AND lease_until>clock_timestamp()
            RETURNING holder_instance_id,term,lease_until
            """, connection);
        Bind(command, partitionKey, holderInstanceId, leaseSeconds);
        return await ReadTermAsync(command, cancellationToken);
    }

    /// <summary>Parks the lease in the past so the next claim — by anyone — starts a new term.</summary>
    public async Task<TermStatus> ReleaseTermAsync(string partitionKey, string holderInstanceId,
        CancellationToken cancellationToken = default)
    {
        Require(partitionKey, nameof(partitionKey)); Require(holderInstanceId, nameof(holderInstanceId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.coordinator
            SET lease_until=clock_timestamp()-interval '1 second'
            WHERE partition_key=@partition AND holder_instance_id=@holder
              AND lease_until>clock_timestamp()
            RETURNING holder_instance_id,term,lease_until
            """, connection);
        command.Parameters.AddWithValue("partition", partitionKey);
        command.Parameters.AddWithValue("holder", holderInstanceId);
        return await ReadTermAsync(command, cancellationToken);
    }

    public async Task<TermStatus?> GetTermAsync(string partitionKey,
        CancellationToken cancellationToken = default)
    {
        Require(partitionKey, nameof(partitionKey));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT holder_instance_id,term,lease_until FROM control.coordinator
            WHERE partition_key=@partition
            """, connection);
        command.Parameters.AddWithValue("partition", partitionKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TermStatus(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2)) : null;
    }

    private static async Task<TermStatus> ReadTermAsync(NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new RedesignTermLostException();
        return new TermStatus(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2));
    }

    private static void Bind(NpgsqlCommand command, string partitionKey, string holderInstanceId,
        int leaseSeconds)
    {
        command.Parameters.AddWithValue("partition", partitionKey);
        command.Parameters.AddWithValue("holder", holderInstanceId);
        command.Parameters.AddWithValue("lease", leaseSeconds);
    }

    private static void CheckLease(int leaseSeconds)
    {
        if (leaseSeconds is <= 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
