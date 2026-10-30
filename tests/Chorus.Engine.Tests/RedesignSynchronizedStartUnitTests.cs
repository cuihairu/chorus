using Chorus.GameServer;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-PERF-03, the unit leg: a room prepared with deferred activation is held — Preparing,
/// not ticking, the round never activated — until ActivateRoom releases it across the start
/// line. Release is fenced (a stale owner or wrong epoch never starts the room), idempotent
/// (a retry after a lost reply answers duplicate and never double-starts the loop), and the
/// default non-deferred prepare keeps its prepare-activate-start shape unchanged.
/// </summary>
public sealed class RedesignSynchronizedStartUnitTests
{
    private readonly ITestOutputHelper _output;

    public RedesignSynchronizedStartUnitTests(ITestOutputHelper output) => _output = output;

    private static PrepareRoomRequest Prepare(string roomId, long epoch, string operationId,
        bool deferred, int durationTicks = 400, int tickMs = 5) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch,
        OperationId = operationId, DurationTicks = durationTicks, RandomSeed = 7, TickMs = tickMs,
        DeferredActivation = deferred,
        Participants = { new GameParticipantProto { BindingId = "binding-1", ParticipantId = "host-1", Faction = "red" } }
    };

    [Fact]
    public async Task AHeldRoomDoesNotTickUntilReleased()
    {
        var frames = new CountingFrameStore();
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var prepared = await runtime.PrepareRoomAsync(Prepare("room-sync-1", 1, "op-sync-1", deferred: true));
        Assert.Equal("Prepared", prepared.Status);
        Assert.False(prepared.Duplicate);

        var held = runtime.FindRoom("room-sync-1");
        Assert.NotNull(held);
        Assert.Equal(RoomRunState.Preparing, held.Status().State);

        // Held means held: long past several tick cadences, nothing committed and the round
        // was never activated.
        await Task.Delay(80);
        Assert.Equal(0, frames.Commits);
        Assert.Equal(0, rounds.ActivateRoundCalls);

        var released = await runtime.ActivateRoomAsync(new ActivateRoomRequest
        {
            RoomId = "room-sync-1", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
            OperationId = "op-sync-1/release"
        });
        Assert.Equal("Running", released.Status);
        Assert.False(released.Duplicate);
        Assert.Equal(1, rounds.ActivateRoundCalls);

        var deadline = Task.Delay(TimeSpan.FromSeconds(10));
        while (frames.Commits < 5 && !deadline.IsCompleted)
            await Task.Delay(20);
        Assert.True(frames.Commits >= 5, $"only {frames.Commits} commits after release");
        Assert.Equal(RoomRunState.Running, held.Status().State);
        _output.WriteLine($"{frames.Commits} commits after release");
    }

    [Fact]
    public async Task ReleaseIsFencedAndIdempotent()
    {
        var frames = new CountingFrameStore();
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ActivateRoomAsync(
            new ActivateRoomRequest
            {
                RoomId = "room-unknown", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
                OperationId = "op-x"
            }));

        await runtime.PrepareRoomAsync(Prepare("room-sync-2", 3, "op-sync-2", deferred: true));

        // A stale fence never releases the room.
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ActivateRoomAsync(
            new ActivateRoomRequest
            {
                RoomId = "room-sync-2", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 2,
                OperationId = "op-stale"
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ActivateRoomAsync(
            new ActivateRoomRequest
            {
                RoomId = "room-sync-2", GameId = "game-a", OwnerInstanceId = "game-other", Epoch = 3,
                OperationId = "op-wrong-owner"
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ActivateRoomAsync(
            new ActivateRoomRequest
            {
                RoomId = "room-sync-2", GameId = "game-b", OwnerInstanceId = "game-1", Epoch = 3,
                OperationId = "op-wrong-game"
            }));
        Assert.Equal(0, frames.Commits);

        // A retry of the deferred prepare answers duplicate and does not un-hold anything.
        var again = await runtime.PrepareRoomAsync(Prepare("room-sync-2", 3, "op-sync-2", deferred: true));
        Assert.True(again.Duplicate);
        Assert.Equal(0, frames.Commits);

        var released = await runtime.ActivateRoomAsync(new ActivateRoomRequest
        {
            RoomId = "room-sync-2", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 3,
            OperationId = "op-sync-2/release"
        });
        Assert.False(released.Duplicate);

        // A retry after a lost reply: duplicate, and the loop still runs exactly once.
        var replay = await runtime.ActivateRoomAsync(new ActivateRoomRequest
        {
            RoomId = "room-sync-2", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 3,
            OperationId = "op-sync-2/release"
        });
        Assert.True(replay.Duplicate);
        Assert.Equal(1, rounds.ActivateRoundCalls);

        var deadline = Task.Delay(TimeSpan.FromSeconds(10));
        var ticksAtFirstCheck = 0L;
        while (!deadline.IsCompleted)
        {
            var status = runtime.FindRoom("room-sync-2")!.Status();
            if (status.LastCommittedTick >= 6) { ticksAtFirstCheck = status.LastCommittedTick; break; }
            await Task.Delay(20);
        }
        await Task.Delay(120);
        var after = runtime.FindRoom("room-sync-2")!.Status().LastCommittedTick;
        // One loop, one cadence: the head keeps advancing at the module's pace, not doubling.
        Assert.True(ticksAtFirstCheck >= 6, "the room never reached tick 6");
        Assert.True(after > ticksAtFirstCheck && after - ticksAtFirstCheck <= 40,
            $"head jumped from {ticksAtFirstCheck} to {after} — more than one loop is ticking");
        _output.WriteLine($"head {ticksAtFirstCheck} -> {after} after 120 ms");
    }

    [Fact]
    public async Task AFailedReleaseRollsBackAndARetryReleasesTheRoom()
    {
        var frames = new CountingFrameStore();
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());
        await runtime.PrepareRoomAsync(Prepare("room-sync-4", 5, "op-sync-4", deferred: true));

        // The round flip fails: the release must roll its flag back, or a retry would
        // answer duplicate and hold the room forever.
        rounds.FailNextActivation = true;
        await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => runtime.ActivateRoomAsync(
            new ActivateRoomRequest
            {
                RoomId = "room-sync-4", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 5,
                OperationId = "op-sync-4/release"
            }));
        Assert.Equal(RoomRunState.Preparing, runtime.FindRoom("room-sync-4")!.Status().State);

        var released = await runtime.ActivateRoomAsync(new ActivateRoomRequest
        {
            RoomId = "room-sync-4", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 5,
            OperationId = "op-sync-4/release"
        });
        Assert.False(released.Duplicate);
        Assert.Equal(2, rounds.ActivateRoundCalls);

        var deadline = Task.Delay(TimeSpan.FromSeconds(10));
        while (frames.Commits < 5 && !deadline.IsCompleted)
            await Task.Delay(20);
        Assert.True(frames.Commits >= 5, $"only {frames.Commits} commits after the retry released the room");
    }

    [Fact]
    public async Task AnImmediatePrepareStillActivatesWithinThePrepare()
    {
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, new CountingFrameStore(), new ScriptedSettlement());

        var prepared = await runtime.PrepareRoomAsync(Prepare("room-sync-3", 1, "op-sync-3", deferred: false));

        Assert.Equal("Running", prepared.Status);
        Assert.False(prepared.Duplicate);
        Assert.Equal(1, rounds.ActivateRoundCalls);
    }

    /// <summary>Counts commits; answers immediately with the next tick's receipt.</summary>
    private sealed class CountingFrameStore : FrameStore.FrameStoreClient
    {
        private int _commits;

        public int Commits { get { lock (this) return _commits; } }

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            lock (this) _commits++;
            return new(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutRecoverySnapshotAsync(
            PutRecoverySnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(LoadRecoveryRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new LoadRecoveryReply
            {
                Found = false, RoomId = request.RoomId
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    /// <summary>Prepares and activates on request, counting the activations it is asked for;
    /// <see cref="FailNextActivation"/> fails exactly one activation to order.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private int _activateRoundCalls;

        public int ActivateRoundCalls { get { lock (this) return _activateRoundCalls; } }

        public bool FailNextActivation { get; set; }


        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply
            {
                RoomId = request.RoomId, Status = "Preparing", Epoch = request.Epoch
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            lock (this) _activateRoundCalls++;
            var fail = FailNextActivation;
            if (fail) FailNextActivation = false;
            var reply = fail
                ? Task.FromException<RoundAdmissionReply>(new Grpc.Core.RpcException(
                    new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "the scripted flip is down")))
                : Task.FromResult(new RoundAdmissionReply
                {
                    RoomId = request.RoomId, Status = "Running", Epoch = request.Epoch
                });
            return new(reply, Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoomTakeoverReply
            {
                OwnerInstanceId = request.OwnerInstance, Status = "Active"
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> SuspendRoundAsync(SuspendRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply
            {
                RoomId = request.RoomId, Status = "Suspended"
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
