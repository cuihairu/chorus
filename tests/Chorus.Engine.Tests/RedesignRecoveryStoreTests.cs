using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Snapshot persistence and recovery reads (architecture 8.2, 10.3): a snapshot row is
/// visible only whole (a write that dies mid-transaction leaves nothing behind), the store
/// verifies the payload hash itself, and the recovery plan hands the recoverer the room head,
/// every snapshot, the inputs the loaded frames reference and the frames after the oldest
/// snapshot.
/// </summary>
public sealed class RedesignRecoveryStoreTests
{
    [Fact]
    public async Task PutSnapshotIsAtomicVerifiedFencedAndHeadBound()
    {
        await using var arrange = await Arrange.RunningRoomWithInboxAsync("gift-1", "gift-2");
        var payload = new byte[] { 7, 8, 9 };
        var hash = SHA256.HashData(payload);
        var ids = await arrange.PendingEventIds();
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-1", "gs-1", arrange.Epoch, 0, 1, [ids[0]], [9, 9, 9], [],
                "1.0.0", "cfg-1"));

        // A write that dies between the insert and the commit leaves no partial snapshot.
        var armed = true;
        var crashing = new RedesignRecoveryStore(arrange.Database.ConnectionString,
            () => { if (armed) throw new InvalidOperationException("simulated crash"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 1, payload, hash, 1, "snap-op-1"));
        armed = false;
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var snapshot = new NpgsqlCommand(
                "SELECT count(*) FROM game.snapshot WHERE room_id='room-1' AND tick_id=1", connection);
            Assert.Equal(0L, Convert.ToInt64(await snapshot.ExecuteScalarAsync()));
            await using var receipt = new NpgsqlCommand("""
                SELECT count(*) FROM infra.operation_receipt
                WHERE operation_kind='snapshot.put' AND target_id='room-1'
                """, connection);
            Assert.Equal(0L, Convert.ToInt64(await receipt.ExecuteScalarAsync()));
        }

        // The same operation converges: one verified row, exact retry replays.
        var put = await crashing.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1",
            arrange.Epoch, 1, payload, hash, 1, "snap-op-1");
        Assert.False(put.Duplicate);
        Assert.True(put.Verified);
        Assert.Equal(1, put.Tick);
        var retry = await crashing.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1",
            arrange.Epoch, 1, payload, hash, 1, "snap-op-1");
        Assert.True(retry.Duplicate);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var snapshot = new NpgsqlCommand(
                "SELECT payload,payload_hash,verified FROM game.snapshot WHERE room_id='room-1' AND tick_id=1",
                connection))
            await using (var reader = await snapshot.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(payload, (byte[])reader[0]);
                Assert.Equal(hash, (byte[])reader[1]);
                Assert.True(reader.GetBoolean(2));
            }
        }

        // The store verifies the hash itself; a mismatched commitment never lands.
        await Assert.ThrowsAsync<ArgumentException>(() => crashing.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 1, [1, 1, 1], hash, 1, "snap-op-2"));
        // Snapshots attach to the head tick only; the fence rejects strangers.
        await Assert.ThrowsAsync<FrameConflictException>(() => crashing.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 0, payload, hash, 1, "snap-op-3"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => crashing.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-9", arrange.Epoch, 1, payload, hash, 1, "snap-op-4"));
        // The same operation with different content conflicts.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => crashing.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 1, [3, 3, 3],
            SHA256.HashData([3, 3, 3]), 1, "snap-op-1"));
        // A suspended room accepts no snapshots.
        await arrange.RoomStore.SuspendRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
            arrange.Epoch, "parked", "suspend-op-1");
        await Assert.ThrowsAsync<FrameConflictException>(() => crashing.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 1, payload, hash, 1, "snap-op-5"));
    }

    [Fact]
    public async Task LoadRecoveryReturnsHeadSnapshotsDeliveriesAndFrames()
    {
        await using var arrange = await Arrange.RunningRoomWithInboxAsync("gift-1", "gift-2");
        var store = new RedesignRecoveryStore(arrange.Database.ConnectionString);
        var ids = await arrange.PendingEventIds();

        // Two frames consume one delivery each; a store-verified snapshot pins the head.
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-1", "gs-1", arrange.Epoch, 0, 1, [ids[0]], [1, 1, 1], [],
                "1.0.0", "cfg-1"));
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-2", "gs-1", arrange.Epoch, 1, 2, [ids[1]], [2, 2, 2], [],
                "1.0.0", "cfg-1"));
        var payload = new byte[] { 5, 6, 7, 8 };
        await store.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 2,
            payload, SHA256.HashData(payload), 1, "snap-op-1");

        var plan = await store.LoadRecoveryAsync("tenant-a", "game-a", "room-1");
        Assert.NotNull(plan);
        Assert.Equal("Running", plan!.Status);
        Assert.Equal(arrange.Epoch, plan.Epoch);
        Assert.Equal(2, plan.LastTick);
        Assert.Equal(3, plan.StateVersion); // activation at 1 plus two commits

        // Snapshots newest-first, with the prepare-time tick-0 row still unverified.
        Assert.Equal(2, plan.Snapshots.Count);
        Assert.Equal(2, plan.Snapshots[0].Tick);
        Assert.True(plan.Snapshots[0].Verified);
        Assert.Equal(payload, plan.Snapshots[0].Payload);
        Assert.Equal(0, plan.Snapshots[1].Tick);
        Assert.False(plan.Snapshots[1].Verified);

        // The delivery log keeps routing order, bindings and payloads — consumed included.
        Assert.Equal(2, plan.Deliveries.Count);
        Assert.Equal([1L, 2L], plan.Deliveries.Select(delivery => delivery.Sequence).ToArray());
        Assert.All(plan.Deliveries, delivery =>
        {
            Assert.True(delivery.Consumed);
            Assert.Equal(arrange.BindingId, delivery.BindingId);
            Assert.StartsWith("{\"kind\": \"gift\"", delivery.PayloadJson, StringComparison.Ordinal);
        });

        // Frames after the oldest snapshot, in tick order, refs preserved.
        Assert.Equal(2, plan.Frames.Count);
        Assert.Equal([1L, 2L], plan.Frames.Select(frame => frame.Tick).ToArray());
        Assert.Equal([ids[0]], plan.Frames[0].InputEventIds);
        Assert.Equal([ids[1]], plan.Frames[1].InputEventIds);
        Assert.Equal([1, 1, 1], plan.Frames[0].StateHash);
        Assert.Equal([2, 2, 2], plan.Frames[1].StateHash);
        Assert.All(plan.Frames, frame =>
        {
            Assert.Equal("1.0.0", frame.ModuleVersion);
            Assert.Equal("cfg-1", frame.ConfigHash);
        });
        var participant = Assert.Single(plan.Participants);
        Assert.Equal("Active", participant.State);
        Assert.Equal(arrange.BindingId, participant.BindingId);

        Assert.Null(await store.LoadRecoveryAsync("tenant-a", "game-a", "room-none"));
    }

    [Fact]
    public async Task AConcurrentCommitNeverLeaksIntoTheSelectedHead()
    {
        await using var arrange = await Arrange.RunningRoomWithInboxAsync("gift-1", "gift-2");
        var ids = await arrange.PendingEventIds();
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-view-1", "gs-1", arrange.Epoch, 0, 1, [ids[0]], [1, 1, 1], [],
                "1.0.0", "cfg-1"));
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-view-2", "gs-1", arrange.Epoch, 1, 2, [ids[1]], [2, 2, 2], [],
                "1.0.0", "cfg-1"));
        var payload = new byte[] { 5, 6, 7, 8 };
        var store = new RedesignRecoveryStore(arrange.Database.ConnectionString);
        await store.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 2,
            payload, SHA256.HashData(payload), 1, "snap-view-2");

        // The view holds between the head read and the material loads: whatever commits in
        // that window must stay invisible to the plan being loaded (FIX-READ-01).
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = new RedesignRecoveryStore(arrange.Database.ConnectionString,
            afterHeadRead: () => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); })
            .LoadRecoveryAsync("tenant-a", "game-a", "room-1");
        await entered.Task;
        var laterPayload = new byte[] { 9, 9 };
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-view-3", "gs-1", arrange.Epoch, 2, 3, [], [3, 3, 3], [],
                "1.0.0", "cfg-1"));
        await store.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 3,
            laterPayload, SHA256.HashData(laterPayload), 1, "snap-view-3");
        release.SetResult();

        var plan = await loading;
        Assert.NotNull(plan);
        // The selected head is the immutable boundary: the epoch, tick and state version the
        // first read fixed are what the whole plan — and any paging over it — must cover.
        Assert.Equal(2, plan!.LastTick);
        Assert.Equal(3, plan.StateVersion); // activation plus two commits; the third is not ours
        Assert.Equal(arrange.Epoch, plan.Epoch);
        Assert.Equal([1L, 2L], plan.Frames.Select(frame => frame.Tick).ToArray());
        Assert.All(plan.Snapshots, snapshot => Assert.True(snapshot.Tick <= 2,
            "a snapshot committed after the view opened leaked in"));

        // The pause proved itself: the next read, on a fresh view, sees everything the held
        // view refused — the test cannot pass by failing to commit at all.
        var after = await store.LoadRecoveryAsync("tenant-a", "game-a", "room-1");
        Assert.Equal(3, after!.LastTick);
        Assert.Equal(4, after.StateVersion);
        Assert.Contains(after.Snapshots, snapshot => snapshot.Tick == 3 && snapshot.Verified);
    }

    [Fact]
    public async Task ALongRoundReadsOnlyTheBoundedCandidatesAndTheirFrameSpan()
    {
        await using var arrange = await Arrange.RunningRoomWithInboxAsync("gift-1");
        var ids = await arrange.PendingEventIds();
        var store = new RedesignRecoveryStore(arrange.Database.ConnectionString);

        // A long round: 250 ticks with verified snapshots at 100 and 200 — the tick-0
        // admission row makes three anchors in total.
        for (var tick = 1; tick <= 250; tick++)
        {
            var inputs = tick == 1 ? [ids[0]] : Array.Empty<string>();
            await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
                new FrameCommit($"commit-long-{tick}", "gs-1", arrange.Epoch, tick - 1, tick,
                    inputs, [(byte)tick, (byte)tick, (byte)tick], [], "1.0.0", "cfg-1"));
            if (tick is 100 or 200)
            {
                var payload = new byte[] { (byte)tick, 1, 2 };
                await store.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch,
                    tick, payload, SHA256.HashData(payload), 1, $"snap-long-{tick}");
            }
        }

        var plan = await store.LoadRecoveryAsync("tenant-a", "game-a", "room-1");
        Assert.NotNull(plan);
        Assert.Equal(250, plan!.LastTick);
        // The bounded candidates: the two newest anchors, newest-first — the tick-0 row stays
        // unread on the normal path.
        Assert.Equal([100L, 200L], plan.Snapshots.Select(snapshot => snapshot.Tick).Reverse().ToArray());
        // The frame span follows the oldest loaded candidate, not the oldest anchor in
        // history: 150 frames bounded by the snapshot interval, never 250.
        Assert.Equal(150, plan.Frames.Count);
        Assert.Equal(101, plan.Frames[0].Tick);
        Assert.Equal(250, plan.Frames[^1].Tick);
        // The read is measured, not guessed (FIX-READ-02): the row counts and payload bytes
        // the plan moved are part of its own answer. The one consumed input lives at tick 1,
        // below the loaded span, so the delivery read pays zero for it (FIX-READ-03).
        Assert.NotNull(plan.Reads);
        Assert.Equal(2, plan.Reads!.SnapshotRows);
        Assert.Equal(150, plan.Reads.FrameRows);
        Assert.Equal(0, plan.Reads.DeliveryRows);
        Assert.Empty(plan.Deliveries);
        Assert.True(plan.Reads.PayloadBytes > 0);
    }

    [Fact]
    public async Task OnlyTheInputsTheLoadedSpanReferencesRideInThePlan()
    {
        // Four consumed inputs and two that no frame ever cites: the delivery history has
        // both layers FIX-READ-03 must not pay for — inputs consumed below the loaded
        // candidate, and inputs that were routed but never referenced at all.
        await using var arrange = await Arrange.RunningRoomWithInboxAsync(
            "gift-r3-1", "gift-r3-2", "gift-r3-3", "gift-r3-4", "gift-r3-5", "gift-r3-6");
        var ids = await arrange.PendingEventIds();
        var store = new RedesignRecoveryStore(arrange.Database.ConnectionString);
        for (var tick = 1; tick <= 4; tick++)
        {
            await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
                new FrameCommit($"commit-r3-{tick}", "gs-1", arrange.Epoch, tick - 1, tick,
                    [ids[tick - 1]], [(byte)tick, (byte)tick], [], "1.0.0", "cfg-1"));
            if (tick == 2)
            {
                var anchor = new byte[] { 4, 4 };
                await store.PutSnapshotAsync("tenant-a", "game-a", "room-1", "gs-1",
                    arrange.Epoch, 2, anchor, SHA256.HashData(anchor), 1, "snap-r3-2");
            }
        }

        // The fallback pair loads the whole history: every consumed input is referenced by a
        // loaded frame, so exactly those four ride along — the two never cited stay unread.
        var full = await store.LoadRecoveryAsync("tenant-a", "game-a", "room-1");
        Assert.NotNull(full);
        Assert.Equal([2L, 0L], full!.Snapshots.Select(snapshot => snapshot.Tick).ToArray());
        Assert.Equal([1L, 2L, 3L, 4L], full.Frames.Select(frame => frame.Tick).ToArray());
        Assert.Equal(ids.Take(4), full.Deliveries.Select(delivery => delivery.EventId));
        Assert.All(full.Deliveries, delivery => Assert.True(delivery.Consumed));
        Assert.Equal(4, full.Reads!.DeliveryRows);

        // A one-candidate read that walks from the verified tick-2 anchor: its span is ticks
        // 3-4, so only those two inputs load. The history below the anchor — and the
        // deliveries beside the span entirely — add nothing to the plan.
        var bounded = await store.LoadRecoveryAsync("tenant-a", "game-a", "room-1",
            candidateDepth: 1);
        Assert.NotNull(bounded);
        Assert.Equal([2L], bounded!.Snapshots.Select(snapshot => snapshot.Tick).ToArray());
        Assert.Equal([3L, 4L], bounded.Frames.Select(frame => frame.Tick).ToArray());
        Assert.Equal(ids.Skip(2).Take(2), bounded.Deliveries.Select(delivery => delivery.EventId));
        Assert.Equal(2, bounded.Reads!.DeliveryRows);
    }

    [Fact]
    public async Task ARecoveringOwnerPinsTheRecoveryHeadWhileEveryNormalWriteStaysFencedOut()
    {
        await using var arrange = await Arrange.RunningRoomWithInboxAsync("gift-1");
        var ids = await arrange.PendingEventIds();
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-take-2-1", "gs-1", arrange.Epoch, 0, 1, [ids[0]], [9, 9, 9], [],
                "1.0.0", "cfg-1"));
        var store = new RedesignRecoveryStore(arrange.Database.ConnectionString);
        var takeover = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "transfer-take-2");
        Assert.Equal("Recovering", takeover.Status);

        var payload = new byte[] { 3, 1, 4 };
        var hash = SHA256.HashData(payload);
        // The frame fence stays closed for everyone while Recovering — the new owner included.
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => arrange.FrameStore.CommitFrameAsync(
            "tenant-a", "game-a", "room-1",
            new FrameCommit("commit-take-2-2", "gs-2", takeover.Epoch, 1, 2, [], [8, 8, 8], [],
                "1.0.0", "cfg-1")));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => store.PutSnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-2", takeover.Epoch, 1, payload, hash, 1, "snap-take-2"));

        // The takeover-named owner pins the verified recovery head; the pin never activates.
        var pinned = await store.PutRecoverySnapshotAsync("tenant-a", "game-a", "room-1", "gs-2",
            takeover.Epoch, 1, payload, hash, 1, "recovery-take-2");
        Assert.False(pinned.Duplicate);
        Assert.True(pinned.Verified);
        Assert.Equal(1, pinned.Tick);
        var replay = await store.PutRecoverySnapshotAsync("tenant-a", "game-a", "room-1", "gs-2",
            takeover.Epoch, 1, payload, hash, 1, "recovery-take-2");
        Assert.True(replay.Duplicate);

        // The old owner, a wrong epoch and a non-head tick are all refused.
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => store.PutRecoverySnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", takeover.Epoch, 1, payload, hash, 1, "recovery-take-2b"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => store.PutRecoverySnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-2", takeover.Epoch + 1, 1, payload, hash, 1, "recovery-take-2c"));
        await Assert.ThrowsAsync<FrameConflictException>(() => store.PutRecoverySnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-2", takeover.Epoch, 0, payload, hash, 1, "recovery-take-2d"));

        var plan = await store.LoadRecoveryAsync("tenant-a", "game-a", "room-1");
        Assert.NotNull(plan);
        Assert.Equal(takeover.Epoch, plan!.Epoch);
        Assert.Equal("Running", plan.Status); // the battle state, not an activation claim
        Assert.Contains(plan.Snapshots, snapshot => snapshot.Tick == 1 && snapshot.Verified);
        await using (var connection = new NpgsqlConnection(arrange.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var ownership = new NpgsqlCommand(
                "SELECT status FROM control.ownership WHERE kind='room' AND entity_id='room-1'", connection);
            Assert.Equal("Recovering", Convert.ToString(await ownership.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task TheRecoverySnapshotEntryClosesOnceTheOwnershipIsActiveAgain()
    {
        await using var arrange = await Arrange.RunningRoomWithInboxAsync("gift-1");
        var ids = await arrange.PendingEventIds();
        await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-take-2-e", "gs-1", arrange.Epoch, 0, 1, [ids[0]], [9, 9, 9], [],
                "1.0.0", "cfg-1"));
        var store = new RedesignRecoveryStore(arrange.Database.ConnectionString);
        var payload = new byte[] { 5, 9 };
        var hash = SHA256.HashData(payload);

        // An Active room never sees the recovery entry: the normal PutSnapshot is its path.
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => store.PutRecoverySnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-1", arrange.Epoch, 1, payload, hash, 1, "recovery-take-2e"));

        var takeover = await arrange.RoomStore.TransferRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-1", "gs-2", 15, "transfer-take-2e");
        Assert.True((await store.PutRecoverySnapshotAsync("tenant-a", "game-a", "room-1", "gs-2",
            takeover.Epoch, 1, payload, hash, 1, "recovery-take-2f")).Verified);
        var confirmed = await arrange.RoomStore.ConfirmRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-2");
        Assert.Equal("Active", confirmed.Status);
        // A repeated confirm replays the same activation (FIX-TAKE-03): the owner and the
        // epoch it reports never move.
        var reconfirmed = await arrange.RoomStore.ConfirmRoomOwnershipAsync("tenant-a", "game-a", "room-1",
            "gs-2");
        Assert.Equal("Active", reconfirmed.Status);
        Assert.Equal(confirmed.Epoch, reconfirmed.Epoch);
        Assert.Equal(confirmed.OwnerInstanceId, reconfirmed.OwnerInstanceId);

        // After the fenced confirm the recovery entry closes again…
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => store.PutRecoverySnapshotAsync(
            "tenant-a", "game-a", "room-1", "gs-2", takeover.Epoch, 1, payload, hash, 1, "recovery-take-2g"));
        // …while an exact replay of the original recovery operation still reads its receipt,
        // and the frame fence reopened only through the confirm: the new owner commits.
        var replay = await store.PutRecoverySnapshotAsync("tenant-a", "game-a", "room-1", "gs-2",
            takeover.Epoch, 1, payload, hash, 1, "recovery-take-2f");
        Assert.True(replay.Duplicate);
        var resumed = await arrange.FrameStore.CommitFrameAsync("tenant-a", "game-a", "room-1",
            new FrameCommit("commit-take-2-h", "gs-2", takeover.Epoch, 1, 2, [], [7, 7, 7], [],
                "1.0.0", "cfg-1"));
        Assert.Equal(2, resumed.Tick);
    }

    /// <summary>Test arrange: an admitted, activated room with routed, pending inputs.</summary>
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
        public long Epoch;
        public string BindingId = "";

        public static async Task<Arrange> RunningRoomWithInboxAsync(params string[] dedupKeys)
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var arrange = new Arrange
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
                var streamer = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "recovery-open-1");
                var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-recovery");
                var session = await arrange.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var binding = await arrange.BindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-1");
                arrange.BindingId = binding.BindingId;
                var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                    "gs-1", 60);
                arrange.Epoch = claim.Epoch;
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
                    [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-1");
                await arrange.RoomStore.PrepareRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
                    claim.Epoch, [1, 2, 3], [4, 5, 6], 1, "prepare-1");
                await arrange.RoomStore.ActivateRoundAsync("tenant-a", "game-a", "room-1", "gs-1",
                    claim.Epoch, "activate-1");
                var batch = dedupKeys.Select(key => new PlatformEventInput(key,
                    RedesignInputEventStore.Durable, "live-key-recovery",
                    $$"""{"kind":"gift","amount":{{Encoding.UTF8.GetByteCount(key)}}}""")).ToArray();
                await arrange.EventStore.IngestAsync("simulator", "app-1",
                    "route-batch-" + Guid.NewGuid().ToString("N"), batch);
                await arrange.InputStore.RouteEventsAsync("simulator", "app-1",
                    "route-op-" + Guid.NewGuid().ToString("N"), 50);
                return arrange;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
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
        private readonly string _name = "chorus_m2_recovery_" + Guid.NewGuid().ToString("N");
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
