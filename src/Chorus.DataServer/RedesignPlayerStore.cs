using System.Text.Json;
using Chorus.Data.Redesign;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.DataServer;

public sealed class RedesignVersionConflictException : Exception;
public sealed class RedesignStaleOwnerException : Exception;

public sealed record ProfileValue(long Version, int SchemaVersion, string DataJson);
public sealed record AssetValue(long Balance, long Version);

/// <summary>M0 player and fenced probe writes. All effects use the same transaction as their receipt.</summary>
public sealed class RedesignPlayerStore(string connectionString)
{
    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    public Task<OperationReceipt?> GetOperationAsync(OperationIdentity identity, OperationFingerprint fingerprint,
        CancellationToken cancellationToken = default) =>
        _receipts.GetAsync(identity, fingerprint, cancellationToken);

    public async Task<ProfileValue?> GetProfileAsync(string tenantId, string gameId, string playerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT version, schema_version, data::text FROM player.profile
            WHERE tenant_id=@tenant AND game_id=@game AND player_id=@player
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("player", playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ProfileValue(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)) : null;
    }

    public async Task<(ProfileValue Value, bool Duplicate)> PatchProfileAsync(
        OperationIdentity identity, OperationFingerprint fingerprint, long expectedVersion,
        int schemaVersion, string dataJson, CancellationToken cancellationToken = default)
    {
        if (identity.OperationKind != "profile.patch" || expectedVersion < 0 || schemaVersion <= 0)
            throw new ArgumentException("Invalid profile operation.");
        using (JsonDocument.Parse(dataJson)) { }
        var result = await _receipts.ExecuteAsync(identity, fingerprint, async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand(expectedVersion == 0 ? """
                INSERT INTO player.profile(tenant_id,game_id,player_id,version,schema_version,data)
                VALUES (@tenant,@game,@player,1,@schema,@data::jsonb)
                ON CONFLICT DO NOTHING RETURNING version
                """ : """
                UPDATE player.profile SET version=version+1,schema_version=@schema,data=@data::jsonb
                WHERE tenant_id=@tenant AND game_id=@game AND player_id=@player AND version=@expected
                RETURNING version
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", identity.TenantId);
            command.Parameters.AddWithValue("game", identity.GameId);
            command.Parameters.AddWithValue("player", identity.TargetId);
            command.Parameters.AddWithValue("schema", schemaVersion);
            command.Parameters.AddWithValue("data", dataJson);
            if (expectedVersion != 0) command.Parameters.AddWithValue("expected", expectedVersion);
            var version = await command.ExecuteScalarAsync(token) as long?;
            if (version is null) throw new RedesignVersionConflictException();
            return JsonSerializer.Serialize(new ProfileValue(version.Value, schemaVersion, dataJson));
        }, cancellationToken);
        var value = JsonSerializer.Deserialize<ProfileValue>(result.ResultJson)
            ?? throw new InvalidOperationException("Stored profile receipt is invalid.");
        return (value, result.Duplicate);
    }

    public async Task<AssetValue> GetAssetAsync(string tenantId, string gameId, string playerId, string assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT balance,version FROM player.asset
            WHERE tenant_id=@tenant AND game_id=@game AND player_id=@player AND asset_id=@asset
            """, connection);
        BindAccount(command, tenantId, gameId, playerId, assetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AssetValue(reader.GetInt64(0), reader.GetInt64(1)) : new AssetValue(0, 0);
    }

    public async Task<(AssetValue Value, bool Duplicate)> ChangeAssetAsync(
        OperationIdentity identity, OperationFingerprint fingerprint, string assetId, long delta,
        string reason, CancellationToken cancellationToken = default)
    {
        if (identity.OperationKind != "asset.change" || string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Invalid asset operation.");
        var result = await _receipts.ExecuteAsync(identity, fingerprint, async (connection, transaction, token) =>
        {
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO player.asset(tenant_id,game_id,player_id,asset_id,balance,version)
                VALUES (@tenant,@game,@player,@asset,0,1) ON CONFLICT DO NOTHING
                """, connection, transaction))
            {
                BindAccount(seed, identity.TenantId, identity.GameId, identity.TargetId, assetId);
                await seed.ExecuteNonQueryAsync(token);
            }
            await using var update = new NpgsqlCommand("""
                UPDATE player.asset SET balance=balance+@delta,version=version+1
                WHERE tenant_id=@tenant AND game_id=@game AND player_id=@player AND asset_id=@asset
                  AND balance+@delta>=0
                RETURNING balance,version
                """, connection, transaction);
            BindAccount(update, identity.TenantId, identity.GameId, identity.TargetId, assetId);
            update.Parameters.AddWithValue("delta", delta);
            AssetValue value;
            await using (var reader = await update.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new RedesignVersionConflictException();
                value = new AssetValue(reader.GetInt64(0), reader.GetInt64(1));
            }
            await using var ledger = new NpgsqlCommand("""
                INSERT INTO player.ledger(tenant_id,game_id,operation_id,line_id,player_id,asset_id,delta,reason,resulting_balance)
                VALUES (@tenant,@game,@operation,'main',@player,@asset,@delta,@reason,@balance)
                """, connection, transaction);
            BindAccount(ledger, identity.TenantId, identity.GameId, identity.TargetId, assetId);
            ledger.Parameters.AddWithValue("operation", identity.OperationId);
            ledger.Parameters.AddWithValue("delta", delta);
            ledger.Parameters.AddWithValue("reason", reason);
            ledger.Parameters.AddWithValue("balance", value.Balance);
            await ledger.ExecuteNonQueryAsync(token);
            return JsonSerializer.Serialize(value);
        }, cancellationToken);
        return (JsonSerializer.Deserialize<AssetValue>(result.ResultJson)
            ?? throw new InvalidOperationException("Stored asset receipt is invalid."), result.Duplicate);
    }

    public async Task<OperationReceipt> CommitFencedProbeAsync(
        OperationIdentity identity, OperationFingerprint fingerprint, string ownerKind, string entityId,
        string ownerInstanceId, long epoch, string effectId, CancellationToken cancellationToken = default)
    {
        if (identity.OperationKind != "fenced.probe" || identity.TargetId != entityId || epoch <= 0)
            throw new ArgumentException("Invalid fenced operation.");
        return await _receipts.ExecuteAsync(identity, fingerprint, async (connection, transaction, token) =>
        {
            await using var lockOwner = new NpgsqlCommand("""
                SELECT tenant_id,game_id,owner_process_instance_id,epoch,lease_until>clock_timestamp(),status
                FROM control.ownership WHERE kind=@kind AND entity_id=@entity FOR UPDATE
                """, connection, transaction);
            lockOwner.Parameters.AddWithValue("kind", ownerKind);
            lockOwner.Parameters.AddWithValue("entity", entityId);
            await using (var reader = await lockOwner.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token) || reader.GetString(0) != identity.TenantId ||
                    reader.GetString(1) != identity.GameId || reader.GetString(2) != ownerInstanceId ||
                    reader.GetInt64(3) != epoch || !reader.GetBoolean(4) || reader.GetString(5) != "Active")
                    throw new RedesignStaleOwnerException();
            }
            await using var effect = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id,kind,payload,status)
                VALUES (@effect,'m0.fenced_probe',@payload::jsonb,'Pending')
                """, connection, transaction);
            effect.Parameters.AddWithValue("effect", effectId);
            effect.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new { ownerKind, entityId, epoch }));
            await effect.ExecuteNonQueryAsync(token);
            return JsonSerializer.Serialize(new { effectId });
        }, cancellationToken);
    }

    private static void BindAccount(NpgsqlCommand command, string tenantId, string gameId, string playerId, string assetId)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("player", playerId);
        command.Parameters.AddWithValue("asset", assetId);
    }
}
