using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-16, the routing gate: the control plane waits for a platform gateway to install and
/// confirm the routing version that carries the room before it activates the battle. A room that
/// stands in the snapshot (a Preparing room publishes its route) with its snapshot version
/// confirmed by some gateway opens the gate; no confirm row, or a confirm of a different version,
/// keeps it closed — the caller must not start the battle while the route is unconfirmed.
/// </summary>
public sealed class RedesignRouteGateStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Room = "room-gate-1";

    [Fact]
    public async Task AConfirmedSnapshotVersionOpensTheGate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await ArrangePreparingRoomAsync(database);

        var snapshot = await control.ReadRoutingSnapshotAsync();
        Assert.Contains(snapshot.Rooms, room => room.RoomId == Room);

        // A gateway installs the snapshot and confirms the version the room appears in.
        await control.ConfirmRoutingVersionAsync("gateway-gate-a", snapshot.Version);

        Assert.True(await control.WaitForRoomRouteConfirmedAsync(Room, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AnUnconfirmedRoomTimesOutClosed()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await ArrangePreparingRoomAsync(database);

        var snapshot = await control.ReadRoutingSnapshotAsync();
        Assert.Contains(snapshot.Rooms, room => room.RoomId == Room);

        // No gateway confirmed anything: the gate stays closed, so no battle starts.
        Assert.False(await control.WaitForRoomRouteConfirmedAsync(Room, TimeSpan.FromMilliseconds(400)));

        // A confirm of a DIFFERENT version is not evidence about this room's route — the
        // confirmed version does not carry the room, so the gate stays closed.
        await control.ConfirmRoutingVersionAsync("gateway-gate-b", snapshot.Version + 1);
        Assert.False(await control.WaitForRoomRouteConfirmedAsync(Room, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task TheGateAppliesOnlyWhenAGatewayIsRegistered()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        // A bare cluster has no delivery path to wait for: no gateway reported a process, so
        // the coordinator skips the route gate (FIX-END-16 boundary).
        Assert.False(await control.HasRegisteredGatewayAsync());

        // A gateway that reported a process under the platform-gateway role makes the gate
        // apply — the cluster now has an ingress whose route installation must be confirmed
        // before a battle starts.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.node(node_id,agent_instance_id) VALUES ('node-gate-1','agent-gate-1');
                INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,
                    desired_state,package_hash)
                VALUES ('node-gate-1/platform-gateway','node-gate-1','platform-gateway','1.0.0',1,
                    'Running','pkg-1');
                INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,
                    report_sequence,lifecycle,readiness,health,admission,started_at)
                VALUES ('gateway-gate-1','node-gate-1/platform-gateway','node-gate-1','agent-gate-1',1,
                    'Running','Ready','Healthy','Accepting',clock_timestamp());
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }
        Assert.True(await control.HasRegisteredGatewayAsync());
    }

    /// <summary>A room standing Preparing under a claimed ownership — the pre-battle entity
    /// whose route the snapshot publishes (FIX-END-16).</summary>
    private static async Task ArrangePreparingRoomAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var ownership = new NpgsqlCommand("""
            INSERT INTO control.ownership(kind,entity_id,tenant_id,game_id,owner_process_instance_id,
                epoch,lease_until,status,version)
            VALUES ('room',@room,@tenant,@game,'gs-gate-1',1,now()+interval '1 minute','Active',1)
            """, connection);
        ownership.Parameters.AddWithValue("tenant", Tenant);
        ownership.Parameters.AddWithValue("game", Game);
        ownership.Parameters.AddWithValue("room", Room);
        await ownership.ExecuteNonQueryAsync();
        await using var room = new NpgsqlCommand("""
            INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash)
            VALUES (@room,@tenant,@game,'Preparing',1,'pkg-1','1.0.0','cfg-1')
            """, connection);
        room.Parameters.AddWithValue("tenant", Tenant);
        room.Parameters.AddWithValue("game", Game);
        room.Parameters.AddWithValue("room", Room);
        await room.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_route_gate_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
