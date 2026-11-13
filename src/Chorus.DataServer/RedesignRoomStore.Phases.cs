using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed partial class RedesignRoomStore
{
    /// <summary>
    /// Deposits the tick-0 snapshot of an admitted round (architecture 7.2): only a room in
    /// Preparing accepts it, so the initial state is pinned exactly once before activation.
    /// Idempotent per (room, operation); the same operation with a different snapshot conflicts.
    /// </summary>
    public async Task<RoundAdmission> PrepareRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, byte[] snapshotPayload, byte[] snapshotHash, int schemaVersion,
        string operationId, int tickMs = 50, string recoveryMode = "DeterministicReplay",
        string offlinePolicy = "", CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(snapshotPayload);
        ArgumentNullException.ThrowIfNull(snapshotHash);
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            RoomId = roomId, SnapshotHash = snapshotHash, SchemaVersion = schemaVersion
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.prepare", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyPrepareAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, snapshotPayload, snapshotHash, schemaVersion, tickMs, recoveryMode, offlinePolicy,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored prepare receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyPrepareAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, byte[] snapshotPayload, byte[] snapshotHash, int schemaVersion,
        int tickMs, string recoveryMode, string offlinePolicy, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var ownershipEpoch = await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        await using (var room = new NpgsqlCommand("""
            SELECT status FROM game.room WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            var status = await room.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RoundAdmissionException("The room does not exist.");
            if (status != Preparing)
                throw new RoundAdmissionException($"A room in status {status} cannot be prepared.");
        }
        await using (var snapshot = new NpgsqlCommand("""
            INSERT INTO game.snapshot(room_id,tick_id,schema_version,payload,payload_hash,verified)
            VALUES (@room,0,@schema,@payload,@hash,false)
            """, connection, transaction))
        {
            snapshot.Parameters.AddWithValue("room", roomId);
            snapshot.Parameters.AddWithValue("schema", schemaVersion);
            snapshot.Parameters.AddWithValue("payload", snapshotPayload);
            snapshot.Parameters.AddWithValue("hash", snapshotHash);
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        // The prepared cadence is a room fact the stall governor needs at recovery time
        // (FIX-TAKE-07): the host that pins the initial state also pins the tick cadence.
        // The recovery mode rides with it (FIX-MODULE-01): the same host names the
        // checkpoint policy the module was actually prepared under, so a recovery rebuilds
        // the module under the room's own identity instead of a caller-supplied guess.
        // The offline policy rides the same way (FIX-SYNC-04): the canonical declaration
        // the room was admitted under is what a later recovery must still match.
        // The paused-input policy is derived from that declaration's fifth field
        // (FIX-SYNC-07): the input store branches on it per accepted event while the
        // room is paused, so it is pinned as its own typed column here, once, from the
        // same canonical string the admission and recovery equality contract already freeze.
        await using (var cadence = new NpgsqlCommand(
            "UPDATE game.room SET tick_ms=@tickms, recovery_mode=@mode, offline_policy=@policy, " +
            "paused_input_policy=split_part(@policy,'|',5) " +
            "WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game",
            connection, transaction))
        {
            cadence.Parameters.AddWithValue("tickms", tickMs);
            cadence.Parameters.AddWithValue("mode", recoveryMode);
            cadence.Parameters.AddWithValue("policy", offlinePolicy);
            cadence.Parameters.AddWithValue("room", roomId);
            cadence.Parameters.AddWithValue("tenant", tenantId);
            cadence.Parameters.AddWithValue("game", gameId);
            await cadence.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(
            new RoundAdmission(roomId, 0, Preparing, ownershipEpoch, Duplicate: false));
    }

    /// <summary>
    /// Flips a prepared room to Running at state version 1 — the actor may commit frames only
    /// after this transaction. Activation without a tick-0 snapshot is rejected visibly, so a
    /// room can never run without its initial state pinned.
    /// </summary>
    public async Task<RoundAdmission> ActivateRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(operationId, nameof(operationId));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            JsonSerializer.SerializeToUtf8Bytes(new { RoomId = roomId }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.activate", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyActivateAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored activate receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyActivateAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ownershipEpoch = await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        await using (var room = new NpgsqlCommand("""
            SELECT status FROM game.room WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            var status = await room.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RoundAdmissionException("The room does not exist.");
            if (status != Preparing)
                throw new RoundAdmissionException($"A room in status {status} cannot be activated.");
        }
        await using (var prepared = new NpgsqlCommand(
            "SELECT 1 FROM game.snapshot WHERE room_id=@room AND tick_id=0", connection, transaction))
        {
            prepared.Parameters.AddWithValue("room", roomId);
            if (await prepared.ExecuteScalarAsync(cancellationToken) is null)
                throw new RoundAdmissionException("The round has no prepared initial state.");
        }
        await using (var activate = new NpgsqlCommand(
            "UPDATE game.room SET status=@status, state_version=1, updated_at=clock_timestamp() WHERE room_id=@room",
            connection, transaction))
        {
            activate.Parameters.AddWithValue("status", Running);
            activate.Parameters.AddWithValue("room", roomId);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteOutboxAsync($"{roomId}/0/activate", "round.activated", new
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Epoch = ownershipEpoch
        }, connection, transaction, cancellationToken);
        return JsonSerializer.Serialize(
            new RoundAdmission(roomId, 0, Running, ownershipEpoch, Duplicate: false));
    }

    /// <summary>
    /// Parks a damaged round visibly (isolation evidence for AT13): a suspended room accepts no
    /// frames and no new deliveries, but keeps its state for recovery (M2-4). Abort remains the
    /// terminal escape hatch for a suspended room that will never recover.
    /// </summary>
    public async Task<RoundAdmission> SuspendRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string reason, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(reason, nameof(reason));
        Require(operationId, nameof(operationId));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            JsonSerializer.SerializeToUtf8Bytes(new { RoomId = roomId, Reason = reason }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.suspend", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplySuspendAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, reason, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored suspend receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplySuspendAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string reason, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var ownershipEpoch = await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        await using (var room = new NpgsqlCommand("""
            SELECT status FROM game.room WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            var status = await room.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RoundAdmissionException("The room does not exist.");
            if (status != Running)
                throw new RoundAdmissionException($"A room in status {status} cannot be suspended.");
        }
        await using (var suspend = new NpgsqlCommand(
            "UPDATE game.room SET status=@status, updated_at=clock_timestamp() WHERE room_id=@room",
            connection, transaction))
        {
            suspend.Parameters.AddWithValue("status", Suspended);
            suspend.Parameters.AddWithValue("room", roomId);
            await suspend.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteOutboxAsync($"{roomId}/0/suspend", "round.suspended", new
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Epoch = ownershipEpoch, Reason = reason
        }, connection, transaction, cancellationToken);
        return JsonSerializer.Serialize(
            new RoundAdmission(roomId, 0, Suspended, ownershipEpoch, Duplicate: false));
    }

    /// <summary>
    /// FIX-SYNC-06: parks a live room at the committed boundary it just froze — all control
    /// sessions offline past the declared grace. The pause is a durable fact, not a stopped
    /// timer: status flips Running to Paused and the frozen head (last_tick, state_version)
    /// plus the wall-clock pause instant persist in the room row, so a process restart
    /// restores the paused room from that head without ticking. Only a Running room pauses;
    /// a repeated pause replays the stored receipt. A paused room accepts no frames — the
    /// freeze is the store's, and only a resume (SYNC-08) lifts it.
    /// </summary>
    public async Task<RoundAdmission> PauseRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string reason, string operationId, long tick,
        long stateVersion, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(reason, nameof(reason));
        Require(operationId, nameof(operationId));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (stateVersion < 1) throw new ArgumentOutOfRangeException(nameof(stateVersion));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            JsonSerializer.SerializeToUtf8Bytes(new { RoomId = roomId, Reason = reason }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.pause", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyPauseAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, reason, tick, stateVersion, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored pause receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyPauseAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string reason, long tick, long stateVersion,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var ownershipEpoch = await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        await using (var room = new NpgsqlCommand("""
            SELECT status,last_tick,state_version FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RoundAdmissionException("The room does not exist.");
            var status = reader.GetString(0);
            if (status != Running)
                throw new RoundAdmissionException($"A room in status {status} cannot be paused.");
            // The frozen boundary the actor named must be the room's actual head — a pause
            // parks the room exactly where it stopped, never ahead of or behind the truth.
            if (reader.GetInt64(1) != tick || reader.GetInt64(2) != stateVersion)
                throw new RoundAdmissionException(
                    $"The pause boundary tick {tick}/v{stateVersion} is not the room's committed " +
                    $"head (tick {reader.GetInt64(1)}/v{reader.GetInt64(2)}).");
        }
        await using (var pause = new NpgsqlCommand("""
            UPDATE game.room SET status=@status, paused_at=clock_timestamp(), paused_reason=@reason,
                updated_at=clock_timestamp()
            WHERE room_id=@room
            """, connection, transaction))
        {
            pause.Parameters.AddWithValue("status", Paused);
            pause.Parameters.AddWithValue("reason", reason);
            pause.Parameters.AddWithValue("room", roomId);
            await pause.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteOutboxAsync($"{roomId}/{tick}/pause", "round.paused", new
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Epoch = ownershipEpoch,
            Reason = reason, Tick = tick, StateVersion = stateVersion
        }, connection, transaction, cancellationToken);
        return JsonSerializer.Serialize(
            new RoundAdmission(roomId, tick, Paused, ownershipEpoch, Duplicate: false));
    }

    /// <summary>FIX-SYNC-08: a control participant's declaration that its current-generation
    /// session re-established on the paused round (authoritative-sync.md:53). The streamer
    /// reports from its mount path — the mount is refused while the round is paused, so that
    /// refusal is the signal that the session stands at the current generation on the paused
    /// round. The store resolves the paused room from the streamer's open live session (its
    /// single active binding is the paused round's admission), so the report names no room: a
    /// streamer with no paused round is a clean no-op. The report is refused unless the
    /// reporting session is the streamer's CURRENT session — a displaced or rotated-out
    /// session's readiness never counts (旧会话准备不计数). The frozen head the store reads
    /// becomes the ready's baseline, and the current binding/connection generations are read
    /// (never reported) so a stale value cannot be smuggled in. One durable row per participant;
    /// re-reporting refreshes it, never doubles the count.</summary>
    public async Task<ResumeReady> ReportResumeReadyAsync(string tenantId, string gameId,
        string streamerId, string sessionId, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(streamerId, nameof(streamerId)); Require(sessionId, nameof(sessionId));
        Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            JsonSerializer.SerializeToUtf8Bytes(new { StreamerId = streamerId, SessionId = sessionId }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.ready", streamerId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyReportResumeReadyAsync(tenantId, gameId, streamerId,
                sessionId, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<ResumeReady>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored ready receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyReportResumeReadyAsync(string tenantId, string gameId,
        string streamerId, string sessionId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // The stale-session fence: the reporting session must be the streamer's CURRENT session.
        // A displaced connection or a rotated-out session is the "旧会话准备不计数" case — it is
        // refused, never silently accepted as a fresh ready.
        await using (var fence = new NpgsqlCommand(
            "SELECT current_session_id FROM auth.streamer_state WHERE streamer_id=@streamer",
            connection, transaction))
        {
            fence.Parameters.AddWithValue("streamer", streamerId);
            var current = (string?)await fence.ExecuteScalarAsync(cancellationToken);
            if (current is null || !StringComparer.Ordinal.Equals(current, sessionId))
                throw new RoundAdmissionException(
                    "The reporting session is not the streamer's current session; an old session's " +
                    "readiness never counts.");
        }
        // Resolve the paused room from the streamer's open live session. The session holds one
        // active binding (ux_live_binding_active), which participates in at most one room; the
        // room is locked before the participation read so a concurrent resume and this report
        // serialize on the room row. A streamer with no paused round is a clean no-op.
        var requiredCount = 0;
        string roomId, participantId, bindingId;
        long epoch, baselineTick, baselineVersion;
        await using (var room = new NpgsqlCommand($"""
            SELECT r.room_id, p.participant_id, b.binding_id, r.epoch, r.last_tick, r.state_version,
                   r.offline_policy
            FROM live.session s
            JOIN live.binding b ON b.live_session_id = s.live_session_id
            JOIN game.participation p ON p.binding_id = b.binding_id AND p.state IN ('Active','Offline')
            JOIN game.room r ON r.room_id = p.room_id AND r.tenant_id=@tenant AND r.game_id=@game
            WHERE s.streamer_id=@streamer AND s.ended_at IS NULL
              AND b.game_id=@game AND b.state IN {ActiveStates}
              AND r.status=@paused
            FOR UPDATE OF r
            LIMIT 1
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            room.Parameters.AddWithValue("streamer", streamerId);
            room.Parameters.AddWithValue("paused", Paused);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return JsonSerializer.Serialize(new ResumeReady("", 0, 0, Paused: false, Duplicate: false));
            roomId = reader.GetString(0);
            participantId = reader.GetString(1);
            bindingId = reader.GetString(2);
            epoch = reader.GetInt64(3);
            baselineTick = reader.GetInt64(4);
            baselineVersion = reader.GetInt64(5);
            // The declared threshold (canonical field 4), reported back for observability.
            requiredCount = int.TryParse(reader.IsDBNull(6) ? "" : reader.GetString(6).Split('|')
                .ElementAtOrDefault(3), out var declared) ? declared : 0;
        }
        // The current facts the ready is declared against: the binding's mount generation and the
        // session's connection generation. Both are read here (never reported), so a stale value
        // cannot be smuggled in — the row records exactly the current generation at the report.
        long bindingGeneration, connectionGeneration;
        await using (var gen = new NpgsqlCommand(
            "SELECT generation FROM live.binding WHERE binding_id=@binding", connection, transaction))
        {
            gen.Parameters.AddWithValue("binding", bindingId);
            var value = await gen.ExecuteScalarAsync(cancellationToken);
            if (value is null) throw new RoundAdmissionException("The binding does not exist.");
            bindingGeneration = Convert.ToInt64(value);
        }
        await using (var gen = new NpgsqlCommand(
            "SELECT connection_generation FROM auth.session WHERE session_id=@session", connection, transaction))
        {
            gen.Parameters.AddWithValue("session", sessionId);
            var value = await gen.ExecuteScalarAsync(cancellationToken);
            if (value is null) throw new RoundAdmissionException("The session does not exist.");
            connectionGeneration = Convert.ToInt64(value);
        }
        await using (var write = new NpgsqlCommand("""
            INSERT INTO game.resume_ready(room_id,participant_id,binding_id,binding_generation,
                connection_generation,epoch,baseline_tick,baseline_version,ready_at)
            VALUES (@room,@participant,@binding,@binding_generation,@connection_generation,
                @epoch,@baseline_tick,@baseline_version,clock_timestamp())
            ON CONFLICT (room_id,participant_id) DO UPDATE SET
                binding_id=EXCLUDED.binding_id,
                binding_generation=EXCLUDED.binding_generation,
                connection_generation=EXCLUDED.connection_generation,
                epoch=EXCLUDED.epoch, baseline_tick=EXCLUDED.baseline_tick,
                baseline_version=EXCLUDED.baseline_version, ready_at=clock_timestamp()
            """, connection, transaction))
        {
            write.Parameters.AddWithValue("room", roomId);
            write.Parameters.AddWithValue("participant", participantId);
            write.Parameters.AddWithValue("binding", bindingId);
            write.Parameters.AddWithValue("binding_generation", bindingGeneration);
            write.Parameters.AddWithValue("connection_generation", connectionGeneration);
            write.Parameters.AddWithValue("epoch", epoch);
            write.Parameters.AddWithValue("baseline_tick", baselineTick);
            write.Parameters.AddWithValue("baseline_version", baselineVersion);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        var readyCount = await ResumeReadyCountAsync(roomId, epoch, connection, transaction, cancellationToken);
        return JsonSerializer.Serialize(new ResumeReady(roomId, readyCount, requiredCount,
            Paused: true, Duplicate: false));
    }

    /// <summary>FIX-SYNC-08: lifts a paused room back to Running at its committed frozen
    /// boundary once the configured number of control participants reported ready
    /// (authoritative-sync.md:53 "followed by a committed resume boundary"). The boundary the
    /// caller names must be the frozen head, and the ready count must reach the declared
    /// threshold — below it the resume is refused (未达到人数不恢复). The flip is one receipt
    /// transaction, so a retry after a lost reply replays the same transition; the room's
    /// ready rows are cleared in the same transaction (they are spent by the resume), and the
    /// paused-input buffer the resumed actor drains is untouched — the freeze is the store's.
    /// </summary>
    public async Task<RoundAdmission> ResumeRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, long tick, long stateVersion, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(operationId, nameof(operationId));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (stateVersion < 1) throw new ArgumentOutOfRangeException(nameof(stateVersion));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            JsonSerializer.SerializeToUtf8Bytes(new { RoomId = roomId, Tick = tick }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.resume", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyResumeRoundAsync(tenantId, gameId, roomId,
                ownerInstanceId, epoch, tick, stateVersion, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored resume receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyResumeRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, long tick, long stateVersion,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var ownershipEpoch = await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        var (required, readyCount, roomEpoch) = (0, 0, 0L);
        await using (var room = new NpgsqlCommand("""
            SELECT status,last_tick,state_version,offline_policy,epoch FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RoundAdmissionException("The room does not exist.");
            var status = reader.GetString(0);
            if (status != Paused)
                throw new RoundAdmissionException($"A room in status {status} cannot be resumed.");
            // The boundary the actor names must be the room's actual frozen head — a resume
            // lifts exactly where the pause parked it, never ahead of or behind the truth.
            if (reader.GetInt64(1) != tick || reader.GetInt64(2) != stateVersion)
                throw new RoundAdmissionException(
                    $"The resume boundary tick {tick}/v{stateVersion} is not the room's frozen " +
                    $"head (tick {reader.GetInt64(1)}/v{reader.GetInt64(2)}).");
            // The declared ready threshold (canonical field 4). A room with no declared
            // policy is not resumable — fail closed, never resume under an undeclared rule.
            required = int.TryParse(reader.IsDBNull(3) ? "" : reader.GetString(3).Split('|').ElementAtOrDefault(3),
                out var declared) ? declared : 0;
            roomEpoch = reader.GetInt64(4);
        }
        // A ready binds to the room's epoch it was declared against: a takeover's epoch bump
        // invalidates every prior ready, so an old-epoch row never pads the resume count.
        readyCount = await ResumeReadyCountAsync(roomId, roomEpoch, connection, transaction, cancellationToken);
        if (required < 1)
            throw new RoundAdmissionException("The room declares no resume ready count; it cannot resume.");
        if (readyCount < required)
            throw new RoundAdmissionException(
                $"The resume needs {required} ready control participant(s); only {readyCount} reported.");
        await using (var resume = new NpgsqlCommand("""
            UPDATE game.room SET status=@status, updated_at=clock_timestamp() WHERE room_id=@room
            """, connection, transaction))
        {
            resume.Parameters.AddWithValue("status", Running);
            resume.Parameters.AddWithValue("room", roomId);
            await resume.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clear = new NpgsqlCommand(
            "DELETE FROM game.resume_ready WHERE room_id=@room", connection, transaction))
        {
            clear.Parameters.AddWithValue("room", roomId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteOutboxAsync($"{roomId}/{tick}/resume", "round.resumed", new
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Epoch = ownershipEpoch,
            Tick = tick, StateVersion = stateVersion, ReadyCount = readyCount
        }, connection, transaction, cancellationToken);
        return JsonSerializer.Serialize(
            new RoundAdmission(roomId, tick, Running, ownershipEpoch, Duplicate: false));
    }

    private static async Task<int> ResumeReadyCountAsync(string roomId, long epoch,
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        // A ready binds to the epoch it was declared against — a takeover's epoch bump invalidates
        // every prior ready, so an old-epoch row never pads the resume count.
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM game.resume_ready WHERE room_id=@room AND epoch=@epoch",
            connection, transaction);
        count.Parameters.AddWithValue("room", roomId);
        count.Parameters.AddWithValue("epoch", epoch);
        return Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
    }
}
