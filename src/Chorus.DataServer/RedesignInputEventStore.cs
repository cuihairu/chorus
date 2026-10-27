using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.DataServer;

public sealed record PlatformEventInput(
    string SourceDedupKey, string Durability, string CanonicalRoomKey, string PayloadJson);

public sealed record PlatformEventReceipt(
    string SourceDedupKey, bool Duplicate, string Disposition, string? BindingId, long? BindingGeneration,
    string? RoomId = null);

public sealed record IngestResult(IReadOnlyList<PlatformEventReceipt> Receipts, bool Duplicate);

public sealed record PendingEvent(
    string SourceDedupKey, string CanonicalRoomKey, string PayloadJson, DateTimeOffset ReceivedAtUtc);

/// <summary>
/// Durable platform event intake (architecture 6.1). A batch, its dedup keys and the batch
/// operation receipt are written in one transaction; the ack may only leave this store after
/// that transaction committed. Attribution follows the first durable receive: the active
/// binding on the canonical room key at that moment wins, duplicates keep the original
/// attribution instead of being re-derived. Durable events without a resolvable binding wait
/// in the persistent inbox (PendingAttribution) for reconciliation; best-effort events are
/// stored once but never inboxed.
/// </summary>
/// <param name="crashAfterPersist">
/// Fault-injection seam for restart tests: invoked inside the transaction after every event
/// insert, before the receipt store commits. An exception there plays a process dying between
/// persisting the batch and acknowledging it; the rollback leaves no partial batch behind.
/// </param>
public sealed class RedesignInputEventStore(string connectionString, Action? crashAfterPersist = null)
{
    public const string Durable = "Durable";
    public const string BestEffort = "BestEffort";
    public const string Attributed = "Attributed";
    public const string PendingAttribution = "PendingAttribution";
    public const string BestEffortDropped = "BestEffortDropped";

