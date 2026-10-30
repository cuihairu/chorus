using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.DataServer;

public sealed record SnapshotReceipt(string RoomId, long Tick, int SchemaVersion, bool Verified, bool Duplicate);

public sealed record RecoverySnapshot(
    long Tick, int SchemaVersion, byte[] Payload, byte[] PayloadHash, bool Verified);

public sealed record RecoveryDelivery(
    string EventId, long Sequence, string? BindingId, string PayloadJson, bool Consumed);

public sealed record RecoveryFrame(
    long Tick, IReadOnlyList<string> InputEventIds, byte[] StateHash, string ModuleVersion, string ConfigHash);

/// <param name="TickMs">
/// The room's persisted tick cadence (FIX-MODULE-01) alongside the rest of the module
/// identity: the recovery rebuilds the module from these room facts — never from a
/// caller-supplied tick or module guess — so the original round's cadence, version,
/// package and checkpoint policy survive a takeover or admin retry unchanged.
/// </param>
public sealed record RecoveryPlan(
    string RoomId, string Status, long Epoch, long LastTick, long StateVersion,
    IReadOnlyList<RecoverySnapshot> Snapshots, IReadOnlyList<RecoveryDelivery> Deliveries,
    IReadOnlyList<RecoveryFrame> Frames, IReadOnlyList<RoundParticipantState> Participants,
    RecoveryReadStats? Reads = null,
    int TickMs = 50, string ModuleVersion = "", string ConfigHash = "",
    string PackageHash = "", string RecoveryMode = "");

/// <summary>What the recovery read actually moved (FIX-READ-02): row counts per material and
/// the payload bytes they carried, so a long round's read cost is measured, not guessed.</summary>
public sealed record RecoveryReadStats(
    int SnapshotRows, int FrameRows, int DeliveryRows, long PayloadBytes);

