using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-REC-01, the recovery route barrier: the control plane activates a recovered room
/// only once every deployed gateway kind — platform-gateway and streamer-gateway — has
/// installed and confirmed the routing version that carries the room's takeover. A role
/// with a registered process but no confirmation keeps the barrier closed; a role nobody
/// deployed is no delivery path to wait for (the FIX-END-16 boundary, per kind); a bare
/// cluster passes vacuously. The confirmations are the persisted routing_confirm rows of
/// exactly that version — never "the notice was sent".
/// </summary>
public sealed class RedesignRec01RouteBarrierStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Room = "room-rec-barrier-1";

    [Fact]
    public async Task ABareClusterOpensTheBarrierVacuously()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await ArrangeRecoveringRoomAsync(database);

        // No gateway process registered at all: no delivery path to wait for.
        Assert.True(await control.WaitForRecoveryRoutesConfirmedAsync(Room, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EveryRegisteredGatewayKindMustConfirmTheTakeoverVersion()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await ArrangeRecoveringRoomAsync(database);
        await SeedGatewayAsync(database, "platform-rec-1", "platform-gateway");
        await SeedGatewayAsync(database, "streamer-rec-1", "streamer-gateway");

        var snapshot = await control.ReadRoutingSnapshotAsync();
        Assert.Contains(snapshot.Rooms, room => room.RoomId == Room);

        // Neither gateway confirmed: the barrier stays closed — no activation.
        Assert.False(await control.WaitForRecoveryRoutesConfirmedAsync(Room, TimeSpan.FromMilliseconds(400)));

        // The platform gateway confirms but the streamer gateway did not: one missing
        // confirmation means the recovery is not whole.
        await control.ConfirmRoutingVersionAsync("platform-rec-1", snapshot.Version);
        Assert.False(await control.WaitForRecoveryRoutesConfirmedAsync(Room, TimeSpan.FromMilliseconds(400)));

        // The streamer gateway confirms the same version: both roles installed it.
        await control.ConfirmRoutingVersionAsync("streamer-rec-1", snapshot.Version);
        Assert.True(await control.WaitForRecoveryRoutesConfirmedAsync(Room, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AKindNobodyDeployedIsNoDeliveryPathToWaitFor()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await ArrangeRecoveringRoomAsync(database);
        await SeedGatewayAsync(database, "platform-rec-2", "platform-gateway");

        var snapshot = await control.ReadRoutingSnapshotAsync();

        // Only the platform gateway is deployed: its own confirmation is the whole
        // installed set — the streamer-gateway leg is vacuous.
        await control.ConfirmRoutingVersionAsync("platform-rec-2", snapshot.Version);
        Assert.True(await control.WaitForRecoveryRoutesConfirmedAsync(Room, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AConfirmationOfADifferentVersionCarriesNoEvidenceAboutTheRoom()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await ArrangeRecoveringRoomAsync(database);
        await SeedGatewayAsync(database, "platform-rec-3", "platform-gateway");

        var snapshot = await control.ReadRoutingSnapshotAsync();

        // A confirm of a version that does not carry the room is not evidence about the
        // room's route — the barrier stays closed.
        await control.ConfirmRoutingVersionAsync("platform-rec-3", snapshot.Version + 1);
        Assert.False(await control.WaitForRecoveryRoutesConfirmedAsync(Room, TimeSpan.FromMilliseconds(400)));
    }

    /// <summary>A room standing mid-takeover: the transfer parked the ownership
    /// Recovering at the new epoch and the room keeps its Running battle status — the
    /// snapshot row the gateways must install.</summary>
    private static async Task ArrangeRecoveringRoomAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand("""
            INSERT INTO control.ownership(kind,entity_id,tenant_id,game_id,owner_process_instance_id,
                epoch,lease_until,status,version)
            VALUES ('room',@room,@tenant,@game,'gs-rec-1',8,now()+interval '1 minute','Recovering',2);
            INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash)
            VALUES (@room,@tenant,@game,'Running',8,'pkg-1','1.0.0','cfg-1')
            """, connection);
        seed.Parameters.AddWithValue("tenant", Tenant);
        seed.Parameters.AddWithValue("game", Game);
        seed.Parameters.AddWithValue("room", Room);
        await seed.ExecuteNonQueryAsync();
    }

    /// <summary>A registered gateway process of one deployed kind.</summary>
    private static async Task SeedGatewayAsync(TemporaryDatabase database, string instance, string role)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand($"""
            INSERT INTO control.node(node_id,agent_instance_id) VALUES ('node-@{instance}','agent-@{instance}');
            INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,
                desired_state,package_hash)
            VALUES ('node-@{instance}/{role}','node-@{instance}','{role}','1.0.0',1,'Running','pkg-1');
            INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,
                report_sequence,lifecycle,readiness,health,admission,started_at)
            VALUES ('{instance}','node-@{instance}/{role}','node-@{instance}','agent-@{instance}',1,
                'Running','Ready','Healthy','Accepting',clock_timestamp())
            """, connection);
        await seed.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_rec01_barrier_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