    private const string ActiveStates = "('Starting','Ready','Running','AwaitingPlatformClose')";
    private const string ClusterTenantId = "@cluster";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Persists a webhook batch atomically. Exact operation retries replay the stored receipts;
    /// a different batch under the same operation id conflicts; events already ingested by
    /// another operation answer with their original attribution as duplicates.
    /// </summary>
    public async Task<IngestResult> IngestAsync(string platform, string appId, string operationId,
        IReadOnlyList<PlatformEventInput> batch, CancellationToken cancellationToken = default)
    {
        Require(platform, nameof(platform));
        Require(appId, nameof(appId));
        Require(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            throw new ArgumentException("The batch must contain at least one event.", nameof(batch));
        foreach (var input in batch)
        {
            Require(input.SourceDedupKey, nameof(input.SourceDedupKey));
            Require(input.CanonicalRoomKey, nameof(input.CanonicalRoomKey));
            Require(input.PayloadJson, nameof(input.PayloadJson));
            if (input.Durability is not (Durable or BestEffort))
                throw new ArgumentException($"Durability must be {Durable} or {BestEffort}.", nameof(batch));
            using (JsonDocument.Parse(input.PayloadJson)) { }
        }
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Platform = platform,
            AppId = appId,
            Events = batch.Select(input => new
            {
                input.SourceDedupKey, input.Durability, input.CanonicalRoomKey, input.PayloadJson
            })
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(ClusterTenantId, OperationIdentity.ClusterGameId, "input.ingest",
                appId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyAsync(platform, appId, batch, connection, transaction, token),
            cancellationToken);
        var receipts = JsonSerializer.Deserialize<PlatformEventReceipt[]>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored ingest receipt is unreadable.");
        if (receipt.Duplicate)
            receipts = receipts.Select(item => item with { Duplicate = true }).ToArray();
        return new IngestResult(receipts, receipt.Duplicate);
    }

    /// <summary>The persistent inbox: durable events still waiting for a binding.</summary>
    public async Task<IReadOnlyList<PendingEvent>> GetPendingAsync(string platform,
        CancellationToken cancellationToken = default)
    {
        Require(platform, nameof(platform));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT source_dedup_key, canonical_room_key, payload::text, received_at
            FROM input.event
            WHERE platform=@platform AND disposition=@disposition
            ORDER BY received_at, event_id
            """, connection);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("disposition", PendingAttribution);
        var pending = new List<PendingEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            pending.Add(new PendingEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        return pending;
    }

    /// <summary>
    /// Reconciliation: once a binding is active on the room key, pending inbox events are
    /// attributed to it in one statement. Receive time is never rewritten.
    /// </summary>
    public async Task<int> ReconcilePendingAsync(string platform, string canonicalRoomKey,
        CancellationToken cancellationToken = default)
    {
        Require(platform, nameof(platform));
        Require(canonicalRoomKey, nameof(canonicalRoomKey));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH candidate AS (
                SELECT s.active_binding_id AS binding_id, s.generation AS generation
                FROM live.slot s
                JOIN live.binding b ON b.binding_id = s.active_binding_id
                WHERE s.platform=@platform AND s.canonical_live_session_key=@room
                  AND b.state IN {ActiveStates}
            )
            UPDATE input.event e
            SET binding_id=candidate.binding_id, binding_generation=candidate.generation,
                disposition=@attributed
            FROM candidate
            WHERE e.platform=@platform AND e.canonical_room_key=@room
              AND e.disposition=@pending
            """, connection);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("room", canonicalRoomKey);
        command.Parameters.AddWithValue("attributed", Attributed);
        command.Parameters.AddWithValue("pending", PendingAttribution);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> ApplyAsync(string platform, string appId, IReadOnlyList<PlatformEventInput> batch,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var receipts = new List<PlatformEventReceipt>(batch.Count);
        foreach (var input in batch)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.PayloadJson));
            string? storedDisposition = null;
            byte[]? storedHash = null;
            string? storedBinding = null;
            long? storedGeneration = null;
            string? storedRoom = null;
            await using (var existing = new NpgsqlCommand("""
                SELECT payload_hash, disposition, binding_id, binding_generation, attributed_room_id
                FROM input.event
                WHERE platform=@platform AND app_id=@app AND source_dedup_key=@key
                """, connection, transaction))
            {
                existing.Parameters.AddWithValue("platform", platform);
                existing.Parameters.AddWithValue("app", appId);
                existing.Parameters.AddWithValue("key", input.SourceDedupKey);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    storedHash = reader.GetFieldValue<byte[]>(0);
                    storedDisposition = reader.GetString(1);
                    storedBinding = reader.IsDBNull(2) ? null : reader.GetString(2);
                    storedGeneration = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                    storedRoom = reader.IsDBNull(4) ? null : reader.GetString(4);
                }
            }
            if (storedDisposition is not null)
            {
                // The same platform event must carry the same payload; "looks similar" is not
                // a duplicate (two legal same-amount gifts have different dedup keys). The
                // reply keeps the original round attribution, not a re-derived one.
                if (storedHash is null || !storedHash.AsSpan().SequenceEqual(hash))
                    throw new IdempotencyConflictException();
                receipts.Add(new PlatformEventReceipt(input.SourceDedupKey, true, storedDisposition,
                    storedBinding, storedGeneration, storedRoom));
                continue;
            }

            string? bindingId = null;
            long generation = 0;
            await using (var slot = new NpgsqlCommand($"""
                SELECT s.active_binding_id, s.generation
                FROM live.slot s
                JOIN live.binding b ON b.binding_id = s.active_binding_id
                WHERE s.platform=@platform AND s.canonical_live_session_key=@room
                  AND b.state IN {ActiveStates}
                """, connection, transaction))
            {
                slot.Parameters.AddWithValue("platform", platform);
                slot.Parameters.AddWithValue("room", input.CanonicalRoomKey);
                await using var reader = await slot.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    bindingId = reader.GetString(0);
                    generation = reader.GetInt64(1);
                }
            }
            var disposition = bindingId is not null ? Attributed
                : input.Durability == BestEffort ? BestEffortDropped : PendingAttribution;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,
                    disposition,binding_id,binding_generation,payload,canonical_room_key,durability)
                VALUES (@event,@platform,@app,@key,@hash,@disposition,@binding,@generation,
                    @payload::jsonb,@room,@durability)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("event", Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("platform", platform);
                insert.Parameters.AddWithValue("app", appId);
                insert.Parameters.AddWithValue("key", input.SourceDedupKey);
                insert.Parameters.AddWithValue("hash", hash);
                insert.Parameters.AddWithValue("disposition", disposition);
                if (bindingId is null)
                {
                    insert.Parameters.AddWithValue("binding", DBNull.Value);
                    insert.Parameters.AddWithValue("generation", DBNull.Value);
                }
                else
                {
                    insert.Parameters.AddWithValue("binding", bindingId);
                    insert.Parameters.AddWithValue("generation", generation);
                }
                insert.Parameters.AddWithValue("payload", input.PayloadJson);
                insert.Parameters.AddWithValue("room", input.CanonicalRoomKey);
                insert.Parameters.AddWithValue("durability", input.Durability);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            receipts.Add(new PlatformEventReceipt(input.SourceDedupKey, false, disposition, bindingId,
                bindingId is null ? null : generation));
        }
        crashAfterPersist?.Invoke();
        return JsonSerializer.Serialize(receipts);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
