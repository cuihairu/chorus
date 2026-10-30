using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The recovery entry's known-actor contract (FIX-ROOM-02): when RecoverRoom meets an actor
/// this process already holds, the answer comes from the actor's own state — never from the
/// dictionary alone. Only a same-epoch live loop answers Running; a completed actor of the
/// same epoch answers the Finishing tail; stopped, disposed and stale-epoch actors refuse
/// with the observed state, the recorded reason and the real committed head.
/// </summary>
public sealed class RedesignRecoverEntryUnitTests
{
    [Fact]
    public async Task ALiveActorOfTheSameEpochAnswersRunningWithItsRealHead()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 200);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 2)),
            "The actor never committed a frame.");
        var head = actor.Status().LastCommittedTick;
        Assert.True(head >= 2);

        var reply = RoomRuntime.AnswerForKnownActor(Recover(epoch: 1), actor.Status());
        Assert.True(reply.Recovered);
        Assert.Equal("Running", reply.Status);
        Assert.Equal(head, reply.LastTick);
        Assert.Contains("already running", reply.Note);

        // The same dictionary entry answers differently once the actor itself is gone:
        // a disposed object is not a running room, whatever the dictionary says.
        await actor.DisposeAsync();
        var afterDispose = RoomRuntime.AnswerForKnownActor(Recover(epoch: 1), actor.Status());
        Assert.False(afterDispose.Recovered);
        Assert.Equal("Disposed", afterDispose.Status);
        Assert.Equal(head, afterDispose.LastTick);
        Assert.Contains("no longer ticks", afterDispose.Note);
    }

    [Fact]
    public async Task AStoppedActorRefusesAndCarriesItsReason()
    {
        var store = new ScriptedFrameStore
        {
            Commit = _ => Task.FromException<FrameReceiptReply>(new RpcException(new Status(
                StatusCode.FailedPrecondition, "The room ownership fence rejected the caller.")))
        };
        var actor = NewRoom(store, durationTicks: 200);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Status().State == RoomRunState.Stopped)),
            "The actor never stopped on the fence.");

        var reply = RoomRuntime.AnswerForKnownActor(Recover(epoch: 1), actor.Status());
        Assert.False(reply.Recovered);
        Assert.Equal("Stopped", reply.Status);
        Assert.Equal(0, reply.LastTick);
        Assert.Contains("frame commit rejected at tick 1", reply.Note);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task ACompletedActorOfTheSameEpochAnswersTheFinishingTailWithItsRealHead()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 3);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Completed)),
            "The actor never committed its final frame.");

        var reply = RoomRuntime.AnswerForKnownActor(Recover(epoch: 1), actor.Status());
        Assert.True(reply.Recovered);
        Assert.Equal("Finishing", reply.Status);
        Assert.Equal(3, reply.LastTick);
        Assert.Contains("no battle ticks", reply.Note);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task AWrongEpochActorIsRefusedEvenWhileLiveOrCompleted()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 3);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 1)),
            "The actor never committed a frame.");

        var live = RoomRuntime.AnswerForKnownActor(Recover(epoch: 2), actor.Status());
        Assert.False(live.Recovered);
        Assert.Contains("epoch 1, not 2", live.Note);

        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Completed)),
            "The actor never committed its final frame.");
        var finished = RoomRuntime.AnswerForKnownActor(Recover(epoch: 2), actor.Status());
        Assert.False(finished.Recovered); // epoch gates before the Finishing tail, as the plan path does
        Assert.Equal(3, finished.LastTick);
        await actor.DisposeAsync();
    }

    private static RecoverRoomRequest Recover(long epoch) => new()
    {
        RoomId = "room-re", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch, TickMs = 25
    };

    private static RoomActor NewRoom(ScriptedFrameStore store, long durationTicks)
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "recover-entry-tests", "1.0.0-test", 25, 100,
            RedesignGameModuleManifest.DeterministicReplay));
        var participants = new List<GameParticipant>
        {
            new("host-re", GameStartConfig.RedFaction), new("guest-re", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(new GameStartConfig(durationTicks, 777, participants, 0));
        var bindings = new Dictionary<string, string> { ["binding-host-re"] = "host-re" };
        return new RoomActor(NoOps, store, module, "game-a", "room-re", "game-1", 1, "cfg-re",
            initial, bindings);
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

    /// <summary>A store that answers plain successes unless a fault is scripted.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _commits;

        public Func<CommitFrameRequest, Task<FrameReceiptReply>>? Commit { get; set; }

        public int CommitCount { get { lock (_gate) return _commits; } }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits++;
            var answer = Commit ?? (commit => Task.FromResult(new FrameReceiptReply
            {
                CommitId = commit.CommitId, RoomId = commit.RoomId,
                Tick = commit.PrevTick + 1, StateVersion = commit.PrevStateVersion + 1
            }));
            return new AsyncUnaryCall<FrameReceiptReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The actor never reaches the real runtime in these tests.</summary>
    private sealed class NoOpLifecycle : IRoomLifecycle
    {
        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly NoOpLifecycle NoOps = new();
}
