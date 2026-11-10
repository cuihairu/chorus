using Chorus.Engine.Routing;
using Npgsql;

namespace Chorus.Control;

/// <summary>Room family gauges (FIX-OBS-08, dictionary chorus_rooms/chorus_rooms_by_state/
/// chorus_rooms_by_tenant): the live room counts the browser management face renders.
/// `total`/`by_state` come from the control plane's room records — the lifecycle authority —
/// and answer on every provider. `by_tenant` reads the data plane's game.room rows: the
/// control plane never learns a room's tenant (assignment requests do not carry one), so the
/// tenant dimension exists only in the PostgreSQL business store.</summary>
public sealed record TenantRooms(string Tenant, long Rooms);

public sealed record RoomStats(
    long Total, long Active, IReadOnlyDictionary<string, long> ByState,
    IReadOnlyList<TenantRooms> ByTenant);

/// <summary>
/// Read-only room statistics (FIX-OBS-08). The two read sources are the two planes' own
/// truths: the control plane owns lifecycle (its records answer total/active/by_state), the
/// data plane owns tenancy (game.room answers by_tenant, counted with the same non-terminal
/// filter the admission counter uses). A non-PostgreSQL control database (local Sqlite
/// development) has no game.room and answers the tenant dimension empty rather than failing:
/// an absent dimension is a stat, not an error. The two planes' lifecycles correspond only
/// approximately (destroyed ≈ settled/closed), so by_tenant need not sum to total.
/// </summary>
public sealed class RoomStatsStore(string connectionString, ControlDatabaseProvider provider, ControlDatabase database)
{
    /// <summary>The live room gauges at this moment. Live is the control plane's rule — a
    /// destroyed room stops counting; active is the subset currently serving.</summary>
    public RoomStats Stats()
    {
        var rooms = database.Read(t => t.List<RoomRecord>("room")).Where(Live).ToArray();
        var byState = rooms.GroupBy(r => r.State.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        return new RoomStats(rooms.Length, rooms.Count(r => r.State == RoomLifecycleState.Active),
            byState, provider == ControlDatabaseProvider.PostgreSql ? ReadTenants() : []);
    }

    /// <summary>Live rooms per tenant from the data plane: game.room rows still in a
    /// non-terminal status, the same set the admission counter keeps admitting into.</summary>
    private IReadOnlyList<TenantRooms> ReadTenants()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT tenant_id, count(*) FROM game.room
            WHERE status NOT IN ('Aborted','Settled','Closed')
            GROUP BY tenant_id ORDER BY tenant_id
            """, connection);
        var result = new List<TenantRooms>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new TenantRooms(reader.GetString(0), reader.GetInt64(1)));
        return result;
    }

    private static bool Live(RoomRecord room) => room.State != RoomLifecycleState.Destroyed;
}
