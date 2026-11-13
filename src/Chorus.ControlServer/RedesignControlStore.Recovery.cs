using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

/// <summary>A persistent room-recovery operation (FIX-TAKE-04): the transfer's target and
/// epoch, the open phase, its deadline, the committed progress and the last error.</summary>
public sealed record RoomRecoveryOperation(
    string TenantId, string GameId, string OperationId, string RoomId, string FromInstance,
    string ToInstance, long TargetEpoch, string Phase, DateTime DeadlineAt, long LastTick,
    string? LastError);

public sealed partial class RedesignControlStore
{
    /// <summary>
    /// The open (still Transferred or Prepared) room-recovery operation, or null
    /// (FIX-TAKE-04). A restarted control plane continues from here — a timed-out recovery
    /// RPC left the operation open, and continuing it never needs a second transfer or
    /// epoch bump. FIX-REC-01: a Prepared row counts as open too — the room recovered but
    /// awaits its route barrier and activation, still fenced Recovering.
    /// </summary>
    public async Task<RoomRecoveryOperation?> GetOpenRoomRecoveryAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT operation_id,from_instance,to_instance,target_epoch,phase,deadline_at,last_tick,last_error
            FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
              AND phase IN ('Transferred','Prepared')
            ORDER BY created_at DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RoomRecoveryOperation(tenantId, gameId, reader.GetString(0), roomId, reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetDateTime(5),
                reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;
    }

