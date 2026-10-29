using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.DataServer;

public sealed record SettlementProgress(
    string RoomId, long ResultVersion, int Total, int Granted, int Pending, bool Duplicate);

public sealed record FinalizedRound(string RoomId, string Status, long Epoch, bool Duplicate);

public sealed record LeaderboardEntry(string PlayerId, long Score);

/// <summary>One room of the settle queue (FIX-SETTLE-01): the committed result's version
/// and how much of its manifest is still unapplied.</summary>
public sealed record PendingSettlement(
    string RoomId, long ResultVersion, int GrantsTotal, int GrantsGranted, int GrantsPending);

/// <summary>
/// Settlement and round finalization (architecture 9.3). The final frame commits the
/// immutable reward manifest — every grant row carries its own asset and amount — and
/// CommitResult applies each planned grant as its own idempotent transaction: a crash
/// mid-settlement only ever retries the incomplete grants, never re-awards the completed
/// ones, and every grant lands exactly one ledger line. FinalizeRound is the persistent
/// terminator: it verifies the preconditions and atomically settles the room, closes the
/// participations, frees the mount pointers and withdraws the ownership — after it, the
/// mounts can open the next round.
/// </summary>
/// <param name="crashAfterGrant">
/// Fault-injection seam for settlement restart tests: invoked inside each grant
/// transaction after the writes, before the commit, with the grant's ordinal. An
/// exception there plays the settler dying on the Nth reward; the rollback leaves that
/// grant Planned and every earlier grant Granted.
/// </param>
public sealed class RedesignSettlementStore(string connectionString, Action<int>? crashAfterGrant = null)
{
    public const string Granted = "Granted";
    public const string Settled = "Settled";