/// <summary>
/// Snapshot persistence and recovery reads (architecture 8.2, 10.3). PutSnapshot attaches a
/// store-verified full snapshot to the last committed frame: the store re-checks the payload
/// hash, so a row that is visible is complete and intact — a snapshot written half-way is
/// never visible. LoadRecovery is the read-only recovery plan: the room head, every snapshot
/// newest-first, the inputs the loaded frames reference, the frames after the oldest snapshot
/// and the participation mapping, all in one consistent read.
/// </summary>
/// <param name="crashAfterSnapshotInsert">
/// Fault-injection seam for restart tests: invoked inside the transaction after the snapshot
/// insert, before the commit. An exception there plays the owner dying between the write and
/// the acknowledgment; the rollback leaves no partial snapshot behind.
/// </param>
/// <param name="afterHeadRead">
/// Fault-injection seam for read-view tests: invoked inside the recovery read transaction
/// after the head row is read, before any material loads. Whatever commits while it holds
/// must stay invisible to the plan it is loading.
/// </param>
public sealed class RedesignRecoveryStore(string connectionString, Action? crashAfterSnapshotInsert = null,
    Action? afterHeadRead = null, Chorus.Data.Redesign.DataMetrics? metrics = null)
{
    /// <summary>How many newest snapshot candidates a normal recovery read loads (FIX-READ-02):
    /// the fallback pair — the same two anchors retention keeps for a settled round.</summary>
    public const int CandidateDepth = 2;

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);
    private readonly Chorus.Data.Redesign.DataMetrics _metrics =
        metrics ?? Chorus.Data.Redesign.DataMetrics.Default;

    /// <summary>
    /// Persists a full snapshot of the state at the room's current head tick. The store
    /// verifies the payload hash itself and marks the row verified; a torn or hash-mismatched
    /// write is rejected, never stored half. Idempotent per (room, operation).
    /// </summary>
    public async Task<SnapshotReceipt> PutSnapshotAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, long tick, byte[] payload, byte[] payloadHash, int schemaVersion,
        string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payloadHash);
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (!payloadHash.AsSpan().SequenceEqual(SHA256.HashData(payload)))
            throw new ArgumentException("The snapshot hash does not match its payload.");
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            RoomId = roomId, Tick = tick, Hash = payloadHash, SchemaVersion = schemaVersion
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "snapshot.put", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyPutAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, tick, payload, payloadHash, schemaVersion, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<SnapshotReceipt>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored snapshot receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    /// <summary>
    /// The Recovering-only snapshot entry (contracts 6.2.1): the takeover-named owner pins a
    /// store-verified snapshot at the recovery head while the ownership is Recovering. The
    /// fence admits exactly that owner at the bumped epoch with a live lease — the old owner,
    /// a wrong epoch and every normal frame or snapshot write stay out. The write never
    /// activates the room: Recovering becomes Active only through the fenced confirm.
    /// Idempotent per (room, operation).
    /// </summary>
    public async Task<SnapshotReceipt> PutRecoverySnapshotAsync(string tenantId, string gameId,
        string roomId, string ownerInstanceId, long epoch, long tick, byte[] payload, byte[] payloadHash,
        int schemaVersion, string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        Require(ownerInstanceId, nameof(ownerInstanceId)); Require(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payloadHash);
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (!payloadHash.AsSpan().SequenceEqual(SHA256.HashData(payload)))
            throw new ArgumentException("The snapshot hash does not match its payload.");
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            RoomId = roomId, Tick = tick, Hash = payloadHash, SchemaVersion = schemaVersion
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "snapshot.recovery", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyPutAsync(tenantId, gameId, roomId, ownerInstanceId,
                epoch, tick, payload, payloadHash, schemaVersion, connection, transaction, token,
                requiredOwnershipStatus: "Recovering"),
            cancellationToken);
        var result = JsonSerializer.Deserialize<SnapshotReceipt>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored snapshot receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyPutAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, long tick, byte[] payload, byte[] payloadHash, int schemaVersion,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken,
        string requiredOwnershipStatus = "Active")
    {
        await LockRoomOwnershipAsync(tenantId, gameId, roomId, ownerInstanceId, epoch,
            connection, transaction, cancellationToken, requiredOwnershipStatus);
        await using (var room = new NpgsqlCommand("""
            SELECT status,last_tick FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new FrameConflictException("The room does not exist.");
            if (reader.GetString(0) != "Running")
                throw new FrameConflictException(
                    $"A room in status {reader.GetString(0)} does not accept snapshots.");
            if (reader.GetInt64(1) != tick)
                throw new FrameConflictException(
                    $"The snapshot must attach to the head tick {reader.GetInt64(1)}, not {tick}.");
        }
        // Plain insert, no upsert: a second, different snapshot for the same tick is a real
        // divergence and surfaces as a constraint violation instead of being dropped silently.
        await using (var snapshot = new NpgsqlCommand("""
            INSERT INTO game.snapshot(room_id,tick_id,schema_version,payload,payload_hash,verified)
            VALUES (@room,@tick,@schema,@payload,@hash,true)
            """, connection, transaction))
        {
            snapshot.Parameters.AddWithValue("room", roomId);
            snapshot.Parameters.AddWithValue("tick", tick);
            snapshot.Parameters.AddWithValue("schema", schemaVersion);
            snapshot.Parameters.AddWithValue("payload", payload);
            snapshot.Parameters.AddWithValue("hash", payloadHash);
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        crashAfterSnapshotInsert?.Invoke();
        return JsonSerializer.Serialize(new SnapshotReceipt(roomId, tick, schemaVersion, Verified: true,
            Duplicate: false));
    }

    /// <summary>
    /// The read-only recovery plan, or null for an unknown room. The whole plan derives from
    /// one repeatable-read snapshot (FIX-READ-01): the head the first read sees fixes the
    /// epoch, LastTick and StateVersion, and the material loads cannot mix in a frame, a
    /// snapshot or a status a concurrent commit produced after that instant — the selected
    /// head is also the immutable boundary any later paging must stay within. The normal
    /// path reads the bounded newest snapshot candidates — the fallback pair, the same two
    /// retention anchors a settled round keeps — and only the frames from the oldest loaded
    /// candidate forward (FIX-READ-02), so a long round's read is bounded by the snapshot
    /// interval, never by the whole history; a room whose history is shorter than the
    /// candidate window simply loads what exists. Snapshots come newest-first, so a
    /// recoverer can walk any candidate snapshot forward to the head and verify every frame
    /// hash on the way. Deliveries load on demand from those frames (FIX-READ-03): only the
    /// input ids the loaded span cites are read, so a long delivery history below or beside
    /// the span adds nothing to the plan.
    /// </summary>
    public async Task<RecoveryPlan?> LoadRecoveryAsync(string tenantId, string gameId, string roomId,
        int candidateDepth = CandidateDepth, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        if (candidateDepth <= 0) throw new ArgumentOutOfRangeException(nameof(candidateDepth));
        await using var connection = new NpgsqlConnection(connectionString);
        var openStart = _metrics.Start();
        try { await connection.OpenAsync(cancellationToken); }
        finally { _metrics.Record(_metrics.ConnOpen, openStart); }
        var readStart = _metrics.Start();
        try
        {
            return await LoadRecoveryQueriesAsync(connection, tenantId, gameId, roomId, candidateDepth,
                cancellationToken);
        }
        finally { _metrics.Record(_metrics.ReadExec, readStart); }
    }

    /// <summary>The recovery read itself, on an open connection (FIX-PERF-02 read metrics):
    /// one repeatable-read view, head first, then every material the plan cites.</summary>
    private async Task<RecoveryPlan?> LoadRecoveryQueriesAsync(NpgsqlConnection connection,
        string tenantId, string gameId, string roomId, int candidateDepth,
        CancellationToken cancellationToken)
    {
        await using var view = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        string status; long epoch, lastTick, stateVersion;
        int tickMs; string moduleVersion, configHash, packageHash, recoveryMode;
        await using (var room = new NpgsqlCommand("""
            SELECT status,epoch,last_tick,state_version,tick_ms,module_version,config_hash,
                   package_hash,recovery_mode
            FROM game.room WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            """, connection, view))
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
            tickMs = reader.GetInt32(4);
            moduleVersion = reader.GetString(5);
            configHash = reader.GetString(6);
            packageHash = reader.GetString(7);
            recoveryMode = reader.GetString(8);
        }
        afterHeadRead?.Invoke();
        var snapshots = new List<RecoverySnapshot>();
        await using (var list = new NpgsqlCommand("""
            SELECT tick_id,schema_version,payload,payload_hash,verified FROM game.snapshot
            WHERE room_id=@room ORDER BY tick_id DESC LIMIT @depth
            """, connection, view))
        {
            list.Parameters.AddWithValue("room", roomId);
            list.Parameters.AddWithValue("depth", candidateDepth);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                snapshots.Add(new RecoverySnapshot(reader.GetInt64(0), reader.GetInt32(1),
                    (byte[])reader[2], (byte[])reader[3], reader.GetBoolean(4)));
        }
        var frames = new List<RecoveryFrame>();
        await using (var log = new NpgsqlCommand("""
            SELECT tick_id,input_refs,state_hash,module_version,config_hash FROM game.frame
            WHERE room_id=@room AND tick_id > @fromTick ORDER BY tick_id
            """, connection, view))
        {
            log.Parameters.AddWithValue("room", roomId);
            // The frame span starts at the oldest loaded candidate: the walk never needs a
            // frame below any candidate it might replay from.
            log.Parameters.AddWithValue("fromTick", snapshots.Count > 0 ? snapshots[^1].Tick : 0);
            await using var reader = await log.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                frames.Add(new RecoveryFrame(reader.GetInt64(0),
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [],
                    (byte[])reader[2], reader.GetString(3), reader.GetString(4)));
        }
        // Only the inputs the loaded frames actually reference (FIX-READ-03): a delivery
        // history no frame in the span cites never rides along — the replay resolves its
        // inputs from the frames it walks, and nothing else reads the log here.
        var referenced = frames.SelectMany(frame => frame.InputEventIds)
            .ToHashSet(StringComparer.Ordinal);
        var deliveries = new List<RecoveryDelivery>();
        if (referenced.Count > 0)
            await using (var inbox = new NpgsqlCommand("""
                SELECT d.event_id,d.sequence,e.binding_id,e.payload::text,d.state
                FROM input.delivery d JOIN input.event e ON e.event_id=d.event_id
                WHERE d.consumer_scope=@scope AND d.room_id=@room AND d.event_id=ANY(@events)
                ORDER BY d.sequence
                """, connection, view))
            {
                inbox.Parameters.AddWithValue("scope", "room:" + roomId);
                inbox.Parameters.AddWithValue("room", roomId);
                inbox.Parameters.AddWithValue("events", referenced.ToArray());
                await using var reader = await inbox.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    deliveries.Add(new RecoveryDelivery(reader.GetString(0), reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                        reader.GetString(4) == "Consumed"));
            }
        var participants = new List<RoundParticipantState>();
        await using (var mounts = new NpgsqlCommand("""
            SELECT participant_id,binding_id,role,state FROM game.participation
            WHERE room_id=@room ORDER BY participant_id
            """, connection, view))
        {
            mounts.Parameters.AddWithValue("room", roomId);
            await using var reader = await mounts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                participants.Add(new RoundParticipantState(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3)));
        }
        var reads = new RecoveryReadStats(
            snapshots.Count, frames.Count, deliveries.Count,
            snapshots.Sum(snapshot => snapshot.Payload.LongLength + snapshot.PayloadHash.LongLength)
                + frames.Sum(frame => frame.StateHash.LongLength)
                + deliveries.Sum(delivery => (long)delivery.PayloadJson.Length));
        var plan = new RecoveryPlan(roomId, status, epoch, lastTick, stateVersion, snapshots, deliveries,
            frames, participants, reads, tickMs, moduleVersion, configHash, packageHash, recoveryMode);
        // The read-only view closes with the plan; a rollback would answer identically, so
        // the commit is just the cheapest exit that keeps the connection reusable.
        await view.CommitAsync(cancellationToken);
        return plan;
    }

    private static async Task LockRoomOwnershipAsync(string tenantId, string gameId, string roomId,
        string ownerInstanceId, long epoch, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken, string requiredOwnershipStatus = "Active")
    {
        await using var ownership = new NpgsqlCommand("""
            SELECT tenant_id,game_id,owner_process_instance_id,epoch,lease_until>clock_timestamp(),status
            FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction);
        ownership.Parameters.AddWithValue("room", roomId);
        await using var reader = await ownership.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != tenantId ||
            reader.GetString(1) != gameId || reader.GetString(2) != ownerInstanceId ||
            reader.GetInt64(3) != epoch || !reader.GetBoolean(4) ||
            reader.GetString(5) != requiredOwnershipStatus)
            throw new RedesignStaleOwnerException();
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
