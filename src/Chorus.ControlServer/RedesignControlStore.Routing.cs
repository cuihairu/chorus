using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

public sealed record RoomRoute(string RoomId, string TenantId, string GameId, string OwnerInstanceId,
    long Epoch, string? Endpoint, string Status);
public sealed record StreamerRoute(string StreamerId, string BindingId, string RoomId, string State,
    string? OwnerInstanceId, long Epoch, string Platform, string CanonicalLiveSessionKey,
    long BindingGeneration, long RoundGeneration, string TenantId);
public sealed record InstanceRoute(string ProcessInstanceId, string Role, string NodeId, string SlotId,
    string? Endpoint, string Readiness, string Admission);
public sealed record RoutingSnapshot(long Version, IReadOnlyList<RoomRoute> Rooms,
    IReadOnlyList<StreamerRoute> Streamers, IReadOnlyList<InstanceRoute> Instances);
/// <summary>The mount's route source as the control plane recorded it (FIX-END-02): the
/// binding the mounts entered, its generation, and the platform identity the login
/// verified — the identity is derived from the live session join, never asserted by the
/// reporter.</summary>
public sealed record RouteSourceRecord(
    string TenantId, string GameId, string StreamerId, string BindingId, string Platform,
    string CanonicalLiveSessionKey, long BindingGeneration, string RoomId,
    DateTime RegisteredAt, DateTime UpdatedAt);

/// <summary>Thrown when a route-source report names a mount that does not exist, belongs
/// to another streamer, tenant or game, or carries no platform-verified live session: an
/// unverified platform identity establishes no route.</summary>
public sealed class RouteSourceUnverifiedException(string message) : Exception(message);

/// <summary>Thrown when a route-source report names a generation older than the recorded
/// row (or not the mount's current one): late evidence never overwrites the newer
/// registration.</summary>
public sealed class StaleBindingGenerationException(string message) : Exception(message);

