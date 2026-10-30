using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed record BindingGrant(
    string BindingId, string LiveSessionId, string GameId, long Generation, string State, bool Duplicate);

public sealed record BindingStatus(
    string LiveSessionId, string BindingId, string GameId, long Generation, string State);

/// <summary>
/// Gameplay mounting on the shared live slot (architecture 6.2). The slot mutex is
/// (platform, canonical live session key) — cross-GameId by design — and one live session has at
/// most one active binding at any time, enforced inside one transaction under the slot lock.
/// </summary>
/// <param name="crashAfterClosingOldBinding">
/// Fault-injection seam for restart tests: invoked mid-switch after the old binding close, before
/// the transaction commits. An exception there plays a process dying between the two steps; the
/// rollback keeps the old mount active.
/// </param>
public sealed class RedesignBindingStore(string connectionString, Action? crashAfterClosingOldBinding = null)
{
    private const string ActiveStates = "('Starting','Ready','Running','AwaitingPlatformClose')";

    private readonly PostgresOperationReceiptStore _receipts = new(connectionString);

    /// <summary>
    /// Registers the verified live broadcast for a streamer. Idempotent per canonical key; the
    /// key may not be re-bound to another tenant or another streamer.
    /// </summary>
    public async Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform,
        string appId, string platformRoomKey, string canonicalLiveSessionKey,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(streamerId, nameof(streamerId));
        Require(platform, nameof(platform)); Require(appId, nameof(appId));
        Require(platformRoomKey, nameof(platformRoomKey)); Require(canonicalLiveSessionKey, nameof(canonicalLiveSessionKey));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var slot = new NpgsqlCommand("""
            INSERT INTO live.slot(platform,canonical_live_session_key,tenant_id)
            VALUES (@platform,@key,@tenant) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            slot.Parameters.AddWithValue("platform", platform);
            slot.Parameters.AddWithValue("key", canonicalLiveSessionKey);
            slot.Parameters.AddWithValue("tenant", tenantId);
            await slot.ExecuteNonQueryAsync(cancellationToken);
        }
        string slotTenant;
        await using (var selectSlot = new NpgsqlCommand("""
            SELECT tenant_id FROM live.slot WHERE platform=@platform AND canonical_live_session_key=@key
            FOR UPDATE
            """, connection, transaction))
        {
            selectSlot.Parameters.AddWithValue("platform", platform);
            selectSlot.Parameters.AddWithValue("key", canonicalLiveSessionKey);
            slotTenant = await selectSlot.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidOperationException("The live slot disappeared during the transaction.");
        }
        if (!StringComparer.Ordinal.Equals(slotTenant, tenantId))
            throw new BindingDeniedException();

        string? existingId = null;
        string? existingStreamer = null;
        await using (var selectSession = new NpgsqlCommand("""
            SELECT live_session_id,streamer_id FROM live.session
            WHERE platform=@platform AND canonical_live_session_key=@key
            """, connection, transaction))
        {
            selectSession.Parameters.AddWithValue("platform", platform);
            selectSession.Parameters.AddWithValue("key", canonicalLiveSessionKey);
            await using var reader = await selectSession.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingId = reader.GetString(0);
                existingStreamer = reader.GetString(1);
            }
        }
        if (existingId is not null)
        {
            if (!StringComparer.Ordinal.Equals(existingStreamer, streamerId))
                throw new BindingDeniedException();
            await transaction.CommitAsync(cancellationToken);
            return existingId;
        }

        var liveSessionId = Guid.NewGuid().ToString("N");
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                platform_room_key,started_at,canonical_live_session_key)
            VALUES (@session,@tenant,@streamer,@platform,@app,@room,clock_timestamp(),@key)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("session", liveSessionId);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("streamer", streamerId);
            insert.Parameters.AddWithValue("platform", platform);
            insert.Parameters.AddWithValue("app", appId);
            insert.Parameters.AddWithValue("room", platformRoomKey);
            insert.Parameters.AddWithValue("key", canonicalLiveSessionKey);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return liveSessionId;
    }

    /// <summary>The streamer's open live session, or null when not live.</summary>
    public async Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(streamerId, nameof(streamerId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT live_session_id FROM live.session
            WHERE tenant_id=@tenant AND streamer_id=@streamer AND ended_at IS NULL
            ORDER BY started_at DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("streamer", streamerId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>The active binding to restore at login, or null when none is mounted.</summary>
    public async Task<BindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
        CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(streamerId, nameof(streamerId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT s.live_session_id,b.binding_id,b.game_id,b.generation,b.state
            FROM live.session s
            JOIN live.binding b ON b.live_session_id=s.live_session_id AND b.state IN {ActiveStates}
            WHERE s.tenant_id=@tenant AND s.streamer_id=@streamer AND s.ended_at IS NULL
            ORDER BY b.generation DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("streamer", streamerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new BindingStatus(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetString(4));
    }

    /// <summary>
    /// Starts or switches the gameplay binding. The old active binding is closed and the new one
    /// committed under the slot lock in one transaction; only the current control session may do
    /// this, re-checked inside the transaction. Exact retries return the original grant.
    /// </summary>
    public async Task<BindingGrant> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
        string gameId, string operationId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(liveSessionId, nameof(liveSessionId));
        Require(sessionId, nameof(sessionId)); Require(gameId, nameof(gameId));
        Require(operationId, nameof(operationId));
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1, JsonSerializer.SerializeToUtf8Bytes(
            new { LiveSessionId = liveSessionId, GameId = gameId }));
        var receipt = await _receipts.ExecuteAsync(
            // The receipt key is live-session scoped, not game scoped: the same operation id with a
            // different game must conflict, not silently mount a second binding.
            new OperationIdentity(tenantId, OperationIdentity.ClusterGameId, "binding.start", liveSessionId,
                operationId),
            fingerprint,
            (connection, transaction, token) => ApplyAsync(tenantId, liveSessionId, sessionId, gameId,
                connection, transaction, token),
            cancellationToken);
        var result = JsonSerializer.Deserialize<BindingResult>(receipt.ResultJson)
            ?? throw new InvalidOperationException("The stored binding receipt is unreadable.");
        return new BindingGrant(result.BindingId, liveSessionId, gameId, result.Generation, result.State,
            receipt.Duplicate);
    }

    private async Task<string> ApplyAsync(string tenantId, string liveSessionId, string sessionId, string gameId,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Fencing first: re-read the caller's session currency inside the business transaction.
        string streamerId;
        await using (var session = new NpgsqlCommand(
            "SELECT streamer_id FROM auth.session WHERE session_id=@session", connection, transaction))
        {
            session.Parameters.AddWithValue("session", sessionId);
            streamerId = await session.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new SessionRevokedException();
        }
        string? currentSessionId;
        await using (var state = new NpgsqlCommand("""
            SELECT current_session_id,ban_state FROM auth.streamer_state
            WHERE streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new SessionRevokedException();
            currentSessionId = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (reader.GetString(1) != "Allowed" || currentSessionId != sessionId)
                throw new SessionRevokedException();
        }

