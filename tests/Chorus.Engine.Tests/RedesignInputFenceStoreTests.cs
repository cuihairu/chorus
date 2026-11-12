using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-06, the receiver-side input-generation fence (architecture 6.4): a standing end
/// operation closes its round's input on every admission leg — the push leg refuses with a
/// visible error, the client leg the same way, the batch leg answers the terminal
/// out-of-round disposition — no matter what the room's status column says, and without
/// touching ownership: frames still commit, accepted events stay queryable, and a newer
/// round is a different, open input generation. The push leg also checks the caller's
/// round-generation claim against the binding row, so a stale snapshot's push for a closed
/// round never lands in a newer one.
/// </summary>
public sealed class RedesignInputFenceStoreTests
{
    private const string Payload = """{"kind":"gift","amount":50}""";

    [Fact]
    public async Task AStandingEndOperationClosesThePushLegWhileFramesStillCommit()
    {
        await using var arrange = await Arrange.RunningRoundAsync();
        var accepted = await arrange.PushAsync("fence-e1", claimGeneration: 1);
        Assert.Equal(1L, accepted.RouteVersion);
        Assert.False(accepted.Duplicate);

        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);

        // The room still shows Running — only the standing operation closes the input. The
        // refusal names the closed generation and the operation, and writes nothing.
        var refused = await Assert.ThrowsAsync<RoundAdmissionException>(
            () => arrange.PushAsync("fence-e2", claimGeneration: 1));
        Assert.Contains("generation 1", refused.Message);
        Assert.Contains("end-op-1", refused.Message);
        await arrange.AssertNothingWrittenAsync("fence-e2");

