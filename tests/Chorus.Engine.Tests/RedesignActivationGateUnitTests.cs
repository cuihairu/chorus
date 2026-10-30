using System.Security.Cryptography;
using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The per-room activation gate (FIX-ROOM-03): prepare and recovery interleave on one room
/// id only through the gate, so concurrent callers never both decide the room is unknown
/// and start a second tick loop. The loser sees the winner's actor and answers through it;
/// a failed activation releases the gate for the next legal attempt.
/// </summary>
public sealed class RedesignActivationGateUnitTests
{
    [Fact]
    public async Task ConcurrentPreparesStartExactlyOneLoopAndTouchTheStoreOnce()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var replies = await Task.WhenAll(Enumerable.Range(1, 5)
            .Select(index => runtime.PrepareRoomAsync(Prepare($"op-{index}"))));
        Assert.Equal(1, replies.Count(reply => !reply.Duplicate));
        Assert.Equal(4, replies.Count(reply => reply.Duplicate));
        // The four losers never reached the data plane: one prepare, one activation.
        Assert.Equal(1, rounds.PrepareCalls);
        Assert.Equal(1, rounds.ActivateCalls);
        Assert.NotNull(runtime.FindRoom("room-gate"));
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 2)),
            "The single admitted room never committed a frame.");
    }

    [Fact]
    public async Task RecoveryWaitsForAnInFlightPrepareInsteadOfReplayingTheRoom()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());
        var insidePrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rounds.Prepare = async _ =>
        {
            insidePrepare.SetResult();
            await releasePrepare.Task;
            return new RoundAdmissionReply { Status = "Preparing" };
        };

        var prepare = runtime.PrepareRoomAsync(Prepare("op-1"));
        await insidePrepare.Task; // prepare holds the gate inside its data-plane call
        var recovery = runtime.RecoverRoomAsync(Recover(epoch: 1));
        await Task.Delay(200);
        Assert.Equal(0, frames.LoadRecoveryCalls); // recovery waits on the gate, not on luck

        releasePrepare.SetResult();
        await prepare;
        var recovered = await recovery;
        Assert.True(recovered.Recovered); // the just-prepared actor answers for the room
        Assert.Equal("Running", recovered.Status);
        Assert.Contains("already running", recovered.Note);
        Assert.Equal(0, frames.LoadRecoveryCalls); // a room this host runs is never replayed
    }

    [Fact]
    public async Task AFailedPrepareReleasesTheGateForTheNextLegalAttempt()
    {
        var rounds = new ScriptedRoundStore
        {
            Prepare = _ => Task.FromException<RoundAdmissionReply>(
                new RpcException(new Status(StatusCode.Unavailable, "the store is away")))
        };
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        await Assert.ThrowsAsync<RpcException>(() => runtime.PrepareRoomAsync(Prepare("op-1")));
        rounds.Prepare = null; // the store is back
        var retry = await runtime.PrepareRoomAsync(Prepare("op-2"));
        Assert.False(retry.Duplicate); // the gate was released, not wedged by the failure
        Assert.Equal(1, rounds.ActivateCalls);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The retried room never committed a frame.");
    }

    [Fact]
    public async Task ConcurrentRecoveriesReplayOnceAndShareTheRecoveredActor()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var both = await Task.WhenAll(runtime.RecoverRoomAsync(Recover(epoch: 1)),
            runtime.RecoverRoomAsync(Recover(epoch: 1)));
        Assert.All(both, reply => Assert.True(reply.Recovered));
        Assert.Contains(both, reply => reply.Note.Contains("already running"));
        // One replay, one recovery-entry pin, one activation: the second caller adopted
        // the recovered actor (FIX-TAKE-03).
        Assert.Equal(1, frames.LoadRecoveryCalls);
        Assert.Equal(1, frames.PutRecoverySnapshotCalls);
        Assert.Equal(0, frames.PutSnapshotCalls);
        Assert.Equal(1, rounds.ConfirmCalls);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The recovered room never resumed ticking.");
    }

    private static PrepareRoomRequest Prepare(string operationId) => new()
    {
        RoomId = "room-gate", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = operationId, DurationTicks = 200, RandomSeed = 777, TickMs = 25,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-gate", BindingId = "binding-host-gate", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-gate", BindingId = "binding-guest-gate", Faction = GameStartConfig.BlueFaction }
        }
    };

    private static RecoverRoomRequest Recover(long epoch) => new()
    {
        RoomId = "room-gate", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch, TickMs = 25
    };

    /// <summary>A recovery plan whose newest snapshot already sits at the head: adopting it
    /// needs no frame replay, only a restore and a head snapshot write.</summary>
    private static LoadRecoveryReply HeadPlan()
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "example-factions", "1.0.0", 25, 100, RedesignGameModuleManifest.DeterministicReplay));
        var initial = module.CreateInitialState(new GameStartConfig(200, 777, new List<GameParticipant>
        {
            new("host-gate", GameStartConfig.RedFaction), new("guest-gate", GameStartConfig.BlueFaction)
        }, 0));
        var payload = module.Serialize(initial);
        return new LoadRecoveryReply
        {
            Found = true, RoomId = "room-gate", Status = "Running", Epoch = 1,
            LastTick = 0, StateVersion = 1,
            // FIX-MODULE-01: the plan carries the room's persisted identity.
            TickMs = 25, ModuleVersion = GameModuleIdentity.ModuleVersion,
            ConfigHash = GameModuleIdentity.ConfigHash, PackageHash = GameModuleIdentity.PackageHash,
            RecoveryMode = GameModuleIdentity.RecoveryMode,
            Snapshots = { new RecoverySnapshotProto
            {
                Tick = 0, SchemaVersion = initial.SchemaVersion,
                Payload = ByteString.CopyFrom(payload),
                PayloadHash = ByteString.CopyFrom(SHA256.HashData(payload)), Verified = true
            } },
            Participants = { new RoundParticipantStateProto
            {
                ParticipantId = "host-gate", BindingId = "binding-host-gate", State = "Active"
            } }
        };
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

    /// <summary>The round store with a scriptable prepare and counted activations.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private int _prepareCalls;
        private int _activateCalls;
        private int _confirmCalls;

        public Func<PrepareRoundRequest, Task<RoundAdmissionReply>>? Prepare { get; set; }

        public int PrepareCalls { get { lock (_gate) return _prepareCalls; } }
        public int ActivateCalls { get { lock (_gate) return _activateCalls; } }
        public int ConfirmCalls { get { lock (_gate) return _confirmCalls; } }

        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _prepareCalls++;
            var answer = Prepare ?? (_ => Task.FromResult(new RoundAdmissionReply
            {
                RoomId = request.RoomId, Status = "Preparing"
            }));
            return new AsyncUnaryCall<RoundAdmissionReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _activateCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _confirmCalls++;
            return new AsyncUnaryCall<RoomTakeoverReply>(
                Task.FromResult(new RoomTakeoverReply
                {
                    OwnerInstanceId = request.OwnerInstance, Status = "Active"
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The frame store: plain successes unless a hook scripts otherwise.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _commits;
        private int _loadRecoveryCalls;
        private int _putRecoverySnapshotCalls;
        private int _putSnapshotCalls;

        public Func<LoadRecoveryRequest, Task<LoadRecoveryReply>>? LoadRecoveryPlan { get; set; }

        public int CommitCount { get { lock (_gate) return _commits; } }
        public int LoadRecoveryCalls { get { lock (_gate) return _loadRecoveryCalls; } }
        public int PutRecoverySnapshotCalls { get { lock (_gate) return _putRecoverySnapshotCalls; } }
        public int PutSnapshotCalls { get { lock (_gate) return _putSnapshotCalls; } }

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

        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(LoadRecoveryRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _loadRecoveryCalls++;
            var answer = LoadRecoveryPlan ?? (_ => Task.FromResult(new LoadRecoveryReply()));
            return new AsyncUnaryCall<LoadRecoveryReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<SnapshotReceiptReply> PutRecoverySnapshotAsync(
            PutRecoverySnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _putRecoverySnapshotCalls++;
            return new AsyncUnaryCall<SnapshotReceiptReply>(Task.FromResult(new SnapshotReceiptReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _putSnapshotCalls++;
            return new AsyncUnaryCall<SnapshotReceiptReply>(Task.FromResult(new SnapshotReceiptReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>Settlement always succeeds; these rooms never finish inside a test.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
