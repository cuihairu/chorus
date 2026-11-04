using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-08 acceptance (恢复边界): a control participant whose current-generation session
/// stands on a paused round reports resume-ready; the store resolves the paused round from the
/// streamer's open live session and fences the report on the session being the streamer's
/// CURRENT session, so an old session's readiness never counts. The resume lifts the round
/// back to Running at the frozen head only once the declared ready count is met — below it the
/// resume is refused (未达人数不恢复) and the freeze holds. The flip is one receipt
/// transaction: a duplicate resume reports the already-running round, never a second tick loop.
/// </summary>
public sealed class RedesignSync08ResumeStoreTests
{
    private const string OfflinePolicy = "ContinueToEnd|30|120|2|Buffer"; // MaxPause=120, Ready=2

    [Fact]
    public async Task ReadyFromTheCurrentSessionsCountAndTheResumeLiftsAtTheFrozenHead()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        // The ready threshold is 2; both control participants report from their current sessions.
        var hostReady = await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Host.StreamerId, arrange.Host.SessionId, "ready-host");
        Assert.True(hostReady.Paused);
        Assert.Equal(1, hostReady.ReadyCount);
        Assert.Equal(2, hostReady.RequiredReadyCount);
        var guestReady = await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Guest.StreamerId, arrange.Guest.SessionId, "ready-guest");
        Assert.Equal(2, guestReady.ReadyCount);

        // The resume lifts the room back to Running at the frozen head (tick 0 / v1), writes
        // exactly one round.resumed outbox, and spends the ready rows in the same transaction.
        var resumed = await arrange.RoomStore.ResumeRoundAsync("tenant-a", "game-a", Arrange.Room,
            "game-1", arrange.Epoch, 0, 1, "resume-1");
        Assert.Equal("Running", resumed.Status);
        Assert.Equal(0, resumed.RoundGeneration);
        Assert.Equal(0, await arrange.RoomHeadTickAsync());
        Assert.Equal(1, await arrange.RoomHeadVersionAsync());
        Assert.Equal(0, await arrange.ResumeReadyCountAsync());
        var outbox = await arrange.OutboxEventsAsync();
        Assert.Single(outbox.Where(e => e == "round.resumed"));
    }

    [Fact]
    public async Task ABelowThresholdResumeIsRefusedAndTheFreezeHolds()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Host.StreamerId, arrange.Host.SessionId, "ready-host");
        // Only one of the two required participants reported — the resume is refused and the
        // room stays Paused at the frozen head (未达人数不恢复).
        await Assert.ThrowsAsync<RoundAdmissionException>(() =>
            arrange.RoomStore.ResumeRoundAsync("tenant-a", "game-a", Arrange.Room,
                "game-1", arrange.Epoch, 0, 1, "resume-short"));
        Assert.Equal("Paused", await arrange.RoomStatusAsync());
        Assert.Equal(1, await arrange.ResumeReadyCountAsync());
    }

    [Fact]
    public async Task AReadyFromAnOldSessionNeverCounts()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        // A second login displaces the host's session — the first session is no longer current.
        await arrange.IdentityStore.LoginAsync(arrange.Host.StreamerId, "gateway-2",
            "connection-2", RandomNumberGenerator.GetBytes(32));
        var error = await Assert.ThrowsAsync<RoundAdmissionException>(() =>
            arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
                arrange.Host.StreamerId, arrange.Host.SessionId, "ready-stale"));
        Assert.Contains("current session", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await arrange.ResumeReadyCountAsync());
    }

    [Fact]
    public async Task AStreamerWithNoPausedRoundIsACleanNoOp()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        // A streamer that holds no paused-round participation reports nothing.
        var ready = await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Bystander.StreamerId, arrange.Bystander.SessionId, "ready-bystander");
        Assert.False(ready.Paused);
        Assert.Equal(0, ready.ReadyCount);
        Assert.Equal(0, await arrange.ResumeReadyCountAsync());
    }

    [Fact]
    public async Task ADuplicateResumeReportsTheRunningRoundAndNeverFlipsTwice()
    {
        await using var arrange = await Arrange.PausedRoundAsync();
        await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Host.StreamerId, arrange.Host.SessionId, "ready-host");
        await arrange.RoomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            arrange.Guest.StreamerId, arrange.Guest.SessionId, "ready-guest");
        await arrange.RoomStore.ResumeRoundAsync("tenant-a", "game-a", Arrange.Room,
            "game-1", arrange.Epoch, 0, 1, "resume-1");
        // The same operation retried after a lost reply replays the committed transition — the
        // room is reported Running again, never a second flip to a fresh tick loop.
        var duplicate = await arrange.RoomStore.ResumeRoundAsync("tenant-a", "game-a", Arrange.Room,
            "game-1", arrange.Epoch, 0, 1, "resume-1");
        Assert.True(duplicate.Duplicate);
        Assert.Equal("Running", duplicate.Status);
        Assert.Equal("Running", await arrange.RoomStatusAsync());
    }

    private sealed class Arrange : IAsyncDisposable
    {
        public const string Room = "sync08-room";
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public (string StreamerId, string SessionId) Host { get; init; }
        public (string StreamerId, string SessionId) Guest { get; init; }
        public (string StreamerId, string SessionId) Bystander { get; init; }
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
                    Control = new RedesignControlStore(database.ConnectionString)
                };
                // Mount three streamers: host and guest join the round; the bystander holds no
                // room participation (the no-paused-round no-op case).
                var host = await MountAsync(stores, "sync08-open-host", "live-key-host");
                var guest = await MountAsync(stores, "sync08-open-guest", "live-key-guest");
                var bystander = await MountAsync(stores, "sync08-open-bystander", "live-key-bystander");

                var claim = await stores.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", Room,
                    "game-1", 60);
                await stores.RoomStore.StartRoundAsync("tenant-a", "game-a", Room, "game-1", claim.Epoch,
                    [new RoundParticipant(host.BindingId, host.StreamerId, "Host"),
                     new RoundParticipant(guest.BindingId, guest.StreamerId, "Guest")],
                    "1.0.0", "cfg-1", "pkg-1", "start-sync08");
                await stores.RoomStore.PrepareRoundAsync("tenant-a", "game-a", Room, "game-1", claim.Epoch,
                    RandomNumberGenerator.GetBytes(64), RandomNumberGenerator.GetBytes(32), 1,
                    "prepare-sync08", tickMs: 25, recoveryMode: "DeterministicReplay",
                    offlinePolicy: OfflinePolicy);
                await stores.RoomStore.ActivateRoundAsync("tenant-a", "game-a", Room, "game-1",
                    claim.Epoch, "activate-sync08");
                await stores.RoomStore.PauseRoundAsync("tenant-a", "game-a", Room, "game-1", claim.Epoch,
                    "all control sessions offline past the declared grace", "pause-sync08", 0, 1);
                return new Arrange
                {
                    Database = database,
                    RoomStore = stores.RoomStore,
                    IdentityStore = stores.IdentityStore,
                    BindingStore = stores.BindingStore,
                    Control = stores.Control,
                    Host = (host.StreamerId, host.SessionId),
                    Guest = (guest.StreamerId, guest.SessionId),
                    Bystander = (bystander.StreamerId, bystander.SessionId),
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
            Arrange stores, string openId, string liveKey)
        {
            var streamer = await stores.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                "app-1", openId);
            var live = await stores.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                "simulator", "app-1", "platform-room", liveKey);
            var session = await stores.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                $"connection-{openId}", RandomNumberGenerator.GetBytes(32));
            var binding = await stores.BindingStore.StartGameplayAsync("tenant-a", live,
                session.SessionId, "game-a", $"mount-{openId}");
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

        public async Task<long> RoomHeadTickAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT last_tick FROM game.room WHERE room_id=@room", connection);
            command.Parameters.AddWithValue("room", Room);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<long> RoomHeadVersionAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT state_version FROM game.room WHERE room_id=@room", connection);
            command.Parameters.AddWithValue("room", Room);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
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
        private readonly string _name = "chorus_sync08_resume_" + Guid.NewGuid().ToString("N");
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
            await using (var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin))
                await drop.ExecuteNonQueryAsync();
        }
    }
}