public sealed partial class RedesignControlStore
{
    /// <summary>
    /// Reads the versioned full routing snapshot (architecture 4.4): rooms with owner epochs and
    /// endpoints, live streamer bindings with their rooms, and every registered instance. The
    /// version is a 64-bit content hash — a deliberate deviation from a monotonic sequence: any
    /// mutation of the routed content produces a different version, so a consumer that missed
    /// Watch notices detects any gap by comparing versions and re-pulls instead of trusting a
    /// stale snapshot. Routing carries endpoints, instances and epochs only — never secrets.
    /// </summary>
    public async Task<RoutingSnapshot> ReadRoutingSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var rooms = new List<RoomRoute>();
        // FIX-END-03: a completed round stays published (Finishing): late readers resolve the
        // room until the end sequence retires its route — completion itself is not destruction.
        // FIX-END-16: a prepared-but-not-yet-activated room publishes its route too (architecture
        // 4.4 battle-start barrier) — the control plane must confirm the platform gateway
        // installed the route BEFORE it activates the battle, so the route has to be visible and
        // confirmable while the room still stands at Tick 0. The gateway may resolve a preparing
        // room and push input to it; the data plane accepts Preparing (FIX-END-06) and the held
        // actor's inbox waits for the activation start line, so input is held, not lost.
        await using (var command = new NpgsqlCommand("""
            SELECT o.entity_id,o.tenant_id,o.game_id,o.owner_process_instance_id,o.epoch,o.status,p.endpoint
            FROM control.ownership o
            JOIN game.room r ON r.room_id=o.entity_id AND r.tenant_id=o.tenant_id AND r.game_id=o.game_id
            LEFT JOIN control.process p ON p.process_instance_id=o.owner_process_instance_id
            WHERE o.kind='room' AND r.status IN ('Preparing','Running','Finishing')
            ORDER BY o.tenant_id,o.game_id,o.entity_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rooms.Add(new RoomRoute(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(5)));
        }

        var streamers = new List<StreamerRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT s.streamer_id,b.binding_id,b.current_room_id,b.state,
                o.owner_process_instance_id,COALESCE(o.epoch,0),
                s.platform,s.canonical_live_session_key,b.generation,b.round_generation,
                b.tenant_id
            FROM live.binding b
            JOIN live.session s ON s.live_session_id=b.live_session_id
            LEFT JOIN control.ownership o ON o.kind='streamer' AND o.tenant_id=b.tenant_id
                AND o.game_id=b.game_id AND o.entity_id=s.streamer_id
            WHERE b.state IN ('Starting','Ready','Running','AwaitingPlatformClose')
            ORDER BY s.streamer_id,b.binding_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                streamers.Add(new StreamerRoute(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
                    reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetString(10)));
        }

        var instances = new List<InstanceRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.process_instance_id,d.role,p.node_id,p.slot_id,p.endpoint,p.readiness,p.admission
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            ORDER BY p.process_instance_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                instances.Add(new InstanceRoute(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.GetString(6)));
        }

        var version = ContentHash(rooms.Select(r =>
            $"room|{r.RoomId}|{r.TenantId}|{r.GameId}|{r.OwnerInstanceId}|{r.Epoch}|{r.Endpoint ?? ""}|{r.Status}")
            .Concat(streamers.Select(s =>
                $"streamer|{s.StreamerId}|{s.BindingId}|{s.RoomId}|{s.State}|{s.OwnerInstanceId ?? ""}|{s.Epoch}|{s.Platform}|{s.CanonicalLiveSessionKey}|{s.BindingGeneration}|{s.RoundGeneration}|{s.TenantId}"))
            .Concat(instances.Select(i =>
                $"instance|{i.ProcessInstanceId}|{i.Role}|{i.NodeId}|{i.SlotId}|{i.Endpoint ?? ""}|{i.Readiness}|{i.Admission}")));
        return new RoutingSnapshot(version, rooms, streamers, instances);
    }

    /// <summary>
    /// FIX-END-16: waits until a platform gateway has installed and confirmed the routing
    /// version that carries the given room (architecture 4.4 battle-start barrier). The room
    /// publishes its route while Preparing, so once it stands in the current snapshot and a
    /// gateway has confirmed exactly that version, the route is installed — the control plane
    /// may then activate the battle. The confirm is a per-gateway global version (schema 0024),
    /// so the check is "some gateway confirmed the snapshot's current version"; the room's
    /// presence in that snapshot is what ties the version to this room. Times out false: the
    /// route never confirmed, and the caller must not start the battle.
    /// </summary>
    public async Task<bool> WaitForRoomRouteConfirmedAsync(string roomId, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = await ReadRoutingSnapshotAsync(cancellationToken);
            if (snapshot.Rooms.Any(room => room.RoomId == roomId)
                && await IsRoutingVersionConfirmedAsync(snapshot.Version, cancellationToken))
                return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(RouteConfirmPollMs, cancellationToken);
        }
    }

    private const int RouteConfirmPollMs = 150;

    private async Task<bool> IsRoutingVersionConfirmedAsync(long version,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM control.routing_confirm WHERE version=@version LIMIT 1", connection);
        command.Parameters.AddWithValue("version", version);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// FIX-END-16 boundary: whether the cluster has deployed a platform gateway at all. The
    /// battle-start barrier (architecture 4.4) waits for a gateway to confirm the room's route
    /// before it activates the battle, because the gateway is the delivery path the battle's
    /// input flows through — no confirmed route means a gateway might deliver input into a
    /// battle that already ticked past it. A cluster with no gateway registered has no delivery
    /// path to wait for (there is no input to lose, and a gateway that joins later installs the
    /// published route and delivers from then on), so the coordinator applies the gate only
    /// once a gateway is actually registered. A gateway registers by reporting a process whose
    /// deployment role is the platform gateway; the report lands only under a pre-registered
    /// node (ReportProcessAsync), so a bare cluster reports none.
    /// </summary>
    public async Task<bool> HasRegisteredGatewayAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            WHERE d.role='platform-gateway' LIMIT 1
            """, connection);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>FNV-1a over the canonical snapshot lines; collisions are acceptable — the version
    /// only signals "content changed", it is not an identity.</summary>
    private static long ContentHash(IEnumerable<string> lines)
    {
        unchecked
        {
            const long prime = 1099511628211;
            var hash = -3750763034362895579L; // 14695981039346656037 as int64
            foreach (var line in lines)
            {
                foreach (var c in line)
                {
                    hash ^= c;
                    hash *= prime;
                }
                hash ^= '\n';
                hash *= prime;
            }
            return hash;
        }
    }
    /// <summary>
    /// Registers a mount's route source (FIX-END-02, architecture 6.4): the StreamerServer
    /// reports the binding right after the mount commits, and the control plane derives the
    /// platform identity from the mount's live session — a session row exists only because a
    /// platform-verified login created it, so an unverified identity establishes no route.
    /// The report must name the mount's current generation; the row is keyed per streamer and
    /// game, and a report naming an older generation than the recorded row never overwrites
    /// it — late evidence loses, always. A replay of the recorded generation answers the one
    /// row as Duplicate.
    /// </summary>
    public async Task<(RouteSourceRecord Record, bool Duplicate)> RegisterRouteSourceAsync(
        string tenantId, string gameId, string streamerId, string bindingId, long bindingGeneration,
        CancellationToken cancellationToken = default)
    {
        if (bindingGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(bindingGeneration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The verified join is the whole fence: the binding must exist, belong to this
        // streamer's tenant and game, and sit on a live session — whose platform identity is
        // what gets recorded, never anything the report claims.
        string platform, canonicalLiveSessionKey, roomId;
        await using (var mount = new NpgsqlCommand("""
            SELECT b.tenant_id,b.game_id,b.generation,b.current_room_id,
                s.streamer_id,s.platform,s.canonical_live_session_key
            FROM live.binding b
            JOIN live.session s ON s.live_session_id=b.live_session_id
            WHERE b.binding_id=@binding
            """, connection))
        {
            mount.Parameters.AddWithValue("binding", bindingId);
            await using var reader = await mount.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RouteSourceUnverifiedException(
                    $"The binding {bindingId} does not exist; no route source is established.");
            if (reader.GetString(0) != tenantId || reader.GetString(1) != gameId ||
                reader.GetString(4) != streamerId)
                throw new RouteSourceUnverifiedException(
                    $"The binding {bindingId} does not belong to {tenantId}/{gameId}/{streamerId}.");
            if (reader.GetInt64(2) != bindingGeneration)
                throw new StaleBindingGenerationException(
                    $"The binding {bindingId} holds generation {reader.GetInt64(2)}, not " +
                    $"{bindingGeneration}; the report is late or ahead of the mount.");
            platform = reader.GetString(5);
            canonicalLiveSessionKey = reader.GetString(6);
            roomId = reader.IsDBNull(3) ? "" : reader.GetString(3);
        }

        // The recorded row first: the newer generation wins by refusing the older report,
        // and the recorded generation replays as the one row.
        var existingGeneration = -1L;
        var existingBinding = "";
        await using (var existing = new NpgsqlCommand("""
            SELECT binding_id,binding_generation FROM control.route_source
            WHERE tenant_id=@tenant AND game_id=@game AND streamer_id=@streamer
            """, connection))
        {
            existing.Parameters.AddWithValue("tenant", tenantId);
            existing.Parameters.AddWithValue("game", gameId);
            existing.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingBinding = reader.GetString(0);
                existingGeneration = reader.GetInt64(1);
            }
        }
        if (existingGeneration > bindingGeneration)
            throw new StaleBindingGenerationException(
                $"The route source for {streamerId} already holds generation {existingGeneration}; " +
                $"generation {bindingGeneration} does not overwrite it.");
        var duplicate = existingGeneration == bindingGeneration && existingBinding == bindingId;
        if (!duplicate)
        {
            // The upsert's WHERE keeps a concurrent older report from overwriting a newer
            // registration between the checks above and this write.
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO control.route_source(tenant_id,game_id,streamer_id,binding_id,
                    platform,canonical_live_session_key,binding_generation,room_id)
                VALUES (@tenant,@game,@streamer,@binding,@platform,@canonical,@generation,@room)
                ON CONFLICT (tenant_id,game_id,streamer_id) DO UPDATE SET
                    binding_id=@binding,platform=@platform,
                    canonical_live_session_key=@canonical,binding_generation=@generation,
                    room_id=@room,updated_at=clock_timestamp()
                WHERE control.route_source.binding_generation < @generation
                """, connection);
            upsert.Parameters.AddWithValue("tenant", tenantId);
            upsert.Parameters.AddWithValue("game", gameId);
            upsert.Parameters.AddWithValue("streamer", streamerId);
            upsert.Parameters.AddWithValue("binding", bindingId);
            upsert.Parameters.AddWithValue("platform", platform);
            upsert.Parameters.AddWithValue("canonical", canonicalLiveSessionKey);
            upsert.Parameters.AddWithValue("generation", bindingGeneration);
            upsert.Parameters.AddWithValue("room", roomId);
            if (await upsert.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new StaleBindingGenerationException(
                    $"A newer route source for {streamerId} won the registration race.");
        }

        // The row as it stands now — replay and fresh write alike.
        await using (var read = new NpgsqlCommand("""
            SELECT binding_id,platform,canonical_live_session_key,binding_generation,room_id,
                registered_at,updated_at
            FROM control.route_source
            WHERE tenant_id=@tenant AND game_id=@game AND streamer_id=@streamer
            """, connection))
        {
            read.Parameters.AddWithValue("tenant", tenantId);
            read.Parameters.AddWithValue("game", gameId);
            read.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The route source row vanished under registration.");
            var record = new RouteSourceRecord(tenantId, gameId, streamerId, reader.GetString(0),
                reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetDateTime(5), reader.GetDateTime(6));
            return (record, duplicate && record.BindingGeneration == bindingGeneration);
        }
    }

    /// <summary>
    /// The match-intent gate (FIX-LINK-03, architecture 6.4): a mount's intent is accepted only
    /// while the mount is the registered route source — the row exists for the binding and still
    /// carries the mount's current generation. An unregistered mount establishes no intent, and a
    /// stale generation means the mount was superseded. The streamer is the row's, never a caller
    /// field — the intent names the mount (binding + generation) and the control plane joins it to
    /// the verified identity.
    /// </summary>
    public async Task<RouteSourceRecord> ValidateMatchIntentSourceAsync(
        string tenantId, string gameId, string bindingId, long bindingGeneration,
        CancellationToken cancellationToken = default)
    {
        if (bindingGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(bindingGeneration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // binding_id is globally unique (a binding belongs to one streamer), so the row is the
        // one source for this mount; the recorded generation is the currency check.
        await using var read = new NpgsqlCommand("""
            SELECT streamer_id,binding_id,binding_generation,platform,canonical_live_session_key,
                room_id,registered_at,updated_at
            FROM control.route_source
            WHERE tenant_id=@tenant AND game_id=@game AND binding_id=@binding
            """, connection);
        read.Parameters.AddWithValue("tenant", tenantId);
        read.Parameters.AddWithValue("game", gameId);
        read.Parameters.AddWithValue("binding", bindingId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new RouteSourceUnverifiedException(
                $"The binding {bindingId} is not a registered route source for {tenantId}/{gameId}; " +
                "the mount establishes no match intent.");
        var streamerId = reader.GetString(0);
        var recordedGeneration = reader.GetInt64(2);
        if (recordedGeneration != bindingGeneration)
            throw new StaleBindingGenerationException(
                $"The route source for {streamerId} holds generation {recordedGeneration}, not " +
                $"{bindingGeneration}; the intent names a superseded mount.");
        return new RouteSourceRecord(tenantId, gameId, streamerId, reader.GetString(1),
            reader.GetString(3), reader.GetString(4), recordedGeneration,
            reader.IsDBNull(5) ? "" : reader.GetString(5), reader.GetDateTime(6),
            reader.GetDateTime(7));
    }
    /// <summary>
    /// Records a PlatformGateway's installed routing version (FIX-END-03): the gateway
    /// confirms the exact version it answers resolves from, and until that confirm lands the
    /// gateway's own face refuses to push — an unconfirmed picture guesses no rooms. The
    /// confirm is keyed by the gateway's process identity from its certificate, so no caller
    /// can confirm on another instance's behalf.
    /// </summary>
    public async Task ConfirmRoutingVersionAsync(string processInstanceId, long version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.routing_confirm(process_instance_id,version)
            VALUES (@instance,@version)
            ON CONFLICT (process_instance_id) DO UPDATE SET
                version=@version,confirmed_at=clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("instance", processInstanceId);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The version a gateway last confirmed, or null when it never did.</summary>
    public async Task<long?> ReadRoutingConfirmAsync(string processInstanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT version FROM control.routing_confirm WHERE process_instance_id=@instance",
            connection);
        command.Parameters.AddWithValue("instance", processInstanceId);
        return (long?)await command.ExecuteScalarAsync(cancellationToken);
    }
}
