using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The settlement executor's claim and retry state (FIX-SETTLE-03): one claim lease per
/// room serialises concurrent executors, an expired lease makes a killed holder's room
/// re-claimable with a bumped attempt, a failure records a bounded backoff and a queryable
/// reason, the budget escalates a manifest that keeps failing to NeedsAttention, and a stale
/// holder cannot overwrite the new holder's state. The per-grant idempotent floor that makes
/// any remaining overlap safe is END-11's, proven separately by
/// TwoSettlersDriveTheSameManifestToOneLinePerGrant.
/// </summary>
public sealed class RedesignSettleTaskStoreTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Room = "room-1";

    [Fact]
    public async Task TwoExecutorsCannotClaimTheSameRoomAtOnce()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        var first = await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-a",
            TimeSpan.FromSeconds(60));
        Assert.NotNull(first);
        Assert.Equal("Executing", first.Status);
        Assert.Equal("executor-a", first.OwnerInstanceId);
        Assert.Equal(1, first.AttemptCount);
        Assert.NotNull(first.LeaseUntil);

        // The live lease is held: the second executor is refused, no bump.
        Assert.Null(await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b",
            TimeSpan.FromSeconds(60)));

        // A completed settlement deletes the task — a fresh discovery starts at attempt 1.
        await control.CompleteSettleTaskAsync(Room);
        Assert.Null(await control.GetSettleTaskAsync(Room));
        var fresh = await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b",
            TimeSpan.FromSeconds(60));
        Assert.NotNull(fresh);
        Assert.Equal("executor-b", fresh.OwnerInstanceId);
        Assert.Equal(1, fresh.AttemptCount);
    }

    [Fact]
    public async Task AnExpiredLeaseCanBeReclaimedWithABumpedAttempt()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-a", TimeSpan.FromSeconds(60));
        await ExpireLeaseAsync(database);

        // The holder died mid-call: the lease lapsed, the next executor reclaims as attempt 2.
        var reclaimed = await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b",
            TimeSpan.FromSeconds(60));
        Assert.NotNull(reclaimed);
        Assert.Equal("executor-b", reclaimed.OwnerInstanceId);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task ARetryableTaskBacksOffAndThenReclaims()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-a", TimeSpan.FromSeconds(60));
        await control.RecordSettleFailureAsync(Room, "executor-a", "The data plane refused",
            maxAttempts: 5, backoff: TimeSpan.FromSeconds(5));

        var backedOff = await control.GetSettleTaskAsync(Room);
        Assert.NotNull(backedOff);
        Assert.Equal("Retryable", backedOff.Status);
        Assert.Equal(1, backedOff.AttemptCount);
        Assert.Equal("The data plane refused", backedOff.LastError);
        Assert.NotNull(backedOff.NextAttempt);
        Assert.True(backedOff.NextAttempt > DateTime.UtcNow.AddSeconds(3),
            "The next attempt must sit a bounded backoff out.");

        // The backoff has not lapsed: nobody may re-claim yet.
        Assert.Null(await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b",
            TimeSpan.FromSeconds(60)));

        // The backoff lapses: the next pass reclaims as attempt 2.
        await ExpireNextAttemptAsync(database);
        var reclaimed = await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b",
            TimeSpan.FromSeconds(60));
        Assert.NotNull(reclaimed);
        Assert.Equal("executor-b", reclaimed.OwnerInstanceId);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task AManifestThatKeepsFailingEscalatesToNeedsAttention()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-a", TimeSpan.FromSeconds(60));
            await control.RecordSettleFailureAsync(Room, "executor-a",
                $"The grant would drive the asset negative (attempt {attempt})",
                maxAttempts: 3, backoff: TimeSpan.FromSeconds(1));
            var state = await control.GetSettleTaskAsync(Room);
            Assert.NotNull(state);
            Assert.Equal(attempt, state.AttemptCount);
            Assert.Equal(attempt == 3 ? "NeedsAttention" : "Retryable", state.Status);
            if (attempt < 3)
                await ExpireNextAttemptAsync(database);
        }

        // NeedsAttention is terminal: no automatic re-claim, the reason stays queryable.
        Assert.Null(await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b",
            TimeSpan.FromSeconds(60)));
        var final = await control.GetSettleTaskAsync(Room);
        Assert.NotNull(final);
        Assert.Equal("NeedsAttention", final.Status);
        Assert.Equal(3, final.AttemptCount);
        Assert.Equal("The grant would drive the asset negative (attempt 3)", final.LastError);
        Assert.Null(final.NextAttempt);
    }

    [Fact]
    public async Task AStaleOwnerCannotOverwriteTheNewHoldersState()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-a", TimeSpan.FromSeconds(60));
        await ExpireLeaseAsync(database);
        await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-b", TimeSpan.FromSeconds(60));

        // The stale holder's late failure report is a no-op — the new holder owns the row.
        await control.RecordSettleFailureAsync(Room, "executor-a", "stale", maxAttempts: 5,
            backoff: TimeSpan.FromSeconds(1));
        var state = await control.GetSettleTaskAsync(Room);
        Assert.NotNull(state);
        Assert.Equal("Executing", state.Status);
        Assert.Equal("executor-b", state.OwnerInstanceId);
        Assert.Equal(2, state.AttemptCount);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task ACompletedSettlementLeavesNoTaskBehind()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var control = new RedesignControlStore(database.ConnectionString);

        await control.ClaimSettleTaskAsync(Tenant, Game, Room, "executor-a", TimeSpan.FromSeconds(60));
        await control.RecordSettleFailureAsync(Room, "executor-a", "transient", maxAttempts: 5,
            backoff: TimeSpan.FromSeconds(1));
        Assert.NotNull(await control.GetSettleTaskAsync(Room));

        await control.CompleteSettleTaskAsync(Room);
        Assert.Null(await control.GetSettleTaskAsync(Room));
        Assert.Equal(0, await TaskRowCountAsync(database));
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_settle_task_" + Guid.NewGuid().ToString("N");
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

    private static async Task ExpireLeaseAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE control.settle_task SET lease_until=clock_timestamp()-interval '1 second' WHERE room_id=@room",
            connection);
        command.Parameters.AddWithValue("room", Room);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExpireNextAttemptAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE control.settle_task SET next_attempt=clock_timestamp()-interval '1 second' WHERE room_id=@room",
            connection);
        command.Parameters.AddWithValue("room", Room);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> TaskRowCountAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM control.settle_task", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
