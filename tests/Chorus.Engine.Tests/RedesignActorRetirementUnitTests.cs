using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Actor retirement (FIX-ROOM-04): an actor whose loop ended without a result leaves the
/// runtime's dictionary — the room id stops masquerading as running — while a finished room
/// keeps its actor for the result tail. Retirement is identity-checked, so a delayed exit
/// never removes a newer actor that has since taken the room, and repeating it never throws.
/// </summary>
public sealed class RedesignActorRetirementUnitTests
{
    [Fact]
    public async Task AStoppedActorLeavesTheDictionaryButACompletedOneStays()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var failing = await runtime.PrepareRoomAsync(Prepare("room-fail", failAfterTick: 1));
        Assert.False(failing.Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(runtime.FindRoom("room-fail") is null)),
            "The stopped actor never left the dictionary.");

        var finishing = await runtime.PrepareRoomAsync(Prepare("room-done", durationTicks: 3));
        Assert.False(finishing.Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(
            runtime.FindRoom("room-done")?.Completed == true)));
        Assert.NotNull(runtime.FindRoom("room-done")); // the result tail keeps its actor
    }

    [Fact]
    public async Task ADelayedRetirementNeverRemovesTheReplacementAndNeverThrows()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-fail", failAfterTick: 1))).Duplicate);
        var stopped = runtime.FindRoom("room-fail");
        Assert.NotNull(stopped);
        Assert.True(await WaitForAsync(() => Task.FromResult(stopped!.Status().State == RoomRunState.Stopped)),
            "The module failure never stopped the actor.");
        Assert.True(await WaitForAsync(() => Task.FromResult(runtime.FindRoom("room-fail") is null)),
            "The stopped actor never left the dictionary.");

        // The room id is free again: a fresh admission takes it with a healthy module.
        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-fail"))).Duplicate);
        var replacement = runtime.FindRoom("room-fail");
        Assert.NotNull(replacement);
        Assert.NotSame(stopped, replacement);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 2)),
            "The replacement actor never committed a frame.");

        runtime.Retire(stopped!);           // the delayed exit of the old actor…
        Assert.Same(replacement, runtime.FindRoom("room-fail")); // …removes nothing
        runtime.Retire(replacement!);       // a live actor is not retirement material
        Assert.Same(replacement, runtime.FindRoom("room-fail"));
    }

    [Fact]
    public async Task RoomStatusesCarryLiveProgressAndRetainedStoppedStatus()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        // A live actor reports its advancing head with the wall clock of its last commit.
        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-live"))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 2)),
            "The live actor never committed a frame.");
        var live = runtime.RoomStatuses().Single(entry => entry.RoomId == "room-live");
        Assert.Equal(RoomRunState.Running, live.Status.State);
        Assert.True(live.Status.LastCommittedTick >= 2);
        Assert.NotNull(live.Status.LastCommittedAt);

        // A stopped actor has left the dictionary, but its final status stays behind so the
        // progress report keeps naming the room instead of silently dropping it.
        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-fail", failAfterTick: 1))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(runtime.FindRoom("room-fail") is null)),
            "The stopped actor never left the dictionary.");
        var stopped = runtime.RoomStatuses().Single(entry => entry.RoomId == "room-fail");
        Assert.Equal(RoomRunState.Stopped, stopped.Status.State);
        Assert.NotNull(stopped.Status.StopReason);
        Assert.Contains(runtime.RoomStatuses(),
            entry => entry.RoomId == "room-live" && entry.Status.State == RoomRunState.Running);
    }

    private static PrepareRoomRequest Prepare(string roomId, long durationTicks = 200,
        int failAfterTick = 0) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = $"op-{roomId}/{failAfterTick}", DurationTicks = durationTicks,
        RandomSeed = 777, TickMs = 25, FailAfterTick = failAfterTick,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-r", BindingId = "binding-host-r", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-r", BindingId = "binding-guest-r", Faction = GameStartConfig.BlueFaction }
        }
    };

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

    /// <summary>The round store: every admission succeeds, suspensions are recorded.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Preparing" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> SuspendRoundAsync(SuspendRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Suspended" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    /// <summary>The frame store: plain inbox and commit successes.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _commits;

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
            return new AsyncUnaryCall<FrameReceiptReply>(
                Task.FromResult(new FrameReceiptReply
                {
                    CommitId = request.CommitId, RoomId = request.RoomId,
                    Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>Settlement always succeeds.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
