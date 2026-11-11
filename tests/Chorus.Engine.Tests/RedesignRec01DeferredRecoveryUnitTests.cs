using System.Security.Cryptography;
using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-REC-01, the deferred recovery: a takeover recovery asked to defer pins the verified
/// head, builds the actor and holds it — Recovered=true, Status="Ready" — while the
/// ownership confirm (the activation grant, FIX-TAKE-03) and the first tick both wait.
/// The release is ActivateRoom: the fenced confirm flips the ownership Active for exactly
/// the takeover owner and only then the loop starts; a refused confirm rolls the release
/// back so a retry still releases, and a lost-reply retry meets the held actor answering
/// Ready — never redoing the pin or the replay. A common-start hold (FIX-PERF-03) keeps
/// its own refusal: only a recovery's hold answers Ready.
/// </summary>
public sealed class RedesignRec01DeferredRecoveryUnitTests
{
    [Fact]
    public async Task ADeferredRecoveryPinsTheHeadHoldsTheActorAndNeverGrants()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls);
        var frames = new ScriptedFrameStore(calls) { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));

        var reply = await runtime.RecoverRoomAsync(Recover());
        Assert.True(reply.Recovered);
        Assert.Equal("Ready", reply.Status);
        Assert.Contains("held", reply.Note);

        // The pin traveled the constrained recovery entry with the takeover credentials…
        Assert.Equal(1, frames.PutRecoverySnapshotCalls);
        var pin = frames.RecoveryPinRequests.Single();
        Assert.Equal("game-2", pin.OwnerInstanceId);
        Assert.Equal(8, pin.Epoch);
        // …but the grant never landed: the ownership stays Recovering for the route barrier.
        Assert.Equal(0, rounds.ConfirmCalls);
        // The actor exists and is held — nothing ticks while the routes install.
        var actor = runtime.FindRoom("room-rec-1");
        Assert.NotNull(actor);
        await Task.Delay(250);
        Assert.Equal(0, frames.CommitCount);
    }

    [Fact]
    public async Task TheDeferredReleaseConfirmsTheGrantBeforeTheFirstTick()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls);
        var frames = new ScriptedFrameStore(calls) { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));
        Assert.True((await runtime.RecoverRoomAsync(Recover())).Recovered);

        var released = await runtime.ActivateRoomAsync(new ActivateRoomRequest
        {
            RoomId = "room-rec-1", GameId = "game-a", OwnerInstanceId = "game-2", Epoch = 8,
            OperationId = "recovery-op-1/activate"
        });
        Assert.Equal("Running", released.Status);

        // The release granted through the fenced ownership confirm — never the round flip,
        // the room was never Preparing.
        Assert.Equal(1, rounds.ConfirmCalls);
        Assert.Equal(0, rounds.ActivateRoundCalls);
        var confirm = rounds.ConfirmRequests.Single();
        Assert.Equal("game-2", confirm.OwnerInstance);
        // The loop started only after the grant.
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The released room never committed a frame.");
        Assert.True(calls.IndexOf("confirm") < calls.IndexOf("commit"),
            "The first tick preceded the activation grant.");
    }

    [Fact]
    public async Task ARefusedReleaseRollsBackSoARetryStillReleases()
    {
        var calls = new List<string>();
        var refusals = 0;
        var rounds = new ScriptedRoundStore(calls)
        {
            Confirm = _ =>
            {
                if (Interlocked.Increment(ref refusals) == 1)
                    return Task.FromException<RoomTakeoverReply>(new RpcException(new Status(
                        StatusCode.FailedPrecondition,
                        "The room ownership is Recovering; confirm needs an unexpired lease.")));
                return Task.FromResult(new RoomTakeoverReply
                {
                    OwnerInstanceId = "game-2", Status = "Active"
                });
            }
        };
        var frames = new ScriptedFrameStore(calls) { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));
        Assert.True((await runtime.RecoverRoomAsync(Recover())).Recovered);

        // The first release's confirm is refused: the exception escapes, nothing ticks…
        await Assert.ThrowsAsync<RpcException>(() => runtime.ActivateRoomAsync(Activate()));
        Assert.Equal(0, frames.CommitCount);
        // …and the rollback means the retry is not answered duplicate — it releases.
        var retried = await runtime.ActivateRoomAsync(Activate());
        Assert.False(retried.Duplicate);
        Assert.Equal("Running", retried.Status);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The retried release never started the loop.");
    }

    [Fact]
    public async Task AHeldRecoveryAnswersReadyWhileACommonStartHoldStillRefuses()
    {
        var calls = new List<string>();
        var rounds = new ScriptedRoundStore(calls);
        var frames = new ScriptedFrameStore(calls) { LoadRecoveryPlan = _ => Task.FromResult(HeadPlan()) };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(calls));
        Assert.True((await runtime.RecoverRoomAsync(Recover())).Recovered);

        // A lost-reply retry meets the held recovery actor: the pin and the replay already
        // landed, so the answer is Ready and the control plane continues at the barrier.
        var held = runtime.FindRoom("room-rec-1");
        Assert.NotNull(held);
        var retry = RoomRuntime.AnswerForKnownActor(Recover(), held.Status());
        Assert.True(retry.Recovered);
        Assert.Equal("Ready", retry.Status);
        Assert.Contains("route barrier", retry.Note);

        // A prepare's common-start hold (FIX-PERF-03) keeps its own refusal — it is not a
        // recovery, and the start line, not the route barrier, is what releases it.
        var store = new ScriptedFrameStore(calls);
        var prepared = NewRoom(store, durationTicks: 200);
        prepared.HoldForCommonStart();
        var refusal = RoomRuntime.AnswerForKnownActor(Recover(), prepared.Status());
        Assert.False(refusal.Recovered);
        Assert.Equal("Preparing", refusal.Status);
        await prepared.DisposeAsync();
    }

    private static RecoverRoomRequest Recover() => new()
    {
        RoomId = "room-rec-1", GameId = "game-a", OwnerInstanceId = "game-2", Epoch = 8,
        TickMs = 25, DeferredActivation = true
    };

    private static ActivateRoomRequest Activate() => new()
    {
        RoomId = "room-rec-1", GameId = "game-a", OwnerInstanceId = "game-2", Epoch = 8,
        OperationId = "recovery-op-1/activate"
    };

    /// <summary>A recovery plan whose newest snapshot already sits at the head: adopting it
    /// needs no frame replay, only a restore, a head pin and the held actor.</summary>
    private static LoadRecoveryReply HeadPlan()
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "example-factions", "1.0.0", 25, 100, RedesignGameModuleManifest.DeterministicReplay));
        var initial = module.CreateInitialState(new GameStartConfig(200, 777, new List<GameParticipant>
        {
            new("host-rec-1", GameStartConfig.RedFaction), new("guest-rec-1", GameStartConfig.BlueFaction)
        }, 0));
        var payload = module.Serialize(initial);
        return new LoadRecoveryReply
        {
            Found = true, RoomId = "room-rec-1", Status = "Running", Epoch = 8,
            LastTick = 0, StateVersion = 1,
            TickMs = 25, ModuleVersion = GameModuleIdentity.ModuleVersion,
            ConfigHash = GameModuleIdentity.ConfigHash, PackageHash = GameModuleIdentity.PackageHash,
            RecoveryMode = GameModuleIdentity.RecoveryMode,
            OfflinePolicy = ModuleOfflinePolicy.ProductionDefault.Canonical,
            Snapshots = { new RecoverySnapshotProto
            {
                Tick = 0, SchemaVersion = initial.SchemaVersion,
                Payload = ByteString.CopyFrom(payload),
                PayloadHash = ByteString.CopyFrom(SHA256.HashData(payload)), Verified = true
            } },
            Participants = { new RoundParticipantStateProto
            {
                ParticipantId = "host-rec-1", BindingId = "binding-host-rec-1", State = "Active"
            } }
        };
    }

    private static RoomActor NewRoom(ScriptedFrameStore store, long durationTicks)
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "rec-01-tests", "1.0.0-test", 25, 100, RedesignGameModuleManifest.DeterministicReplay));
        var participants = new List<GameParticipant>
        {
            new("host-rec-1", GameStartConfig.RedFaction), new("guest-rec-1", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(new GameStartConfig(durationTicks, 777, participants, 0));
        var bindings = new Dictionary<string, string> { ["binding-host-rec-1"] = "host-rec-1" };
        return new RoomActor(NoOps, store, module, "game-a", "room-rec-1", "game-2", 8, "cfg-rec-1",
            initial, bindings);
    }

    private static readonly NoOpLifecycle NoOps = new();

    private sealed class NoOpLifecycle : IRoomLifecycle
    {
        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportEndAsync(string gameId, string roomId, long finalTick, long resultVersion,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PauseRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            long tick, long resultVersion, string reason,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    /// <summary>The round store with a scriptable activation confirm, the round-flip
    /// counter and a call log.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private readonly IList<string> _log;
        private int _confirmCalls;
        private int _activateRoundCalls;

        public ScriptedRoundStore(IList<string> log) => _log = log;

        public Func<ConfirmRoomOwnershipRequest, Task<RoomTakeoverReply>>? Confirm { get; set; }

        public List<ConfirmRoomOwnershipRequest> ConfirmRequests { get; } = new();

        public int ConfirmCalls { get { lock (_gate) return _confirmCalls; } }

        public int ActivateRoundCalls { get { lock (_gate) return _activateRoundCalls; } }

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

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(
            ActivateRoundRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _activateRoundCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
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

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(
            PutSnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
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