        string platform;
        string canonicalKey;
        await using (var live = new NpgsqlCommand("""
            SELECT platform,canonical_live_session_key,tenant_id,streamer_id,ended_at
            FROM live.session WHERE live_session_id=@session FOR UPDATE
            """, connection, transaction))
        {
            live.Parameters.AddWithValue("session", liveSessionId);
            await using var reader = await live.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.IsDBNull(4) ||
                !StringComparer.Ordinal.Equals(reader.GetString(2), tenantId) ||
                !StringComparer.Ordinal.Equals(reader.GetString(3), streamerId))
                throw new BindingDeniedException();
            platform = reader.GetString(0);
            canonicalKey = reader.IsDBNull(1) ? throw new BindingDeniedException() : reader.GetString(1);
        }
        long generation;
        await using (var slot = new NpgsqlCommand("""
            SELECT generation,tenant_id FROM live.slot
            WHERE platform=@platform AND canonical_live_session_key=@key FOR UPDATE
            """, connection, transaction))
        {
            slot.Parameters.AddWithValue("platform", platform);
            slot.Parameters.AddWithValue("key", canonicalKey);
            await using var reader = await slot.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                !StringComparer.Ordinal.Equals(reader.GetString(1), tenantId))
                throw new BindingDeniedException();
            generation = reader.GetInt64(0) + 1;
        }
        var bindingId = Guid.NewGuid().ToString("N");
        await using (var close = new NpgsqlCommand($"""
            UPDATE live.binding SET state='Closed'
            WHERE live_session_id=@session AND state IN {ActiveStates}
            """, connection, transaction))
        {
            close.Parameters.AddWithValue("session", liveSessionId);
            await close.ExecuteNonQueryAsync(cancellationToken);
        }
        crashAfterClosingOldBinding?.Invoke();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,state)
            VALUES (@binding,@session,@tenant,@game,@generation,'Ready')
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("binding", bindingId);
            insert.Parameters.AddWithValue("session", liveSessionId);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("generation", generation);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateSlot = new NpgsqlCommand("""
            UPDATE live.slot SET generation=@generation,active_binding_id=@binding
            WHERE platform=@platform AND canonical_live_session_key=@key
            """, connection, transaction))
        {
            updateSlot.Parameters.AddWithValue("generation", generation);
            updateSlot.Parameters.AddWithValue("binding", bindingId);
            updateSlot.Parameters.AddWithValue("platform", platform);
            updateSlot.Parameters.AddWithValue("key", canonicalKey);
            await updateSlot.ExecuteNonQueryAsync(cancellationToken);
        }
        return JsonSerializer.Serialize(new BindingResult(bindingId, generation, "Ready"));
    }

    private sealed record BindingResult(string BindingId, long Generation, string State);

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
