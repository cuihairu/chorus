using System.Security.Cryptography;
using Chorus.GameServer;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The fenced activation of a completed recovery (FIX-TAKE-03): an unknown room recovers
/// under its Recovering fence — the pin travels the constrained recovery entry, the fenced
/// confirm flips the ownership Active, and only then does the loop tick. A recovery whose
/// pin or confirm is refused neither activates, settles nor ticks, and a retry after the
/// activation replays through the normal snapshot entry instead.
/// </summary>
public sealed class RedesignTakeoverActivationUnitTests
{
    [Fact]
    public async Task ARecoveringRoomPinsItsHeadAndActivatesBeforeTheFirstTick()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls);
        var frames = new ScriptedFrameStore(calls) { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));

        var reply = await runtime.RecoverRoomAsync(Recover());
        Assert.True(reply.Recovered);
        Assert.Equal("Running", reply.Status);

        // The pin went through the constrained recovery entry with the takeover credentials.
        Assert.Equal(1, frames.PutRecoverySnapshotCalls);
        Assert.Equal(0, frames.PutSnapshotCalls);
        var pin = frames.RecoveryPinRequests.Single();
        Assert.Equal("game-2", pin.OwnerInstanceId);
        Assert.Equal(8, pin.Epoch);
        // The confirm names the same owner — the pinned head is the activation credential.
        var confirm = rounds.ConfirmRequests.Single();
        Assert.Equal("game-2", confirm.OwnerInstance);
        Assert.Equal(1, rounds.ConfirmCalls);
        // The room ticks only after the activation.
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The activated room never committed a frame.");
        Assert.True(calls.IndexOf("pin-recovery") < calls.IndexOf("confirm"),
            "The head was not pinned before the activation.");
        Assert.True(calls.IndexOf("confirm") < calls.IndexOf("commit"),
            "The first tick preceded the activation.");
        Assert.NotNull(runtime.FindRoom("room-take-3"));
    }

    [Fact]
    public async Task AFencedRecovererIsNeverActivatedAndNeverTicks()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls);
        var frames = new ScriptedFrameStore(calls)
        {
            LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()),
            PutRecovery = _ => Task.FromException<SnapshotReceiptReply>(new RpcException(new Status(
                StatusCode.FailedPrecondition, "The recovery snapshot fence rejected the caller."))),
            PutSnapshot = _ => Task.FromException<SnapshotReceiptReply>(new RpcException(new Status(
                StatusCode.FailedPrecondition, "The room ownership fence rejected the caller.")))
        };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));

        var reply = await runtime.RecoverRoomAsync(Recover());
        Assert.False(reply.Recovered);
        Assert.Contains("The head snapshot was rejected", reply.Note);
        Assert.Equal(0, rounds.ConfirmCalls); // a refused pin never activates
        Assert.Null(runtime.FindRoom("room-take-3"));
        Assert.Equal(0, frames.CommitCount);
    }

    [Fact]
    public async Task ARefusedActivationLeavesTheRoomUnrecoveredWithoutATick()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls)
        {
            Confirm = _ => Task.FromException<RoomTakeoverReply>(new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "The room ownership is Recovering; confirm needs an unexpired lease.")))
        };
        var frames = new ScriptedFrameStore(calls) { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));

        var reply = await runtime.RecoverRoomAsync(Recover());
        Assert.False(reply.Recovered);
        Assert.Contains("The activation was refused", reply.Note);
        Assert.Contains("unexpired lease", reply.Note);
        Assert.Equal(1, frames.PutRecoverySnapshotCalls); // the head is pinned…
        Assert.Equal(1, rounds.ConfirmCalls); // …but the confirm refuses…
        Assert.Null(runtime.FindRoom("room-take-3")); // …so no loop exists…
        Assert.Equal(0, frames.CommitCount); // …and nothing ever ticked.
    }

    [Fact]
    public async Task ARetryAfterTheActivationPinsThroughTheNormalSnapshotEntry()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls);
        var frames = new ScriptedFrameStore(calls)
        {
            LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()),
            // The recovery entry closed: the activation already flipped the ownership.
            PutRecovery = _ => Task.FromException<SnapshotReceiptReply>(new RpcException(new Status(
                StatusCode.FailedPrecondition, "The recovery snapshot fence rejected the caller.")))
        };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));

        var reply = await runtime.RecoverRoomAsync(Recover());
        Assert.True(reply.Recovered);
        Assert.Equal(1, frames.PutSnapshotCalls); // the Active twin carried the pin
        Assert.Equal(1, rounds.ConfirmCalls); // and the confirm replays the activation
        Assert.NotNull(runtime.FindRoom("room-take-3"));
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The re-recovered room never resumed ticking.");
    }

    [Fact]
    public async Task ARefusedFinishingActivationNeverSettles()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls)
        {
            Confirm = _ => Task.FromException<RoomTakeoverReply>(new RpcException(new Status(
                StatusCode.FailedPrecondition, "The room is held by 'game-9', not 'game-2'.")))
        };
        var frames = new ScriptedFrameStore(calls)
        {
            LoadRecoveryPlan = _ => Task.FromResult(HeadPlan(status: "Finishing"))
        };
        var settlement = new ScriptedSettlement(calls);
        await using var runtime = new RoomRuntime(rounds, frames, settlement);

        var reply = await runtime.RecoverRoomAsync(Recover());
        Assert.False(reply.Recovered);
        Assert.Equal("Finishing", reply.Status);
        Assert.Contains("The activation was refused", reply.Note);
        Assert.Equal(0, settlement.SettleCalls); // a stale recoverer settles nothing
    }

    private static RecoverRoomRequest Recover() => new()
    {
        RoomId = "room-take-3", GameId = "game-a", OwnerInstanceId = "game-2", Epoch = 8, TickMs = 25
    };

    /// <summary>A recovery plan whose newest snapshot already sits at the head: adopting it
    /// needs no frame replay, only a restore, a head pin and the activation.</summary>
    private static LoadRecoveryReply HeadPlan(string status = "Running")
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "example-factions", "1.0.0", 25, 100, RedesignGameModuleManifest.DeterministicReplay));
        var initial = module.CreateInitialState(new GameStartConfig(200, 777, new List<GameParticipant>
        {
            new("host-take-3", GameStartConfig.RedFaction), new("guest-take-3", GameStartConfig.BlueFaction)
        }, 0));
        var payload = module.Serialize(initial);
        return new LoadRecoveryReply
        {
            Found = true, RoomId = "room-take-3", Status = status, Epoch = 8,
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
                ParticipantId = "host-take-3", BindingId = "binding-host-take-3", State = "Active"
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

    /// <summary>The round store with a scriptable activation confirm and a call log.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private readonly IList<string> _log;
        private int _confirmCalls;

        public ScriptedRoundStore(IList<string> log) => _log = log;

        public Func<ConfirmRoomOwnershipRequest, Task<RoomTakeoverReply>>? Confirm { get; set; }

        public List<ConfirmRoomOwnershipRequest> ConfirmRequests { get; } = new();

        public int ConfirmCalls { get { lock (_gate) return _confirmCalls; } }

        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var answer = Confirm ?? (_ => Task.FromResult(new RoomTakeoverReply
            {
                OwnerInstanceId = request.OwnerInstance, Status = "Active"
            }));
            lock (_gate)
            {
                _confirmCalls++;
                ConfirmRequests.Add(request);
                _log.Add("confirm");
            }
            return new AsyncUnaryCall<RoomTakeoverReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The frame store: plain successes unless a hook scripts otherwise.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly IList<string> _log;
        private int _commits;
        private int _putRecoverySnapshotCalls;
        private int _putSnapshotCalls;

        public ScriptedFrameStore(IList<string> log) => _log = log;

        public Func<LoadRecoveryRequest, Task<LoadRecoveryReply>>? LoadRecoveryPlan { get; set; }
        public Func<PutRecoverySnapshotRequest, Task<SnapshotReceiptReply>>? PutRecovery { get; set; }
        public Func<PutSnapshotRequest, Task<SnapshotReceiptReply>>? PutSnapshot { get; set; }

        public List<PutRecoverySnapshotRequest> RecoveryPinRequests { get; } = new();

        public int CommitCount { get { lock (_gate) return _commits; } }
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
            lock (_gate)
            {
                _commits++;
                _log.Add("commit");
            }
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
            var answer = LoadRecoveryPlan ?? (_ => Task.FromResult(new LoadRecoveryReply()));
            return new AsyncUnaryCall<LoadRecoveryReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<SnapshotReceiptReply> PutRecoverySnapshotAsync(
            PutRecoverySnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var answer = PutRecovery ?? (_ => Task.FromResult(new SnapshotReceiptReply
            {
                RoomId = request.RoomId, Tick = request.Tick, Verified = true
            }));
            lock (_gate)
            {
                _putRecoverySnapshotCalls++;
                RecoveryPinRequests.Add(request);
                _log.Add("pin-recovery");
            }
            return new AsyncUnaryCall<SnapshotReceiptReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var answer = PutSnapshot ?? (_ => Task.FromResult(new SnapshotReceiptReply
            {
                RoomId = request.RoomId, Tick = request.Tick, Verified = true
            }));
            lock (_gate)
            {
                _putSnapshotCalls++;
                _log.Add("pin-normal");
            }
            return new AsyncUnaryCall<SnapshotReceiptReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>Settlement always succeeds; the call log shows whether it ran at all.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        private readonly object _gate = new();
        private readonly IList<string> _log;
        private int _settleCalls;

        public ScriptedSettlement(IList<string> log) => _log = log;

        public int SettleCalls { get { lock (_gate) return _settleCalls; } }

        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _settleCalls++;
                _log.Add("settle");
            }
            return new AsyncUnaryCall<CommitResultReply>(Task.FromResult(new CommitResultReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }
}
