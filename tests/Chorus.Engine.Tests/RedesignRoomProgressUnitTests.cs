using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Per-room progress persistence (FIX-TAKE-06): the row is the head the host last reported,
/// progress_at is database time and only moves when the reported head moves, a stale report
/// sequence never regresses a row, rooms the process stopped naming are pruned, and the stall
/// query names only frozen Running and explicitly Stopped actors on hosts whose heartbeat is
/// still fresh — never business-paused or completed rooms, never rooms on a stale host.
/// </summary>
public sealed class RedesignRoomProgressUnitTests
{
    [Fact]
    public async Task ProgressAtStandsStillWhileTheReportedHeadDoesNotMove()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RecordRoomProgressAsync("game-1", 1,
            [new("room-1", 1, "Running", 5, 1_000, null)]);
        var first = await ReadRowAsync(database.ConnectionString, "game-1", "room-1");
        Assert.Equal(5, first.LastTick);
        Assert.Equal(1, first.ReportSequence);

        await Task.Delay(50);
        await control.RecordRoomProgressAsync("game-1", 2,
            [new("room-1", 1, "Running", 5, 2_000, null)]);
        var second = await ReadRowAsync(database.ConnectionString, "game-1", "room-1");
        // A frozen tick across reports is the stall signal, never fresh evidence.
        Assert.Equal(first.ProgressAt, second.ProgressAt);
        Assert.True(second.ReportedAt > first.ReportedAt,
            "reported_at never moved on the second report.");
        Assert.Equal(2, second.ReportSequence);
        Assert.Equal(2_000, second.LastCommittedUnixMs);
    }

    [Fact]
    public async Task AMovingHeadAdvancesProgressAt()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RecordRoomProgressAsync("game-1", 1,
            [new("room-1", 1, "Running", 5, 1_000, null)]);
        var first = await ReadRowAsync(database.ConnectionString, "game-1", "room-1");

        await Task.Delay(50);
        await control.RecordRoomProgressAsync("game-1", 2,
            [new("room-1", 1, "Running", 6, 2_000, null)]);
        var second = await ReadRowAsync(database.ConnectionString, "game-1", "room-1");
        Assert.Equal(6, second.LastTick);
        Assert.True(second.ProgressAt > first.ProgressAt,
            "The head moved but progress_at never advanced.");
    }

    [Fact]
    public async Task AStaleReportSequenceDoesNotRegressARoom()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RecordRoomProgressAsync("game-1", 5,
            [new("room-1", 1, "Running", 10, 1_000, null)]);
        // An out-of-order replay of an older report must not masquerade as current evidence.
        await control.RecordRoomProgressAsync("game-1", 3,
            [new("room-1", 1, "Stopped", 4, null, "the actor loop failed: stale")]);
        var row = await ReadRowAsync(database.ConnectionString, "game-1", "room-1");
        Assert.Equal("Running", row.State);
        Assert.Equal(10, row.LastTick);
        Assert.Equal(5, row.ReportSequence);
        Assert.Null(row.StopReason);
    }

    [Fact]
    public async Task RoomsTheProcessNoLongerNamesArePruned()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        await control.RecordRoomProgressAsync("game-1", 1,
            [new("room-1", 1, "Running", 5, null, null),
             new("room-2", 1, "Running", 3, null, null)]);
        Assert.Equal(2, await database.CountAsync("control.room_progress"));

        // A room the process no longer names has no reporter; keeping its row would let a
        // dead report masquerade as live evidence.
        await control.RecordRoomProgressAsync("game-1", 2, [new("room-1", 1, "Running", 6, null, null)]);
        Assert.Equal(1, await database.CountAsync("control.room_progress"));
        Assert.NotNull(await ReadRowAsync(database.ConnectionString, "game-1", "room-1"));
    }

    [Fact]
    public async Task TheStallListNamesFrozenAndStoppedRoomsOnFreshHostsOnly()
    {
        await using var database = await CreateDatabaseAsync();
        var control = new RedesignControlStore(database.ConnectionString);
        var started = DateTime.UtcNow;
        await control.RegisterNodeAsync("node-a", "agent-1", "{}");
        await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server", "game-server",
            "game-1", 1, "Running", "Ready", "Healthy", "Accepting", "Recovered", started);
        await control.RegisterNodeAsync("node-b", "agent-2", "{}");
        await control.ReportProcessAsync("node-b", "agent-2", "node-b/game-server", "game-server",
            "game-2", 1, "Running", "Ready", "Healthy", "Accepting", "Recovered", started);

        await control.RecordRoomProgressAsync("game-1", 1,
        [
            new("room-frozen", 1, "Running", 3, null, null),
            new("room-stopped", 1, "Stopped", 7, null, "the actor loop failed: boom"),
            new("room-paused", 1, "Stopped", 9, null,
                "frame commit rejected at tick 9: A room in status Suspended does not accept frame commits."),
            new("room-done", 1, "Completed", 200, null, null),
            new("room-fresh", 1, "Running", 40, null, null)
        ]);
        await control.RecordRoomProgressAsync("game-2", 1,
            [new("room-on-stale-host", 1, "Running", 11, null, null)]);

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            // Freeze the evidence the stall query reads: the frozen room's head stopped a
            // minute ago, the second host's heartbeat died a minute ago.
            await using var backdate = new NpgsqlCommand("""
                UPDATE control.room_progress SET progress_at=clock_timestamp()-interval '60 seconds'
                WHERE (room_id='room-frozen' AND process_instance_id='game-1')
                   OR (room_id='room-on-stale-host' AND process_instance_id='game-2')
                """, connection);
            Assert.Equal(2, await backdate.ExecuteNonQueryAsync());
            await using var staleHeartbeat = new NpgsqlCommand(
                "UPDATE control.process SET observed_at=clock_timestamp()-interval '60 seconds' WHERE process_instance_id='game-2'",
                connection);
            Assert.Equal(1, await staleHeartbeat.ExecuteNonQueryAsync());
            await using var room = new NpgsqlCommand("""
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash,tick_ms)
                VALUES ('room-paused','tenant-a','game-a','Suspended',1,'pkg','1.0.0','cfg',25),
                       ('room-frozen','tenant-a','game-a','Running',1,'pkg','1.0.0','cfg',25),
                       ('room-stopped','tenant-a','game-a','Running',1,'pkg','1.0.0','cfg',40)
                """, connection);
            await room.ExecuteNonQueryAsync();
        }

        var stalled = await control.ListStalledRoomsAsync(stallSeconds: 30, freshSeconds: 20);
        Assert.Equal(["room-frozen", "room-stopped"],
            stalled.Select(room => room.RoomId).ToList());
        var frozen = stalled.Single(room => room.RoomId == "room-frozen");
        Assert.Equal("game-1", frozen.ProcessInstanceId);
        Assert.Equal("Running", frozen.State);
        Assert.Null(frozen.StopReason);
        // The identity facts governance needs ride along, so it acts without a second read.
        Assert.Equal("tenant-a", frozen.TenantId);
        Assert.Equal("game-a", frozen.GameId);
        Assert.Equal(25, frozen.TickMs);
        var stopped = stalled.Single(room => room.RoomId == "room-stopped");
        Assert.Contains("boom", stopped.StopReason);
        Assert.Equal(40, stopped.TickMs);
    }

    private sealed record ProgressRow(DateTime ProgressAt, DateTime ReportedAt,
        long LastTick, long ReportSequence, string State, long? LastCommittedUnixMs, string? StopReason);

    private static async Task<ProgressRow> ReadRowAsync(string connectionString,
        string processInstanceId, string roomId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT progress_at, reported_at, last_tick, report_sequence, state,
                   CASE WHEN last_committed_at IS NULL THEN NULL
                        ELSE (extract(epoch from last_committed_at)*1000)::bigint END,
                   stop_reason
            FROM control.room_progress WHERE process_instance_id=@p AND room_id=@r
            """, connection);
        command.Parameters.AddWithValue("p", processInstanceId);
        command.Parameters.AddWithValue("r", roomId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No progress row for {processInstanceId}/{roomId}.");
        return new ProgressRow(reader.GetFieldValue<DateTime>(0), reader.GetFieldValue<DateTime>(1),
            reader.GetInt64(2),
            reader.GetInt64(3), reader.GetString(4),
            await reader.IsDBNullAsync(5) ? null : reader.GetInt64(5),
            await reader.IsDBNullAsync(6) ? null : reader.GetString(6));
    }

    private static async Task<TemporaryDatabase> CreateDatabaseAsync()
    {
        var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
        var database = new TemporaryDatabase(source);
        await database.InitializeAsync();
        return database;
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_room_progress_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var database = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = database.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async Task<int> CountAsync(string table)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
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
