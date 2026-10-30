using Chorus.GameServer;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The frame-commit pending contract (FIX-FRAME-01/02/03) as unit evidence: a room actor
/// against a scripted frame store. The store never answers twice the same way — replies get
/// lost after the commit landed, the channel drops, the fence rejects — and the actor must
/// still move its head forward exactly once per tick, on the original request, without ever
/// re-reading the inbox while a frame is unconfirmed. The fault-injection host forms of the
/// same contract live in the host tests (FIX-FRAME-04/05).
/// </summary>
public sealed class RedesignFrameCommitUnitTests
{
    [Fact]
    public async Task AnUnconfirmedFrameIsHeldAndResentVerbatimUntilItIsConfirmed()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var lostReply = RpcError(StatusCode.Unavailable, "the reply was lost after the frame committed");
        // Two unknown outcomes, then a send whose reply only lands when the test releases it:
        // the actor is provably stuck on the third attempt while the asserts run.
        var held = new TaskCompletionSource<FrameReceiptReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.CommitSteps.Enqueue(_ => Task.FromException<FrameReceiptReply>(lostReply));
        store.CommitSteps.Enqueue(_ => Task.FromException<FrameReceiptReply>(lostReply));
        store.CommitSteps.Enqueue(_ => held.Task);
        var actor = NewRoom(store, lifecycle);
        var subscriber = actor.Subscribe(0, 1);
        actor.Start();

        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 3)),
            "The actor never reached a third commit attempt.");
        // FIX-FRAME-01: no new input was read and the tick was not re-applied while the
        // frame was unconfirmed — every resend carries the identical canonical request.
        Assert.Equal(1, store.InboxReadCount);
        Assert.Equal(3, store.CommitCount);
        var sent = store.CommitsSnapshot();
        var first = sent[0].ToByteArray();
        Assert.All(sent, request => Assert.Equal(first, request.ToByteArray()));

        held.SetResult(Receipt(sent[0], tick: 1, stateVersion: 2));
        var delta = await NextAsync(subscriber);
        Assert.NotNull(delta);
        Assert.Equal(SyncMessage.KindOneofCase.Delta, delta.KindCase);
        Assert.Equal(1, delta.Delta.Tick);
        // Only after the verdict may the loop read the inbox for the next tick (>= not ==:
        // once freed, the timer bursts through skipped ticks and the count jumps past 2).
        Assert.True(await WaitForAsync(() => Task.FromResult(store.InboxReadCount >= 2)),
            "The actor never resumed reading the inbox after its frame confirmed.");
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task ADuplicateReceiptCompletesThePendingFrameAndAdvancesItExactlyOnce()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var lostReply = RpcError(StatusCode.Unavailable, "the reply was lost after the frame committed");
        // The first send's reply vanished; the retry hits the store's idempotency: the frame
        // already committed, so the store replays its stored receipt with duplicate=true.
        store.CommitSteps.Enqueue(_ => Task.FromException<FrameReceiptReply>(lostReply));
        store.CommitSteps.Enqueue(request =>
            Task.FromResult(Receipt(request, tick: 1, stateVersion: 2, duplicate: true)));
        var actor = NewRoom(store, lifecycle, durationTicks: 1);
        var subscriber = actor.Subscribe(0, 1);
        actor.Start();

        // The one-tick room finishes: exactly one delta, then the frozen result, then end.
        var delta = await NextAsync(subscriber);
        Assert.NotNull(delta);
        Assert.Equal(SyncMessage.KindOneofCase.Delta, delta.KindCase);
        Assert.Equal(1, delta.Delta.Tick);
        Assert.Equal(2, delta.Delta.StateVersion);
        Assert.Equal(1, delta.Delta.BaseVersion);
        var result = await NextAsync(subscriber);
        Assert.NotNull(result);
        Assert.Equal(SyncMessage.KindOneofCase.RoundResult, result.KindCase);
        Assert.Null(await NextAsync(subscriber));

        // FIX-FRAME-02: the head advanced exactly once and the room settled — a replayed
        // receipt never takes a second step.
        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Completed)));
        Assert.Equal(2, store.CommitCount);
        Assert.Equal(1, store.InboxReadCount);
        Assert.Empty(lifecycle.SuspendReasons);
        Assert.Equal(new[] { "room-fc" }, lifecycle.SettledRooms);
        var late = actor.Subscribe(0, 1);
        var snapshot = await NextAsync(late);
        Assert.Equal(SyncMessage.KindOneofCase.Snapshot, snapshot!.KindCase);
        Assert.Equal(1, snapshot.Snapshot.Tick);
        Assert.Equal(2, snapshot.Snapshot.StateVersion);
        late.Dispose();
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task AnUnknownOutcomeIsResentVerbatimUpToTheRetryCeilingThenParksVisibly()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var lostReply = RpcError(StatusCode.Unavailable, "the store is unreachable");
        store.DefaultCommit = _ => Task.FromException<FrameReceiptReply>(lostReply);
        // FIX-PERF-06: the unknown outcome is bounded. The test seam makes the resends
        // instant; production backs off 250ms..8s per resend before the ceiling parks the
        // room for replay recovery instead of retrying forever silent under Running.
        var actor = NewRoom(store, lifecycle, commitRetryDelay: TimeSpan.Zero);
        actor.Start();

        // The first send plus MaxCommitRetries resends, then the room parks visibly.
        Assert.True(await WaitForAsync(() => Task.FromResult(
                lifecycle.SuspendCount == 1 && actor.Status().State == RoomRunState.Stopped)),
            $"The actor never parked the unknown-outcome room (sends={store.CommitCount}).");
        Assert.Equal(9, store.CommitCount);
        Assert.Contains("stayed unknown after 8 retries", actor.Status().StopReason);
        Assert.Contains("the store is unreachable", actor.Status().StopReason);
        Assert.Contains("stayed unknown after 8 retries", lifecycle.SingleReason());
        // The whole resend chain carried one identical request, and the loop stopped reading
        // the inbox the moment the first frame went pending: a parked room publishes nothing
        // uncommitted and re-consumes no input.
        var sent = store.CommitsSnapshot();
        var first = sent[0].ToByteArray();
        Assert.All(sent, request => Assert.Equal(first, request.ToByteArray()));
        Assert.Equal(1, store.InboxReadCount);
        Assert.False(actor.Completed);
        var subscriber = actor.Subscribe(0, 1);
        var message = await NextAsync(subscriber);
        Assert.Equal(SyncMessage.KindOneofCase.ResyncRequired, message!.KindCase);
        Assert.Null(await NextAsync(subscriber));
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task ADeterministicRejectionStopsTheActorAndParksTheRoomWithAReason()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var rejection = RpcError(StatusCode.FailedPrecondition,
            "The room ownership fence rejected the caller.");
        store.DefaultCommit = _ => Task.FromException<FrameReceiptReply>(rejection);
        var actor = NewRoom(store, lifecycle);
        var subscriber = actor.Subscribe(0, 1);
        actor.Start();

        Assert.True(await WaitForAsync(() => Task.FromResult(lifecycle.SuspendCount == 1)),
            "The actor never parked the rejected room.");
        Assert.Contains("frame commit rejected", lifecycle.SingleReason());
        Assert.Contains("fence rejected", lifecycle.SingleReason());
        // The rejection ended the loop, so the subscriber is told to resync rather than
        // wait for frames that will never come; no retry and no re-read may follow.
        var resync = await NextAsync(subscriber);
        Assert.NotNull(resync);
        Assert.Equal(SyncMessage.KindOneofCase.ResyncRequired, resync.KindCase);
        Assert.Null(await NextAsync(subscriber));
        Assert.False(actor.Completed);
        Assert.Equal(1, store.CommitCount);
        Assert.Equal(1, store.InboxReadCount);
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task ARoomThatRunsToItsFinalTickReportsCompleted()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var actor = NewRoom(store, lifecycle, durationTicks: 3);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Completed)),
            "The room never committed its final frame.");
        var status = actor.Status();
        Assert.Equal(RoomRunState.Completed, status.State);
        Assert.Equal(1, status.Epoch);
        Assert.Equal(3, status.LastCommittedTick);
        Assert.Null(status.StopReason);
        await actor.DisposeAsync();
        // Teardown after the result keeps the completed state.
        Assert.Equal(RoomRunState.Completed, actor.Status().State);
    }

    [Fact]
    public async Task ModuleFailuresStopTheRoomWithARecordedReason()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        // The module's deterministic fault seam (AT13): every tick from the first throws.
        var actor = NewRoom(store, lifecycle, durationTicks: 50, failAfterTick: 1);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(lifecycle.SuspendCount == 1)),
            "The actor never parked the failing room.");
        var status = actor.Status();
        Assert.Equal(RoomRunState.Stopped, status.State);
        Assert.Contains("module failed 3 consecutive ticks", status.StopReason);
        Assert.Equal(0, status.LastCommittedTick);
        Assert.False(actor.Completed);
        Assert.Equal(0, store.CommitCount);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task AFencedRoomStopsWithTheRejectionReason()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var rejection = RpcError(StatusCode.FailedPrecondition,
            "The room ownership fence rejected the caller.");
        store.DefaultCommit = _ => Task.FromException<FrameReceiptReply>(rejection);
        var actor = NewRoom(store, lifecycle);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(lifecycle.SuspendCount == 1)),
            "The actor never parked the rejected room.");
        var status = actor.Status();
        Assert.Equal(RoomRunState.Stopped, status.State);
        Assert.Contains("frame commit rejected at tick 1", status.StopReason);
        Assert.Equal(0, status.LastCommittedTick);
        Assert.False(actor.Completed);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task DisposingALiveRoomMarksItDisposedNotCompleted()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var actor = NewRoom(store, lifecycle, durationTicks: 200);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 2)),
            "The room never committed a frame.");
        var beforeDispose = store.CommitCount;
        await actor.DisposeAsync();
        var status = actor.Status();
        Assert.Equal(RoomRunState.Disposed, status.State);
        Assert.True(status.LastCommittedTick >= 2);
        Assert.False(actor.Completed);
        Assert.Null(status.StopReason);
        Assert.True(beforeDispose >= 2);
    }

    private static RpcException RpcError(StatusCode status, string detail) =>
        new(new Status(status, detail));

    private static FrameReceiptReply Receipt(CommitFrameRequest request, long tick, long stateVersion,
        bool duplicate = false) => new()
    {
        CommitId = request.CommitId, RoomId = request.RoomId, Tick = tick,
        StateVersion = stateVersion, Duplicate = duplicate
    };

    private static RoomActor NewRoom(ScriptedFrameStore store, ScriptedLifecycle lifecycle,
        long durationTicks = 200, int failAfterTick = 0, TimeSpan? commitRetryDelay = null)
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "frame-commit-tests", "1.0.0-test", 25, 100,
            RedesignGameModuleManifest.DeterministicReplay));
        var participants = new List<GameParticipant>
        {
            new("host-fc", GameStartConfig.RedFaction), new("guest-fc", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(
            new GameStartConfig(durationTicks, 777, participants, failAfterTick));
        var bindings = new Dictionary<string, string> { ["binding-host-fc"] = "host-fc" };
        return new RoomActor(lifecycle, store, module, "game-fc", "room-fc", "game-a", 1, "cfg-fc",
            initial, bindings, commitRetryDelay: commitRetryDelay);
    }

    /// <summary>Reads the next committed message with a bound: a room that never publishes
    /// must fail the test, not hang it.</summary>
    private static async Task<SyncMessage?> NextAsync(RoomSubscriber subscriber)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await subscriber.NextAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("No committed message arrived within 30 seconds.");
        }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(20);
        }
        return await probe();
    }

    /// <summary>
    /// A frame store whose replies are scripted per send. Steps dequeue in order; once the
    /// script runs out, <see cref="DefaultCommit"/> answers (a plain success by default).
    /// </summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly List<ReadInboxRequest> _inboxReads = new();
        private readonly List<CommitFrameRequest> _commitRequests = new();

        public Queue<Func<CommitFrameRequest, Task<FrameReceiptReply>>> CommitSteps { get; } = new();
        public Func<CommitFrameRequest, Task<FrameReceiptReply>>? DefaultCommit { get; set; }

        public int InboxReadCount { get { lock (_gate) return _inboxReads.Count; } }
        public int CommitCount { get { lock (_gate) return _commitRequests.Count; } }

        public CommitFrameRequest[] CommitsSnapshot() { lock (_gate) return _commitRequests.ToArray(); }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _inboxReads.Add(request);
            var reply = new ReadInboxReply();
            return new AsyncUnaryCall<ReadInboxReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commitRequests.Add(request);
            var step = CommitSteps.Count > 0
                ? CommitSteps.Dequeue()
                : DefaultCommit ?? (commit => Task.FromResult(Receipt(commit,
                    commit.PrevTick + 1, commit.PrevStateVersion + 1)));
            return new AsyncUnaryCall<FrameReceiptReply>(step(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The actor's runtime asks, recorded instead of reaching the data plane.</summary>
    private sealed class ScriptedLifecycle : IRoomLifecycle
    {
        private readonly object _gate = new();
        private readonly List<string> _suspends = new();
        private readonly List<string> _settles = new();

        public int SuspendCount { get { lock (_gate) return _suspends.Count; } }
        public string[] SuspendReasons { get { lock (_gate) return _suspends.ToArray(); } }
        public string[] SettledRooms { get { lock (_gate) return _settles.ToArray(); } }

        public string SingleReason() { lock (_gate) return _suspends[0]; }

        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default)
        {
            lock (_gate) _suspends.Add(reason);
            return Task.CompletedTask;
        }

        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _settles.Add(roomId);
            return Task.CompletedTask;
        }
    }
}