    /// <summary>The most pending rooms one discovery page answers (FIX-SETTLE-01): the
    /// queue walks in bounded pages, never one unbounded answer over the whole history.</summary>
    public const int DiscoveryPageLimit = 500;

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Applies every planned grant of the room's committed result. Grants already Granted
    /// are skipped without touching their accounts; each remaining grant is one receipt
    /// transaction over its own operation id. The reply states the manifest progress, so
    /// a repeated send is visible as zero pending work.
    /// </summary>
    public async Task<SettlementProgress> CommitResultAsync(string tenantId, string gameId, string roomId,
        string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(roomId, nameof(roomId)); Require(operationId, nameof(operationId));
        var (resultVersion, pending) = await ReadManifestAsync(tenantId, gameId, roomId, cancellationToken);
        if (resultVersion is null)
            throw new SettlementRejectedException("The room has no committed result.");
        foreach (var grant in pending)
        {
            await _receipts.ExecuteAsync(
                new OperationIdentity(tenantId, gameId, "settlement.grant", roomId, grant.OperationId),
                OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    grant.PlayerId, grant.AssetId, grant.Delta, grant.GrantKey
                })),
                (connection, transaction, token) => ApplyGrantAsync(tenantId, gameId, roomId, grant,
                    connection, transaction, token),
                cancellationToken);
        }
        var (_, total, granted, left) = await CountGrantsAsync(tenantId, gameId, roomId, resultVersion.Value,
            cancellationToken);
        return new SettlementProgress(roomId, resultVersion.Value, total, granted, left,
            Duplicate: pending.Count == 0 && total > 0);
    }

    private sealed record PendingGrant(
        string PlayerId, string GrantKey, string OperationId, string AssetId, long Delta, int Ordinal);

    private async Task<string> ApplyGrantAsync(string tenantId, string gameId, string roomId, PendingGrant grant,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Account serialisation follows the architecture lock order: the account row is
        // the only writer-scoped lock, and the manifest is walked in (player, key) order.
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO player.asset(tenant_id,game_id,player_id,asset_id,balance,version)
            VALUES (@tenant,@game,@player,@asset,0,1) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue("tenant", tenantId);
            seed.Parameters.AddWithValue("game", gameId);
            seed.Parameters.AddWithValue("player", grant.PlayerId);
            seed.Parameters.AddWithValue("asset", grant.AssetId);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
        long balance;
        await using (var update = new NpgsqlCommand("""
            UPDATE player.asset SET balance=balance+@delta,version=version+1
            WHERE tenant_id=@tenant AND game_id=@game AND player_id=@player AND asset_id=@asset
              AND balance+@delta>=0
            RETURNING balance
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("game", gameId);
            update.Parameters.AddWithValue("player", grant.PlayerId);
            update.Parameters.AddWithValue("asset", grant.AssetId);
            update.Parameters.AddWithValue("delta", grant.Delta);
            balance = await update.ExecuteScalarAsync(cancellationToken) as long?
                ?? throw new SettlementRejectedException(
                    $"The grant for {grant.PlayerId} would drive the asset negative.");
        }
        await using (var ledger = new NpgsqlCommand("""
            INSERT INTO player.ledger(tenant_id,game_id,operation_id,line_id,player_id,asset_id,delta,
                reason,resulting_balance)
            VALUES (@tenant,@game,@operation,'grant',@player,@asset,@delta,@reason,@balance)
            """, connection, transaction))
        {
            ledger.Parameters.AddWithValue("tenant", tenantId);
            ledger.Parameters.AddWithValue("game", gameId);
            ledger.Parameters.AddWithValue("operation", grant.OperationId);
            ledger.Parameters.AddWithValue("player", grant.PlayerId);
            ledger.Parameters.AddWithValue("asset", grant.AssetId);
            ledger.Parameters.AddWithValue("delta", grant.Delta);
            ledger.Parameters.AddWithValue("reason", $"settlement:{roomId}/{grant.GrantKey}");
            ledger.Parameters.AddWithValue("balance", balance);
            await ledger.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var mark = new NpgsqlCommand("""
            UPDATE settlement.grant SET status='Granted'
            WHERE room_id=@room AND player_id=@player AND grant_key=@key
            """, connection, transaction))
        {
            mark.Parameters.AddWithValue("room", roomId);
            mark.Parameters.AddWithValue("player", grant.PlayerId);
            mark.Parameters.AddWithValue("key", grant.GrantKey);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        // The leaderboard projection rides the grant transaction: a visible score is
        // always backed by a committed ledger line (architecture 9.2).
        await using (var board = new NpgsqlCommand("""
            INSERT INTO game.leaderboard_score(tenant_id,game_id,board_id,player_id,score)
            VALUES (@tenant,@game,@board,@player,@delta)
            ON CONFLICT (tenant_id,game_id,board_id,player_id)
            DO UPDATE SET score=game.leaderboard_score.score+@delta, updated_at=clock_timestamp()
            """, connection, transaction))
        {
            board.Parameters.AddWithValue("tenant", tenantId);
            board.Parameters.AddWithValue("game", gameId);
            board.Parameters.AddWithValue("board", grant.AssetId);
            board.Parameters.AddWithValue("player", grant.PlayerId);
            board.Parameters.AddWithValue("delta", grant.Delta);
            await board.ExecuteNonQueryAsync(cancellationToken);
        }
        crashAfterGrant?.Invoke(grant.Ordinal);
        return JsonSerializer.Serialize(new { grant.PlayerId, grant.AssetId, Balance = balance });
    }

    /// <summary>
    /// The persistent terminator (architecture 9.3). One transaction verifies the
    /// preconditions — committed result, complete manifest, no undisposed input — then
    /// settles the room, closes participations, clears the mount pointers that still name
    /// this room and withdraws the ownership with an epoch bump. Idempotent per operation;
    /// a room already settled under a different operation is a visible rejection.
    /// </summary>
    public async Task<FinalizedRound> FinalizeRoundAsync(string tenantId, string gameId, string roomId,
        string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(roomId, nameof(roomId)); Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(new
        {
            RoomId = roomId
        }));
        var receipt = await _receipts.ExecuteAsync(
            new OperationIdentity(tenantId, gameId, "round.finalize", roomId, operationId),
            fingerprint,
            (connection, transaction, token) => ApplyFinalizeAsync(tenantId, gameId, roomId,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<FinalizedRound>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored finalize receipt is unreadable.");
        return result with { Duplicate = receipt.Duplicate };
    }

    private async Task<string> ApplyFinalizeAsync(string tenantId, string gameId, string roomId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // The finalizer does not hold the ownership (architecture 9.3): it serializes on
        // the ownership row so no owner action races the release.
        await using (var fence = new NpgsqlCommand("""
            SELECT entity_id FROM control.ownership WHERE kind='room' AND entity_id=@room FOR UPDATE
            """, connection, transaction))
        {
            fence.Parameters.AddWithValue("room", roomId);
            await fence.ExecuteScalarAsync(cancellationToken);
        }
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
                ?? throw new SettlementRejectedException("The room does not exist.");
            if (status is not (RedesignFrameStore.Finishing or Settled))
                throw new SettlementRejectedException($"A room in status {status} cannot be finalized.");
            if (status == Settled)
                throw new SettlementRejectedException("The room is already settled.");
        }
        long resultVersion;
        await using (var result = new NpgsqlCommand(
            "SELECT max(result_version) FROM game.result WHERE room_id=@room", connection, transaction))
        {
            result.Parameters.AddWithValue("room", roomId);
            var version = await result.ExecuteScalarAsync(cancellationToken) as long?;
            if (version is null)
                throw new SettlementRejectedException("The room has no committed result to finalize.");
            resultVersion = version.Value;
        }
        await using (var manifest = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE status<>'Granted'),count(*) FROM settlement.grant
            WHERE room_id=@room AND result_version=@version
            """, connection, transaction))
        {
            manifest.Parameters.AddWithValue("room", roomId);
            manifest.Parameters.AddWithValue("version", resultVersion);
            await using var reader = await manifest.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new SettlementRejectedException("The reward manifest is unreadable.");
            if (reader.GetInt64(0) > 0)
                throw new SettlementRejectedException(
                    "The reward manifest is incomplete: settle the grants before finalizing.");
        }
        await using (var inbox = new NpgsqlCommand("""
            SELECT count(*) FROM input.delivery WHERE consumer_scope=@scope AND state='Pending'
            """, connection, transaction))
        {
            inbox.Parameters.AddWithValue("scope", "room:" + roomId);
            if (Convert.ToInt64(await inbox.ExecuteScalarAsync(cancellationToken)) > 0)
                throw new SettlementRejectedException(
                    "The round still has undisposed inputs; finalize after they reach a terminal state.");
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
        await using (var settle = new NpgsqlCommand(
            "UPDATE game.room SET status=@status, updated_at=clock_timestamp() WHERE room_id=@room",
            connection, transaction))
        {
            settle.Parameters.AddWithValue("status", Settled);
            settle.Parameters.AddWithValue("room", roomId);
            await settle.ExecuteNonQueryAsync(cancellationToken);
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
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@effect,'round.settled',@payload::jsonb,'Pending')
            ON CONFLICT (effect_id) DO NOTHING
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("effect", $"{roomId}/0/settle");
            outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
            {
                RoomId = roomId, TenantId = tenantId, GameId = gameId, ResultVersion = resultVersion,
                ReleasedEpoch = releasedEpoch
            }));
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(new FinalizedRound(roomId, Settled, releasedEpoch, Duplicate: false));
    }

    /// <summary>
    /// The deterministic board read: score descending, player id ascending — ties keep a
    /// stable order, the projection is only ever read here.
    /// </summary>
    public async Task<IReadOnlyList<LeaderboardEntry>> ReadLeaderboardAsync(string tenantId, string gameId,
        string boardId, int maxEntries, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        Require(boardId, nameof(boardId));
        if (maxEntries is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var entries = new List<LeaderboardEntry>();
        await using (var board = new NpgsqlCommand("""
            SELECT player_id,score FROM game.leaderboard_score
            WHERE tenant_id=@tenant AND game_id=@game AND board_id=@board
            ORDER BY score DESC, player_id ASC LIMIT @max
            """, connection))
        {
            board.Parameters.AddWithValue("tenant", tenantId);
            board.Parameters.AddWithValue("game", gameId);
            board.Parameters.AddWithValue("board", boardId);
            board.Parameters.AddWithValue("max", maxEntries);
            await using var reader = await board.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new LeaderboardEntry(reader.GetString(0), reader.GetInt64(1)));
        }
        return entries;
    }

    /// <summary>
    /// The settle queue discovery read (FIX-SETTLE-01): rooms whose committed result still
    /// has unapplied grants, one bounded page in deterministic room order. Rooms without a
    /// committed result never appear, and a partially applied manifest stays listed until
    /// every grant of its version is Granted — a settler that died mid-queue is simply
    /// rediscovered, never double-counted. The cursor is the last room id the page carried
    /// (empty starts the walk), so no page scans or returns the whole history.
    /// </summary>
    public async Task<IReadOnlyList<PendingSettlement>> ListPendingSettlementsAsync(string tenantId,
        string gameId, string? cursorRoomId, int limit, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(gameId, nameof(gameId));
        if (limit is < 1 or > DiscoveryPageLimit)
            throw new ArgumentOutOfRangeException(nameof(limit),
                $"The discovery page limit must be within 1..{DiscoveryPageLimit}: {limit}");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var rooms = new List<PendingSettlement>();
        await using (var queue = new NpgsqlCommand("""
            SELECT r.room_id,latest.version,g.granted,g.total
            FROM game.room r
            JOIN LATERAL (
                SELECT max(result_version) AS version FROM game.result WHERE room_id=r.room_id
            ) latest ON latest.version IS NOT NULL
            JOIN LATERAL (
                SELECT count(*) FILTER (WHERE status='Granted') AS granted, count(*) AS total
                FROM settlement.grant WHERE room_id=r.room_id AND result_version=latest.version
            ) g ON g.granted < g.total
            WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.room_id > @cursor
            ORDER BY r.room_id
            LIMIT @limit
            """, connection))
        {
            queue.Parameters.AddWithValue("tenant", tenantId);
            queue.Parameters.AddWithValue("game", gameId);
            // An empty cursor starts the walk: every room id sorts past the empty string.
            queue.Parameters.AddWithValue("cursor", cursorRoomId ?? "");
            queue.Parameters.AddWithValue("limit", limit);
            await using var reader = await queue.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var granted = Convert.ToInt32(reader.GetInt64(2));
                var total = Convert.ToInt32(reader.GetInt64(3));
                rooms.Add(new PendingSettlement(reader.GetString(0), reader.GetInt64(1),
                    total, granted, total - granted));
            }
        }
        return rooms;
    }

    private async Task<(long? ResultVersion, List<PendingGrant> Pending)> ReadManifestAsync(
        string tenantId, string gameId, string roomId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        long? resultVersion;
        await using (var result = new NpgsqlCommand(
            "SELECT max(result_version) FROM game.result WHERE room_id=@room", connection))
        {
            result.Parameters.AddWithValue("room", roomId);
            resultVersion = await result.ExecuteScalarAsync(cancellationToken) as long?;
        }
        var pending = new List<PendingGrant>();
        if (resultVersion is null) return (null, pending);
        await using (var manifest = new NpgsqlCommand("""
            SELECT player_id,grant_key,operation_id,asset_id,delta FROM settlement.grant
            WHERE room_id=@room AND result_version=@version AND status<>'Granted'
            ORDER BY player_id,grant_key
            """, connection))
        {
            manifest.Parameters.AddWithValue("room", roomId);
            manifest.Parameters.AddWithValue("version", resultVersion.Value);
            await using var reader = await manifest.ExecuteReaderAsync(cancellationToken);
            var ordinal = 0;
            while (await reader.ReadAsync(cancellationToken))
                pending.Add(new PendingGrant(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), ++ordinal));
        }
        return (resultVersion, pending);
    }

    private async Task<(long Version, int Total, int Granted, int Pending)> CountGrantsAsync(
        string tenantId, string gameId, string roomId, long resultVersion, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var manifest = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE status='Granted'),count(*) FROM settlement.grant
            WHERE room_id=@room AND result_version=@version
            """, connection);
        manifest.Parameters.AddWithValue("room", roomId);
        manifest.Parameters.AddWithValue("version", resultVersion);
        await using var reader = await manifest.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new SettlementRejectedException("The reward manifest is unreadable.");
        var granted = Convert.ToInt32(reader.GetInt64(0));
        var total = Convert.ToInt32(reader.GetInt64(1));
        return (resultVersion, total, granted, total - granted);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}

public sealed class SettlementRejectedException(string message) : Exception(message);
