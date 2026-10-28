using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.DataServer;

public sealed record PrunedRound(
    string RoomId, long KeepTick, long FramesDeleted, long SnapshotsDeleted, bool Duplicate);

public sealed class RedesignRetentionException(string message) : Exception(message);

/// <summary>
/// Retention and cleanup boundary (architecture 9 data retention, AT21). Pruning is a
/// deliberate, receipted control-plane action with fail-visible guards: only a settled round
/// releases its frame prefix, only strictly below the older of its last two snapshots — the
/// pair a recoverer falls back through — only with zero undisposed inputs, and
/// never inside the settled-round retention window unless the operator explicitly overrides it.
/// Raw input events, receipts, results, ledger lines and grants are never touched: dedup,
/// audit and settlement evidence outlive the frames.
/// </summary>
public sealed class RedesignRetentionStore(string connectionString)
{
    public static readonly TimeSpan DefaultSettledRetention = TimeSpan.FromDays(7);

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Releases the frame prefix below the round's retention anchor. The anchor is the older
    /// of the room's last two snapshots of any kind, so a recoverer still has the whole
    /// fallback pair and every frame above them; rows below the anchor go with the prefix.
    /// A round whose snapshot history is too short to free anything answers an honest no-op
    /// instead of inventing headroom.
    /// </summary>
    public async Task<PrunedRound> PruneRoundAsync(string tenantId, string gameId, string roomId,
        string operationId, bool overrideRetentionWindow = false,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(roomId, nameof(roomId)); Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            RoomId = roomId, OverrideRetentionWindow = overrideRetentionWindow
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "retention.prune", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyPruneAsync(tenantId, gameId, roomId,
                overrideRetentionWindow, connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<PrunedRound>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored prune receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyPruneAsync(string tenantId, string gameId, string roomId,
        bool overrideWindow, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        string status; DateTime settledAt;
        await using (var room = new NpgsqlCommand("""
            SELECT status,updated_at FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game FOR UPDATE
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            await using var reader = await room.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RedesignRetentionException("The room does not exist.");
            status = reader.GetString(0);
            settledAt = reader.GetDateTime(1);
        }
        if (status != RedesignSettlementStore.Settled)
            throw new RedesignRetentionException(
                $"A room in status {status} keeps its whole record; only a settled round may release a prefix.");
        if (!overrideWindow && DateTimeOffset.UtcNow - settledAt < DefaultSettledRetention)
            throw new RedesignRetentionException(
                $"The settled round is inside its {DefaultSettledRetention.TotalHours:0} hour retention window; " +
                "prune after it expires or override it explicitly.");
        await using (var inbox = new NpgsqlCommand("""
            SELECT count(*) FROM input.delivery WHERE consumer_scope=@scope AND state='Pending'
            """, connection, transaction))
        {
            inbox.Parameters.AddWithValue("scope", "room:" + roomId);
            if (Convert.ToInt64(await inbox.ExecuteScalarAsync(cancellationToken)) > 0)
                throw new RedesignRetentionException(
                    "The round still has undisposed inputs; retention never deletes them to free space.");
        }
        // The anchor: the older of the last two snapshots of any kind — admission rows, cadence
        // checkpoints and store-verified deposits alike. Recovery may fall back one snapshot
        // when the newest fails to restore, so the pair survives the release and every frame
        // above the older one stays; both snapshot paths ride a single transaction and replay
        // re-checks each frame hash on the walk, so neither kind is a weaker anchor.
        long keepTick;
        await using (var anchor = new NpgsqlCommand("""
            SELECT tick_id FROM game.snapshot
            WHERE room_id=@room ORDER BY tick_id DESC LIMIT 2
            """, connection, transaction))
        {
            anchor.Parameters.AddWithValue("room", roomId);
            await using var reader = await anchor.ExecuteReaderAsync(cancellationToken);
            var ticks = new List<long>();
            while (await reader.ReadAsync(cancellationToken)) ticks.Add(reader.GetInt64(0));
            if (ticks.Count == 0)
                throw new RedesignRetentionException(
                    "The round has no snapshot anchor; nothing can be released.");
            keepTick = ticks[^1];
        }
        long framesDeleted;
        await using (var frames = new NpgsqlCommand(
            "DELETE FROM game.frame WHERE room_id=@room AND tick_id<@keep", connection, transaction))
        {
            frames.Parameters.AddWithValue("room", roomId);
            frames.Parameters.AddWithValue("keep", keepTick);
            framesDeleted = await frames.ExecuteNonQueryAsync(cancellationToken);
        }
        long snapshotsDeleted;
        await using (var snapshots = new NpgsqlCommand(
            "DELETE FROM game.snapshot WHERE room_id=@room AND tick_id<@keep", connection, transaction))
        {
            snapshots.Parameters.AddWithValue("room", roomId);
            snapshots.Parameters.AddWithValue("keep", keepTick);
            snapshotsDeleted = await snapshots.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var mark = new NpgsqlCommand("""
            UPDATE game.room SET pruned_before_tick=GREATEST(pruned_before_tick,@keep)
            WHERE room_id=@room AND pruned_before_tick<@keep
            """, connection, transaction))
        {
            mark.Parameters.AddWithValue("room", roomId);
            mark.Parameters.AddWithValue("keep", keepTick);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(new PrunedRound(roomId, keepTick, framesDeleted,
            snapshotsDeleted, Duplicate: false));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{name}' must not be empty.", name);
    }
}
