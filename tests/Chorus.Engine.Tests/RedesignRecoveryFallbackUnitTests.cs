using Chorus.GameServer;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-READ-04, the unit leg: a recovery whose whole candidate window fails must not park
/// the room while older anchors remain untried — the read deepens on demand, one window at
/// a time, and only a history that is exhausted (or the safety cap) ends in the visible
/// suspension. The scripted frame store answers each candidate depth with its own window,
/// so the exact request ladder is part of the assertion; the replay chain itself is the
/// real module's, so hashes are earned, not asserted.
/// </summary>
public sealed class RedesignRecoveryFallbackUnitTests
{
    private readonly ITestOutputHelper _output;

    public RedesignRecoveryFallbackUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AFailedWindowDeepensTheReadUntilAnOlderAnchorReplays()
    {
        // The real chain: 90 ticks from the initial state, honest snapshots at 0, 30, 60
        // and per-frame hashes the module actually computes.
        var module = new ModuleChain(90, [0, 30, 60]);
        // The default window serves the two newest anchors, both corrupt on disk: the
        // tick-60 row lost its hash commitment, the tick-30 payload is garbage. A deeper
        // read surfaces the tick-0 anchor with the frames to bridge from it.
        var frames = new WindowedFrameStore(depth => depth <= 2
            ? module.View(["60:hash", "30:payload"], frameFrom: 31)
            : module.View(["60:hash", "30:payload", "0:good"], frameFrom: 1));
        var rounds = new CountingRoundStore();
        await using var runtime = new RoomRuntime(rounds, frames, new IdleSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-r4-unit", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.True(reply.Recovered);
        Assert.Equal("Running", reply.Status);
        Assert.Equal(0L, reply.SnapshotTick);
        Assert.Equal(90L, reply.LastTick);
        Assert.Equal(90, reply.ReplayedFrames);
        // The ladder is the point: the first window, then exactly one deepening — no
        // unbounded scanning, no retry of the refuted window.
        Assert.Equal([2, 4], frames.RequestedDepths);
        _output.WriteLine($"depth ladder: {string.Join(",", frames.RequestedDepths)}; {reply.Note}");
        Assert.Equal(0, rounds.SuspendCalls);
    }

    [Fact]
    public async Task AnExhaustedHistoryParksVisiblyWithoutGuessing()
    {
        // However deep the read goes, this history holds only the two corrupt anchors: the
        // deepened reply carries fewer candidates than the window asked for, which is the
        // store's way of saying the history is over.
        var module = new ModuleChain(90, [0, 30, 60]);
        var frames = new WindowedFrameStore(_ => module.View(["60:hash", "30:payload"], frameFrom: 31));
        var rounds = new CountingRoundStore();
        await using var runtime = new RoomRuntime(rounds, frames, new IdleSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-r4-unit", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        Assert.Equal("Suspended", reply.Status);
        Assert.Equal([2, 4], frames.RequestedDepths);
        Assert.Equal(1, rounds.SuspendCalls);
    }

    /// <summary>
    /// The deterministic battle chain: the initial state, honest snapshots at the given
    /// ticks and every frame hash, all produced by the real module. View() renders one
    /// recovery reply; an anchor spec of "tick:hash" corrupts the commitment, "tick:payload"
    /// corrupts the bytes and "tick:good" leaves the anchor honest.
    /// </summary>
    private sealed class ModuleChain
    {
        private readonly RedesignGameModuleManifest _manifest = new("example-factions", "1.0.0", 25,
            RedesignGameModuleManifest.Default.SnapshotEveryTicks,
            RedesignGameModuleManifest.DeterministicReplay);

        private readonly Dictionary<long, (byte[] Payload, byte[] Hash, int Schema)> _anchors = [];
        private readonly List<RecoveryFrameProto> _frames = [];

        public ModuleChain(long head, int[] anchorTicks)
        {
            var module = new ExampleGameModule(_manifest);
            var state = module.CreateInitialState(new GameStartConfig(head + 30, 4242,
                [new GameParticipant("host-1", GameStartConfig.RedFaction),
                 new GameParticipant("guest-1", GameStartConfig.BlueFaction)]));
            Keep(0, module, state);
            for (var tick = 1; tick <= head; tick++)
            {
                var outcome = module.ApplyTick(state, tick, []);
                _frames.Add(new RecoveryFrameProto
                {
                    Tick = tick,
                    StateHash = ByteString.CopyFrom(module.ComputeStateHash(outcome.State)),
                    ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
                });
                state = outcome.State;
                if (anchorTicks.Contains(tick)) Keep(tick, module, state);
            }

            void Keep(long tick, ExampleGameModule module, ExampleGameState state) =>
                _anchors[tick] = (module.Serialize(state), module.ComputeStateHash(state),
                    state.SchemaVersion);
        }

        public LoadRecoveryReply View(string[] anchorSpecs, int frameFrom) => new()
        {
            Found = true, RoomId = "room-r4-unit", Status = "Running", Epoch = 1,
            LastTick = _frames.Count, StateVersion = _frames.Count + 1,
            // FIX-MODULE-01: the plan carries the room's persisted identity; a scripted
            // recovery must restate it or the identity check refuses the plan.
            TickMs = 25, ModuleVersion = GameModuleIdentity.ModuleVersion,
            ConfigHash = GameModuleIdentity.ConfigHash, PackageHash = GameModuleIdentity.PackageHash,
            RecoveryMode = GameModuleIdentity.RecoveryMode,
            Snapshots =
            {
                anchorSpecs.Select(spec =>
                {
                    var parts = spec.Split(':');
                    var tick = long.Parse(parts[0]);
                    var anchor = _anchors[tick];
                    return new RecoverySnapshotProto
                    {
                        Tick = tick, SchemaVersion = anchor.Schema,
                        Payload = parts[1] == "payload"
                            ? ByteString.CopyFrom("not-a-snapshot"u8)
                            : ByteString.CopyFrom(anchor.Payload),
                        PayloadHash = parts[1] == "hash"
                            ? ByteString.CopyFrom(new byte[32])
                            : ByteString.CopyFrom(anchor.Hash),
                        Verified = tick != 0
                    };
                })
            },
            Frames = { _frames.Where(frame => frame.Tick >= frameFrom) }
        };
    }

    /// <summary>The frame store that answers each candidate depth from the test's script
    /// and records the ladder of windows the recoverer asked for.</summary>
    private sealed class WindowedFrameStore(Func<int, LoadRecoveryReply> window) : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly List<int> _requestedDepths = [];

        public int[] RequestedDepths { get { lock (_gate) return [.. _requestedDepths]; } }

        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(
            LoadRecoveryRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _requestedDepths.Add(request.CandidateDepth);
            return new AsyncUnaryCall<LoadRecoveryReply>(Task.FromResult(window(request.CandidateDepth)),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutRecoverySnapshotAsync(
            PutRecoverySnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    /// <summary>Round admissions and confirms succeed; suspensions are counted.</summary>
    private sealed class CountingRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private int _suspendCalls;

        public int SuspendCalls { get { lock (_gate) return _suspendCalls; } }

        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Preparing" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoomTakeoverReply
            {
                OwnerInstanceId = request.OwnerInstance, Status = "Active"
            }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> SuspendRoundAsync(SuspendRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _suspendCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(Task.FromResult(new RoundAdmissionReply
                {
                    RoomId = request.RoomId, Status = "Suspended"
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
    }

    /// <summary>Settlement accepts whatever the recovered round commits.</summary>
    private sealed class IdleSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
