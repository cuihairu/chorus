using Npgsql;

namespace Chorus.Control;

/// <summary>One (tenant, game) bucket of the online breakdown (FIX-OBS-09).</summary>
public sealed record TenantGameCount(string Tenant, string Game, long Count);

/// <summary>One tenant bucket of the online breakdown (FIX-OBS-09).</summary>
public sealed record TenantCount(string Tenant, long Count);

/// <summary>The online gauges (FIX-OBS-09, dictionary chorus_online_players/
/// chorus_online_streamers/chorus_online_rooms). The totals answer from the control plane's
/// connection records under the same freshness rule the routing data uses (a healthy gateway
/// lease, a matching gateway instance, a live connection lease); the per-dimension splits
/// need tenancy, which only the data plane knows — sessions join game.room by room id, and
/// `rooms with at least one participant` reads the data plane's own game.participation truth
/// (active participations per room). A non-PostgreSQL control database answers the splits
/// empty while the totals keep answering: an absent dimension is a stat, not an error.</summary>
public sealed record OnlineStats(
    long PlayersTotal, long StreamersTotal,
    IReadOnlyList<TenantGameCount> PlayersByTenantGame,
    IReadOnlyList<TenantCount> StreamersByTenant,
    IReadOnlyList<TenantGameCount> RoomsByTenantGame);

/// <summary>
/// Read-only online statistics (FIX-OBS-09). Distinct users, not connections — a player
/// with two sessions in one (tenant, game) counts once. Splits are bounded rosters only
/// (tenant, game); per-streamer breakdowns are a query over the roster, never a metric
/// label (the dictionary's cardinality rule).
/// </summary>
public sealed class OnlineStatsStore(string connectionString, ControlDatabaseProvider provider, ControlDatabase database)
{
    private static readonly string TerminalStatusFilter = "status NOT IN ('Aborted','Settled','Closed')";

    /// <summary>The online gauges at this moment. Freshness reuses the routing face's rule
    /// and lease constants — a connection counts while its gateway does.</summary>
    public OnlineStats Stats()
    {
        var (nodes, connections) = database.Read(t => (
            t.List<NodeReport>("node"), t.List<ConnectionRecord>("connection")));
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var fresh = connections.Where(c =>
        {
            var gateway = nodes.FirstOrDefault(n => n.Id == c.GatewayId);
            return gateway is not null && gateway.InstanceId == c.GatewayInstance
                && now - gateway.LastHeartbeatMs < RoomControlStore.NodeLeaseMs
                && now - c.LastSeenMs < RoomControlStore.ConnectionLeaseMs;
        }).ToArray();
        var players = fresh.Where(c => c.Role != "streamer").ToArray();
        var streamers = fresh.Where(c => c.Role == "streamer").ToArray();

        if (provider != ControlDatabaseProvider.PostgreSql)
            return new OnlineStats(DistinctUsers(players), DistinctUsers(streamers), [], [], []);

        var roomDimensions = ReadRoomDimensions();
        return new OnlineStats(DistinctUsers(players), DistinctUsers(streamers),
            GroupByTenantGame(players, roomDimensions),
            GroupByTenant(streamers, roomDimensions),
            ReadParticipatingRooms());
    }

    private static long DistinctUsers(IReadOnlyList<ConnectionRecord> connections) =>
        // Distinct users, not sessions: one person, two sessions, one online player.
        connections.Select(c => c.UserId).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Sessions join their room's data-plane row for the bounded roster dimensions;
    /// a session whose room has no game.room row (not yet prepared, or already terminal)
    /// carries no tenancy and stays out of the splits (it still counts in the totals).</summary>
    private static IReadOnlyList<TenantGameCount> GroupByTenantGame(
        IReadOnlyList<ConnectionRecord> connections,
        Dictionary<string, (string Tenant, string Game)> roomDimensions)
    {
        var buckets = new Dictionary<(string Tenant, string Game), HashSet<string>>();
        foreach (var connection in connections)
        {
            if (!roomDimensions.TryGetValue(connection.SessionId, out var dimension)) continue;
            var key = (dimension.Tenant, dimension.Game);
            if (!buckets.TryGetValue(key, out var users))
                buckets[key] = users = new HashSet<string>(StringComparer.Ordinal);
            users.Add(connection.UserId);
        }
        return buckets.OrderBy(b => b.Key.Tenant, StringComparer.Ordinal)
            .ThenBy(b => b.Key.Game, StringComparer.Ordinal)
            .Select(b => new TenantGameCount(b.Key.Tenant, b.Key.Game, b.Value.Count))
            .ToList();
    }

    private static IReadOnlyList<TenantCount> GroupByTenant(
        IReadOnlyList<ConnectionRecord> connections,
        Dictionary<string, (string Tenant, string Game)> roomDimensions)
    {
        var buckets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (!roomDimensions.TryGetValue(connection.SessionId, out var dimension)) continue;
            if (!buckets.TryGetValue(dimension.Tenant, out var users))
                buckets[dimension.Tenant] = users = new HashSet<string>(StringComparer.Ordinal);
            users.Add(connection.UserId);
        }
        return buckets.OrderBy(b => b.Key, StringComparer.Ordinal)
            .Select(b => new TenantCount(b.Key, b.Value.Count))
            .ToList();
    }

    /// <summary>The data plane's own participant truth: rooms with at least one active
    /// participation, per (tenant, game).</summary>
    private IReadOnlyList<TenantGameCount> ReadParticipatingRooms()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"""
            SELECT r.tenant_id, r.game_id, count(DISTINCT p.room_id)
            FROM game.participation p JOIN game.room r ON r.room_id = p.room_id
            WHERE p.state = 'Active' GROUP BY r.tenant_id, r.game_id
            ORDER BY r.tenant_id, r.game_id
            """, connection);
        var result = new List<TenantGameCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new TenantGameCount(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    /// <summary>Live rooms' roster dimensions from the data plane (the same non-terminal
    /// set OBS-08's tenant dimension counts).</summary>
    private Dictionary<string, (string Tenant, string Game)> ReadRoomDimensions()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"""
            SELECT room_id, tenant_id, game_id FROM game.room WHERE {TerminalStatusFilter}
            """, connection);
        var result = new Dictionary<string, (string Tenant, string Game)>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        return result;
    }
}
