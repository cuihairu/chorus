using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignFrameStoreTests
{
    [Fact]
    public async Task FrameCommitsAreFencedIdempotentAndAdvanceTheContiguousWatermark()
    {
        await using var arrange = await Arrange.RoundWithInboxAsync("gift-1", "gift-2", "gift-3");
        var ids = await arrange.PendingEventIds();

        var inbox = await arrange.FrameStore.ReadInboxAsync("tenant-a", "game-a", "room-1", 50);
        Assert.Equal(0, inbox.Watermark);
        Assert.Equal(3, inbox.Items.Count);
        Assert.Equal([1L, 2L, 3L], inbox.Items.Select(item => item.Sequence).ToArray());
        Assert.All(inbox.Items, item => Assert.Equal(1, item.RouteVersion));
        Assert.All(inbox.Items, item => Assert.StartsWith("""{"kind": "gift", "amount": """, item.PayloadJson));

        var first = await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-1", "gs-1", arrange.Epoch, 0, 0, [ids[0], ids[1]],
                [1, 2, 3],
                [new FrameEffect("score", """{"player":"p1","points":10}"""),
                    new FrameEffect("score", """{"player":"p2","points":5}""")],
                "1.0.0", "cfg-1"));
        Assert.False(first.Duplicate);
        Assert.Equal(1, first.Tick);
        Assert.Equal(1, first.StateVersion);
        Assert.Equal(2, first.Watermark);

        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var frame = new NpgsqlCommand("""
                SELECT commit_id,jsonb_array_length(input_refs),state_hash,jsonb_array_length(effects)
                FROM game.frame WHERE room_id='room-1' AND tick_id=1
                """, connection))
            await using (var reader = await frame.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("commit-1", reader.GetString(0));
                Assert.Equal(2, reader.GetInt32(1));
                Assert.Equal(new byte[] { 1, 2, 3 }, reader.GetFieldValue<byte[]>(2));
                Assert.Equal(2, reader.GetInt32(3));
            }
            await using (var consumed = new NpgsqlCommand("""
                SELECT count(*) FILTER (WHERE state='Consumed'),count(*) FILTER (WHERE state='Pending')
                FROM input.delivery WHERE consumer_scope='room:room-1'
                """, connection))
            await using (var reader = await consumed.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using (var cursor = new NpgsqlCommand("""
                SELECT next_delivery_sequence,consumed_contiguous_sequence FROM input.cursor
                WHERE consumer_scope='room:room-1'
                """, connection))
            await using (var reader = await cursor.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(4L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
            await using (var room = new NpgsqlCommand(
                "SELECT status,last_tick,state_version,state_hash FROM game.room WHERE room_id='room-1'",
                connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Running", reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(new byte[] { 1, 2, 3 }, reader.GetFieldValue<byte[]>(3));
            }
            await using (var outbox = new NpgsqlCommand("""
                SELECT effect_id,kind FROM infra.outbox
                WHERE effect_id IN ('room-1/1/0','room-1/1/1') ORDER BY effect_id
                """, connection))
            await using (var reader = await outbox.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("score", reader.GetString(1));
                Assert.True(await reader.ReadAsync());
                Assert.Equal("score", reader.GetString(1));
            }
        }

        // The exact commit retry replays the stored receipt; a different frame under the same
        // commit id conflicts; a stale prev tick and a stale fence are both visible rejects.
        var retry = await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-1", "gs-1", arrange.Epoch, 0, 0, [ids[0], ids[1]],
                [1, 2, 3],
                [new FrameEffect("score", """{"player":"p1","points":10}"""),
                    new FrameEffect("score", """{"player":"p2","points":5}""")],
                "1.0.0", "cfg-1"));
        Assert.True(retry.Duplicate);
        Assert.Equal(1, retry.Tick);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-1", "gs-1", arrange.Epoch, 0, 0, [ids[0], ids[1]],
                [9, 9], [], "1.0.0", "cfg-1")));
        await Assert.ThrowsAsync<FrameConflictException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-late", "gs-1", arrange.Epoch, 0, 0, [ids[2]],
                [4], [], "1.0.0", "cfg-1")));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-stale", "gs-1", arrange.Epoch + 3, 1, 1, [ids[2]],
                [4], [], "1.0.0", "cfg-1")));

        var second = await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-2", "gs-1", arrange.Epoch, 1, 1, [ids[2]],
                [4], [], "1.0.0", "cfg-1"));
        Assert.Equal(2, second.Tick);
        Assert.Equal(2, second.StateVersion);
        Assert.Equal(3, second.Watermark);

        // The watermark never crosses a hole: consuming sequence 5 while 4 pends keeps the
        // prefix at 3; closing the hole releases the whole prefix.
        await arrange.Route("gift-4", "gift-5");
        var later = (await arrange.FrameStore.ReadInboxAsync("tenant-a", "game-a", "room-1", 50)).Items;
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-3", "gs-1", arrange.Epoch, 2, 2,
                [later[1].EventId], [5], [], "1.0.0", "cfg-1"));
        var holed = await arrange.FrameStore.ReadInboxAsync("tenant-a", "game-a", "room-1", 50);
        Assert.Equal(3, holed.Watermark);
        Assert.Single(holed.Items);
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-4", "gs-1", arrange.Epoch, 3, 3,
                [holed.Items[0].EventId], [6], [], "1.0.0", "cfg-1"));
        var drained = await arrange.FrameStore.ReadInboxAsync("tenant-a", "game-a", "room-1", 50);
        Assert.Equal(5, drained.Watermark);
        Assert.Empty(drained.Items);

        // An input already consumed by another frame is rejected visibly, as is a ref that
        // never belonged to this round's inbox and a duplicated ref.
        await Assert.ThrowsAsync<FrameConflictException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-eaten", "gs-1", arrange.Epoch, 4, 4, [ids[0]],
                [7], [], "1.0.0", "cfg-1")));
        await Assert.ThrowsAsync<FrameConflictException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-foreign", "gs-1", arrange.Epoch, 4, 4, ["event-none"],
                [7], [], "1.0.0", "cfg-1")));
        await arrange.Route("gift-6");
        var tail = (await arrange.FrameStore.ReadInboxAsync("tenant-a", "game-a", "room-1", 50)).Items[0];
        await Assert.ThrowsAsync<FrameConflictException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-dupref", "gs-1", arrange.Epoch, 4, 4, [tail.EventId, tail.EventId],
                [7], [], "1.0.0", "cfg-1")));
    }

    [Fact]
    public async Task FullCheckpointAndFinalFrameAreAtomicWithTheFrame()
    {
        await using var checkpoint = await Arrange.RoundWithInboxAsync("snap-1");
        var committed = await checkpoint.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("snap-commit-1", "gs-1", checkpoint.Epoch, 0, 0,
                [(await checkpoint.PendingEventIds())[0]], [1, 1], [], "1.0.0", "cfg-1",
                SnapshotPayload: [7, 7, 7], SnapshotHash: [1, 1], SnapshotSchemaVersion: 1));
        Assert.Equal(1, committed.Tick);
        await using (var connection = new NpgsqlConnection(checkpoint.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var snapshot = new NpgsqlCommand("""
                SELECT payload,payload_hash,verified FROM game.snapshot
                WHERE room_id='room-1' AND tick_id=1 AND schema_version=1
                """, connection))
            await using (var reader = await snapshot.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(new byte[] { 7, 7, 7 }, reader.GetFieldValue<byte[]>(0));
                Assert.Equal(new byte[] { 1, 1 }, reader.GetFieldValue<byte[]>(1));
                Assert.False(reader.GetBoolean(2));
            }
        }

        // A checkpoint that fails inside the frame transaction rolls the whole tick back —
        // the store never degrades to an unrecoverable checkpoint (architecture 8.2).
        await using var rollback = await Arrange.RoundWithInboxAsync("snap-2");
        var rollbackIds = await rollback.PendingEventIds();
        await using (var connection = new NpgsqlConnection(rollback.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var collide = new NpgsqlCommand("""
                INSERT INTO game.snapshot(room_id,tick_id,schema_version,payload,payload_hash,verified)
                VALUES ('room-1',1,1,@payload,@hash,false)
                """, connection))
            {
                collide.Parameters.AddWithValue("payload", new byte[] { 1 });
                collide.Parameters.AddWithValue("hash", new byte[] { 1 });
                await collide.ExecuteNonQueryAsync();
            }
        }
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => rollback.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("snap-commit-2", "gs-1", rollback.Epoch, 0, 0, [rollbackIds[0]],
                [2, 2], [], "1.0.0", "cfg-1",
                SnapshotPayload: [8, 8], SnapshotHash: [2, 2], SnapshotSchemaVersion: 1)));
        Assert.Equal("23505", conflict.SqlState);
        await using (var connection = new NpgsqlConnection(rollback.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var frame = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id='room-1'", connection))
                Assert.Equal(0L, Convert.ToInt64(await frame.ExecuteScalarAsync()));
            await using (var pending = new NpgsqlCommand(
                "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-1' AND state='Pending'",
                connection))
                Assert.Equal(1L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
            await using (var room = new NpgsqlCommand(
                "SELECT last_tick,state_version FROM game.room WHERE room_id='room-1'", connection))
            await using (var reader = await room.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }
        }
        // The failed attempt left no receipt either: the same commit id retries cleanly as a
        // regular deterministic tick.
        var recovered = await rollback.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("snap-commit-2", "gs-1", rollback.Epoch, 0, 0, [rollbackIds[0]],
                [2, 2], [], "1.0.0", "cfg-1"));
        Assert.False(recovered.Duplicate);
        Assert.Equal(1, recovered.Tick);

        // The final frame carries the result and the planned grant list, and moves the room
        // to Finishing; further battle ticks are rejected.
        await using var finale = await Arrange.RoundWithInboxAsync("final-1");
        var finalIds = await finale.PendingEventIds();
        var result = await finale.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("final-commit-1", "gs-1", finale.Epoch, 0, 0, [finalIds[0]],
                [3, 3], [], "1.0.0", "cfg-1",
                Final: true, ResultJson: """{"winner":"p1","score":[10,5]}""", PlanHash: [9, 9],
                Grants: [new PlannedGrant("p1", "champion", "grant-1"),
                    new PlannedGrant("p2", "consolation", "grant-2")]));
        Assert.Equal(1, result.Tick);
        await using (var connection = new NpgsqlConnection(finale.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var stored = new NpgsqlCommand("""
                SELECT final_frame,plan_hash,result::text FROM game.result
                WHERE room_id='room-1' AND result_version=1
                """, connection))
            await using (var reader = await stored.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(new byte[] { 9, 9 }, reader.GetFieldValue<byte[]>(1));
                Assert.Contains("p1", reader.GetString(2));
            }
            await using (var grants = new NpgsqlCommand("""
                SELECT player_id,grant_key,status,operation_id FROM settlement.grant
                WHERE room_id='room-1' ORDER BY player_id
                """, connection))
            await using (var reader = await grants.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(("p1", "champion", "Planned", "grant-1"),
                    (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(("p2", "consolation", "Planned", "grant-2"),
                    (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            await using (var room = new NpgsqlCommand(
                "SELECT status FROM game.room WHERE room_id='room-1'", connection))
                Assert.Equal(RedesignFrameStore.Finishing, await room.ExecuteScalarAsync());
        }
        var finalRetry = await finale.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("final-commit-1", "gs-1", finale.Epoch, 0, 0, [finalIds[0]],
                [3, 3], [], "1.0.0", "cfg-1",
                Final: true, ResultJson: """{"winner":"p1","score":[10,5]}""", PlanHash: [9, 9],
                Grants: [new PlannedGrant("p1", "champion", "grant-1"),
                    new PlannedGrant("p2", "consolation", "grant-2")]));
        Assert.True(finalRetry.Duplicate);
        await Assert.ThrowsAsync<FrameConflictException>(() => finale.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("final-commit-2", "gs-1", finale.Epoch, 1, 1, [],
                [4], [], "1.0.0", "cfg-1")));
    }

    [Fact]
    public async Task FrameCrashInjectionRollsBackAndRetryConverges()
    {
        await using var arrange = await Arrange.RoundWithInboxAsync("crash-1", "crash-2");
        var ids = await arrange.PendingEventIds();
        var armed = false;
        var crashing = new RedesignFrameStore(arrange.Database.ConnectionString,
            () =>
            {
                if (armed) throw new InvalidOperationException("simulated process death after the frame insert");
            });

        armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("crash-commit-1", "gs-1", arrange.Epoch, 0, 0, ids,
                [5, 5], [new FrameEffect("score", """{"player":"p1","points":1}""")], "1.0.0", "cfg-1")));
        armed = false;
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var frame = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id='room-1'", connection))
                Assert.Equal(0L, Convert.ToInt64(await frame.ExecuteScalarAsync()));
            await using (var pending = new NpgsqlCommand("""
                SELECT count(*) FROM input.delivery
                WHERE consumer_scope='room:room-1' AND state='Pending'
                """, connection))
                Assert.Equal(2L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
            await using (var cursor = new NpgsqlCommand("""
                SELECT consumed_contiguous_sequence FROM input.cursor WHERE consumer_scope='room:room-1'
                """, connection))
                Assert.Equal(0L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
            await using (var outbox = new NpgsqlCommand(
                "SELECT count(*) FROM infra.outbox WHERE effect_id LIKE 'room-1/1/%'", connection))
                Assert.Equal(0L, Convert.ToInt64(await outbox.ExecuteScalarAsync()));
        }

        var recovered = await crashing.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("crash-commit-1", "gs-1", arrange.Epoch, 0, 0, ids,
                [5, 5], [new FrameEffect("score", """{"player":"p1","points":1}""")], "1.0.0", "cfg-1"));
        Assert.False(recovered.Duplicate);
        Assert.Equal(1, recovered.Tick);
        Assert.Equal(2, recovered.Watermark);
        var duplicate = await crashing.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("crash-commit-1", "gs-1", arrange.Epoch, 0, 0, ids,
                [5, 5], [new FrameEffect("score", """{"player":"p1","points":1}""")], "1.0.0", "cfg-1"));
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, duplicate.Tick);
    }

    /// <summary>Test arrange shared by the frame facts: a Running room with routed inputs.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignFrameStore FrameStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignInputEventStore EventStore = null!;
        public RedesignRoundInputStore InputStore = null!;
        public long Epoch;

        public static async Task<Arrange> RoundWithInboxAsync(params string[] dedupKeys)
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                return await BuildAsync(database, dedupKeys);
            }
            catch
            {
                // A failed arrange must not leak its freshly created database behind.
                await database.DisposeAsync();
                throw;
            }
        }

        private static async Task<Arrange> BuildAsync(TemporaryDatabase database, string[] dedupKeys)
        {
            var arrange = new Arrange
            {
                Database = database,
                FrameStore = new RedesignFrameStore(database.ConnectionString),
                IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                BindingStore = new RedesignBindingStore(database.ConnectionString),
                Control = new RedesignControlStore(database.ConnectionString),
                RoomStore = new RedesignRoomStore(database.ConnectionString),
                EventStore = new RedesignInputEventStore(database.ConnectionString),
                InputStore = new RedesignRoundInputStore(database.ConnectionString)
            };
            var streamer = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
                "frame-open-1");
            var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                "simulator", "app-1", "platform-room", "live-key-frame");
            var session = await arrange.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                "connection-1", RandomNumberGenerator.GetBytes(32));
            var binding = await arrange.BindingStore.StartGameplayAsync("tenant-a", live, session.SessionId,
                "game-a", "mount-1");
            var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                "gs-1", 60);
            arrange.Epoch = claim.Epoch;
            await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
                [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                "1.0.0", "cfg-1", "pkg-1", "start-1");
            // Room activation (Preparing → Running with the initial snapshot) lands in M2-3;
            // the frame store under test observes a Running room.
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var activate = new NpgsqlCommand(
                    "UPDATE game.room SET status='Running' WHERE room_id='room-1'", connection);
                await activate.ExecuteNonQueryAsync();
            }
            await arrange.Route(dedupKeys);
            return arrange;
        }

        public async Task Route(params string[] dedupKeys)
        {
            if (dedupKeys.Length == 0) return;
            var batch = dedupKeys.Select(key => new PlatformEventInput(key,
                RedesignInputEventStore.Durable, "live-key-frame",
                $$"""{"kind":"gift","amount":{{Encoding.UTF8.GetByteCount(key)}}}""")).ToArray();
            await EventStore.IngestAsync("simulator", "app-1", "route-batch-" + Guid.NewGuid().ToString("N"),
                batch);
            await InputStore.RouteEventsAsync("simulator", "app-1",
                "route-op-" + Guid.NewGuid().ToString("N"), 50);
        }

        public async Task<List<string>> PendingEventIds()
        {
            var inbox = await FrameStore.ReadInboxAsync("tenant-a", "game-a", "room-1", 500);
            Assert.NotEmpty(inbox.Items);
            return inbox.Items.Select(item => item.EventId).ToList();
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m2_frames_" + Guid.NewGuid().ToString("N");
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
