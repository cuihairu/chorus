using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed class RoundAdmissionException : Exception
{
    public RoundAdmissionException(string message) : base(message) { }
}

/// <summary>
/// The capacity guard (architecture 9 data retention, AT21): pressure on the shared store
/// rejects new rounds visibly with a counted reason — it never frees space by deleting.
/// </summary>
public sealed class RedesignCapacityException(string message) : Exception(message);

public sealed record RoundParticipant(string BindingId, string ParticipantId, string Role);

public sealed record RoundAdmission(string RoomId, long RoundGeneration, string Status, long Epoch, bool Duplicate);

/// <summary>FIX-SYNC-08: the answer a resume-ready report returns — how many control
/// participants have reported ready against the frozen head and how many the declared
/// policy requires before a resume boundary may commit.</summary>
public sealed record ResumeReady(string RoomId, int ReadyCount, int RequiredReadyCount, bool Paused, bool Duplicate);

public sealed record RoundParticipantState(string ParticipantId, string BindingId, string Role, string State);

public sealed record RoundStatus(
    string RoomId, string Status, long Epoch, long LastTick, long StateVersion,
    IReadOnlyList<RoundParticipantState> Participants);

/// <summary>An ownership handover: the new holder, the bumped epoch, the post-transfer state.</summary>
public sealed record RoomTakeover(string OwnerInstanceId, long Epoch, string Status);

