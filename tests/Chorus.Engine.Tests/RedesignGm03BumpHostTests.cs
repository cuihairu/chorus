using Chorus.ControlServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The operator-gated epoch bump end to end (FIX-GM-03): the storage transaction commits (old
/// new epoch, owner kept, Recovering), the route change is a separate surface from that commit
/// (the caller sees Pending until a gateway confirms the routing version that carries the new
/// epoch, then Synced on a replay), and the bump is no implicit platform-end or room-end — the
/// room stays Running and no end operation is created. A low role, a stale expected and a
/// replayed id under different content are each refused.
/// </summary>
public sealed class RedesignGm03BumpHostTests
{
    [Fact]
    public async Task TheBumpCommitsStorageAndSeparatesTheRouteConfirmation()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        var operators = new RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "alice-token");
        await operators.RegisterOperatorAsync("viewer-carl", "viewer", ["tenant-a"], ["game-a"], "carl-token");

        // One running room at epoch 1, owned by gs-gm03.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand("""
                INSERT INTO control.ownership(kind,entity_id,owner_process_instance_id,epoch,
                    lease_until,status,version,tenant_id,game_id)
                VALUES('room','room-bump','gs-gm03',1,now()+interval '60 second','Active',1,
                    'tenant-a','game-a')
                """, connection);
            await ownership.ExecuteNonQueryAsync();
            await using var room = new NpgsqlCommand("""
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,
                    package_hash,module_version,config_hash)
                VALUES('room-bump','tenant-a','game-a','Running',1,'pkg-1','1.0.0','cfg-1')
                """, connection);
            await room.ExecuteNonQueryAsync();
        }

        using var certificate = environment.Certificate("tenant-a", "game-server", "gm03-game");
        using var channel = environment.Channel(environment.ControlPort, certificate, "control-server");
        var client = new Admin.AdminClient(channel);
        var gm = new GmOperatorClient(client, "gm-alice", "alice-token");
        var control = new RedesignControlStore(environment.DatabaseConnectionString);

        // (1) The bump commits the storage: epoch moves to 2, the owner is kept, the ownership
        // parks Recovering — and the route is still only Pending.
        var bump = await gm.BumpRoomEpochAsync("tenant-a", "game-a", "room-bump", "op-1", "test bump",
            expectedEpoch: 1);
        Assert.False(bump.Duplicate);
        Assert.Equal("op-1", bump.OperationId);
        Assert.Equal(1, bump.OldEpoch);
        Assert.Equal(2, bump.NewEpoch);
        Assert.Equal("gs-gm03", bump.OwnerInstanceId);
        Assert.Equal("Recovering", bump.OwnershipStatus);
        Assert.Equal("Pending", bump.RouteSyncStatus);

        // (2) No implicit room-end: the room still stands Running and no end operation exists.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var status = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-bump'", connection);
            Assert.Equal("Running", await status.ExecuteScalarAsync());
            await using var endCount = new NpgsqlCommand(
                "SELECT count(*) FROM control.end_operation WHERE room_id='room-bump'", connection);
            Assert.Equal(0L, Convert.ToInt64(await endCount.ExecuteScalarAsync()));
        }

        // (3) A gateway installs and confirms the routing version that carries the bumped epoch.
        var snapshot = await control.ReadRoutingSnapshotAsync();
        Assert.Contains(snapshot.Rooms, room => room.RoomId == "room-bump" && room.Epoch == 2);
        await control.ConfirmRoutingVersionAsync("gw-gm03", snapshot.Version);

        // (4) A lost reply replays by operation id: the recorded outcome returns (duplicate) and
        // the route is now Synced — the same command never re-bumped the epoch.
        var replay = await gm.BumpRoomEpochAsync("tenant-a", "game-a", "room-bump", "op-1", "test bump",
            expectedEpoch: 1);
        Assert.True(replay.Duplicate);
        Assert.Equal(2, replay.NewEpoch);
        Assert.Equal(1, replay.OldEpoch);
        Assert.Equal("Synced", replay.RouteSyncStatus);

        // (5) A role below the command's minimum is refused.
        var lowRole = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(channel)
            .BumpRoomEpochAsync(new AdminBumpRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump", RequestId = "op-low",
                Reason = "low role", Operator = "viewer-carl", ExpectedEpoch = 2
            }, new Metadata { { "authorization", "Bearer carl-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, lowRole.StatusCode);

        // (6) A stale expected epoch is refused (the room already moved to 2).
        var stale = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(channel)
            .BumpRoomEpochAsync(new AdminBumpRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump", RequestId = "op-stale",
                Reason = "stale", Operator = "gm-alice", ExpectedEpoch = 5
            }, new Metadata { { "authorization", "Bearer alice-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);

        // (7) The same request id under different content conflicts (already spent).
        var conflict = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(channel)
            .BumpRoomEpochAsync(new AdminBumpRoomEpochRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump", RequestId = "op-1",
                Reason = "different target", Operator = "gm-alice", ExpectedEpoch = 1, TargetEpoch = 3
            }, new Metadata { { "authorization", "Bearer alice-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.AlreadyExists, conflict.StatusCode);
    }
}
