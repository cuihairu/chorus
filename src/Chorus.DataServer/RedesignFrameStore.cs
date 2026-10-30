using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Npgsql;

namespace Chorus.DataServer;

public sealed class FrameConflictException(string message) : Exception(message);

public sealed record FrameEffect(string Kind, string PayloadJson);

public sealed record PlannedGrant(string PlayerId, string GrantKey, string OperationId,
    string AssetId, long Delta);

/// <summary>
/// One authoritative tick commit (architecture 8.2): everything a tick changed — the frame
/// record, input consumption, the contiguous watermark, the room's tick/version/hash, effect
/// outbox rows, an optional full checkpoint and the final result with its grant plan — lands
/// in a single receipt transaction. A commit id replays its stored receipt; a different
/// content under the same commit id conflicts; a stale prev tick or version is a visible
/// conflict, never a silent overwrite.
/// </summary>
public sealed record FrameCommit(
    string CommitId, string OwnerInstanceId, long Epoch,
    long PrevTick, long PrevStateVersion,
    IReadOnlyList<string> InputEventIds,
    byte[] StateHash,
    IReadOnlyList<FrameEffect> Effects,
    string ModuleVersion, string ConfigHash,
    byte[]? SnapshotPayload = null, byte[]? SnapshotHash = null, int SnapshotSchemaVersion = 0,
    bool Final = false, string? ResultJson = null, byte[]? PlanHash = null,
    IReadOnlyList<PlannedGrant>? Grants = null);

public sealed record FrameReceipt(
    string CommitId, string RoomId, long Tick, long StateVersion, long Watermark, bool Duplicate);

public sealed record InboxItem(
    string EventId, long Sequence, long RouteVersion, string? BindingId, string PayloadJson,
    string Platform, string SourceDedupKey);

public sealed record RoundInbox(long Watermark, IReadOnlyList<InboxItem> Items);

