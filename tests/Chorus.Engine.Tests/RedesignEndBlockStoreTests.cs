using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The release block record (FIX-SETTLE-05): a data-plane refusal that parks the release
/// names its evidence reason on the end operation row, keeps the moment the continuous wait
/// began across later refusals of the same episode, and the release transition adds the
/// dwell to a cumulative total and clears the reason — the total survives the release, the
/// reason only matters while the room is blocked. A refusal that lands after the row left
/// the release phase records nothing, and a release with no block adds no dwell.
/// </summary>
public sealed class RedesignEndBlockStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Room = "room-st5-1";
    private const string Operation = "end-st5-1";

    [Fact]
    public async Task ABlockedReleaseRecordsTheReasonAndDwellThenClearsOnTheRelease()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await CreateEndOperationAsync(database, EndOperationPhases.PlatformClosed);

        var first = "The reward manifest is incomplete: settle the grants before finalizing.";
        await control.RecordEndOperationBlockAsync(Tenant, Game, Operation, first);
        var blocked = await control.GetEndOperationAsync(Tenant, Game, Room);
        Assert.NotNull(blocked);
        Assert.Equal(first, blocked.BlockedReason);
        Assert.NotNull(blocked.BlockedSince);
        Assert.Equal(first, blocked.LastError);
        var since = blocked.BlockedSince!.Value;

        // A later refusal of the same episode refreshes the reason, not the wait's start.
        var second = "The round still has undisposed inputs; finalize after they reach a terminal state.";
        await control.RecordEndOperationBlockAsync(Tenant, Game, Operation, second);
        blocked = await control.GetEndOperationAsync(Tenant, Game, Room);
        Assert.NotNull(blocked);
        Assert.Equal(second, blocked.BlockedReason);
        Assert.Equal(since, blocked.BlockedSince!.Value);

        // The wait ran ten seconds; the release accumulates it and clears the reason.
        await BackdateBlockSinceAsync(database, seconds: 10);
        var released = await control.AdvanceEndOperationPhaseAsync(Tenant, Game, Operation,
            EndOperationPhases.PlatformClosed, EndOperationPhases.Released);
        Assert.Equal(EndOperationPhases.Released, released.Phase);
        Assert.Null(released.BlockedReason);
        Assert.Null(released.BlockedSince);
        Assert.InRange(released.BlockedTotalMs, 8000, 30000);

        // A later rung is not a block episode: the total is untouched.
        var destroyed = await control.AdvanceEndOperationPhaseAsync(Tenant, Game, Operation,
            EndOperationPhases.Released, EndOperationPhases.Destroyed);
        Assert.Equal(released.BlockedTotalMs, destroyed.BlockedTotalMs);
    }

    [Fact]
    public async Task ABlockIsNotRecordedOnceTheRowLeftTheReleasePhase()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await CreateEndOperationAsync(database, EndOperationPhases.PlatformClosed);
        await control.AdvanceEndOperationPhaseAsync(Tenant, Game, Operation,
            EndOperationPhases.PlatformClosed, EndOperationPhases.Released);

        // The room is released; a stale refusal must not paint a block onto it.
        await control.RecordEndOperationBlockAsync(Tenant, Game, Operation, "stale refusal");
        var state = await control.GetEndOperationAsync(Tenant, Game, Room);
        Assert.NotNull(state);
        Assert.Null(state.BlockedReason);
        Assert.Null(state.BlockedSince);
        Assert.Equal(0, state.BlockedTotalMs);
    }

    [Fact]
    public async Task AReleaseWithoutABlockLeavesNoBlockRecord()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await CreateEndOperationAsync(database, EndOperationPhases.PlatformClosed);

        var released = await control.AdvanceEndOperationPhaseAsync(Tenant, Game, Operation,
            EndOperationPhases.PlatformClosed, EndOperationPhases.Released);
        Assert.Null(released.BlockedReason);
        Assert.Null(released.BlockedSince);
        Assert.Equal(0, released.BlockedTotalMs);
    }

    [Fact]
    public async Task ASnapshotExposesTheBlockWhileItStandsAndTheDwellAfterItClears()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        var admin = new RedesignAdminStore(database.ConnectionString);
        await CreateEndOperationAsync(database, EndOperationPhases.PlatformClosed);
        await CreateRoomAsync(database);

        await control.RecordEndOperationBlockAsync(Tenant, Game, Operation, "manifest incomplete");
        var standing = await admin.GetRoomSnapshotAsync(Room);
        Assert.True(standing.Found);
        Assert.Equal("manifest incomplete", standing.ReleaseBlockedReason);
        Assert.NotNull(standing.ReleaseBlockedSince);
        Assert.Equal(0, standing.ReleaseBlockedTotalMs);

        await BackdateBlockSinceAsync(database, seconds: 5);
        await control.AdvanceEndOperationPhaseAsync(Tenant, Game, Operation,
            EndOperationPhases.PlatformClosed, EndOperationPhases.Released);
        var settled = await admin.GetRoomSnapshotAsync(Room);
        Assert.True(settled.Found);
        Assert.Null(settled.ReleaseBlockedReason);
        Assert.Null(settled.ReleaseBlockedSince);
        Assert.InRange(settled.ReleaseBlockedTotalMs, 3000, 20000);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_end_block_" + Guid.NewGuid().ToString("N");
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

    private static async Task CreateEndOperationAsync(TemporaryDatabase database, string phase)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.end_operation(tenant_id,game_id,end_operation_id,room_id,
                round_generation,binding_generations,final_tick,result_version,phase)
            VALUES (@tenant,@game,@operation,@room,1,'[]'::jsonb,10,1,@phase)
            """, connection);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("game", Game);
        command.Parameters.AddWithValue("operation", Operation);
        command.Parameters.AddWithValue("room", Room);
        command.Parameters.AddWithValue("phase", phase);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateRoomAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash)
            VALUES (@room,@tenant,@game,'Finishing',1,'pkg-1','1.0.0','cfg-1')
            """, connection);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("game", Game);
        command.Parameters.AddWithValue("room", Room);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task BackdateBlockSinceAsync(TemporaryDatabase database, int seconds)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET blocked_since=clock_timestamp()-make_interval(secs => @seconds)
            WHERE end_operation_id=@operation
            """, connection);
        command.Parameters.AddWithValue("seconds", seconds);
        command.Parameters.AddWithValue("operation", Operation);
        await command.ExecuteNonQueryAsync();
    }
}