    /// <summary>
    /// Records a recovery attempt's outcome on the room's open operation row: the phase
    /// (Activated on success, Failed with the reason otherwise) and the committed progress.
    /// The row is progress, never authority — the ownership row stays the fence. A no-op
    /// when no open row exists, so rooms transferred before the record landed still recover.
    /// FIX-REC-01: the record also lands from the Prepared phase — a control-plane restart
    /// between the recovery's Ready and its activation completes from there instead of
    /// re-reading an RPC reply.
    /// </summary>
    public async Task CompleteRoomRecoveryAsync(string tenantId, string gameId, string roomId,
        bool recovered, long lastTick, string? lastError, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.recovery_operation
            SET phase=@phase, last_tick=GREATEST(last_tick,@tick),
                last_error=@error, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
              AND phase IN ('Transferred','Prepared')
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("phase", recovered ? "Activated" : "Failed");
        command.Parameters.AddWithValue("tick", lastTick);
        command.Parameters.AddWithValue("error", lastError is null ? DBNull.Value : lastError);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // FIX-REC-01: how long a Prepared recovery (recovery landed, routes installing) may sit
    // past its preparation before the deadline sweep treats it as expired. Covers the route
    // barrier wait (10 s), the activation RPC (10 s) and one retry margin.
    private const int PreparedGraceSeconds = 45;

    /// <summary>
    /// Records the recovery preparation on the room's open operation row (FIX-REC-01): the
    /// target pinned the verified head and holds the actor, so the phase becomes Prepared
    /// and the deadline extends — the barrier wait and the activation must not be swept away
    /// by the transfer's own lease. A barrier that never confirms re-marks the same phase
    /// with the error for the operator and the sweep. Idempotent from Transferred or
    /// Prepared; a no-op on any other phase.
    /// </summary>
    public async Task MarkRoomRecoveryPreparedAsync(string tenantId, string gameId, string roomId,
        string? error, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.recovery_operation
            SET phase='Prepared', deadline_at=clock_timestamp()+@grace*interval '1 second',
                last_error=@error, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
              AND phase IN ('Transferred','Prepared')
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("grace", PreparedGraceSeconds);
        command.Parameters.AddWithValue("error", error is null ? DBNull.Value : error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// FIX-REC-01, the recovery route barrier (architecture 10.3): the control plane
    /// activates a recovered room only once every deployed gateway kind has installed and
    /// confirmed the routing version that carries the room's takeover (the snapshot row
    /// already carries the new epoch and the Recovering status since the transfer). Per
    /// role — platform-gateway and streamer-gateway — the version must be confirmed by an
    /// instance of that role while any instance of it is registered; a role with no
    /// registered process is no delivery path to wait for (the FIX-END-16 boundary, per
    /// kind). The confirmations are the persisted routing_confirm rows, never "the notice
    /// was sent". Times out false: the caller must not grant the activation.
    /// </summary>
    public async Task<bool> WaitForRecoveryRoutesConfirmedAsync(string roomId, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = await ReadRoutingSnapshotAsync(cancellationToken);
            if (snapshot.Rooms.Any(room => room.RoomId == roomId)
                && await AreGatewayRoutesConfirmedAsync(snapshot.Version, cancellationToken))
                return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(RouteConfirmPollMs, cancellationToken);
        }
    }

    /// <summary>Whether every gateway role with a registered process confirmed this exact
    /// snapshot version. A version carries all rooms, so one confirm per role is the role's
    /// installation evidence (the room's presence in the snapshot is what ties the version
    /// to the room).</summary>
    private async Task<bool> AreGatewayRoutesConfirmedAsync(long version,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.role,
                   count(DISTINCT p.process_instance_id) AS registered,
                   count(DISTINCT CASE WHEN rc.version=@version THEN p.process_instance_id END) AS confirmed
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            LEFT JOIN control.routing_confirm rc ON rc.process_instance_id=p.process_instance_id
            WHERE d.role IN ('platform-gateway','streamer-gateway')
            GROUP BY d.role
            """, connection);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (reader.GetInt64(2) == 0) return false;
        // Every registered role confirmed (an empty result is the bare cluster FIX-END-16
        // boundary — no gateway process at all, no delivery path to wait for).
        return true;
    }

    /// <summary>
    /// Open recovery operations past their deadline, oldest first (FIX-TAKE-05): the sweep's
    /// work list. Deadline comparison is database time — a target that keeps reporting live
    /// but never finishes its recovery appears here exactly like a silent one.
    /// </summary>
    public async Task<List<RoomRecoveryOperation>> ListExpiredOpenRoomRecoveriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id,game_id,operation_id,room_id,from_instance,to_instance,
                target_epoch,phase,deadline_at,last_tick,last_error
            FROM control.recovery_operation
            WHERE phase IN ('Transferred','Prepared') AND deadline_at<clock_timestamp()
            ORDER BY deadline_at LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var expired = new List<RoomRecoveryOperation>();
        while (await reader.ReadAsync(cancellationToken))
            expired.Add(new RoomRecoveryOperation(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6), reader.GetString(7), reader.GetDateTime(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        return expired;
    }

    /// <summary>FIX-SYNC-08: a paused room whose ready count reached the declared threshold —
    /// the resume leg's work list (authoritative-sync.md:53). The count and the threshold are
    /// both derived in SQL so the driver never parses a canonical; a room with no declared
    /// policy (an empty field) yields a NULL comparison and never qualifies.</summary>
    public sealed record ResumeReadyRoom(string RoomId, string TenantId, string GameId, long Epoch);

    /// <summary>FIX-SYNC-08: a paused room whose pause exceeded its declared maximum duration
    /// — the timeout leg's work list (authoritative-sync.md:54). Any paused room with a
    /// positive declared cap qualifies; a room with no declared cap (an empty field) never
    /// times out.</summary>
    public sealed record OverduePausedRoom(string RoomId, string TenantId, string GameId, long Epoch);

    public async Task<List<ResumeReadyRoom>> ListResumeReadyPausedRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT g.room_id,g.tenant_id,g.game_id,g.epoch
            FROM game.room g
            WHERE g.status='Paused'
              AND (SELECT count(*) FROM game.resume_ready r
                   WHERE r.room_id=g.room_id AND r.epoch=g.epoch)
                  >= NULLIF(split_part(g.offline_policy,'|',4),'')::int
            ORDER BY g.paused_at LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rooms = new List<ResumeReadyRoom>();
        while (await reader.ReadAsync(cancellationToken))
            rooms.Add(new ResumeReadyRoom(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3)));
        return rooms;
    }

    public async Task<List<OverduePausedRoom>> ListOverduePausedRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT g.room_id,g.tenant_id,g.game_id,g.epoch
            FROM game.room g
            WHERE g.status='Paused' AND g.paused_at IS NOT NULL
              AND g.paused_at < clock_timestamp()
                  - NULLIF(split_part(g.offline_policy,'|',3),'')::int * interval '1 second'
            ORDER BY g.paused_at LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rooms = new List<OverduePausedRoom>();
        while (await reader.ReadAsync(cancellationToken))
            rooms.Add(new OverduePausedRoom(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3)));
        return rooms;
    }

    /// <summary>
    /// How many recovery operations a room has ever recorded — the sweep's reassignment
    /// budget. A room that keeps timing out stops being moved automatically and waits for
    /// an operator instead of churning epochs forever.
    /// </summary>
    public async Task<int> CountRoomRecoveryOperationsAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>One room's reported progress (FIX-TAKE-06).</summary>
    public sealed record RoomProgressEntry(string RoomId, long Epoch, string State, long LastTick,
        long? LastCommittedUnixMs, string? StopReason);

    /// <summary>One stalled room as the stall query names it (FIX-TAKE-06). The tenant, game
    /// and prepared tick cadence ride along so governance can act without a second read
    /// (FIX-TAKE-07); they are null only when the room's own row is already gone.</summary>
    public sealed record StalledRoom(string ProcessInstanceId, string? TenantId, string? GameId,
        string RoomId, long Epoch, string State, long LastTick, int TickMs, string? StopReason);

    /// <summary>
    /// Records one report's per-room progress (FIX-TAKE-06), in one transaction with the
    /// pruning of rooms the process stopped naming. progress_at is database time and only
    /// moves when the reported head (state or tick) moves — a frozen tick across reports
    /// is the stall signal, never fresh evidence. An out-of-order report (a stale sequence)
    /// never regresses a row.
    /// </summary>
    public async Task RecordRoomProgressAsync(string processInstanceId, long reportSequence,
        IReadOnlyList<RoomProgressEntry> progress, CancellationToken cancellationToken = default)
    {
        if (reportSequence < 0) throw new ArgumentOutOfRangeException(nameof(reportSequence));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in progress)
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO control.room_progress(process_instance_id,room_id,epoch,state,
                    last_tick,last_committed_at,stop_reason,report_sequence)
                VALUES (@process,@room,@epoch,@state,@tick,
                    CASE WHEN @ms>0 THEN to_timestamp(@ms::double precision/1000.0) END,
                    @reason,@seq)
                ON CONFLICT (process_instance_id, room_id) DO UPDATE SET
                    epoch=excluded.epoch, state=excluded.state, last_tick=excluded.last_tick,
                    last_committed_at=excluded.last_committed_at, stop_reason=excluded.stop_reason,
                    report_sequence=excluded.report_sequence, reported_at=clock_timestamp(),
                    progress_at=CASE WHEN excluded.state=control.room_progress.state
                            AND excluded.last_tick=control.room_progress.last_tick
                        THEN control.room_progress.progress_at ELSE clock_timestamp() END
                WHERE excluded.report_sequence >= control.room_progress.report_sequence
                """, connection, transaction);
            upsert.Parameters.AddWithValue("process", processInstanceId);
            upsert.Parameters.AddWithValue("room", entry.RoomId);
            upsert.Parameters.AddWithValue("epoch", entry.Epoch);
            upsert.Parameters.AddWithValue("state", entry.State);
            upsert.Parameters.AddWithValue("tick", entry.LastTick);
            upsert.Parameters.AddWithValue("ms", entry.LastCommittedUnixMs ?? 0L);
            upsert.Parameters.AddWithValue("reason",
                (object?)entry.StopReason ?? DBNull.Value);
            upsert.Parameters.AddWithValue("seq", reportSequence);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        // A room the process no longer names has no reporter; keeping its row would let a
        // dead report masquerade as live evidence.
        await using var prune = new NpgsqlCommand(
            "DELETE FROM control.room_progress WHERE process_instance_id=@process AND NOT (room_id = ANY(@rooms))",
            connection, transaction);
        prune.Parameters.AddWithValue("process", processInstanceId);
        prune.Parameters.AddWithValue("rooms",
            progress.Select(entry => entry.RoomId).ToArray());
        await prune.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The rooms whose gameplay stopped while their host's heartbeat stayed fresh (FIX-TAKE-06,
    /// the identification half; the governance policy is FIX-TAKE-07). A silently frozen
    /// Running actor (database-time progress older than the threshold) and an explicitly
    /// Stopped actor both appear; a business-paused room — its actor parked by the store's
    /// suspension rejection — is not faulty merely because its tick froze, so that exact
    /// park reason is excluded. A completed round is normal, never stalled.
    /// </summary>
    public async Task<List<StalledRoom>> ListStalledRoomsAsync(int stallSeconds, int freshSeconds,
        CancellationToken cancellationToken = default)
    {
        if (stallSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(stallSeconds));
        if (freshSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(freshSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.process_instance_id, g.tenant_id, g.game_id, r.room_id, r.epoch, r.state,
                r.last_tick, g.tick_ms, r.stop_reason
            FROM control.room_progress r
            JOIN control.process p ON p.process_instance_id = r.process_instance_id
            LEFT JOIN game.room g ON g.room_id = r.room_id
            WHERE p.observed_at > clock_timestamp() - @fresh * interval '1 second'
              AND ( (r.state = 'Running'
                        AND r.progress_at < clock_timestamp() - @stall * interval '1 second')
                 OR r.state = 'Stopped' )
              AND NOT ( r.state = 'Stopped'
                    AND r.stop_reason LIKE '%A room in status Suspended does not accept frame commits%'
                    AND coalesce(g.status, '') = 'Suspended' )
            ORDER BY r.progress_at
            """, connection);
        command.Parameters.AddWithValue("stall", stallSeconds);
        command.Parameters.AddWithValue("fresh", freshSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var stalled = new List<StalledRoom>();
        while (await reader.ReadAsync(cancellationToken))
            stalled.Add(new StalledRoom(reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? 0 : reader.GetInt32(7),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8)));
        return stalled;
    }
}