/// <summary>
/// Persistent frame store (architecture 8.2, 9.3). The lock order per commit is ownership →
/// room → input deliveries → input cursor; the watermark only advances across the contiguous
/// consumed prefix, so a hole left by an unconsumed delivery is never crossed. The
/// FullCheckpoint branch writes the snapshot in the same transaction — a failed checkpoint
/// rolls the whole frame back, it can never degrade to an unrecoverable state.
/// </summary>
/// <param name="crashAfterFrameInsert">
/// Fault-injection seam for restart tests: invoked inside the transaction after the frame
/// insert, before the commit. An exception there plays the owner dying between persisting the
/// tick and acknowledging it; the rollback leaves the inputs unconsumed.
/// </param>
public sealed class RedesignFrameStore(string connectionString, Action? crashAfterFrameInsert = null,
    DataMetrics? metrics = null)
{
    public const string Consumed = "Consumed";
    public const string Finishing = "Finishing";

    private readonly DataMetrics _metrics = metrics ?? DataMetrics.Default;
    private readonly PostgresOperationReceiptStore _receipts = new(connectionString, metrics);

    /// <summary>
    /// Commits tick <c>last_tick + 1</c> under the ownership fence. The caller states the tick
    /// and state version it derived the candidate from; a mismatch is rejected visibly.
    /// </summary>
    public async Task<FrameReceipt> CommitFrameAsync(string tenantId, string gameId, string roomId,
        FrameCommit commit, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(commit);
        Require(commit.CommitId, "CommitId"); Require(commit.OwnerInstanceId, "OwnerInstanceId");
        Require(commit.ModuleVersion, "ModuleVersion"); Require(commit.ConfigHash, "ConfigHash");
        ArgumentNullException.ThrowIfNull(commit.InputEventIds);
        ArgumentNullException.ThrowIfNull(commit.StateHash);
        ArgumentNullException.ThrowIfNull(commit.Effects);
        foreach (var eventId in commit.InputEventIds) Require(eventId, "InputEventIds");
        foreach (var effect in commit.Effects)
        {
            Require(effect.Kind, "Effects.Kind"); Require(effect.PayloadJson, "Effects.PayloadJson");
            if (effect.Kind.Contains('/', StringComparison.Ordinal))
                throw new ArgumentException("The effect kind may not contain '/'.", "Effects.Kind");
        }
        if (commit.Epoch <= 0) throw new ArgumentOutOfRangeException(nameof(commit));
        if (commit.PrevTick < 0) throw new ArgumentOutOfRangeException(nameof(commit));
        if (commit.PrevStateVersion < 0) throw new ArgumentOutOfRangeException(nameof(commit));
        var checkpoint = commit.SnapshotPayload is not null;
        if (checkpoint && (commit.SnapshotHash is null || commit.SnapshotSchemaVersion <= 0))
            throw new ArgumentException("A full checkpoint needs a payload hash and a schema version.",
                nameof(commit));
        if (commit.Final && (commit.ResultJson is null || commit.PlanHash is null))
            throw new ArgumentException("A final frame carries a result document and a plan hash.",
                nameof(commit));
        if (commit.Final)
        {
            using (JsonDocument.Parse(commit.ResultJson!)) { }
            ArgumentNullException.ThrowIfNull(commit.Grants);
            foreach (var grant in commit.Grants)
            {
                Require(grant.PlayerId, "Grants.PlayerId"); Require(grant.GrantKey, "Grants.GrantKey");
                Require(grant.OperationId, "Grants.OperationId"); Require(grant.AssetId, "Grants.AssetId");
                if (grant.Delta == 0)
                    throw new ArgumentException("A grant carries a non-zero amount.", nameof(commit));
            }
        }

        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            commit.CommitId, commit.OwnerInstanceId, commit.Epoch, commit.PrevTick,
            commit.PrevStateVersion, commit.InputEventIds, StateHash = commit.StateHash,
            Effects = commit.Effects.Select(effect => new { effect.Kind, effect.PayloadJson }),
            commit.ModuleVersion, commit.ConfigHash,
            Checkpoint = checkpoint
                ? new { Hash = commit.SnapshotHash, commit.SnapshotSchemaVersion } : null,
            commit.Final, commit.ResultJson, commit.PlanHash,
            Grants = commit.Final
                ? commit.Grants!.Select(grant => new
                {
                    grant.PlayerId, grant.GrantKey, grant.OperationId, grant.AssetId, grant.Delta
                }) : null
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "frame.commit", roomId, commit.CommitId),
            fingerprint,
            (connection, transaction, token) => ApplyFrameAsync(tenantId, gameId, roomId, commit, checkpoint,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<FrameReceipt>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored frame receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyFrameAsync(string tenantId, string gameId, string roomId,
        FrameCommit commit, bool checkpoint, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await LockRoomOwnershipAsync(tenantId, gameId, roomId, commit.OwnerInstanceId, commit.Epoch,
            connection, transaction, cancellationToken);
        string status; long lastTick, stateVersion;
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
                throw new FrameConflictException("The room does not exist.");
            status = reader.GetString(0);
            lastTick = reader.GetInt64(1);
            stateVersion = reader.GetInt64(2);
        }
        if (status != "Running")
            throw new FrameConflictException($"A room in status {status} does not accept frame commits.");
        if (lastTick != commit.PrevTick || stateVersion != commit.PrevStateVersion)
            throw new FrameConflictException(
                $"The room moved on: tick {lastTick}/version {stateVersion}, not {commit.PrevTick}/{commit.PrevStateVersion}.");
        var tick = lastTick + 1;

        // The actor fixed the final input order; the store verifies every ref is an
        // unconsumed delivery of this round's inbox before any of them is consumed.
        var scope = "room:" + roomId;
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var deliveries = new NpgsqlCommand("""
            SELECT event_id,sequence,state FROM input.delivery
            WHERE event_id=ANY(@ids) AND consumer_scope=@scope AND room_id=@room
            """, connection, transaction))
        {
            deliveries.Parameters.AddWithValue("ids", commit.InputEventIds.ToArray());
            deliveries.Parameters.AddWithValue("scope", scope);
            deliveries.Parameters.AddWithValue("room", roomId);
            await using var reader = await deliveries.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(2) != "Pending")
                    throw new FrameConflictException(
                        $"The input {reader.GetString(0)} was already consumed by another frame.");
                sequences[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        if (commit.InputEventIds.GroupBy(id => id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new FrameConflictException("Input refs must be distinct.");
        if (sequences.Count != commit.InputEventIds.Count)
            throw new FrameConflictException(
                "Every input ref must be a pending delivery of this round's inbox.");
        await using (var consume = new NpgsqlCommand("""
            UPDATE input.delivery SET state=@consumed
            WHERE consumer_scope=@scope AND event_id=ANY(@ids) AND state='Pending'
            """, connection, transaction))
        {
            consume.Parameters.AddWithValue("consumed", Consumed);
            consume.Parameters.AddWithValue("scope", scope);
            consume.Parameters.AddWithValue("ids", commit.InputEventIds.ToArray());
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != sequences.Count)
                throw new FrameConflictException("An input was consumed concurrently.");
        }

        await using (var frame = new NpgsqlCommand("""
            INSERT INTO game.frame(room_id,tick_id,commit_id,input_refs,state_hash,effects,
                module_version,config_hash)
            VALUES (@room,@tick,@commit,@refs::jsonb,@hash,@effects::jsonb,@module,@config)
            """, connection, transaction))
        {
            frame.Parameters.AddWithValue("room", roomId);
            frame.Parameters.AddWithValue("tick", tick);
            frame.Parameters.AddWithValue("commit", commit.CommitId);
            frame.Parameters.AddWithValue("refs", JsonSerializer.Serialize(commit.InputEventIds));
            frame.Parameters.AddWithValue("hash", commit.StateHash);
            frame.Parameters.AddWithValue("effects", JsonSerializer.Serialize(commit.Effects));
            frame.Parameters.AddWithValue("module", commit.ModuleVersion);
            frame.Parameters.AddWithValue("config", commit.ConfigHash);
            await frame.ExecuteNonQueryAsync(cancellationToken);
        }
        if (checkpoint)
        {
            // Same transaction as the frame: a failed checkpoint rolls the whole tick back
            // (architecture 8.2) instead of degrading to an unrecoverable checkpoint.
            await using var snapshot = new NpgsqlCommand("""
                INSERT INTO game.snapshot(room_id,tick_id,schema_version,payload,payload_hash,verified)
                VALUES (@room,@tick,@schema,@payload,@hash,false)
                """, connection, transaction);
            snapshot.Parameters.AddWithValue("room", roomId);
            snapshot.Parameters.AddWithValue("tick", tick);
            snapshot.Parameters.AddWithValue("schema", commit.SnapshotSchemaVersion);
            snapshot.Parameters.AddWithValue("payload", commit.SnapshotPayload!);
            snapshot.Parameters.AddWithValue("hash", commit.SnapshotHash!);
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }

        // The watermark only crosses the contiguous consumed prefix — never over a hole.
        long watermark;
        await using (var prefix = new NpgsqlCommand("""
            UPDATE input.cursor SET consumed_contiguous_sequence = GREATEST(consumed_contiguous_sequence,
                COALESCE((SELECT min(sequence) - 1 FROM input.delivery
                          WHERE consumer_scope=@scope AND state <> @consumed),
                         (SELECT COALESCE(max(sequence), 0) FROM input.delivery
                          WHERE consumer_scope=@scope)))
            WHERE consumer_scope=@scope
            RETURNING consumed_contiguous_sequence
            """, connection, transaction))
        {
            prefix.Parameters.AddWithValue("scope", scope);
            prefix.Parameters.AddWithValue("consumed", Consumed);
            // No cursor row yet means nothing was ever routed into this round: watermark 0.
            var advanced = await prefix.ExecuteScalarAsync(cancellationToken);
            watermark = advanced is null ? 0 : Convert.ToInt64(advanced);
        }

        var stateVersion2 = stateVersion + 1;
        await using (var advance = new NpgsqlCommand("""
            UPDATE game.room SET last_tick=@tick, state_version=@version, state_hash=@hash,
                updated_at=clock_timestamp()
            WHERE room_id=@room
            """, connection, transaction))
        {
            advance.Parameters.AddWithValue("tick", tick);
            advance.Parameters.AddWithValue("version", stateVersion2);
            advance.Parameters.AddWithValue("hash", commit.StateHash);
            advance.Parameters.AddWithValue("room", roomId);
            await advance.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var index = 0; index < commit.Effects.Count; index++)
        {
            var effect = commit.Effects[index];
            await using var outbox = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id,kind,payload,status)
                VALUES (@effect,@kind,@payload::jsonb,'Pending')
                ON CONFLICT (effect_id) DO NOTHING
                """, connection, transaction);
            outbox.Parameters.AddWithValue("effect", $"{roomId}/{tick}/{index}");
            outbox.Parameters.AddWithValue("kind", effect.Kind);
            outbox.Parameters.AddWithValue("payload", effect.PayloadJson);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        if (commit.Final)
        {
            await using (var result = new NpgsqlCommand("""
                INSERT INTO game.result(room_id,result_version,final_frame,plan_hash,result)
                VALUES (@room,1,@tick,@plan,@result::jsonb)
                """, connection, transaction))
            {
                result.Parameters.AddWithValue("room", roomId);
                result.Parameters.AddWithValue("tick", tick);
                result.Parameters.AddWithValue("plan", commit.PlanHash!);
                result.Parameters.AddWithValue("result", commit.ResultJson!);
                await result.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var grant in commit.Grants!)
            {
                await using var planned = new NpgsqlCommand("""
                    INSERT INTO settlement.grant(room_id,result_version,player_id,grant_key,status,operation_id,
                        asset_id,delta)
                    VALUES (@room,1,@player,@key,'Planned',@operation,@asset,@delta)
                    """, connection, transaction);
                planned.Parameters.AddWithValue("room", roomId);
                planned.Parameters.AddWithValue("player", grant.PlayerId);
                planned.Parameters.AddWithValue("key", grant.GrantKey);
                planned.Parameters.AddWithValue("operation", grant.OperationId);
                planned.Parameters.AddWithValue("asset", grant.AssetId);
                planned.Parameters.AddWithValue("delta", grant.Delta);
                await planned.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var finish = new NpgsqlCommand(
                "UPDATE game.room SET status=@status, updated_at=clock_timestamp() WHERE room_id=@room",
                connection, transaction))
            {
                finish.Parameters.AddWithValue("status", Finishing);
                finish.Parameters.AddWithValue("room", roomId);
                await finish.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        crashAfterFrameInsert?.Invoke();
        return JsonSerializer.Serialize(
            new FrameReceipt(commit.CommitId, roomId, tick, stateVersion2, watermark, Duplicate: false));
    }

    /// <summary>
    /// The round inbox (architecture 6.1.6): unconsumed deliveries of this round in routing
    /// order with their payloads, plus the contiguous watermark the actor resumes after.
    /// </summary>
    public async Task<RoundInbox> ReadInboxAsync(string tenantId, string gameId, string roomId,
        int maxEvents, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId)); Require(roomId, nameof(roomId));
        if (maxEvents is <= 0 or > 500)
            throw new ArgumentException("The inbox window is 1..500 events.", nameof(maxEvents));
        await using var connection = new NpgsqlConnection(connectionString);
        var openStart = _metrics.Start();
        try { await connection.OpenAsync(cancellationToken); }
        finally { _metrics.Record(_metrics.ConnOpen, openStart); }
        var readStart = _metrics.Start();
        try
        {
            return await ReadInboxQueriesAsync(connection, roomId, maxEvents, cancellationToken);
        }
        finally { _metrics.Record(_metrics.ReadExec, readStart); }
    }

    private static async Task<RoundInbox> ReadInboxQueriesAsync(NpgsqlConnection connection,
        string roomId, int maxEvents, CancellationToken cancellationToken)
    {
        var scope = "room:" + roomId;
        long watermark;
        await using (var cursor = new NpgsqlCommand(
            "SELECT consumed_contiguous_sequence FROM input.cursor WHERE consumer_scope=@scope", connection))
        {
            cursor.Parameters.AddWithValue("scope", scope);
            watermark = Convert.ToInt64(await cursor.ExecuteScalarAsync(cancellationToken));
        }
        var items = new List<InboxItem>();
        await using (var inbox = new NpgsqlCommand("""
            SELECT d.event_id,d.sequence,d.route_version,e.binding_id,e.payload::text,
                e.platform,e.source_dedup_key
            FROM input.delivery d JOIN input.event e ON e.event_id=d.event_id
            WHERE d.consumer_scope=@scope AND d.room_id=@room AND d.state='Pending'
            ORDER BY d.sequence LIMIT @max
            """, connection))
        {
            inbox.Parameters.AddWithValue("scope", scope);
            inbox.Parameters.AddWithValue("room", roomId);
            inbox.Parameters.AddWithValue("max", maxEvents);
            await using var reader = await inbox.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new InboxItem(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6)));
        }
        return new RoundInbox(watermark, items);
    }

    private static async Task LockRoomOwnershipAsync(string tenantId, string gameId, string roomId,
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
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