        // The settlement writes keep their authority: the frame commit that consumes the
        // accepted event still lands while the fence refuses new input, and the ownership
        // row never moved — the fence is the operation row, not a revoked owner.
        var frame = await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-fence-1", "gs-1", arrange.Epoch, 0, 0, ["fence-e1"],
                [1, 2, 3], [new FrameEffect("score", """{"player":"p1","points":5}""")],
                "1.0.0", "cfg-1"));
        Assert.Equal(1, frame.Tick);
        Assert.Equal(1, frame.Watermark);
        Assert.Equal("Active", await arrange.ScalarAsync(
            "SELECT status FROM control.ownership WHERE kind='room' AND entity_id='room-1'"));
        Assert.Equal(arrange.Epoch, Convert.ToInt64(await arrange.ScalarAsync(
            "SELECT epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'")));

        // The accepted event stays queryable and reaches its terminal disposition — consumed
        // by the frame above, exactly once.
        Assert.Equal("Consumed", await arrange.ScalarAsync(
            "SELECT state FROM input.delivery WHERE event_id='fence-e1'"));

        // A verbatim retry of the accepted event replays its receipt as Duplicate: the fence
        // closes new input, it does not unaccept history.
        var replay = await arrange.PushAsync("fence-e1", claimGeneration: 1);
        Assert.True(replay.Duplicate);
        Assert.Equal(accepted.Sequence, replay.Sequence);
    }

    [Fact]
    public async Task TheFenceRefusesRegardlessOfTheRoomStatusColumn()
    {
        await using var arrange = await Arrange.RunningRoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);

        // The status column says Finishing: the room's own frozen state refuses first.
        await arrange.ExecuteAsync("UPDATE game.room SET status='Finishing' WHERE room_id='room-1'");
        var byStatus = await Assert.ThrowsAsync<RoundAdmissionException>(
            () => arrange.PushAsync("fence-status", claimGeneration: 1));
        Assert.Contains("does not accept input", byStatus.Message);

        // The recovery shape: the status column flips back to Running — the persisted
        // operation is the fence now, and the refusal names the closed generation.
        await arrange.ExecuteAsync("UPDATE game.room SET status='Running' WHERE room_id='room-1'");
        var byOperation = await Assert.ThrowsAsync<RoundAdmissionException>(
            () => arrange.PushAsync("fence-recovered", claimGeneration: 1));
        Assert.Contains("closed by the end operation", byOperation.Message);
        await arrange.AssertNothingWrittenAsync("fence-status");
        await arrange.AssertNothingWrittenAsync("fence-recovered");

        // A bindingless push has no claim to check; the room's current generation (the same
        // join the begin derives from) carries the same rule.
        await Assert.ThrowsAsync<RoundAdmissionException>(() => arrange.InputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, "fence-scoped", "simulator",
            "app-1", null, 0, Payload));
        await arrange.AssertNothingWrittenAsync("fence-scoped");
    }

    [Fact]
    public async Task AnOldOperationCannotCloseANewerRound()
    {
        await using var arrange = await Arrange.RunningRoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);

        // The mount moves on: the first round aborts, the second starts a generation ahead.
        await arrange.NextRoundAsync("room-2");

        // The newer round is a different, open input generation: the operation that stands
        // for the first round closes nothing here, and the push lands with its own version.
        var accepted = await arrange.PushAsync("fence-next-1", claimGeneration: 2, roomId: "room-2");
        Assert.Equal(2L, accepted.RouteVersion);
        Assert.Equal("room-2", accepted.RoomId);
    }

    [Fact]
    public async Task AStaleRouteClaimNeverLandsInTheNewerRound()
    {
        await using var arrange = await Arrange.RunningRoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.NextRoundAsync("room-2");

        // The push names the new room but claims the old round's generation: the binding row
        // answers 2, the claim says 1 — refused, and nothing is written. Re-resolving the
        // route and retrying the same event id is the caller's only honest next step.
        var stale = await Assert.ThrowsAsync<RoundAdmissionException>(
            () => arrange.PushAsync("fence-stale-claim", claimGeneration: 1, roomId: "room-2"));
        Assert.Contains("round generation 1", stale.Message);
        Assert.Contains("holds 2", stale.Message);
        await arrange.AssertNothingWrittenAsync("fence-stale-claim");

        // The same push with the current generation's claim lands — the fence judges the
        // claim, not the caller.
        var fresh = await arrange.PushAsync("fence-fresh-claim", claimGeneration: 2, roomId: "room-2");
        Assert.Equal(2L, fresh.RouteVersion);
    }

    [Fact]
    public async Task TheClientAndBatchLegsMeetTheSameClosedGeneration()
    {
        await using var arrange = await Arrange.RunningRoundAsync();

        // The client leg before the operation: the command enters the round's inbox.
        await arrange.InputStore.AcceptClientInputAsync("tenant-a", "game-a", arrange.SessionId,
            "room-1", arrange.Epoch, "fence-command-1", """{"action":"leave"}""");

        // The batch leg before the operation: the platform event routes into the room.
        var batched = await arrange.RouteAsync("fence-batch-1");
        Assert.Equal("Routed", batched.Disposition);
        var routed = await arrange.DeliveryCountAsync();
        Assert.Equal(2, routed);

        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);

        // The client leg after: the same visible refusal the push leg meets.
        var command = await Assert.ThrowsAsync<RoundAdmissionException>(
            () => arrange.InputStore.AcceptClientInputAsync("tenant-a", "game-a", arrange.SessionId,
                "room-1", arrange.Epoch, "fence-command-2", """{"action":"leave"}"""));
        Assert.Contains("closed by the end operation", command.Message);

        // The batch leg after: a terminal out-of-round disposition naming the room — a
        // queryable terminal state, never a delivery, never a rolled-back batch.
        var item = await arrange.RouteAsync("fence-batch-2");
        Assert.Equal("OutOfRound", item.Disposition);
        Assert.Equal("room-1", item.RoomId);
        Assert.Null(item.Sequence);
        Assert.Equal("OutOfRound", await arrange.ScalarAsync(
            "SELECT disposition FROM input.event WHERE source_dedup_key='fence-batch-2'"));
        Assert.Equal("room-1", await arrange.ScalarAsync(
            "SELECT attributed_room_id FROM input.event WHERE source_dedup_key='fence-batch-2'"));
        Assert.Equal(routed, await arrange.DeliveryCountAsync());
    }

    /// <summary>One mounted binding, one Running round with a live lease, and the input/frame
    /// stores under test — the minimum every fence fact starts from.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public RedesignRoundInputStore InputStore = null!;
        public RedesignInputEventStore EventStore = null!;
        public RedesignFrameStore FrameStore = null!;
        public string SessionId = null!;
        public (string BindingId, string StreamerId) Binding { get; init; }
        public long Epoch { get; private set; }

        public async Task<RoomEventReceipt> PushAsync(string eventId, long claimGeneration,
            string roomId = "room-1") =>
            await InputStore.AcceptRoomEventAsync("tenant-a", "game-a", roomId, "gs-1", Epoch,
                eventId, "simulator", "app-1", Binding.BindingId, claimGeneration, Payload);

        /// <summary>The mount's next round: the first room aborts (releasing the pointer) and
        /// the named room starts a generation ahead with the same binding.</summary>
        public async Task NextRoundAsync(string roomId)
        {
            await RoomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1", Epoch,
                "round-over", "abort-room-1");
            var claim = await Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", roomId,
                "gs-1", 60);
            await RoomStore.StartRoundAsync("tenant-a", "game-a", roomId, "gs-1", claim.Epoch,
                [new RoundParticipant(Binding.BindingId, Binding.StreamerId, "Host")],
                "1.0.0", "cfg-1", "pkg-1", "start-" + roomId);
        }

        /// <summary>Ingests one durable platform event under the round's canonical live key and
        /// routes the batch; each ingest routes exactly one attributed event, so the window
        /// answers one item the fence fact reads back.</summary>
        public async Task<RouteEventsItem> RouteAsync(string dedupKey)
        {
            await EventStore.IngestAsync("simulator", "app-1", "ingest-" + dedupKey,
                [new PlatformEventInput(dedupKey, RedesignInputEventStore.Durable, CanonicalKey, Payload)]);
            var result = await InputStore.RouteEventsAsync("simulator", "app-1",
                "route-" + dedupKey, 50);
            return result.Events.Single();
        }

        public async Task<long> DeliveryCountAsync() =>
            Convert.ToInt64(await ScalarAsync(
                "SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-1'"));

        public async Task AssertNothingWrittenAsync(string eventId)
        {
            Assert.Equal(0L, await ScalarAsync(
                $"SELECT count(*) FROM input.event WHERE event_id='{eventId}'"));
            Assert.Equal(0L, await ScalarAsync(
                $"SELECT count(*) FROM input.delivery WHERE event_id='{eventId}'"));
            // A refusal consumes no idempotency receipt: the same event id stays retryable
            // forever without ever being poisoned by a fence it could not pass.
            Assert.Equal(0L, await ScalarAsync(
                $"SELECT count(*) FROM infra.operation_receipt WHERE operation_id='{eventId}'"));
        }

        public async Task<object> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync() ?? DBNull.Value;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private string CanonicalKey = null!;

        public static async Task<Arrange> RunningRoundAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var arrange = new Arrange
                {
                    Database = database,
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString),
                    InputStore = new RedesignRoundInputStore(database.ConnectionString),
                    EventStore = new RedesignInputEventStore(database.ConnectionString),
                    FrameStore = new RedesignFrameStore(database.ConnectionString)
                };
                var streamer = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "fence-open-1");
                var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-fence");
                var session = await arrange.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var binding = await arrange.BindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-1");
                var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                    "gs-1", 60);
                arrange.Epoch = claim.Epoch;
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
                    [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-1");
                // The frame store observes a Running room (activation lands with the host
                // runtime; the store tests flip the column the same way).
                await using (var connection = new NpgsqlConnection(database.ConnectionString))
                {
                    await connection.OpenAsync();
                    await using var activate = new NpgsqlCommand(
                        "UPDATE game.room SET status='Running' WHERE room_id='room-1'", connection);
                    await activate.ExecuteNonQueryAsync();
                }
                return new Arrange
                {
                    Database = database,
                    RoomStore = arrange.RoomStore,
                    IdentityStore = arrange.IdentityStore,
                    BindingStore = arrange.BindingStore,
                    Control = arrange.Control,
                    InputStore = arrange.InputStore,
                    EventStore = arrange.EventStore,
                    FrameStore = arrange.FrameStore,
                    SessionId = session.SessionId,
                    CanonicalKey = "live-key-fence",
                    Binding = (binding.BindingId, streamer.StreamerId),
                    Epoch = claim.Epoch
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_input_fence_" + Guid.NewGuid().ToString("N");
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
