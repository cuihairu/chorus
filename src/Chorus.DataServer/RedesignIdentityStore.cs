using System.Security.Cryptography;
using System.Text;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed record ResolvedStreamer(string TenantId, string Platform, string AppId, string OpenId, string StreamerId);
public sealed record SessionGrant(string SessionId, string StreamerId, long SessionVersion, DateTime ExpiresAtUtc, bool Duplicate);
public sealed record SessionConnection(string SessionId, string StreamerId, long SessionVersion, long ConnectionGeneration, bool Duplicate);
public sealed record SessionSnapshot(
    string StreamerId, string TenantId, long SessionVersion, DateTime ExpiresAtUtc,
    string GatewayInstanceId, string ConnectionId, long ConnectionGeneration,
    bool Revoked, bool Closed, bool Expired, bool Banned, bool Current);

public sealed class RedesignIdentityStore(string connectionString)
{
    public async Task<ResolvedStreamer> ResolveIdentityAsync(string tenantId, string platform, string appId,
        string openId, CancellationToken cancellationToken = default)
    {
        Require(tenantId, nameof(tenantId)); Require(platform, nameof(platform));
        Require(appId, nameof(appId)); Require(openId, nameof(openId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var candidate = Guid.NewGuid().ToString("N");
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO auth.identity(platform,app_id,open_id,tenant_id,player_id)
            VALUES (@platform,@app,@open,@tenant,@player) ON CONFLICT(platform,app_id,open_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("platform", platform);
            insert.Parameters.AddWithValue("app", appId);
            insert.Parameters.AddWithValue("open", openId);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("player", candidate);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        string existingTenant;
        string streamerId;
        await using (var select = new NpgsqlCommand("""
            SELECT tenant_id,player_id FROM auth.identity
            WHERE platform=@platform AND app_id=@app AND open_id=@open FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("platform", platform);
            select.Parameters.AddWithValue("app", appId);
            select.Parameters.AddWithValue("open", openId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Identity mapping disappeared.");
            existingTenant = reader.GetString(0);
            streamerId = reader.GetString(1);
        }
        if (!StringComparer.Ordinal.Equals(existingTenant, tenantId))
            throw new InvalidOperationException("Platform identity is already bound to another tenant.");
        await using (var state = new NpgsqlCommand("""
            INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES (@streamer,@tenant)
            ON CONFLICT(streamer_id) DO NOTHING
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            state.Parameters.AddWithValue("tenant", tenantId);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ResolvedStreamer(tenantId, platform, appId, openId, streamerId);
    }

    public async Task<SessionGrant> LoginAsync(string streamerId, string gatewayInstanceId,
        string connectionId, byte[] nextResumeSecret, CancellationToken cancellationToken = default)
    {
        RequireSecret(nextResumeSecret);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long version;
        await using (var state = new NpgsqlCommand("""
            SELECT current_session_version,ban_state FROM auth.streamer_state WHERE streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Streamer identity is not resolved.");
            if (reader.GetString(1) != "Allowed") throw new SessionRevokedException();
            version = checked(reader.GetInt64(0) + 1);
        }
        var sessionId = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow.AddMinutes(30);
        await using (var revoke = new NpgsqlCommand("""
            UPDATE auth.session SET revoked_at=clock_timestamp()
            WHERE streamer_id=@streamer AND revoked_at IS NULL
            """, connection, transaction))
        {
            revoke.Parameters.AddWithValue("streamer", streamerId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var session = new NpgsqlCommand("""
            INSERT INTO auth.session(session_id,streamer_id,session_version,gateway_instance_id,connection_id,
                resume_hash,expires_at)
            VALUES (@session,@streamer,@version,@gateway,@connection,@resume,@expires)
            """, connection, transaction))
        {
            session.Parameters.AddWithValue("session", sessionId);
            session.Parameters.AddWithValue("streamer", streamerId);
            session.Parameters.AddWithValue("version", version);
            session.Parameters.AddWithValue("gateway", gatewayInstanceId);
            session.Parameters.AddWithValue("connection", connectionId);
            session.Parameters.AddWithValue("resume", Hash(nextResumeSecret));
            session.Parameters.AddWithValue("expires", expiresAt);
            await session.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE auth.streamer_state SET current_session_version=@version,current_session_id=@session
            WHERE streamer_id=@streamer
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("streamer", streamerId);
            update.Parameters.AddWithValue("session", sessionId);
            update.Parameters.AddWithValue("version", version);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SessionGrant(sessionId, streamerId, version, expiresAt, false);
    }

    public async Task<SessionGrant> ResumeAsync(string previousSessionId, string gatewayInstanceId,
        string connectionId, string operationId, byte[] previousSecret, byte[] nextSecret,
        CancellationToken cancellationToken = default)
    {
        Require(previousSessionId, nameof(previousSessionId)); Require(operationId, nameof(operationId));
        RequireSecret(previousSecret); RequireSecret(nextSecret);
        if (CryptographicOperations.FixedTimeEquals(previousSecret, nextSecret))
            throw new ArgumentException("Resume secret must rotate.", nameof(nextSecret));
        var previousHash = Hash(previousSecret);
        var nextHash = Hash(nextSecret);
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{previousSessionId}\n{Convert.ToHexString(previousHash)}\n{Convert.ToHexString(nextHash)}"));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string streamerId;
        await using (var lookup = new NpgsqlCommand("SELECT streamer_id FROM auth.session WHERE session_id=@session", connection, transaction))
        {
            lookup.Parameters.AddWithValue("session", previousSessionId);
            streamerId = await lookup.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new SessionRevokedException();
        }
        long currentVersion;
        string banState;
        string? currentSessionId;
        await using (var state = new NpgsqlCommand("""
            SELECT current_session_version,current_session_id,ban_state FROM auth.streamer_state
            WHERE streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new SessionRevokedException();
            currentVersion = reader.GetInt64(0);
            currentSessionId = reader.IsDBNull(1) ? null : reader.GetString(1);
            banState = reader.GetString(2);
        }
        if (banState != "Allowed") throw new SessionRevokedException();

        string? duplicateSession = null;
        long duplicateVersion = 0;
        DateTime duplicateRetryUntil = default;
        await using (var rotation = new NpgsqlCommand("""
            SELECT previous_secret_hash,request_hash,result_session_id,result_session_version,retry_until
            FROM auth.rotation WHERE streamer_id=@streamer AND operation_id=@operation
            """, connection, transaction))
        {
            rotation.Parameters.AddWithValue("streamer", streamerId);
            rotation.Parameters.AddWithValue("operation", operationId);
            await using var reader = await rotation.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var storedPrevious = (byte[])reader[0];
                var storedRequest = (byte[])reader[1];
                duplicateSession = reader.GetString(2);
                duplicateVersion = reader.GetInt64(3);
                duplicateRetryUntil = reader.GetDateTime(4);
                if (!CryptographicOperations.FixedTimeEquals(storedPrevious, previousHash) ||
                    !CryptographicOperations.FixedTimeEquals(storedRequest, requestHash) || duplicateRetryUntil <= DateTime.UtcNow)
                    throw new SessionRevokedException();
            }
        }
        if (duplicateSession is not null)
        {
            if (currentVersion != duplicateVersion || currentSessionId != duplicateSession)
                throw new SessionReplacedException();
            var expires = await GetExpiryAsync(connection, transaction, duplicateSession, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SessionGrant(duplicateSession, streamerId, duplicateVersion, expires, true);
        }

        await using (var previous = new NpgsqlCommand("""
            SELECT resume_hash,expires_at,revoked_at,session_version FROM auth.session
            WHERE session_id=@session AND streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            previous.Parameters.AddWithValue("session", previousSessionId);
            previous.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new SessionRevokedException();
            var stored = (byte[])reader[0];
            if (!CryptographicOperations.FixedTimeEquals(stored, previousHash) || !reader.IsDBNull(2) ||
                reader.GetDateTime(1) <= DateTime.UtcNow || reader.GetInt64(3) != currentVersion ||
                currentSessionId != previousSessionId)
                throw new SessionRevokedException();
        }

        var newSessionId = Guid.NewGuid().ToString("N");
        var newVersion = checked(currentVersion + 1);
        var expiresAt = DateTime.UtcNow.AddMinutes(30);
        await using (var revoke = new NpgsqlCommand("UPDATE auth.session SET revoked_at=clock_timestamp() WHERE session_id=@session", connection, transaction))
        {
            revoke.Parameters.AddWithValue("session", previousSessionId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO auth.session(session_id,streamer_id,session_version,gateway_instance_id,connection_id,
                resume_hash,expires_at)
            VALUES (@session,@streamer,@version,@gateway,@connection,@resume,@expires)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("session", newSessionId);
            insert.Parameters.AddWithValue("streamer", streamerId);
            insert.Parameters.AddWithValue("version", newVersion);
            insert.Parameters.AddWithValue("gateway", gatewayInstanceId);
            insert.Parameters.AddWithValue("connection", connectionId);
            insert.Parameters.AddWithValue("resume", nextHash);
            insert.Parameters.AddWithValue("expires", expiresAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE auth.streamer_state SET current_session_version=@version,current_session_id=@session
            WHERE streamer_id=@streamer
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("streamer", streamerId);
            update.Parameters.AddWithValue("session", newSessionId);
            update.Parameters.AddWithValue("version", newVersion);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insertRotation = new NpgsqlCommand("""
            INSERT INTO auth.rotation(streamer_id,operation_id,previous_secret_hash,request_hash,
                result_session_id,result_session_version,retry_until)
            VALUES (@streamer,@operation,@previous,@request,@session,@version,clock_timestamp()+interval '2 minutes')
            """, connection, transaction))
        {
            insertRotation.Parameters.AddWithValue("streamer", streamerId);
            insertRotation.Parameters.AddWithValue("operation", operationId);
            insertRotation.Parameters.AddWithValue("previous", previousHash);
            insertRotation.Parameters.AddWithValue("request", requestHash);
            insertRotation.Parameters.AddWithValue("session", newSessionId);
            insertRotation.Parameters.AddWithValue("version", newVersion);
            await insertRotation.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SessionGrant(newSessionId, streamerId, newVersion, expiresAt, false);
    }

    /// <summary>
    /// Binds the session to a new gateway connection, incrementing the connection generation. The
    /// current resume secret authorizes the switch; nothing rotates. Exact retries return the same
    /// generation within the retry window.
    /// </summary>
    public async Task<SessionConnection> AttachConnectionAsync(string sessionId, string gatewayInstanceId,
        string connectionId, string operationId, byte[] currentSecret, CancellationToken cancellationToken = default)
    {
        Require(sessionId, nameof(sessionId)); Require(operationId, nameof(operationId));
        Require(gatewayInstanceId, nameof(gatewayInstanceId)); Require(connectionId, nameof(connectionId));
        RequireSecret(currentSecret);
        var currentHash = Hash(currentSecret);
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"attach\n{sessionId}\n{Convert.ToHexString(currentHash)}\n{gatewayInstanceId}\n{connectionId}"));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string streamerId;
        await using (var lookup = new NpgsqlCommand("SELECT streamer_id FROM auth.session WHERE session_id=@session", connection, transaction))
        {
            lookup.Parameters.AddWithValue("session", sessionId);
            streamerId = await lookup.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new SessionRevokedException();
        }
        long currentVersion;
        string? currentSessionId;
        await using (var state = new NpgsqlCommand("""
            SELECT current_session_version,current_session_id,ban_state FROM auth.streamer_state
            WHERE streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            state.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new SessionRevokedException();
            currentVersion = reader.GetInt64(0);
            currentSessionId = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (reader.GetString(2) != "Allowed") throw new SessionRevokedException();
        }

        string? duplicateSession = null;
        long duplicateVersion = 0;
        long duplicateGeneration = 0;
        await using (var rotation = new NpgsqlCommand("""
            SELECT previous_secret_hash,request_hash,result_session_id,result_session_version,
                   result_connection_generation,retry_until
            FROM auth.rotation WHERE streamer_id=@streamer AND operation_id=@operation
            """, connection, transaction))
        {
            rotation.Parameters.AddWithValue("streamer", streamerId);
            rotation.Parameters.AddWithValue("operation", operationId);
            await using var reader = await rotation.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var storedPrevious = (byte[])reader[0];
                var storedRequest = (byte[])reader[1];
                duplicateSession = reader.GetString(2);
                duplicateVersion = reader.GetInt64(3);
                if (reader.IsDBNull(4)) throw new SessionRevokedException();
                duplicateGeneration = reader.GetInt64(4);
                var retryUntil = reader.GetDateTime(5);
                if (!CryptographicOperations.FixedTimeEquals(storedPrevious, currentHash) ||
                    !CryptographicOperations.FixedTimeEquals(storedRequest, requestHash) || retryUntil <= DateTime.UtcNow)
                    throw new SessionRevokedException();
            }
        }
        if (duplicateSession is not null)
        {
            if (currentVersion != duplicateVersion || currentSessionId != duplicateSession)
                throw new SessionReplacedException();
            await transaction.CommitAsync(cancellationToken);
            return new SessionConnection(duplicateSession, streamerId, duplicateVersion, duplicateGeneration, true);
        }

        long generation;
        await using (var session = new NpgsqlCommand("""
            SELECT resume_hash,expires_at,revoked_at,session_version,connection_generation
            FROM auth.session WHERE session_id=@session AND streamer_id=@streamer FOR UPDATE
            """, connection, transaction))
        {
            session.Parameters.AddWithValue("session", sessionId);
            session.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await session.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new SessionRevokedException();
            var stored = (byte[])reader[0];
            if (!CryptographicOperations.FixedTimeEquals(stored, currentHash) || !reader.IsDBNull(2) ||
                reader.GetDateTime(1) <= DateTime.UtcNow || reader.GetInt64(3) != currentVersion ||
                currentSessionId != sessionId)
                throw new SessionRevokedException();
            generation = checked(reader.GetInt64(4) + 1);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE auth.session SET connection_generation=@generation,gateway_instance_id=@gateway,
                connection_id=@connection,connection_closed_at=NULL
            WHERE session_id=@session
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("session", sessionId);
            update.Parameters.AddWithValue("generation", generation);
            update.Parameters.AddWithValue("gateway", gatewayInstanceId);
            update.Parameters.AddWithValue("connection", connectionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insertRotation = new NpgsqlCommand("""
            INSERT INTO auth.rotation(streamer_id,operation_id,previous_secret_hash,request_hash,
                result_session_id,result_session_version,result_connection_generation,retry_until)
            VALUES (@streamer,@operation,@previous,@request,@session,@version,@generation,
                clock_timestamp()+interval '2 minutes')
            """, connection, transaction))
        {
            insertRotation.Parameters.AddWithValue("streamer", streamerId);
            insertRotation.Parameters.AddWithValue("operation", operationId);
            insertRotation.Parameters.AddWithValue("previous", currentHash);
            insertRotation.Parameters.AddWithValue("request", requestHash);
            insertRotation.Parameters.AddWithValue("session", sessionId);
            insertRotation.Parameters.AddWithValue("version", currentVersion);
            insertRotation.Parameters.AddWithValue("generation", generation);
            await insertRotation.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SessionConnection(sessionId, streamerId, currentVersion, generation, false);
    }

    /// <summary>
    /// Applies a gateway close notification. Only a notification carrying the current gateway,
    /// connection and generation marks the session offline; stale notifications change nothing.
    /// </summary>
    public async Task<bool> CloseConnectionAsync(string sessionId, string gatewayInstanceId, string connectionId,
        long connectionGeneration, CancellationToken cancellationToken = default)
    {
        Require(sessionId, nameof(sessionId)); Require(gatewayInstanceId, nameof(gatewayInstanceId));
        Require(connectionId, nameof(connectionId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string currentGateway;
        string currentConnection;
        long currentGeneration;
        await using (var select = new NpgsqlCommand("""
            SELECT gateway_instance_id,connection_id,connection_generation,revoked_at
            FROM auth.session WHERE session_id=@session FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("session", sessionId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            if (!reader.IsDBNull(3)) return false;
            currentGateway = reader.GetString(0);
            currentConnection = reader.GetString(1);
            currentGeneration = reader.GetInt64(2);
        }
        if (!StringComparer.Ordinal.Equals(currentGateway, gatewayInstanceId) ||
            !StringComparer.Ordinal.Equals(currentConnection, connectionId) || currentGeneration != connectionGeneration)
            return false;
        await using (var update = new NpgsqlCommand(
            "UPDATE auth.session SET connection_closed_at=clock_timestamp() WHERE session_id=@session",
            connection, transaction))
        {
            update.Parameters.AddWithValue("session", sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The session row with live facts for access-credential consumers, or null when unknown.</summary>
    public async Task<SessionSnapshot?> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Require(sessionId, nameof(sessionId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.streamer_id,s.session_version,s.expires_at,s.revoked_at,s.gateway_instance_id,
                   s.connection_id,s.connection_generation,s.connection_closed_at,
                   st.current_session_version,st.current_session_id,st.ban_state,i.tenant_id
            FROM auth.session s
            JOIN auth.streamer_state st ON st.streamer_id=s.streamer_id
            LEFT JOIN LATERAL (SELECT tenant_id FROM auth.identity WHERE player_id=s.streamer_id LIMIT 1) i ON true
            WHERE s.session_id=@session
            """, connection);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var expiresAt = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
        return new SessionSnapshot(
            reader.GetString(0), reader.IsDBNull(11) ? "" : reader.GetString(11), reader.GetInt64(1),
            expiresAt, reader.GetString(4), reader.GetString(5),
            reader.GetInt64(6), !reader.IsDBNull(3), !reader.IsDBNull(7), expiresAt <= DateTime.UtcNow,
            reader.GetString(10) != "Allowed",
            reader.GetInt64(8) == reader.GetInt64(1) && !reader.IsDBNull(9) &&
            StringComparer.Ordinal.Equals(reader.GetString(9), sessionId));
    }

    /// <summary>The tenant that owns this streamer identity, or null when unknown.</summary>
    public async Task<string?> GetIdentityTenantAsync(string streamerId, CancellationToken cancellationToken = default)
    {
        Require(streamerId, nameof(streamerId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT tenant_id FROM auth.identity WHERE player_id=@streamer LIMIT 1", connection);
        command.Parameters.AddWithValue("streamer", streamerId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>The streamer bound to a session, or null; lets callers fence cross-tenant use before mutating.</summary>
    public async Task<string?> GetSessionStreamerAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT streamer_id FROM auth.session WHERE session_id=@session", connection);
        command.Parameters.AddWithValue("session", sessionId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<DateTime> GetExpiryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sessionId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT expires_at FROM auth.session WHERE session_id=@session", connection, transaction);
        command.Parameters.AddWithValue("session", sessionId);
        return (DateTime)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new SessionRevokedException());
    }

    private static byte[] Hash(byte[] secret) => SHA256.HashData(secret);
    private static void RequireSecret(byte[] secret)
    {
        if (secret is null || secret.Length < 32) throw new ArgumentException("Resume secrets must contain at least 256 bits.", nameof(secret));
    }
    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}
