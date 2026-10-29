using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Chorus.Protocol.Redesign;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Idempotent settlement and round finalization (architecture 9.3): every grant of the
/// immutable manifest lands exactly one ledger line, a settler crash on the Nth reward only
/// ever retries the incomplete grants, and FinalizeRound atomically settles the room, frees
/// the mounts and withdraws the ownership so the next round can open. The settle queue
/// (FIX-SETTLE-01) lists exactly the rooms whose committed result still has unapplied
/// grants, one bounded page in room order. Profile writes cannot override ledger-backed
/// assets (architecture 9.2, AT18).
/// </summary>
public sealed class RedesignSettlementStoreTests
{
    [Fact]
    public async Task GrantsSettleIdempotentlyAndCrashOnNthRetriesOnlyTheIncomplete()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");

        // The settler dies on the second reward: the first grant stays committed, the
        // second rolls back with its transaction, the third was never touched (AT20).
        var crashing = new RedesignSettlementStore(arrange.Database.ConnectionString,
            ordinal => { if (ordinal == 2) throw new InvalidOperationException("settler crash"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            crashing.CommitResultAsync("tenant-a", "game-a", "room-1", "settle-op-1"));
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var grants = new NpgsqlCommand("""
                SELECT player_id,status FROM settlement.grant WHERE room_id='room-1' ORDER BY player_id
                """, connection);
            await using (var reader = await grants.ExecuteReaderAsync())
            {
                var rows = new List<(string, string)>();
                while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
                Assert.Equal([("p1", "Granted"), ("p2", "Planned"), ("p3", "Planned")], rows);
            }
            await using var ledger = new NpgsqlCommand(
                "SELECT count(*) FROM player.ledger", connection);
            Assert.Equal(1L, Convert.ToInt64(await ledger.ExecuteScalarAsync()));
        }

        // The retry continues the manifest: every grant lands exactly one ledger line and
        // one projection row; a repeated send reports zero pending work.
        var store = new RedesignSettlementStore(arrange.Database.ConnectionString);
        var progress = await store.CommitResultAsync("tenant-a", "game-a", "room-1", "settle-op-1");
        Assert.Equal(3, progress.Total);
        Assert.Equal(3, progress.Granted);
        Assert.Equal(0, progress.Pending);
        var repeated = await store.CommitResultAsync("tenant-a", "game-a", "room-1", "settle-op-2");
        Assert.True(repeated.Duplicate);
        Assert.Equal(0, repeated.Pending);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var ledger = new NpgsqlCommand("""
                SELECT count(DISTINCT operation_id),count(*) FROM player.ledger
                WHERE reason LIKE 'settlement:room-1/%'
                """, connection))
            {
                await using var reader = await ledger.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(3L, reader.GetInt64(0));
                Assert.Equal(3L, reader.GetInt64(1));
            }
            await using var balances = new NpgsqlCommand("""
                SELECT player_id,balance FROM player.asset WHERE asset_id='points' ORDER BY player_id
                """, connection);
            await using (var assetReader = await balances.ExecuteReaderAsync())
            {
                var rows = new List<(string, long)>();
                while (await assetReader.ReadAsync()) rows.Add((assetReader.GetString(0), assetReader.GetInt64(1)));
                Assert.Equal([("p1", 100L), ("p2", 30L), ("p3", 30L)], rows);
            }
        }
        // The board orders deterministically: score descending, player id ascending on ties.
        var board = await store.ReadLeaderboardAsync("tenant-a", "game-a", "points", 10);
        Assert.Equal([("p1", 100L), ("p2", 30L), ("p3", 30L)],
            board.Select(entry => (entry.PlayerId, entry.Score)).ToArray());
        await Assert.ThrowsAsync<SettlementRejectedException>(() =>
            store.CommitResultAsync("tenant-a", "game-a", "room-none", "settle-none"));
    }

    [Fact]
    public async Task TheSettleQueueListsOnlyRoomsWithUnappliedGrantsPageByPage()
    {
        await using var arrange = await Arrange.CreateAsync();
        var store = new RedesignSettlementStore(arrange.Database.ConnectionString);

        // Four rooms: one still running without a result, one whose settler died after the
        // first grant, one with a fresh untouched manifest, one settled all the way
        // through. The queue names exactly the middle two (FIX-SETTLE-01).
        await arrange.AdmittedAsync("room-live", "settle-open-9", "settle-live-9");
        await arrange.FinalFrameAsync("room-pending-1", "settle-open-7", "settle-live-7");
        var crashing = new RedesignSettlementStore(arrange.Database.ConnectionString,
            ordinal => { if (ordinal == 2) throw new InvalidOperationException("settler crash"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            crashing.CommitResultAsync("tenant-a", "game-a", "room-pending-1", "settle-op-1"));
        await arrange.FinalFrameAsync("room-pending-2", "settle-open-8", "settle-live-8");
        await arrange.FinalFrameAsync("room-done", "settle-open-6", "settle-live-6");
        await store.CommitResultAsync("tenant-a", "game-a", "room-done", "settle-op-done");
        await store.FinalizeRoundAsync("tenant-a", "game-a", "room-done", "fin-done");

        var queue = await store.ListPendingSettlementsAsync("tenant-a", "game-a", null,
            RedesignSettlementStore.DiscoveryPageLimit);
        Assert.Equal(["room-pending-1", "room-pending-2"],
            queue.Select(room => room.RoomId).ToArray());
        // A settler that died mid-queue is rediscovered with its progress visible.
        Assert.Equal(1L, queue[0].ResultVersion);
        Assert.Equal(3, queue[0].GrantsTotal);
        Assert.Equal(1, queue[0].GrantsGranted);
        Assert.Equal(2, queue[0].GrantsPending);
        Assert.Equal(0, queue[1].GrantsGranted);
        Assert.Equal(3, queue[1].GrantsPending);

        // One room per page: the walk resumes strictly past the cursor, every room
        // exactly once, and a bounded page never answers the whole history.
        var seen = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = await store.ListPendingSettlementsAsync("tenant-a", "game-a", cursor, 1);
            if (page.Count == 0) break;
            seen.AddRange(page.Select(room => room.RoomId));
            cursor = page[^1].RoomId;
        }
        Assert.Equal(["room-pending-1", "room-pending-2"], seen);

        // Completing a manifest takes its room off the queue: discovery drives completion,
        // and a cursor past the last room answers an honest empty page.
        await store.CommitResultAsync("tenant-a", "game-a", "room-pending-2", "settle-op-2");
        var after = await store.ListPendingSettlementsAsync("tenant-a", "game-a", null,
            RedesignSettlementStore.DiscoveryPageLimit);
        Assert.Equal(["room-pending-1"], after.Select(room => room.RoomId).ToArray());
        Assert.Empty(await store.ListPendingSettlementsAsync("tenant-a", "game-a",
            "room-zz", 1));

        // Page sizes outside the clamp refuse visibly instead of answering unbounded.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ListPendingSettlementsAsync("tenant-a", "game-a", null, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ListPendingSettlementsAsync("tenant-a", "game-a", null,
                RedesignSettlementStore.DiscoveryPageLimit + 1));
    }

    [Fact]
    public async Task FinalizeRoundIsFencedIdempotentAndFreesTheNextRound()
    {
        await using var arrange = await Arrange.CreateAsync();
        var roomA = await arrange.FinalFrameAsync("room-1", "settle-open-2", "settle-live-2");
        // room-2 carries a routed-but-unconsumed gift from its Running days: the input
        // keeps the round from finalizing until it reaches a terminal disposition.
        var roomB = await arrange.FinalFrameAsync("room-2", "settle-open-3", "settle-live-3",
            pendingInput: true);
        var roomC = await arrange.FinalFrameAsync("room-3", "settle-open-4", "settle-live-4");
        await arrange.AdmittedAsync("room-4", "settle-open-5", "settle-live-5");

        var store = new RedesignSettlementStore(arrange.Database.ConnectionString);
        // Preconditions are visible rejections, never silent settlements: a Running room,
        // an unsolved manifest, and a settled manifest with an undisposed input.
        await Assert.ThrowsAsync<SettlementRejectedException>(() =>
            store.FinalizeRoundAsync("tenant-a", "game-a", "room-4", "finalize-4"));
        await Assert.ThrowsAsync<SettlementRejectedException>(() =>
            store.FinalizeRoundAsync("tenant-a", "game-a", "room-3", "finalize-3"));
        await store.CommitResultAsync("tenant-a", "game-a", "room-2", "settle-2");
        await Assert.ThrowsAsync<SettlementRejectedException>(() =>
            store.FinalizeRoundAsync("tenant-a", "game-a", "room-2", "finalize-2"));

        await store.CommitResultAsync("tenant-a", "game-a", "room-1", "settle-1");
        var finalized = await store.FinalizeRoundAsync("tenant-a", "game-a", "room-1", "finalize-1");
        Assert.Equal("Settled", finalized.Status);
        Assert.Equal(roomA.Epoch + 1, finalized.Epoch);
        var retry = await store.FinalizeRoundAsync("tenant-a", "game-a", "room-1", "finalize-1");
        Assert.True(retry.Duplicate);
        await Assert.ThrowsAsync<SettlementRejectedException>(() =>
            store.FinalizeRoundAsync("tenant-a", "game-a", "room-1", "finalize-1b"));

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var room = new NpgsqlCommand("""
                SELECT status FROM game.room WHERE room_id='room-1'
                """, connection);
            Assert.Equal("Settled", await room.ExecuteScalarAsync());
            await using var participation = new NpgsqlCommand(
                "SELECT state FROM game.participation WHERE room_id='room-1'", connection);
            Assert.Equal("Closed", await participation.ExecuteScalarAsync());
            await using (var binding = new NpgsqlCommand(
                "SELECT current_room_id,round_generation FROM live.binding WHERE binding_id=@binding", connection))
            {
                binding.Parameters.AddWithValue("binding", roomA.BindingId);
                await using var reader = await binding.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using var ownership = new NpgsqlCommand(
                "SELECT status,epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'", connection);
            await using (var ownerReader = await ownership.ExecuteReaderAsync())
            {
                Assert.True(await ownerReader.ReadAsync());
                Assert.Equal("Released", ownerReader.GetString(0));
                Assert.Equal(roomA.Epoch + 1, ownerReader.GetInt64(1));
            }
            await using var outbox = new NpgsqlCommand(
                "SELECT kind FROM infra.outbox WHERE effect_id='room-1/0/settle'", connection);
            Assert.Equal("round.settled", await outbox.ExecuteScalarAsync());
        }

        // The freed mount opens the next round: a fresh claim for a new room under a new
        // epoch accepts the same binding, whose generation advances (AT20: 能够开启下一局).
        var reclaim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1-next",
            "gs-1", 60);
        await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1-next", "gs-1", reclaim.Epoch,
            [new RoundParticipant(roomA.BindingId, roomA.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-next-1");
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var binding = new NpgsqlCommand(
                "SELECT current_room_id,round_generation FROM live.binding WHERE binding_id=@binding", connection))
            {
                binding.Parameters.AddWithValue("binding", roomA.BindingId);
                await using var reader = await binding.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("room-1-next", reader.GetString(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
        }
    }

    [Fact]
    public async Task ProfileWritesCannotOverrideLedgerBackedAssets()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.FinalFrameAsync("room-1", "settle-open-6", "settle-live-6");
        var store = new RedesignSettlementStore(arrange.Database.ConnectionString);
        await store.CommitResultAsync("tenant-a", "game-a", "room-1", "settle-1");

        // A profile document may claim anything — the asset balance is owned by the ledger
        // and no JSON write path reaches it (AT18: 无 JSON 覆盖奖励).
        var players = new RedesignPlayerStore(arrange.Database.ConnectionString);
        var identity = new OperationIdentity("tenant-a", "game-a", "profile.patch", "p1", "profile-1");
        var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
            Encoding.UTF8.GetBytes("""{"assets":{"points":999999}}"""));
        var (value, duplicate) = await players.PatchProfileAsync(identity, fingerprint, 0, 1,
            """{"assets":{"points":999999}}""");
        Assert.False(duplicate);
        Assert.Equal(1, value.Version);
        Assert.Equal(100, (await players.GetAssetAsync("tenant-a", "game-a", "p1", "points")).Balance);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand(
                "SELECT count(*) FROM player.ledger WHERE player_id='p1'", connection);
            Assert.Equal(1L, Convert.ToInt64(await ledger.ExecuteScalarAsync()));
        }
        var board = await store.ReadLeaderboardAsync("tenant-a", "game-a", "points", 10);
        Assert.Equal(100L, board.Single(entry => entry.PlayerId == "p1").Score);
    }

    [Fact]
    public async Task ConcurrentGrantsSerializeOnTheAccountRow()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.FinalFrameAsync("room-1", "settle-open-7", "settle-live-7");
        await arrange.FinalFrameAsync("room-2", "settle-open-8", "settle-live-8");
        var store = new RedesignSettlementStore(arrange.Database.ConnectionString);

        // Two rounds settle the same players at the same time: the account row serializes
        // the balance moves, every grant lands exactly one line and the board sums both
        // rounds (AT18: 并发资产增减).
        await Task.WhenAll(
            store.CommitResultAsync("tenant-a", "game-a", "room-1", "settle-race-1"),
            store.CommitResultAsync("tenant-a", "game-a", "room-2", "settle-race-2"));

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var ledger = new NpgsqlCommand("""
                SELECT count(DISTINCT operation_id),count(*) FROM player.ledger
                WHERE player_id='p1' AND reason LIKE 'settlement:room-%'
                """, connection))
            {
                await using var reader = await ledger.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
            await using var balance = new NpgsqlCommand("""
                SELECT balance FROM player.asset WHERE asset_id='points' AND player_id='p1'
                """, connection);
            Assert.Equal(200L, Convert.ToInt64(await balance.ExecuteScalarAsync()));
        }
        var board = await store.ReadLeaderboardAsync("tenant-a", "game-a", "points", 10);
        Assert.Equal([("p1", 200L), ("p2", 60L), ("p3", 60L)],
            board.Select(entry => (entry.PlayerId, entry.Score)).ToArray());
    }

    /// <summary>Test arrange: stores plus admitted rooms with final frames and manifests.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignFrameStore FrameStore = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public RedesignInputEventStore EventStore = null!;
        public RedesignRoundInputStore InputStore = null!;

        public static async Task<Arrange> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                return new Arrange
                {
                    Database = database,
                    FrameStore = new RedesignFrameStore(database.ConnectionString),
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString),
                    EventStore = new RedesignInputEventStore(database.ConnectionString),
                    InputStore = new RedesignRoundInputStore(database.ConnectionString)
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public sealed record Room(string RoomId, string BindingId, string StreamerId, long Epoch);

        /// <summary>Mounts a binding, admits the room and activates it (tick 0, version 1).</summary>
        public async Task<Room> AdmittedAsync(string roomId, string openId, string liveKey)
        {
            var streamer = await IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                "app-1", openId + "-open");
            var live = await BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                "simulator", "app-1", "platform-room", liveKey);
            var session = await IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                "connection-" + openId, RandomNumberGenerator.GetBytes(32));
            var binding = await BindingStore.StartGameplayAsync("tenant-a", live, session.SessionId,
                "game-a", "mount-" + openId);
            var claim = await Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", roomId,
                "gs-1", 60);
            await RoomStore.StartRoundAsync("tenant-a", "game-a", roomId, "gs-1", claim.Epoch,
                [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                "1.0.0", "cfg-1", "pkg-1", "start-" + roomId);
            await RoomStore.PrepareRoundAsync("tenant-a", "game-a", roomId, "gs-1", claim.Epoch,
                [1, 2, 3], [4, 5, 6], 1, "prepare-" + roomId);
            await RoomStore.ActivateRoundAsync("tenant-a", "game-a", roomId, "gs-1", claim.Epoch,
                "activate-" + roomId);
            return new Room(roomId, binding.BindingId, streamer.StreamerId, claim.Epoch);
        }

        /// <summary>Commits a final frame carrying the three-grant manifest, room → Finishing.</summary>
        public async Task<Room> FinalFrameAsync(string roomId, string openId, string liveKey,
            bool pendingInput = false)
        {
            var room = await AdmittedAsync(roomId, openId, liveKey);
            if (pendingInput) await RoutePendingGiftAsync(liveKey);
            await FrameStore.CommitFrameAsync("tenant-a", "game-a", roomId,
                new FrameCommit("final-" + roomId, "gs-1", room.Epoch, 0, 1, [], [9, 9, 9], [],
                    "1.0.0", "cfg-1", Final: true, ResultJson: """{"winner":"red"}""",
                    PlanHash: SHA256.HashData(Encoding.UTF8.GetBytes("""{"winner":"red"}""")),
                    Grants:
                    [
                        new PlannedGrant("p1", "round-score", roomId + "/grant/p1", "points", 100),
                        new PlannedGrant("p2", "round-score", roomId + "/grant/p2", "points", 30),
                        new PlannedGrant("p3", "round-score", roomId + "/grant/p3", "points", 30)
                    ]));
            return room;
        }

        /// <summary>Routes one gift into the room's inbox and leaves it Pending.</summary>
        public async Task RoutePendingGiftAsync(string liveKey)
        {
            await EventStore.IngestAsync("simulator", "app-1", "route-batch-" + liveKey,
            [
                new PlatformEventInput("gift-" + liveKey, RedesignInputEventStore.Durable,
                    liveKey, """{"kind":"gift","amount":5}""")
            ]);
            await InputStore.RouteEventsAsync("simulator", "app-1", "route-op-" + liveKey, 50);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_settlement_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