/// <summary>
/// Round admission on the shared mount pointers (architecture 2, 6.2). Starting a round is one
/// receipt transaction: the room ownership fence, every participant binding and the room row are
/// locked in the unified order, participations are created, and each binding's current room
/// pointer moves with a per-mount round counter. Aborting releases exactly the same set — the
/// FinalizeRound skeleton minus settlement — so a mount can enter its next round generation.
/// </summary>
/// <param name="crashAfterPointerUpdate">
/// Fault-injection seam for restart tests: invoked inside the transaction after the binding
/// pointers moved, before the commit. An exception there plays a process dying between the
/// pointer update and the acknowledgment; the rollback leaves no room behind.
/// </param>
public sealed class RedesignRoomStore(string connectionString, Action? crashAfterPointerUpdate = null,
    int? maxActiveRooms = null)
{
    public const string Preparing = "Preparing";
    public const string Aborted = "Aborted";
    public const string Running = "Running";
    public const string Suspended = "Suspended";
    // FIX-SYNC-06: the room paused at a committed boundary — all control sessions offline
    // past the declared grace. A paused room accepts no frames and a recovery restores
    // Paused (no tick loop); a later resume (SYNC-08) restarts the tick from the frozen head.
    public const string Paused = "Paused";
    private const string ActiveStates = "('Starting','Ready','Running','AwaitingPlatformClose')";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Admits a round under the caller's ownership fence. Idempotent per (room, operation); the
    /// same operation with different participants or module pinning conflicts. A binding that
    /// still holds an active or offline participation is rejected visibly.
    /// </summary>
    public async Task<RoundAdmission> StartRoundAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, IReadOnlyList<RoundParticipant> participants,
        string moduleVersion, string configHash, string packageHash, string operationId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(moduleVersion, nameof(moduleVersion));
        Require(configHash, nameof(configHash)); Require(packageHash, nameof(packageHash));
        Require(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(participants);
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (participants.Count == 0)
            throw new ArgumentException("A round needs at least one participant.", nameof(participants));
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
            RoomId = roomId, OwnerInstanceId = ownerInstanceId, Epoch = epoch, ModuleVersion = moduleVersion,
            ConfigHash = configHash, PackageHash = packageHash,
            Participants = ordered.Select(participant => new
            {
                participant.BindingId, participant.ParticipantId, participant.Role
            })
        }));
        OperationReceipt receipt;
        try
        {
            receipt = await _receipts.ExecuteAsync(
                new OperationIdentity(tenantId, gameId, "round.start", roomId, operationId),
                fingerprint,
                (connection, transaction, token) => ApplyStartAsync(tenantId, gameId, roomId, ownerInstanceId,
                    epoch, ordered, moduleVersion, configHash, packageHash, connection, transaction, token),
                cancellationToken);
        }
        catch (RedesignCapacityException)
        {
            await CountRejectionAsync("start-round:active-rooms", cancellationToken);
            throw;
        }
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored round receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    /// <summary>
    /// Counts a capacity rejection where it belongs — outside the refused admission's
    /// transaction, so the visible evidence survives the rollback the rejection causes.
    /// </summary>
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
        catch (PostgresException)
        {
            // The counter is a metric on the refusal path; never let it mask the refusal.
        }
    }

    private async Task<string> ApplyStartAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, RoundParticipant[] participants,
        string moduleVersion, string configHash, string packageHash,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (maxActiveRooms is { } cap)
        {
            // Admissions serialize on one advisory lock so concurrent starts cannot race
            // past the cap; the count is every non-terminal room, because suspended rounds
            // still hold recovery-relevant data (AT21).
            await using (var admissionLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('game.room:admission'))", connection, transaction))
                await admissionLock.ExecuteNonQueryAsync(cancellationToken);
            await using (var active = new NpgsqlCommand("""
                SELECT count(*) FROM game.room
                WHERE status NOT IN ('Aborted','Settled','Closed')
                """, connection, transaction))
            {
                if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken)) >= cap)
                    throw new RedesignCapacityException(
                        $"The node already runs {cap} non-terminal rooms; new rounds are rejected " +
                        "until capacity frees — retention never deletes quietly to make room.");
            }
        }
        var ownershipEpoch = await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        long roundGeneration = 0;
        var generations = new (string BindingId, long Generation)[participants.Length];
        for (var index = 0; index < participants.Length; index++)
        {
            var participant = participants[index];
            await using (var binding = new NpgsqlCommand($"""
                SELECT tenant_id,game_id,state,current_room_id FROM live.binding
                WHERE binding_id=@binding FOR UPDATE
                """, connection, transaction))
            {
                binding.Parameters.AddWithValue("binding", participant.BindingId);
                await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new RoundAdmissionException("A participant binding does not exist.");
                if (reader.GetString(0) != tenantId || reader.GetString(1) != gameId)
                    throw new RoundAdmissionException("A participant binding belongs to another tenant or game.");
                if (!ActiveStates.Contains("'" + reader.GetString(2) + "'"))
                    throw new RoundAdmissionException("A participant binding is not in an active state.");
                if (!reader.IsDBNull(3) && reader.GetString(3) is not null)
                    throw new RoundAdmissionException("A participant binding already points at a room.");
            }
            await using (var active = new NpgsqlCommand("""
                SELECT 1 FROM game.participation WHERE binding_id=@binding
                  AND state IN ('Active','Offline')
                """, connection, transaction))
            {
                active.Parameters.AddWithValue("binding", participant.BindingId);
                if (await active.ExecuteScalarAsync(cancellationToken) is not null)
                    throw new RoundAdmissionException("The binding still holds a live participation.");
            }
            await using (var pointer = new NpgsqlCommand("""
                UPDATE live.binding SET current_room_id=@room, round_generation=round_generation+1
                WHERE binding_id=@binding RETURNING round_generation
                """, connection, transaction))
            {
                pointer.Parameters.AddWithValue("room", roomId);
                pointer.Parameters.AddWithValue("binding", participant.BindingId);
                generations[index] = (participant.BindingId,
                    Convert.ToInt64(await pointer.ExecuteScalarAsync(cancellationToken)));
            }
            roundGeneration = Math.Max(roundGeneration, generations[index].Generation);
        }
        await using (var room = new NpgsqlCommand("""
            INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash)
            VALUES (@room,@tenant,@game,@status,@epoch,@package,@module,@config)
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            room.Parameters.AddWithValue("status", Preparing);
            room.Parameters.AddWithValue("epoch", ownershipEpoch);
            room.Parameters.AddWithValue("package", packageHash);
            room.Parameters.AddWithValue("module", moduleVersion);
            room.Parameters.AddWithValue("config", configHash);
            await room.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var participant in participants)
        {
            await using (var participation = new NpgsqlCommand("""
                INSERT INTO game.participation(room_id,participant_id,binding_id,role,state)
                VALUES (@room,@participant,@binding,@role,'Active')
                """, connection, transaction))
            {
                participation.Parameters.AddWithValue("room", roomId);
                participation.Parameters.AddWithValue("participant", participant.ParticipantId);
                participation.Parameters.AddWithValue("binding", participant.BindingId);
                participation.Parameters.AddWithValue("role", participant.Role);
                await participation.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await WriteOutboxAsync($"{roomId}/0/start", "round.started", new
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Epoch = ownershipEpoch,
            Participants = generations
        }, connection, transaction, cancellationToken);
        crashAfterPointerUpdate?.Invoke();
        return JsonSerializer.Serialize(new RoundAdmission(roomId, roundGeneration, Preparing, ownershipEpoch,
            Duplicate: false));
    }

    /// <summary>
    /// Terminal release for a round that never settles: closes participations, clears only the
    /// matching mount pointers, marks the room aborted and releases its ownership with an epoch
    /// bump. Idempotent per (room, operation).
    /// </summary>
    public async Task<RoundAdmission> AbortRoundAsync(string tenantId, string gameId, string roomId,
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
            new OperationIdentity(tenantId, gameId, "round.abort", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyAbortAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, reason, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<RoundAdmission>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored abort receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyAbortAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, string reason, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken);
        string status;
        await using (var room = new NpgsqlCommand("""
            SELECT status FROM game.room WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            status = await room.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RoundAdmissionException("The room does not exist.");
            if (status is not (Preparing or Running or Suspended))
                throw new RoundAdmissionException($"A room in status {status} cannot be aborted.");
        }
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
            await using (var pointer = new NpgsqlCommand("""
                UPDATE live.binding SET current_room_id=NULL
                WHERE binding_id=@binding AND current_room_id=@room
                """, connection, transaction))
            {
                pointer.Parameters.AddWithValue("binding", bindingId);
                pointer.Parameters.AddWithValue("room", roomId);
                await pointer.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var abort = new NpgsqlCommand(
            "UPDATE game.room SET status=@status, updated_at=clock_timestamp() WHERE room_id=@room",
            connection, transaction))
        {
            abort.Parameters.AddWithValue("status", Aborted);
            abort.Parameters.AddWithValue("room", roomId);
            await abort.ExecuteNonQueryAsync(cancellationToken);
        }
        long releasedEpoch;
        await using (var ownership = new NpgsqlCommand("""
            UPDATE control.ownership SET status='Released', epoch=epoch+1
            WHERE kind='room' AND entity_id=@room
            RETURNING epoch
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("room", roomId);
            releasedEpoch = Convert.ToInt64(await ownership.ExecuteScalarAsync(cancellationToken));
        }
        await WriteOutboxAsync($"{roomId}/0/abort", "round.aborted", new
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Reason = reason, ReleasedEpoch = releasedEpoch
        }, connection, transaction, cancellationToken);
        return JsonSerializer.Serialize(new RoundAdmission(roomId, 0, Aborted, releasedEpoch, Duplicate: false));
    }

    /// <summary>Read-only room snapshot for routing and clients; null when the room is unknown.</summary>
    public async Task<RoundStatus?> GetRoundAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string status; long epoch, lastTick, stateVersion;
        await using (var room = new NpgsqlCommand("""
            SELECT status,epoch,last_tick,state_version FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            """, connection))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            status = reader.GetString(0);
            epoch = reader.GetInt64(1);
            lastTick = reader.GetInt64(2);
            stateVersion = reader.GetInt64(3);
        }
        var participants = new List<RoundParticipantState>();
        await using (var list = new NpgsqlCommand("""
            SELECT participant_id,binding_id,role,state FROM game.participation
            WHERE room_id=@room ORDER BY participant_id
            """, connection))
        {
            list.Parameters.AddWithValue("room", roomId);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                participants.Add(new RoundParticipantState(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3)));
        }
        return new RoundStatus(roomId, status, epoch, lastTick, stateVersion, participants);
    }

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

    /// <summary>
    /// Transfers a live room's ownership to a new instance (architecture 10.3): the authority
    /// handover bumps the ownership and room epochs in one transaction, marks the room
    /// Recovering and extends the lease — the old owner is fenced from the new epoch, and no
    /// frame or snapshot write passes the fence until the ownership is confirmed Active again.
    /// FIX-TAKE-04: the same transaction records the recovery operation (target owner, epoch,
    /// deadline); a retry after a lost reply replays the recorded outcome by the operation id,
    /// so the epoch never bumps twice for one recovery.
    /// </summary>
    public async Task<RoomTakeover> TransferRoomOwnershipAsync(string tenantId, string gameId,
        string roomId, string fromInstance, string toInstance, int leaseSeconds,
        string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(fromInstance, nameof(fromInstance)); Require(toInstance, nameof(toInstance));
        Require(operationId, nameof(operationId));
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A same-id retry of an already-committed transfer replays its outcome instead of
        // failing the from-Active guard — the reply was lost, not the transfer.
        await using (var recorded = new NpgsqlCommand("""
            SELECT room_id,from_instance,to_instance,target_epoch FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
            """, connection, transaction))
        {
            recorded.Parameters.AddWithValue("tenant", tenantId);
            recorded.Parameters.AddWithValue("game", gameId);
            recorded.Parameters.AddWithValue("operation", operationId);
            await using var reader = await recorded.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var room = reader.GetString(0);
                var from = reader.GetString(1);
                var to = reader.GetString(2);
                var recordedEpoch = reader.GetInt64(3);
                await reader.DisposeAsync();
                if (room != roomId || from != fromInstance || to != toInstance)
                    throw new IdempotencyConflictException();
                return new RoomTakeover(to, recordedEpoch, await OwnershipStatusAsync(
                    tenantId, gameId, roomId, connection, transaction, cancellationToken) ?? "Recovering");
            }
        }
        await using (var ownership = new NpgsqlCommand("""
            SELECT tenant_id,game_id,owner_process_instance_id,status
            FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue("room", roomId);
            await using var reader = await ownership.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != tenantId ||
                reader.GetString(1) != gameId)
                throw new RedesignStaleOwnerException();
            var holder = reader.GetString(2);
            var status = reader.GetString(3);
            if (holder != fromInstance)
                throw new InvalidOperationException($"The room is held by {holder}, not {fromInstance}.");
            if (status != "Active")
                throw new InvalidOperationException($"The room ownership is {status}; only an Active room transfers.");
            await reader.DisposeAsync();
        }
        long epoch;
        await using (var move = new NpgsqlCommand("""
            UPDATE control.ownership SET owner_process_instance_id=@to, epoch=epoch+1,
                lease_until=clock_timestamp()+@lease*interval '1 second', status='Recovering',
                version=version+1
            WHERE kind='room' AND entity_id=@room
            RETURNING epoch
            """, connection, transaction))
        {
            move.Parameters.AddWithValue("to", toInstance);
            move.Parameters.AddWithValue("room", roomId);
            move.Parameters.AddWithValue("lease", leaseSeconds);
            var moved = await move.ExecuteScalarAsync(cancellationToken);
            if (moved is null || (epoch = Convert.ToInt64(moved)) <= 1)
                throw new InvalidOperationException("The ownership vanished mid-transfer.");
        }
        await using (var operation = new NpgsqlCommand("""
            INSERT INTO control.recovery_operation(tenant_id,game_id,operation_id,room_id,
                from_instance,to_instance,target_epoch,phase,deadline_at)
            VALUES (@tenant,@game,@operation,@room,@from,@to,@epoch,'Transferred',
                clock_timestamp()+@lease*interval '1 second')
            """, connection, transaction))
        {
            operation.Parameters.AddWithValue("tenant", tenantId);
            operation.Parameters.AddWithValue("game", gameId);
            operation.Parameters.AddWithValue("operation", operationId);
            operation.Parameters.AddWithValue("room", roomId);
            operation.Parameters.AddWithValue("from", fromInstance);
            operation.Parameters.AddWithValue("to", toInstance);
            operation.Parameters.AddWithValue("epoch", epoch);
            operation.Parameters.AddWithValue("lease", leaseSeconds);
            await operation.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var room = new NpgsqlCommand("""
            UPDATE game.room SET epoch=epoch+1, updated_at=clock_timestamp()
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
              AND status IN ('Running','Finishing')
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await room.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new RoomTakeover(toInstance, epoch, "Recovering");
    }

    /// <summary>The ownership row's current status, or null when the room has none.</summary>
    private static async Task<string?> OwnershipStatusAsync(string tenantId, string gameId, string roomId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var status = new NpgsqlCommand("""
            SELECT status FROM control.ownership
            WHERE kind='room' AND entity_id=@room AND tenant_id=@tenant AND game_id=@game
            """, connection, transaction);
        status.Parameters.AddWithValue("room", roomId);
        status.Parameters.AddWithValue("tenant", tenantId);
        status.Parameters.AddWithValue("game", gameId);
        return await status.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// Moves a timed-out open recovery to a different instance (FIX-TAKE-05, architecture
    /// failure table): one transaction closes the expired operation Failed, bumps the
    /// ownership and room epochs again and opens the replacement operation with a fresh
    /// deadline. Database time decides expiry — a live-but-slow target is reassigned exactly
    /// like a silent one, and no path extends a deadline once written. The displaced target
    /// cannot revive its epoch: the confirm path only admits the holder the ownership names.
    /// </summary>
    public async Task<RoomTakeover> ReassignRoomRecoveryAsync(string tenantId, string gameId,
        string roomId, string operationId, string newOperationId, string fromInstance,
        string toInstance, int leaseSeconds, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(operationId, nameof(operationId)); Require(newOperationId, nameof(newOperationId));
        Require(fromInstance, nameof(fromInstance)); Require(toInstance, nameof(toInstance));
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var recorded = new NpgsqlCommand("""
            SELECT room_id,to_instance,target_epoch,phase,deadline_at FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
            FOR UPDATE
            """, connection, transaction))
        {
            recorded.Parameters.AddWithValue("tenant", tenantId);
            recorded.Parameters.AddWithValue("game", gameId);
            recorded.Parameters.AddWithValue("operation", operationId);
            await using var reader = await recorded.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(3) != "Transferred")
                throw new InvalidOperationException("The recovery operation is no longer open.");
            if (reader.GetString(0) != roomId || reader.GetString(1) != fromInstance)
                throw new InvalidOperationException(
                    "The recovery operation was recorded for a different room or target.");
            if (reader.GetDateTime(4) > DateTime.UtcNow)
                throw new InvalidOperationException(
                    "The recovery operation has not passed its deadline; the target keeps its chance.");
            var recordedEpoch = reader.GetInt64(2);
            await reader.DisposeAsync();
            await using (var ownership = new NpgsqlCommand("""
                SELECT owner_process_instance_id,status,epoch FROM control.ownership
                WHERE kind='room' AND entity_id=@room FOR UPDATE
                """, connection, transaction))
            {
                ownership.Parameters.AddWithValue("room", roomId);
                await using var owner = await ownership.ExecuteReaderAsync(cancellationToken);
                if (!await owner.ReadAsync(cancellationToken))
                    throw new RedesignStaleOwnerException();
                var holder = owner.GetString(0);
                var status = owner.GetString(1);
                var ownerEpoch = owner.GetInt64(2);
                await owner.DisposeAsync();
                if (holder != fromInstance)
                    throw new InvalidOperationException($"The room is held by {holder}, not {fromInstance}.");
                if (status != "Recovering")
                    throw new InvalidOperationException(
                        $"The room ownership is {status}; only a Recovering room reassigns.");
                if (ownerEpoch != recordedEpoch)
                    throw new InvalidOperationException("The recovery operation names a stale epoch.");
            }
        }
        long epoch;
        await using (var move = new NpgsqlCommand("""
            UPDATE control.ownership SET owner_process_instance_id=@to, epoch=epoch+1,
                lease_until=clock_timestamp()+@lease*interval '1 second', version=version+1
            WHERE kind='room' AND entity_id=@room
            RETURNING epoch
            """, connection, transaction))
        {
            move.Parameters.AddWithValue("to", toInstance);
            move.Parameters.AddWithValue("room", roomId);
            move.Parameters.AddWithValue("lease", leaseSeconds);
            var moved = await move.ExecuteScalarAsync(cancellationToken);
            if (moved is null || (epoch = Convert.ToInt64(moved)) <= 1)
                throw new InvalidOperationException("The ownership vanished mid-reassignment.");
        }
        await using (var closed = new NpgsqlCommand("""
            UPDATE control.recovery_operation SET phase='Failed',
                last_error='The recovery deadline passed before activation.',
                updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
              AND phase='Transferred'
            """, connection, transaction))
        {
            closed.Parameters.AddWithValue("tenant", tenantId);
            closed.Parameters.AddWithValue("game", gameId);
            closed.Parameters.AddWithValue("operation", operationId);
            await closed.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var operation = new NpgsqlCommand("""
            INSERT INTO control.recovery_operation(tenant_id,game_id,operation_id,room_id,
                from_instance,to_instance,target_epoch,phase,deadline_at)
            VALUES (@tenant,@game,@operation,@room,@from,@to,@epoch,'Transferred',
                clock_timestamp()+@lease*interval '1 second')
            """, connection, transaction))
        {
            operation.Parameters.AddWithValue("tenant", tenantId);
            operation.Parameters.AddWithValue("game", gameId);
            operation.Parameters.AddWithValue("operation", newOperationId);
            operation.Parameters.AddWithValue("room", roomId);
            operation.Parameters.AddWithValue("from", fromInstance);
            operation.Parameters.AddWithValue("to", toInstance);
            operation.Parameters.AddWithValue("epoch", epoch);
            operation.Parameters.AddWithValue("lease", leaseSeconds);
            await operation.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var room = new NpgsqlCommand("""
            UPDATE game.room SET epoch=epoch+1, updated_at=clock_timestamp()
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
              AND status IN ('Running','Finishing')
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await room.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new RoomTakeover(toInstance, epoch, "Recovering");
    }

    /// <summary>
    /// Confirms a transferred ownership: Recovering becomes Active again, so the new owner's
    /// recovery and frame writes pass the fence. Idempotent for an already-Active owner.
    /// </summary>
    public async Task<RoomTakeover> ConfirmRoomOwnershipAsync(string tenantId, string gameId,
        string roomId, string ownerInstance, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstance, nameof(ownerInstance));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var confirm = new NpgsqlCommand("""
            UPDATE control.ownership SET status='Active', version=version+1
            WHERE kind='room' AND entity_id=@room AND tenant_id=@tenant AND game_id=@game
              AND owner_process_instance_id=@owner AND status='Recovering'
              AND lease_until>clock_timestamp()
            """, connection))
        {
            confirm.Parameters.AddWithValue("room", roomId);
            confirm.Parameters.AddWithValue("tenant", tenantId);
            confirm.Parameters.AddWithValue("game", gameId);
            confirm.Parameters.AddWithValue("owner", ownerInstance);
            await confirm.ExecuteNonQueryAsync(cancellationToken);
        }
        var takeover = await GetTakeoverAsync(tenantId, gameId, roomId, cancellationToken)
            ?? throw new InvalidOperationException("The room has no ownership row.");
        if (takeover.OwnerInstanceId != ownerInstance)
            throw new InvalidOperationException($"The room is held by {takeover.OwnerInstanceId}, not {ownerInstance}.");
        if (takeover.Status != "Active")
            throw new InvalidOperationException($"The room ownership is {takeover.Status}; confirm needs Recovering.");
        return takeover;
    }

    private async Task<RoomTakeover?> GetTakeoverAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,status
            FROM control.ownership WHERE kind='room' AND entity_id=@room
              AND tenant_id=@tenant AND game_id=@game
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RoomTakeover(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }

    private static async Task<long> LockRoomOwnershipAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var ownership = new NpgsqlCommand("""
            SELECT tenant_id,game_id,owner_process_instance_id,epoch,lease_until>clock_timestamp(),status
            FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction);
        ownership.Parameters.AddWithValue("room", roomId);
        await using var reader = await ownership.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != tenantId ||
            reader.GetString(1) != gameId || reader.GetString(2) != ownerInstanceId ||
            reader.GetInt64(3) != epoch || !reader.GetBoolean(4) || reader.GetString(5) != "Active")
            throw new RedesignStaleOwnerException();
        return reader.GetInt64(3);
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
