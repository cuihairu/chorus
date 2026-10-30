using Chorus.GameServer;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The sustained-slowness policy (FIX-PERF-06) as unit evidence: a room whose commits keep
/// blowing the tick budget becomes observably Degraded — the loop keeps committing (progress
/// over silence), the state and the recovery answer name it — and one on-budget commit clears
/// the streak and the state. The bounded unknown-outcome retry ceiling and its parking safety
/// live with the frame-commit contract tests.
/// </summary>
public sealed class RedesignSustainedOverBudgetUnitTests
{
    [Fact]
    public async Task SustainedOverBudgetCommitsDegradeTheRoomVisiblyAndItKeepsCommitting()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        // Budget: TickMs 25 x factor 4 = 100ms; every scripted commit takes 130ms — over
        // budget — and a window of 3 consecutive trips the Degraded state. Production keeps
        // the 10x10 constants; the seams only make the unit evidence fast.
        store.DefaultCommit = request => SlowCommit(130, request);
        var actor = NewRoom(store, lifecycle, durationTicks: 100, overBudgetFactor: 4, overBudgetWindow: 3);
        var subscriber = actor.Subscribe(0, 1);
        actor.Start();

        Assert.True(await WaitForAsync(() => Task.FromResult(
                store.CommitCount >= 3 && actor.Status().State == RoomRunState.Degraded)),
            $"The room never degraded (state={actor.Status().State}, commits={store.CommitCount}).");
        Assert.Equal(RoomRunState.Degraded, actor.Status().State);
        Assert.True(store.CommitCount >= 3);

        // The degraded room keeps committing: committed frames keep flowing to subscribers
        // while the state says Degraded — progress over silence, never a parked head.
        var first = await NextAsync(subscriber);
        Assert.Equal(SyncMessage.KindOneofCase.Delta, first!.KindCase);
        var second = await NextAsync(subscriber);
        Assert.Equal(SyncMessage.KindOneofCase.Delta, second!.KindCase);

        // The store recovers: one on-budget commit clears the streak and the state.
        store.DefaultCommit = request => Task.FromResult(Receipt(request));
        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Status().State == RoomRunState.Running)),
            "One on-budget commit never cleared the Degraded state.");
        Assert.Equal(RoomRunState.Running, actor.Status().State);
        Assert.Empty(lifecycle.SuspendReasons);
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task AnOnBudgetCommitBreaksTheStreakSoAShortSlownessNeverDegrades()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        // The third commit is on budget — it must break the streak, so two slow commits
        // before and two after (with the on-budget one between) never degrade the room.
        store.CommitSteps.Enqueue(request => SlowCommit(130, request));
        store.CommitSteps.Enqueue(request => SlowCommit(130, request));
        store.CommitSteps.Enqueue(request => Task.FromResult(Receipt(request)));
        store.DefaultCommit = request => SlowCommit(130, request);
        var actor = NewRoom(store, lifecycle, durationTicks: 30, overBudgetFactor: 4, overBudgetWindow: 3);
        actor.Start();

        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 5)),
            "The room never reached the fifth commit.");
        Assert.Equal(RoomRunState.Running, actor.Status().State);
        Assert.Empty(lifecycle.SuspendReasons);

        // Three consecutive over-budget commits do degrade — the streak is real, not noise.
        Assert.True(await WaitForAsync(() => Task.FromResult(
                store.CommitCount >= 6 && actor.Status().State == RoomRunState.Degraded)),
            "Three consecutive over-budget commits never degraded the room.");
        Assert.Equal(RoomRunState.Degraded, actor.Status().State);
        await actor.DisposeAsync();
    }

    private static RoomActor NewRoom(ScriptedFrameStore store, ScriptedLifecycle lifecycle,
        long durationTicks = 200, int overBudgetFactor = 0, int overBudgetWindow = 0)
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "sustained-over-budget-tests", "1.0.0-test", 25, 100,
            RedesignGameModuleManifest.DeterministicReplay));
        var participants = new List<GameParticipant>
        {
            new("host-sb", GameStartConfig.RedFaction), new("guest-sb", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(
            new GameStartConfig(durationTicks, 4242, participants, 0));
        var bindings = new Dictionary<string, string> { ["binding-host-sb"] = "host-sb" };
        return new RoomActor(lifecycle, store, module, "game-sb", "room-sb", "game-a", 1, "cfg-sb",
            initial, bindings, overBudgetFactor: overBudgetFactor, overBudgetWindow: overBudgetWindow);
    }

    private static Task<FrameReceiptReply> SlowCommit(int ms, CommitFrameRequest request) =>
        Task.Delay(ms).ContinueWith(_ => Receipt(request));

    private static FrameReceiptReply Receipt(CommitFrameRequest request) => new()
    {
        CommitId = request.CommitId, RoomId = request.RoomId, Tick = request.PrevTick + 1,
        StateVersion = request.PrevStateVersion + 1
    };

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

    /// <summary>A frame store whose replies are scripted per send; the script runs out, then
    /// <see cref="DefaultCommit"/> answers (a plain success by default).</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly List<ReadInboxRequest> _inboxReads = new();
        private readonly List<CommitFrameRequest> _commitRequests = new();

        public Queue<Func<CommitFrameRequest, Task<FrameReceiptReply>>> CommitSteps { get; } = new();
        public Func<CommitFrameRequest, Task<FrameReceiptReply>>? DefaultCommit { get; set; }

        public int InboxReadCount { get { lock (_gate) return _inboxReads.Count; } }
        public int CommitCount { get { lock (_gate) return _commitRequests.Count; } }

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
                : DefaultCommit ?? (commit => Task.FromResult(Receipt(commit)));
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

        public int SuspendCount { get { lock (_gate) return _suspends.Count; } }

        public string[] SuspendReasons { get { lock (_gate) return _suspends.ToArray(); } }

        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default)
        {
            lock (_gate) _suspends.Add(reason);
            return Task.CompletedTask;
        }

        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
