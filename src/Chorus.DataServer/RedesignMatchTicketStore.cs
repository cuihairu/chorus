using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed class RedesignMatchException(string message) : Exception(message);

public sealed record MatchTicket(
    string TicketId, string BindingId, string State, long Version, string PartitionKey,
    DateTime? ExpiresAtUtc, string? ReservationId, bool Duplicate);

/// <summary>
/// Match queue tickets (architecture 7.1, 7.3). One active ticket per mount is enforced by
/// the partial unique index, so a duplicate enqueue is a visible refusal, not an overwrite.
/// Cancellation and the later reservation transaction race on the same locked ticket rows —
/// whoever locks first wins and the loser sees a committed state. The lock order follows the
/// unified sequence: session, binding, then ticket.
/// </summary>
public sealed class RedesignMatchTicketStore(string connectionString)
{
    public const string Queued = "Queued";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Enqueues a ticket for a mounted streamer. The current session, the active binding and
    /// the absence of a live participation all validate before the insert; the partial unique
    /// index turns a concurrent duplicate into the same visible refusal.
    /// </summary>
    public async Task<MatchTicket> EnqueueTicketAsync(
        string tenantId, string gameId, string bindingId, string sessionId, string partitionKey,
        string operationId, DateTime? expiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(bindingId, nameof(bindingId));
        Require(sessionId, nameof(sessionId)); Require(partitionKey, nameof(partitionKey)); Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            BindingId = bindingId, SessionId = sessionId, PartitionKey = partitionKey,
            ExpiresUnixMs = expiresAtUtc is null ? 0 : new DateTimeOffset(expiresAtUtc.Value).ToUnixTimeMilliseconds()
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "match.enqueue", bindingId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyEnqueueAsync(tenantId, gameId, bindingId, sessionId,
                partitionKey, expiresAtUtc, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<StoredTicket>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored ticket receipt is unreadable.");
        return Ticket(result, receipt.Duplicate);
    }

    private async Task<string> ApplyEnqueueAsync(string tenantId, string gameId, string bindingId,
        string sessionId, string partitionKey, DateTime? expiresAtUtc,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Session, then binding, then ticket (unified lock order). The reads mirror the client
        // input boundary: a missing, revoked, replaced or banned session never enqueues.
        string streamerId;
        await using (var session = new NpgsqlCommand(
            "SELECT streamer_id FROM auth.session WHERE session_id=@session", connection, transaction))
        {
            session.Parameters.AddWithValue("session", sessionId);
            streamerId = await session.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new SessionRevokedException();
        }
        await using (var state = new NpgsqlCommand("""
            SELECT current_session_id,ban_state FROM auth.streamer_state
            WHERE streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != sessionId || reader.GetString(1) != "Allowed")
                throw new SessionRevokedException();
        }
        await using (var current = new NpgsqlCommand("""
            SELECT revoked_at,expires_at FROM auth.session
            WHERE session_id=@session AND streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            current.Parameters.AddWithValue("session", sessionId);
            current.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.IsDBNull(0)
                || reader.GetDateTime(1) <= DateTime.UtcNow)
                throw new SessionRevokedException();
        }

        string liveSessionId;
        await using (var binding = new NpgsqlCommand("""
            SELECT live_session_id,tenant_id,game_id,state FROM live.binding
            WHERE binding_id=@binding FOR UPDATE
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue("binding", bindingId);
            await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RedesignMatchException("The binding does not exist.");
            if (reader.GetString(1) != tenantId || reader.GetString(2) != gameId)
                throw new RedesignMatchException("The binding belongs to another tenant or game.");
            if (reader.GetString(3) is not ("Starting" or "Ready" or "Running" or "AwaitingPlatformClose"))
                throw new RedesignMatchException("The binding is no longer active.");
            liveSessionId = reader.GetString(0);
        }
        await using (var live = new NpgsqlCommand(
            "SELECT streamer_id FROM live.session WHERE live_session_id=@live", connection, transaction))
        {
            live.Parameters.AddWithValue("live", liveSessionId);
            if (await live.ExecuteScalarAsync(cancellationToken) is not string owner || owner != streamerId)
                throw new RedesignMatchException("The binding belongs to another streamer.");
        }
        await using (var participation = new NpgsqlCommand("""
            SELECT 1 FROM game.participation WHERE binding_id=@binding AND state IN ('Active','Offline')
            """, connection, transaction))
        {
            participation.Parameters.AddWithValue("binding", bindingId);
            if (await participation.ExecuteScalarAsync(cancellationToken) is not null)
                throw new RedesignMatchException("The mount already participates in a round.");
        }

        var ticketId = Guid.NewGuid().ToString("N");
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO match.ticket(ticket_id,tenant_id,game_id,binding_id,state,version,
                partition_key,expires_at)
            VALUES (@ticket,@tenant,@game,@binding,'Queued',1,@partition,@expires)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("ticket", ticketId);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("binding", bindingId);
            insert.Parameters.AddWithValue("partition", partitionKey);
            if (expiresAtUtc is null) insert.Parameters.AddWithValue("expires", DBNull.Value);
            else insert.Parameters.AddWithValue("expires", expiresAtUtc.Value);
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException error) when (error.SqlState == "23505"
                && error.ConstraintName == "ux_match_ticket_active")
            {
                throw new RedesignMatchException("The mount already holds an active ticket.");
            }
        }
        return JsonSerializer.Serialize(new StoredTicket(
            ticketId, bindingId, Queued, 1, partitionKey,
            expiresAtUtc is null ? 0 : new DateTimeOffset(expiresAtUtc.Value).ToUnixTimeMilliseconds(), null));
    }

    /// <summary>
    /// Cancels the mount's queued ticket. A ticket already held by a reservation refuses —
    /// the reservation transaction owns the transition; a committed ticket belongs to its
    /// round and is never cancelled here.
    /// </summary>
    public async Task<MatchTicket> CancelTicketAsync(
        string tenantId, string gameId, string bindingId, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(bindingId, nameof(bindingId));
        Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            BindingId = bindingId
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "match.cancel", bindingId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyCancelAsync(tenantId, gameId, bindingId,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<StoredTicket>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored cancel receipt is unreadable.");
        return Ticket(result, receipt.Duplicate);
    }

    private async Task<string> ApplyCancelAsync(string tenantId, string gameId, string bindingId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var binding = new NpgsqlCommand("""
            SELECT 1 FROM live.binding WHERE binding_id=@binding AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue("binding", bindingId);
            binding.Parameters.AddWithValue("tenant", tenantId);
            binding.Parameters.AddWithValue("game", gameId);
            if (await binding.ExecuteScalarAsync(cancellationToken) is null)
                throw new RedesignMatchException("The binding does not exist.");
        }
        string ticketId; string state; long version; string partitionKey;
        long expiresUnixMs; string? reservationId;
        await using (var ticket = new NpgsqlCommand("""
            SELECT ticket_id,state,version,partition_key,
                (CASE WHEN expires_at IS NULL THEN 0 ELSE (EXTRACT(EPOCH FROM expires_at)*1000)::bigint END),
                reservation_id
            FROM match.ticket
            WHERE binding_id=@binding AND state IN ('Queued','Reserved','Preparing')
            ORDER BY created_at DESC LIMIT 1 FOR UPDATE
            """, connection, transaction))
        {
            ticket.Parameters.AddWithValue("binding", bindingId);
            await using var reader = await ticket.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RedesignMatchException("The mount holds no active ticket to cancel.");
            ticketId = reader.GetString(0);
            state = reader.GetString(1);
            version = reader.GetInt64(2);
            partitionKey = reader.GetString(3);
            expiresUnixMs = reader.GetInt64(4);
            reservationId = reader.IsDBNull(5) ? null : reader.GetString(5);
        }
        if (state != Queued)
            throw new RedesignMatchException(
                $"A ticket in state {state} is held by a reservation and cannot be cancelled.");
        await using (var update = new NpgsqlCommand("""
            UPDATE match.ticket SET state='Cancelled',version=version+1 WHERE ticket_id=@ticket
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("ticket", ticketId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(new StoredTicket(
            ticketId, bindingId, Cancelled, version + 1, partitionKey, expiresUnixMs, reservationId));
    }

    /// <summary>
    /// Retires queued tickets whose own expiry passed. A deterministic sweep, idempotent by
    /// predicate; SKIP LOCKED keeps it out of the way of enqueue and cancel. Releasing the
    /// partial unique index slot lets the mount queue again.
    /// </summary>
    public async Task<int> ExpireQueuedAsync(int max, CancellationToken cancellationToken = default)
    {
        if (max is <= 0 or > 500) throw new ArgumentException("The sweep window is 1..500 tickets.", nameof(max));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var sweep = new NpgsqlCommand("""
            UPDATE match.ticket SET state='Expired',version=version+1
            WHERE ticket_id IN (
                SELECT ticket_id FROM match.ticket
                WHERE state='Queued' AND expires_at IS NOT NULL AND expires_at<clock_timestamp()
                ORDER BY created_at LIMIT @max FOR UPDATE SKIP LOCKED)
            RETURNING ticket_id
            """, connection);
        sweep.Parameters.AddWithValue("max", max);
        await using var reader = await sweep.ExecuteReaderAsync(cancellationToken);
        var expired = 0;
        while (await reader.ReadAsync(cancellationToken)) expired++;
        return expired;
    }

    /// <summary>The mount's latest ticket regardless of state — the player-facing status read.</summary>
    public async Task<MatchTicket?> GetTicketAsync(
        string tenantId, string gameId, string bindingId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(bindingId, nameof(bindingId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT ticket_id,state,version,partition_key,
                (CASE WHEN expires_at IS NULL THEN 0 ELSE (EXTRACT(EPOCH FROM expires_at)*1000)::bigint END),
                reservation_id
            FROM match.ticket WHERE tenant_id=@tenant AND game_id=@game AND binding_id=@binding
            ORDER BY created_at DESC, ticket_id LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("binding", bindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new MatchTicket(reader.GetString(0), bindingId, reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3),
            reader.GetInt64(4) == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).UtcDateTime,
            reader.IsDBNull(5) ? null : reader.GetString(5), Duplicate: false);
    }

    /// <summary>
    /// The coordinator's candidate window: live queued tickets of one partition in queue
    /// order. Tickets past their own expiry are excluded here and retired by the sweep.
    /// </summary>
    public async Task<IReadOnlyList<MatchTicket>> ListQueuedAsync(
        string partitionKey, int max, CancellationToken cancellationToken = default)
    {
        Require(partitionKey, nameof(partitionKey));
        if (max is <= 0 or > 500) throw new ArgumentException("The candidate window is 1..500 tickets.", nameof(max));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT ticket_id,binding_id,version,
                (CASE WHEN expires_at IS NULL THEN 0 ELSE (EXTRACT(EPOCH FROM expires_at)*1000)::bigint END),
                reservation_id
            FROM match.ticket
            WHERE partition_key=@partition AND state='Queued'
              AND (expires_at IS NULL OR expires_at>=clock_timestamp())
            ORDER BY created_at LIMIT @max
            """, connection);
        command.Parameters.AddWithValue("partition", partitionKey);
        command.Parameters.AddWithValue("max", max);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tickets = new List<MatchTicket>();
        while (await reader.ReadAsync(cancellationToken))
            tickets.Add(new MatchTicket(reader.GetString(0), reader.GetString(1), Queued, reader.GetInt64(2),
                partitionKey,
                reader.GetInt64(3) == 0 ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime,
                reader.IsDBNull(4) ? null : reader.GetString(4), Duplicate: false));
        return tickets;
    }

    private static MatchTicket Ticket(StoredTicket stored, bool duplicate) => new(
        stored.TicketId, stored.BindingId, stored.State, stored.Version, stored.PartitionKey,
        stored.ExpiresUnixMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(stored.ExpiresUnixMs).UtcDateTime,
        string.IsNullOrEmpty(stored.ReservationId) ? null : stored.ReservationId, duplicate);

    private sealed record StoredTicket(
        string TicketId, string BindingId, string State, long Version, string PartitionKey,
        long ExpiresUnixMs, string? ReservationId);

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
