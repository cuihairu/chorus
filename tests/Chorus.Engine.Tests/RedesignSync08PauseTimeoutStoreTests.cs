using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-08 acceptance (暂停超时终结): a paused round that exceeded its declared MaxPause
/// is terminated by the module's freeze rule — CommitPausedResult is the ONLY path that moves
/// a paused room toward a terminal result, flipping Paused→Finishing (never Settled), writing
/// the terminal result at the frozen frame and the planned grants, and spending the ready
/// rows in the same transaction. The timeout discovery respects the declared MaxPause (canonical
/// field 3) and selects a paused room regardless of the rest of the offline policy, so the
/// deadline is honored strictly; a fresh pause is never misjudged overdue.
/// </summary>
public sealed class RedesignSync08PauseTimeoutStoreTests
{
    private const string OfflinePolicy = "ContinueToEnd|30|120|1|Buffer"; // MaxPause=120, Ready=1

    [Fact]
    public async Task CommitPausedResultFlipsPausedToFinishingWithResultGrantAndSpentReady()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        var resultJson = """{"ranking":[{"player":"p1","score":1}],"winner":"p1"}""";
        var planHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(resultJson));
        var receipt = await arrange.FrameStore.CommitPausedResultAsync("tenant-a", "game-a", Arrange.Room,
            "game-1", arrange.Epoch, "finish-paused-1", 0, planHash, resultJson,
            [new PlannedGrant("p1", "round-score", "grant/p1", "points", 1)]);

        Assert.Equal(0, receipt.Tick);
        Assert.Equal("Finishing", await arrange.RoomStatusAsync());
        // The terminal result sits at the frozen frame with the planned grant, and the ready
        // rows the pause-timeout invalidates are gone.
        var (version, frame) = await arrange.TerminalResultAsync();
        Assert.Equal(1, version);
        Assert.Equal(0, frame);
        var grant = await arrange.GrantAsync("p1", "round-score");
        Assert.Equal("Planned", grant);
        Assert.Equal(0, await arrange.ResumeReadyCountAsync());
        var outbox = await arrange.OutboxEventsAsync();
        Assert.Single(outbox.Where(e => e == "round.finished"));
    }

    [Fact]
    public async Task CommitPausedResultRefusesANonPausedRoom()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        // Resume the room first (the single control participant reports ready), so it is no
        // longer Paused — a paused result cannot be committed against a room that left Paused.
        await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Host.StreamerId, arrange.Host.SessionId, "ready-before-resume");
        await arrange.RoomStore.ResumeRoundAsync("tenant-a", "game-a", Arrange.Room,
            "game-1", arrange.Epoch, 0, 1, "resume-then-finish");
        await Assert.ThrowsAsync<FrameConflictException>(() =>
            arrange.FrameStore.CommitPausedResultAsync("tenant-a", "game-a", Arrange.Room,
                "game-1", arrange.Epoch, "finish-after-resume", 0,
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("{}")), "{}",
                [new PlannedGrant("p1", "round-score", "grant/p1", "points", 1)]));
    }

    [Fact]
    public async Task CommitPausedResultRefusesABoundaryAwayFromTheFrozenHead()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        await Assert.ThrowsAsync<FrameConflictException>(() =>
            arrange.FrameStore.CommitPausedResultAsync("tenant-a", "game-a", Arrange.Room,
                "game-1", arrange.Epoch, "finish-wrong-tick", 7,
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("{}")), "{}",
                [new PlannedGrant("p1", "round-score", "grant/p1", "points", 1)]));
        Assert.Equal("Paused", await arrange.RoomStatusAsync());
    }

    [Fact]
    public async Task OverdueDiscoveryRespectsTheDeclaredMaxPause()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        // A fresh pause (just now) is not overdue yet.
        var fresh = await arrange.Control.ListOverduePausedRoomsAsync();
        Assert.DoesNotContain(fresh, room => room.RoomId == Arrange.Room);
        // Age the pause beyond the declared MaxPause (120s) — the timeout discovers the room
        // regardless of the rest of the offline policy (no FinishByRule filter).
        await arrange.AgePauseAsync(200);
        var overdue = await arrange.Control.ListOverduePausedRoomsAsync();
        Assert.Contains(overdue, room => room.RoomId == Arrange.Room && room.GameId == "game-a");
    }

    private sealed class Arrange : IAsyncDisposable
    {
        public const string Room = "sync08-timeout-room";
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public RedesignFrameStore FrameStore = null!;
        public (string StreamerId, string SessionId) Host { get; init; }
        public long Epoch { get; init; }
        public TemporaryDatabase Database { get; init; } = null!;

        public static async Task<Arrange> PausedRoundAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var stores = new Arrange
                {
                    Database = database,
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString),
                    FrameStore = new RedesignFrameStore(database.ConnectionString)
                };
                var host = await MountAsync(stores, "sync08-t-open-host");
                var claim = await stores.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", Room,
                    "game-1", 60);
                await stores.RoomStore.StartRoundAsync("tenant-a", "game-a", Room, "game-1", claim.Epoch,
                    [new RoundParticipant(host.BindingId, host.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-sync08-timeout");
                await stores.RoomStore.PrepareRoundAsync("tenant-a", "game-a", Room, "game-1", claim.Epoch,
                    RandomNumberGenerator.GetBytes(64), RandomNumberGenerator.GetBytes(32), 1,
                    "prepare-sync08-timeout", tickMs: 25, recoveryMode: "DeterministicReplay",
                    offlinePolicy: OfflinePolicy);
                await stores.RoomStore.ActivateRoundAsync("tenant-a", "game-a", Room, "game-1",
                    claim.Epoch, "activate-sync08-timeout");
                await stores.RoomStore.PauseRoundAsync("tenant-a", "game-a", Room, "game-1", claim.Epoch,
                    "all control sessions offline past the declared grace", "pause-sync08-timeout", 0, 1);
                return new Arrange
                {
                    Database = database,
                    RoomStore = stores.RoomStore,
                    IdentityStore = stores.IdentityStore,
                    BindingStore = stores.BindingStore,
                    Control = stores.Control,
                    FrameStore = stores.FrameStore,
                    Host = (host.StreamerId, host.SessionId),
                    Epoch = claim.Epoch
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
            Arrange stores, string openId)
        {
            var streamer = await stores.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                "app-1", openId);
            var live = await stores.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                "simulator", "app-1", "platform-room", "live-" + openId);
            var session = await stores.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                "connection-" + openId, RandomNumberGenerator.GetBytes(32));
            var binding = await stores.BindingStore.StartGameplayAsync("tenant-a", live,
                session.SessionId, "game-a", "mount-" + openId);
            return (streamer.StreamerId, session.SessionId, binding.BindingId);
        }

        public async Task<string> RoomStatusAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id=@room", connection);
            command.Parameters.AddWithValue("room", Room);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task AgePauseAsync(int seconds)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE game.room SET paused_at=clock_timestamp() - @age * interval '1 second' WHERE room_id=@room",
                connection);
            command.Parameters.AddWithValue("age", seconds);
            command.Parameters.AddWithValue("room", Room);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<(long Version, long Frame)> TerminalResultAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT result_version,final_frame FROM game.result WHERE room_id=@room", connection);
            command.Parameters.AddWithValue("room", Room);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return (0, 0);
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        public async Task<string?> GrantAsync(string playerId, string grantKey)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status FROM settlement.grant WHERE room_id=@room AND player_id=@player AND grant_key=@key",
                connection);
            command.Parameters.AddWithValue("room", Room);
            command.Parameters.AddWithValue("player", playerId);
            command.Parameters.AddWithValue("key", grantKey);
            return (string?)await command.ExecuteScalarAsync();
        }

        public async Task<long> ResumeReadyCountAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM game.resume_ready WHERE room_id=@room", connection);
            command.Parameters.AddWithValue("room", Room);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<List<string>> OutboxEventsAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            var events = new List<string>();
            await using var command = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id LIKE @prefix", connection);
            command.Parameters.AddWithValue("prefix", Room + "/%");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) events.Add(reader.GetString(0));
            return events;
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_sync08_timeout_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
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
