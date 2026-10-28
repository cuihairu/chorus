using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.DataServer;

/// <summary>The coordinator term the reservation carries no longer leads the partition.</summary>
public sealed class RedesignStaleTermException : Exception;

public sealed record ReservedParticipant(string BindingId, string ParticipantId, string Role);

public sealed record MatchReservation(
    string ReservationId, string RoomId, string TenantId, string GameId, string PartitionKey,
    long CoordinatorTerm, DateTime ExpiresAtUtc, string Status,
    IReadOnlyList<ReservedParticipant> Participants, string? PreparedReceiptJson, bool Duplicate);

/// <summary>
/// Match reservations (architecture 7.2, 10.1). Reserving is one receipt transaction behind
/// the coordinator term: the term gate locks the partition lease first, then the candidate
/// bindings and tickets in the unified order — a wrong-platform candidate (a different
/// partition) or a ticket lost to a concurrent cancel refuses visibly. The reservation holds
/// admission capacity until it commits or a sweep retires it: an expired reservation returns
/// its tickets to the queue, aborts a room it already started and releases the ownership,
/// so capacity never leaks permanently (AT11).
/// </summary>
/// <param name="crashAfterReservation">Fault-injection seam: invoked inside the transaction
/// after the tickets moved to Reserved, before the commit.</param>
public sealed class RedesignMatchReservationStore(string connectionString,
    Action? crashAfterReservation = null, int? maxActiveReservations = null)
{
    public const string Reserved = "Reserved";
    public const string Prepared = "Prepared";
    public const string Expired = "Expired";
    public const string Committed = "Committed";
    public const string Aborted = "Aborted";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Reserves the candidate set under the coordinator's term. Every participant binding must
    /// hold exactly one queued ticket of this partition — a candidate from another partition
    /// (wrong platform policy) or one cancelled concurrently refuses the whole reservation.
    /// </summary>
    public async Task<MatchReservation> ReserveAsync(
        string tenantId, string gameId, string partitionKey, long coordinatorTerm,
        string holderInstanceId, string roomId, IReadOnlyList<ReservedParticipant> participants,
        int reservationSeconds, string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(partitionKey, nameof(partitionKey)); Require(holderInstanceId, nameof(holderInstanceId));
        Require(roomId, nameof(roomId)); Require(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(participants);
        if (coordinatorTerm <= 0) throw new ArgumentOutOfRangeException(nameof(coordinatorTerm));
        if (reservationSeconds is <= 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(reservationSeconds));
        if (participants.Count == 0)
            throw new ArgumentException("A reservation needs at least one participant.", nameof(participants));
        var ordered = participants
            .OrderBy(participant => participant.BindingId, StringComparer.Ordinal)
            .ThenBy(participant => participant.ParticipantId, StringComparer.Ordinal).ToArray();
        foreach (var participant in ordered)
        {
            Require(participant.BindingId, "BindingId");
            Require(participant.ParticipantId, "ParticipantId");
            Require(participant.Role, "Role");
        }
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            RoomId = roomId, PartitionKey = partitionKey, CoordinatorTerm = coordinatorTerm,
            Participants = ordered.Select(participant => new
            {
                participant.BindingId, participant.ParticipantId, participant.Role
            })
        }));
        OperationReceipt receipt;
        try
        {
            receipt = await _receipts.ExecuteAsync(
                new OperationIdentity(tenantId, gameId, "match.reserve", roomId, operationId),
                fingerprint,
                (connection, transaction, token) => ApplyReserveAsync(tenantId, gameId, partitionKey,
                    coordinatorTerm, holderInstanceId, roomId, ordered, reservationSeconds,
                    connection, transaction, token),
                cancellationToken);
        }
        catch (RedesignCapacityException)
        {
            await CountRejectionAsync("reserve-match:active-rooms", cancellationToken);
            throw;
        }
        return Deserialize(receipt.ResultJson, receipt.Duplicate);
    }

    private async Task<string> ApplyReserveAsync(string tenantId, string gameId, string partitionKey,
        long coordinatorTerm, string holderInstanceId, string roomId, ReservedParticipant[] ordered,
        int reservationSeconds, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await RequireTermAsync(partitionKey, coordinatorTerm, holderInstanceId, connection, transaction,
            cancellationToken);

        if (maxActiveReservations is { } cap)
        {
            // Reservations and started rooms serialize on the same admission lock and count
            // against the same cap: a reserved match holds its slot from reserve until commit
            // or retirement. A room already started by its reservation is counted by the room
            // itself, so only 'Reserved' reservations add to it (conservatively: the start
            // window double-holds one slot rather than ever over-admitting).
            await using (var admissionLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('game.room:admission'))", connection, transaction))
                await admissionLock.ExecuteNonQueryAsync(cancellationToken);
            await using (var active = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM game.room
                          WHERE status NOT IN ('Aborted','Settled','Closed'))
                     + (SELECT count(*) FROM match.reservation WHERE status='Reserved')
                """, connection, transaction))
            {
                if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken)) >= cap)
                    throw new RedesignCapacityException(
                        $"The node already runs {cap} non-terminal rooms or reservations; new matches " +
                        "are rejected until capacity frees.");
            }
        }

        // Bindings before tickets (unified lock order); every candidate must be an active
        // mount of this tenant/game without a live participation.
        foreach (var participant in ordered)
        {
            await using (var binding = new NpgsqlCommand("""
                SELECT tenant_id,game_id,state FROM live.binding WHERE binding_id=@binding FOR UPDATE
                """, connection, transaction))
            {
                binding.Parameters.AddWithValue("binding", participant.BindingId);
                await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new RedesignMatchException($"The binding {participant.BindingId} does not exist.");
                if (reader.GetString(0) != tenantId || reader.GetString(1) != gameId)
                    throw new RedesignMatchException(
                        $"The binding {participant.BindingId} belongs to another tenant or game.");
                if (reader.GetString(2) is not ("Starting" or "Ready" or "Running" or "AwaitingPlatformClose"))
                    throw new RedesignMatchException(
                        $"The binding {participant.BindingId} is no longer active.");
            }
            await using (var liveParticipation = new NpgsqlCommand("""
                SELECT 1 FROM game.participation WHERE binding_id=@binding AND state IN ('Active','Offline')
                """, connection, transaction))
            {
                liveParticipation.Parameters.AddWithValue("binding", participant.BindingId);
                if (await liveParticipation.ExecuteScalarAsync(cancellationToken) is not null)
                    throw new RedesignMatchException(
                        $"The binding {participant.BindingId} already participates in a round.");
            }
        }

        // Each candidate must hold exactly one queued ticket of THIS partition: a cancelled
        // ticket, a ticket held by another reservation, or a candidate from another partition
        // (wrong platform policy) refuses the whole reservation.
        var ticketIds = new List<string>();
        await using (var tickets = new NpgsqlCommand("""
            SELECT binding_id,ticket_id,partition_key,state,
                (expires_at IS NOT NULL AND expires_at<clock_timestamp())
            FROM match.ticket
            WHERE binding_id=ANY(@bindings) AND state IN ('Queued','Reserved','Preparing')
            ORDER BY binding_id FOR UPDATE
            """, connection, transaction))
        {
            tickets.Parameters.AddWithValue("bindings",
                ordered.Select(participant => participant.BindingId).ToArray());
            await using var reader = await tickets.ExecuteReaderAsync(cancellationToken);
            var byBinding = new Dictionary<string, (string TicketId, string Partition, string State, bool Stale)>();
            while (await reader.ReadAsync(cancellationToken))
                byBinding[reader.GetString(0)] =
                    (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
            foreach (var participant in ordered)
            {
                if (!byBinding.TryGetValue(participant.BindingId, out var ticket))
                    throw new RedesignMatchException(
                        $"The mount {participant.BindingId} holds no candidate ticket.");
                if (ticket.State != RedesignMatchTicketStore.Queued)
                    throw new RedesignMatchException(
                        $"The ticket of {participant.BindingId} is already held by a reservation.");
                if (ticket.Stale)
                    throw new RedesignMatchException($"The ticket of {participant.BindingId} has expired.");
                if (ticket.Partition != partitionKey)
                    throw new RedesignMatchException(
                        $"The ticket of {participant.BindingId} belongs to partition {ticket.Partition}; " +
                        $"candidates from another partition never share a reservation.");
                ticketIds.Add(ticket.TicketId);
            }
        }

        var reservationId = Guid.NewGuid().ToString("N");
        long expiresUnixMs;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO match.reservation(reservation_id,room_id,partition_key,coordinator_term,
                expires_at,status,tenant_id,game_id,participants)
            VALUES (@reservation,@room,@partition,@term,
                clock_timestamp()+@seconds*interval '1 second','Reserved',@tenant,@game,@participants::jsonb)
            RETURNING (EXTRACT(EPOCH FROM expires_at)*1000)::bigint
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("reservation", reservationId);
            insert.Parameters.AddWithValue("room", roomId);
            insert.Parameters.AddWithValue("partition", partitionKey);
            insert.Parameters.AddWithValue("term", coordinatorTerm);
            insert.Parameters.AddWithValue("seconds", reservationSeconds);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("participants", JsonSerializer.Serialize(ordered.Select(
                participant => new { participant.BindingId, participant.ParticipantId, participant.Role })));
            try
            {
                expiresUnixMs = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            }
            catch (PostgresException error) when (error.SqlState == "23505"
                && error.ConstraintName == "match_reservation_room_id_key")
            {
                throw new RedesignMatchException("The room is already reserved by another match.");
            }
        }
        await using (var hold = new NpgsqlCommand("""
            UPDATE match.ticket SET state='Reserved',version=version+1,reservation_id=@reservation
            WHERE ticket_id=ANY(@tickets)
            """, connection, transaction))
        {
            hold.Parameters.AddWithValue("reservation", reservationId);
            hold.Parameters.AddWithValue("tickets", ticketIds.ToArray());
            await hold.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteOutboxAsync($"{reservationId}/reserve", "match.reserved", new
        {
            ReservationId = reservationId, RoomId = roomId, TenantId = tenantId, GameId = gameId,
            PartitionKey = partitionKey, CoordinatorTerm = coordinatorTerm,
            Participants = ordered.Select(participant => participant.BindingId)
        }, connection, transaction, cancellationToken);
        crashAfterReservation?.Invoke();
        return Serialize(new StoredReservation(
            reservationId, roomId, tenantId, gameId, partitionKey, coordinatorTerm,
            expiresUnixMs, Reserved,
            ordered.Select(participant =>
                new ReservedParticipant(participant.BindingId, participant.ParticipantId, participant.Role))
                .ToList(), null));
    }

    /// <summary>
    /// Extends the reservation under the same coordinator term. An expired reservation cannot
    /// be revived — the sweep owns it; a slow prepare must renew before the deadline.
    /// </summary>
    public async Task<MatchReservation> RenewReservationAsync(
        string tenantId, string gameId, string reservationId, long coordinatorTerm,
        string holderInstanceId, int reservationSeconds, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(reservationId, nameof(reservationId)); Require(holderInstanceId, nameof(holderInstanceId));
        Require(operationId, nameof(operationId));
        if (coordinatorTerm <= 0) throw new ArgumentOutOfRangeException(nameof(coordinatorTerm));
        if (reservationSeconds is <= 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(reservationSeconds));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            ReservationId = reservationId, ReservationSeconds = reservationSeconds
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "match.renew", reservationId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyRenewAsync(tenantId, gameId, reservationId,
                coordinatorTerm, holderInstanceId, reservationSeconds, connection, transaction, token),
            cancellationToken);
        return Deserialize(receipt.ResultJson, receipt.Duplicate);
    }

    private async Task<string> ApplyRenewAsync(string tenantId, string gameId, string reservationId,
        long coordinatorTerm, string holderInstanceId, int reservationSeconds,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var partitionKey = await ReadPartitionAsync(reservationId, connection, transaction, cancellationToken);
        await RequireTermAsync(partitionKey, coordinatorTerm, holderInstanceId, connection, transaction,
            cancellationToken);
        long expiresUnixMs = 0;
        await using (var reservation = new NpgsqlCommand("""
            UPDATE match.reservation
            SET expires_at=clock_timestamp()+@seconds*interval '1 second'
            WHERE reservation_id=@reservation AND tenant_id=@tenant AND game_id=@game
              AND status IN ('Reserved','Prepared') AND expires_at>clock_timestamp()
            RETURNING (EXTRACT(EPOCH FROM expires_at)*1000)::bigint
            """, connection, transaction))
        {
            reservation.Parameters.AddWithValue("reservation", reservationId);
            reservation.Parameters.AddWithValue("tenant", tenantId);
            reservation.Parameters.AddWithValue("game", gameId);
            reservation.Parameters.AddWithValue("seconds", reservationSeconds);
            expiresUnixMs = Convert.ToInt64(await reservation.ExecuteScalarAsync(cancellationToken));
        }
        if (expiresUnixMs == 0)
            throw new RedesignMatchException("The reservation is not active or its deadline already passed.");
        return await ReadReservationJsonAsync(reservationId, connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Records the prepare receipt: the host derived the tick-0 state and the room ownership is
    /// held. The receipt is the successor's evidence after a coordinator death (AT11).
    /// </summary>
    public async Task<MatchReservation> MarkPreparedAsync(
        string tenantId, string gameId, string reservationId, long coordinatorTerm,
        string holderInstanceId, string preparedReceiptJson, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(reservationId, nameof(reservationId)); Require(holderInstanceId, nameof(holderInstanceId));
        Require(preparedReceiptJson, nameof(preparedReceiptJson)); Require(operationId, nameof(operationId));
        if (coordinatorTerm <= 0) throw new ArgumentOutOfRangeException(nameof(coordinatorTerm));
        using (JsonDocument.Parse(preparedReceiptJson)) { }
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            ReservationId = reservationId, PreparedReceiptJson = preparedReceiptJson
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "match.prepare", reservationId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyMarkPreparedAsync(tenantId, gameId, reservationId,
                coordinatorTerm, holderInstanceId, preparedReceiptJson, connection, transaction, token),
            cancellationToken);
        return Deserialize(receipt.ResultJson, receipt.Duplicate);
    }

    private async Task<string> ApplyMarkPreparedAsync(string tenantId, string gameId, string reservationId,
        long coordinatorTerm, string holderInstanceId, string preparedReceiptJson,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var partitionKey = await ReadPartitionAsync(reservationId, connection, transaction, cancellationToken);
        await RequireTermAsync(partitionKey, coordinatorTerm, holderInstanceId, connection, transaction,
            cancellationToken);
        string roomId;
        await using (var reservation = new NpgsqlCommand("""
            SELECT room_id FROM match.reservation
            WHERE reservation_id=@reservation AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            reservation.Parameters.AddWithValue("reservation", reservationId);
            reservation.Parameters.AddWithValue("tenant", tenantId);
            reservation.Parameters.AddWithValue("game", gameId);
            roomId = await reservation.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RedesignMatchException("The reservation does not exist.");
        }
        await using (var ownership = new NpgsqlCommand("""
            SELECT 1 FROM control.ownership
            WHERE kind='room' AND entity_id=@room AND status='Active'
              AND lease_until>clock_timestamp()
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("room", roomId);
            if (await ownership.ExecuteScalarAsync(cancellationToken) is null)
                throw new RedesignMatchException("The room ownership is not held for the reserved room.");
        }
        await using (var prepared = new NpgsqlCommand("""
            UPDATE match.reservation SET status='Prepared',prepared_receipt=@receipt::jsonb
            WHERE reservation_id=@reservation AND status='Reserved'
              AND expires_at>clock_timestamp()
            RETURNING 1
            """, connection, transaction))
        {
            prepared.Parameters.AddWithValue("reservation", reservationId);
            prepared.Parameters.AddWithValue("receipt", preparedReceiptJson);
            if (await prepared.ExecuteScalarAsync(cancellationToken) is null)
                throw new RedesignMatchException(
                    "The reservation is not in the reserved state or its deadline already passed.");
        }
        return await ReadReservationJsonAsync(reservationId, connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Commits the match in one receipt transaction (architecture 7.2.5): the coordinator term
    /// gates the write, the room ownership epoch fences it, and every ticket commits together
    /// or not at all. A write carrying a displaced room owner's epoch never commits (AT25
    /// mechanics).
    /// </summary>
    public async Task<MatchReservation> CommitMatchAsync(
        string tenantId, string gameId, string reservationId, long coordinatorTerm,
        string holderInstanceId, string roomInstanceId, long roomEpoch, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(reservationId, nameof(reservationId)); Require(holderInstanceId, nameof(holderInstanceId));
        Require(roomInstanceId, nameof(roomInstanceId)); Require(operationId, nameof(operationId));
        if (coordinatorTerm <= 0) throw new ArgumentOutOfRangeException(nameof(coordinatorTerm));
        if (roomEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(roomEpoch));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            ReservationId = reservationId, RoomInstanceId = roomInstanceId, RoomEpoch = roomEpoch
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "match.commit", reservationId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyCommitMatchAsync(tenantId, gameId, reservationId,
                coordinatorTerm, holderInstanceId, roomInstanceId, roomEpoch, connection, transaction, token),
            cancellationToken);
        return Deserialize(receipt.ResultJson, receipt.Duplicate);
    }

    private async Task<string> ApplyCommitMatchAsync(string tenantId, string gameId, string reservationId,
        long coordinatorTerm, string holderInstanceId, string roomInstanceId, long roomEpoch,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var partitionKey = await ReadPartitionAsync(reservationId, connection, transaction, cancellationToken);
        await RequireTermAsync(partitionKey, coordinatorTerm, holderInstanceId, connection, transaction,
            cancellationToken);
        string roomId;
        await using (var navigation = new NpgsqlCommand(
            "SELECT room_id FROM match.reservation WHERE reservation_id=@reservation",
            connection, transaction))
        {
            navigation.Parameters.AddWithValue("reservation", reservationId);
            roomId = await navigation.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RedesignMatchException("The reservation does not exist.");
        }

        // The ownership fence (unified lock order: ownership before reservation — the retire
        // path locks the same pair in the same order, so commit and release serialize instead
        // of deadlocking). A missing row, another instance, another epoch, a dead lease or a
        // released row never commits.
        await using (var ownership = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,lease_until>clock_timestamp(),status
            FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != roomInstanceId || reader.GetInt64(1) != roomEpoch
                || !reader.GetBoolean(2) || reader.GetString(3) != "Active")
                throw new RedesignStaleOwnerException();
        }

        await using (var reservation = new NpgsqlCommand("""
            SELECT status,expires_at>clock_timestamp() FROM match.reservation
            WHERE reservation_id=@reservation AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            reservation.Parameters.AddWithValue("reservation", reservationId);
            reservation.Parameters.AddWithValue("tenant", tenantId);
            reservation.Parameters.AddWithValue("game", gameId);
            await using var reader = await reservation.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RedesignMatchException("The reservation does not exist.");
            if (reader.GetString(0) is not (Reserved or Prepared))
                throw new RedesignMatchException("The reservation is already committed or retired.");
            if (!reader.GetBoolean(1))
                throw new RedesignMatchException("The reservation deadline already passed; the sweep owns it.");
        }

        // Everyone commits together: the tickets leave the active-unique slots in the same
        // transaction that stamps the reservation.
        await using (var tickets = new NpgsqlCommand("""
            UPDATE match.ticket SET state='Committed',version=version+1
            WHERE reservation_id=@reservation AND state IN ('Reserved','Preparing')
            """, connection, transaction))
        {
            tickets.Parameters.AddWithValue("reservation", reservationId);
            await tickets.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var commit = new NpgsqlCommand("""
            UPDATE match.reservation SET status='Committed' WHERE reservation_id=@reservation
            """, connection, transaction))
        {
            commit.Parameters.AddWithValue("reservation", reservationId);
            await commit.ExecuteNonQueryAsync(cancellationToken);
        }
        var committed = await ReadReservationRowAsync(reservationId, connection, transaction, cancellationToken);
        await WriteOutboxAsync($"{reservationId}/commit", "match.committed", new
        {
            ReservationId = reservationId, RoomId = roomId, TenantId = tenantId, GameId = gameId,
            committed.PartitionKey, committed.CoordinatorTerm, committed.Participants,
            RoomInstanceId = roomInstanceId, RoomEpoch = roomEpoch
        }, connection, transaction, cancellationToken);
        return await ReadReservationJsonAsync(reservationId, connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Cancels the reservation under the coordinator's term: the tickets return to the queue
    /// and a room the reservation already started is released with the full skeleton. A
    /// committed reservation never aborts.
    /// </summary>
    public async Task<MatchReservation> AbortReservationAsync(
        string tenantId, string gameId, string reservationId, long coordinatorTerm,
        string holderInstanceId, string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(reservationId, nameof(reservationId)); Require(holderInstanceId, nameof(holderInstanceId));
        Require(operationId, nameof(operationId));
        if (coordinatorTerm <= 0) throw new ArgumentOutOfRangeException(nameof(coordinatorTerm));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            ReservationId = reservationId
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "match.abort", reservationId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyAbortReservationAsync(tenantId, gameId, reservationId,
                coordinatorTerm, holderInstanceId, connection, transaction, token),
            cancellationToken);
        return Deserialize(receipt.ResultJson, receipt.Duplicate);
    }

    private async Task<string> ApplyAbortReservationAsync(string tenantId, string gameId, string reservationId,
        long coordinatorTerm, string holderInstanceId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var partitionKey = await ReadPartitionAsync(reservationId, connection, transaction, cancellationToken);
        await RequireTermAsync(partitionKey, coordinatorTerm, holderInstanceId, connection, transaction,
            cancellationToken);
        if (!await RetireAsync(reservationId, Aborted, "reservation-aborted", "match.aborted",
                $"{reservationId}/abort", requireExpired: false, connection, transaction, cancellationToken))
            throw new RedesignMatchException("The reservation is already committed or retired.");
        return await ReadReservationJsonAsync(reservationId, connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Retires expired reservations (Reserved or Prepared past the deadline). A terminator, not
    /// a term holder: time decides. A room the reservation already started is aborted with the
    /// full release skeleton (participations, pointers, ownership epoch bump); tickets return
    /// to the queue unless their own expiry passed. Capacity never leaks (AT11).
    /// </summary>
    public async Task<int> ExpireReservationsAsync(int max, CancellationToken cancellationToken = default)
    {
        if (max is <= 0 or > 100) throw new ArgumentException("The sweep window is 1..100 reservations.", nameof(max));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var expired = 0;
        // Candidates are picked without locks; RetireAsync re-validates under the row locks, so
        // a reservation renewed or retired in between is skipped rather than double-processed.
        await using (var sweep = new NpgsqlCommand("""
            SELECT reservation_id FROM match.reservation
            WHERE status IN ('Reserved','Prepared') AND expires_at<clock_timestamp()
            ORDER BY expires_at LIMIT @max
            """, connection, transaction))
        {
            sweep.Parameters.AddWithValue("max", max);
            var reservationIds = new List<string>();
            await using (var reader = await sweep.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) reservationIds.Add(reader.GetString(0));
            foreach (var reservationId in reservationIds)
                if (await RetireAsync(reservationId, Expired, "reservation-expired", "match.expired",
                        $"{reservationId}/expire", requireExpired: true, connection, transaction,
                        cancellationToken))
                    expired++;
        }
        await transaction.CommitAsync(cancellationToken);
        return expired;
    }

    /// <summary>Read-only reservation snapshot; null when unknown.</summary>
    public async Task<MatchReservation?> GetReservationAsync(
        string tenantId, string gameId, string reservationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(reservationId, nameof(reservationId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = ReadReservationCommand(reservationId, connection, null);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadReservation(reader, duplicate: false) : null;
    }

    /// <summary>
    /// The partitions holding unretired reservations — the recovery half of the coordinator's
    /// discovery: an orphaned reservation can be the only living thing in its partition.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListReservationPartitionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT partition_key FROM match.reservation
            WHERE status IN ('Reserved','Prepared')
            ORDER BY partition_key
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>
    /// The partition's unretired reservations in deadline order — the successor coordinator's
    /// recovery scan. Terminal reservations are history and never re-driven.
    /// </summary>
    public async Task<IReadOnlyList<MatchReservation>> ListActiveReservationsAsync(
        string partitionKey, int max, CancellationToken cancellationToken = default)
    {
        Require(partitionKey, nameof(partitionKey));
        if (max is <= 0 or > 500) throw new ArgumentException("The recovery window is 1..500 reservations.", nameof(max));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT reservation_id,room_id,tenant_id,game_id,partition_key,coordinator_term,
                (EXTRACT(EPOCH FROM expires_at)*1000)::bigint,status,participants::text,
                COALESCE(prepared_receipt::text,'')
            FROM match.reservation
            WHERE partition_key=@partition AND status IN ('Reserved','Prepared')
            ORDER BY expires_at LIMIT @max
            """, connection);
        command.Parameters.AddWithValue("partition", partitionKey);
        command.Parameters.AddWithValue("max", max);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var reservations = new List<MatchReservation>();
        while (await reader.ReadAsync(cancellationToken))
            reservations.Add(ReadReservation(reader, duplicate: false));
        return reservations;
    }

    /// <summary>
    /// The shared release path for retirement (explicit abort or the expiry sweep): abort a
    /// started room with the full skeleton, release the ownership with an epoch bump, return
    /// the tickets to the queue (unless their own expiry passed) and stamp the reservation.
    /// Locks the ownership before the reservation — the same pair in the same order as commit —
    /// so a concurrent commit serializes instead of deadlocking. Returns false when another
    /// writer already finished the reservation, or renewed it past the deadline, under the locks.
    /// </summary>
    private static async Task<bool> RetireAsync(string reservationId, string status, string reason,
        string outboxKind, string effectId, bool requireExpired,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        string roomId;
        await using (var navigation = new NpgsqlCommand(
            "SELECT room_id FROM match.reservation WHERE reservation_id=@reservation",
            connection, transaction))
        {
            navigation.Parameters.AddWithValue("reservation", reservationId);
            roomId = await navigation.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RedesignMatchException("The reservation does not exist.");
        }
        await using (var ownershipLock = new NpgsqlCommand("""
            SELECT 1 FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction))
        {
            ownershipLock.Parameters.AddWithValue("room", roomId);
            await ownershipLock.ExecuteNonQueryAsync(cancellationToken);
        }
        string tenantId; string gameId;
        await using (var reservation = new NpgsqlCommand("""
            SELECT tenant_id,game_id,status,expires_at<clock_timestamp()
            FROM match.reservation WHERE reservation_id=@reservation FOR UPDATE
            """, connection, transaction))
        {
            reservation.Parameters.AddWithValue("reservation", reservationId);
            await using var reader = await reservation.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RedesignMatchException("The reservation does not exist.");
            tenantId = reader.GetString(0);
            gameId = reader.GetString(1);
            if (reader.GetString(2) is not ("Reserved" or "Prepared"))
                return false;
            if (requireExpired && !reader.GetBoolean(3))
                return false;
        }

        string roomStatus;
        await using (var room = new NpgsqlCommand(
            "SELECT status FROM game.room WHERE room_id=@room FOR UPDATE", connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            roomStatus = await room.ExecuteScalarAsync(cancellationToken) as string ?? "None";
        }
        long releasedEpoch = 0;
        if (roomStatus is not ("None" or "Aborted" or "Settled" or "Closed"))
        {
            // The FinalizeRound/AbortRound release skeleton, run by a terminator that does not
            // hold the ownership: close participations, clear only matching pointers, abort the
            // room, release the ownership with an epoch bump.
            var released = new List<string>();
            await using (var close = new NpgsqlCommand("""
                UPDATE game.participation SET state='Closed'
                WHERE room_id=@room AND state IN ('Active','Offline')
                RETURNING binding_id
                """, connection, transaction))
            {
                close.Parameters.AddWithValue("room", roomId);
                await using var reader = await close.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    if (!reader.IsDBNull(0)) released.Add(reader.GetString(0));
            }
            foreach (var bindingId in released.OrderBy(id => id, StringComparer.Ordinal))
            {
                await using var pointer = new NpgsqlCommand("""
                    UPDATE live.binding SET current_room_id=NULL
                    WHERE binding_id=@binding AND current_room_id=@room
                    """, connection, transaction);
                pointer.Parameters.AddWithValue("binding", bindingId);
                pointer.Parameters.AddWithValue("room", roomId);
                await pointer.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var abort = new NpgsqlCommand(
                "UPDATE game.room SET status='Aborted',updated_at=clock_timestamp() WHERE room_id=@room",
                connection, transaction))
            {
                abort.Parameters.AddWithValue("room", roomId);
                await abort.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var outbox = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id,kind,payload,status)
                VALUES (@effect,'round.aborted',@payload::jsonb,'Pending')
                ON CONFLICT (effect_id) DO NOTHING
                """, connection, transaction))
            {
                outbox.Parameters.AddWithValue("effect", $"{roomId}/0/abort");
                outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
                {
                    RoomId = roomId, TenantId = tenantId, GameId = gameId, Reason = reason
                }));
                await outbox.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var ownership = new NpgsqlCommand("""
            UPDATE control.ownership SET status='Released',epoch=epoch+1,lease_until=clock_timestamp(),
                version=version+1
            WHERE kind='room' AND entity_id=@room AND status='Active'
            RETURNING epoch
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("room", roomId);
            var result = await ownership.ExecuteScalarAsync(cancellationToken);
            if (result is not null) releasedEpoch = Convert.ToInt64(result);
        }

        // Tickets return to the queue — unless their own expiry passed, in which case they
        // retire with the reservation.
        await using (var tickets = new NpgsqlCommand("""
            UPDATE match.ticket SET
                state=CASE WHEN expires_at IS NOT NULL AND expires_at<clock_timestamp()
                     THEN 'Expired' ELSE 'Queued' END,
                version=version+1,reservation_id=NULL
            WHERE reservation_id=@reservation AND state IN ('Reserved','Preparing')
            """, connection, transaction))
        {
            tickets.Parameters.AddWithValue("reservation", reservationId);
            await tickets.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var stamp = new NpgsqlCommand(
            "UPDATE match.reservation SET status=@status WHERE reservation_id=@reservation",
            connection, transaction))
        {
            stamp.Parameters.AddWithValue("status", status);
            stamp.Parameters.AddWithValue("reservation", reservationId);
            await stamp.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@effect,@kind,@payload::jsonb,'Pending')
            ON CONFLICT (effect_id) DO NOTHING
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("effect", effectId);
            outbox.Parameters.AddWithValue("kind", outboxKind);
            outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
            {
                ReservationId = reservationId, RoomId = roomId, Reason = reason, ReleasedEpoch = releasedEpoch
            }));
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    /// <summary>The coordinator gate (unified lock order: coordinator first). Fails visibly
    /// when the term no longer leads the partition — an expired term never commits.</summary>
    private static async Task RequireTermAsync(string partitionKey, long coordinatorTerm,
        string holderInstanceId, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var coordinator = new NpgsqlCommand("""
            SELECT holder_instance_id,term,lease_until>clock_timestamp() FROM control.coordinator
            WHERE partition_key=@partition FOR UPDATE
            """, connection, transaction);
        coordinator.Parameters.AddWithValue("partition", partitionKey);
        await using var reader = await coordinator.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != holderInstanceId
            || reader.GetInt64(1) != coordinatorTerm || !reader.GetBoolean(2))
            throw new RedesignStaleTermException();
    }

    private static async Task<string> ReadPartitionAsync(string reservationId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var partition = new NpgsqlCommand(
            "SELECT partition_key FROM match.reservation WHERE reservation_id=@reservation",
            connection, transaction);
        partition.Parameters.AddWithValue("reservation", reservationId);
        return await partition.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new RedesignMatchException("The reservation does not exist.");
    }

    private static NpgsqlCommand ReadReservationCommand(string reservationId, NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
    {
        var command = new NpgsqlCommand("""
            SELECT reservation_id,room_id,tenant_id,game_id,partition_key,coordinator_term,
                (EXTRACT(EPOCH FROM expires_at)*1000)::bigint,status,participants::text,
                COALESCE(prepared_receipt::text,'')
            FROM match.reservation WHERE reservation_id=@reservation
            """, connection, transaction);
        command.Parameters.AddWithValue("reservation", reservationId);
        return command;
    }

    private static async Task<MatchReservation> ReadReservationRowAsync(string reservationId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = ReadReservationCommand(reservationId, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new RedesignMatchException("The reservation does not exist.");
        return ReadReservation(reader, duplicate: false);
    }

    private static async Task<string> ReadReservationJsonAsync(string reservationId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var stored = await ReadReservationRowAsync(reservationId, connection, transaction, cancellationToken);
        return Serialize(new StoredReservation(stored.ReservationId, stored.RoomId, stored.TenantId,
            stored.GameId, stored.PartitionKey, stored.CoordinatorTerm,
            new DateTimeOffset(stored.ExpiresAtUtc).ToUnixTimeMilliseconds(), stored.Status,
            stored.Participants, stored.PreparedReceiptJson));
    }

    private static MatchReservation ReadReservation(NpgsqlDataReader reader, bool duplicate)
    {
        var participants = JsonSerializer.Deserialize<ReservedParticipant[]>(reader.GetString(8)) ?? [];
        return new MatchReservation(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)).UtcDateTime, reader.GetString(7),
            participants, reader.GetString(9) is { Length: > 0 } json ? json : null, duplicate);
    }

    private sealed record StoredReservation(
        string ReservationId, string RoomId, string TenantId, string GameId, string PartitionKey,
        long CoordinatorTerm, long ExpiresUnixMs, string Status,
        IReadOnlyList<ReservedParticipant> Participants, string? PreparedReceiptJson);

    private static string Serialize(StoredReservation stored) => JsonSerializer.Serialize(stored);

    private static MatchReservation Deserialize(string json, bool duplicate)
    {
        var stored = JsonSerializer.Deserialize<StoredReservation>(json)
            ?? throw new InvalidOperationException("The stored reservation receipt is unreadable.");
        return new MatchReservation(stored.ReservationId, stored.RoomId, stored.TenantId, stored.GameId,
            stored.PartitionKey, stored.CoordinatorTerm,
            stored.ExpiresUnixMs == 0 ? DateTime.UtcNow
                : DateTimeOffset.FromUnixTimeMilliseconds(stored.ExpiresUnixMs).UtcDateTime,
            stored.Status, stored.Participants, stored.PreparedReceiptJson, duplicate);
    }

    /// <summary>Counts a capacity rejection outside the refused transaction, like room admission.</summary>
    private async Task CountRejectionAsync(string kind, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var counter = new NpgsqlCommand("""
                INSERT INTO ops.capacity_rejection(kind,count,last_rejected_at)
                VALUES (@kind,1,clock_timestamp())
                ON CONFLICT (kind) DO UPDATE
                SET count = ops.capacity_rejection.count + 1, last_rejected_at = clock_timestamp()
                """, connection);
            counter.Parameters.AddWithValue("kind", kind);
            await counter.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException) { }
    }

    private static async Task WriteOutboxAsync(string effectId, string kind, object payload,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var outbox = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@effect,@kind,@payload::jsonb,'Pending')
            ON CONFLICT (effect_id) DO NOTHING
            """, connection, transaction);
        outbox.Parameters.AddWithValue("effect", effectId);
        outbox.Parameters.AddWithValue("kind", kind);
        outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await outbox.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
