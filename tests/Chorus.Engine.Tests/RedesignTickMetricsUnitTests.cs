using System.Diagnostics.Metrics;
using Chorus.GameServer;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-PERF-01, the unit leg: every tick pays its time to its own instrument — the inbox
/// pull, the module tick, the serialize and the commit each land in a histogram of their
/// own, no label ever carries a room id, and delaying exactly one store phase (the commit
/// round trip) lifts that instrument alone while the untouched phases stay low, which is
/// the whole point of separating them. The recovery read and replay are their own
/// instruments, outside the tick set.
/// </summary>
public sealed class RedesignTickMetricsUnitTests
{
    private const string TestMeter = "Chorus.Room.Tests";

    private readonly ITestOutputHelper _output;

    public RedesignTickMetricsUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ADelayedCommitPhaseRaisesOnlyItsOwnInstrument()
    {
        var manifest = new RedesignGameModuleManifest("example-factions", "1.0.0", 5,
            SnapshotEveryTicks: 1, RedesignGameModuleManifest.DeterministicReplay);
        var module = new ExampleGameModule(manifest);
        var state = module.CreateInitialState(new GameStartConfig(200, 4242,
            [new GameParticipant("host-1", GameStartConfig.RedFaction),
             new GameParticipant("guest-1", GameStartConfig.BlueFaction)]));
        // A private meter: whatever ticks parallel suites drive must not land here.
        using var metrics = new RoomMetrics(TestMeter);
        using var samples = new MeterSamples(TestMeter);
        var frames = new ScriptedFrameStore(commitDelayMs: 60);
        await using var actor = new RoomActor(new IdleRuntime(), frames, module, "game-a",
            "room-metrics-1", "game-1", 1, "cfg-1", state,
            new Dictionary<string, string>(), lastTick: 0, stateVersion: 1,
            hangAfterTick: 0, hangMs: 0, metrics: metrics);
        actor.Start();

        // Run until the delayed phase has answered enough ticks, whatever the cadence
        // drift under an occasionally loaded machine.
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (samples.Count("room.tick.commit") < 10 && !deadline.IsCompleted)
            await Task.Delay(50);

        // Stop the clock before reading it: a tick lands its commit before its whole,
        // so live counts can straddle an in-flight tick and disagree by one. After the
        // actor is disposed every instrument holds exactly one sample per completed
        // tick, and the counts are structurally equal.
        await actor.DisposeAsync();
        var snap = samples.Snapshot();
        var commits = snap.TryGetValue("room.tick.commit", out var c) ? c : [];
        Assert.True(commits.Length >= 10, $"only {commits.Length} commit samples");
        _output.WriteLine($"{commits.Length} ticks: commit {commits.Min():0.0}..{commits.Max():0.0} ms, " +
            $"apply ≤ {MaxOf(snap, "room.tick.apply"):0.0} ms, " +
            $"inbox ≤ {MaxOf(snap, "room.tick.inbox_pull"):0.0} ms");

        // The delayed phase rose: every commit sample carries the injected delay, and so
        // does every whole-tick sample — the tick cannot finish before its commit does.
        Assert.True(commits.All(value => value >= 55.0),
            $"commit floor {commits.Min():0.0} ms is below the injected delay");
        Assert.All(snap["room.tick.whole"], value => Assert.True(value >= 55.0));

        // The untouched phases stayed fast where it counts — the median, robust against
        // the scheduler spikes a shared machine throws at any single sample: the delay
        // did not leak into their instruments.
        static double Median(double[] values)
        {
            var ordered = values.Order().ToArray();
            return ordered.Length % 2 == 1
                ? ordered[ordered.Length / 2]
                : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        }
        Assert.True(Median(snap["room.tick.apply"]) < 20.0, "the apply median rose");
        Assert.True(Median(snap["room.tick.inbox_pull"]) < 20.0, "the inbox median rose");
        Assert.True(Median(snap["room.tick.serialize"]) < 20.0, "the serialize median rose");

        // Every committed tick paid every phase exactly once — no instrument counted
        // another's work — and no sample ever carried a label.
        Assert.Equal(commits.Length, snap["room.tick.whole"].Length);
        Assert.Equal(commits.Length, snap["room.tick.inbox_pull"].Length);
        Assert.Equal(commits.Length, snap["room.tick.apply"].Length);
        Assert.Equal(commits.Length, snap["room.tick.serialize"].Length);
        Assert.Equal(0, samples.TaggedMeasurements);
        Assert.Equal(0, snap.TryGetValue("room.tick.commit_retry", out _)
            ? snap["room.tick.commit_retry"].Length : 0);
    }

    private static double MaxOf(Dictionary<string, double[]> snapshot, string instrument) =>
        snapshot.TryGetValue(instrument, out var values) ? values.Max() : 0.0;

    [Fact]
    public async Task RecoveryPhasesRecordTheirOwnInstruments()
    {
        using var metrics = new RoomMetrics(TestMeter);
        using var samples = new MeterSamples(TestMeter);
        var module = new ExampleGameModule(new RedesignGameModuleManifest("example-factions",
            "1.0.0", 5, RedesignGameModuleManifest.Default.SnapshotEveryTicks,
            RedesignGameModuleManifest.DeterministicReplay));
        var initial = module.CreateInitialState(new GameStartConfig(120, 4242,
            [new GameParticipant("host-1", GameStartConfig.RedFaction)]));
        var frames = new ScriptedFrameStore(commitDelayMs: 0, recoveryPlan: new LoadRecoveryReply
        {
            Found = true, RoomId = "room-metrics-2", Status = "Running", Epoch = 1,
            LastTick = 0, StateVersion = 1,
            // FIX-MODULE-01: the plan carries the room's persisted identity.
            TickMs = 25, ModuleVersion = GameModuleIdentity.ModuleVersion,
            ConfigHash = GameModuleIdentity.ConfigHash, PackageHash = GameModuleIdentity.PackageHash,
            RecoveryMode = GameModuleIdentity.RecoveryMode,
            Snapshots = { new RecoverySnapshotProto
            {
                Tick = 0, SchemaVersion = initial.SchemaVersion,
                Payload = ByteString.CopyFrom(module.Serialize(initial)),
                PayloadHash = ByteString.CopyFrom(module.ComputeStateHash(initial))
            } }
        });
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), frames,
            new ScriptedSettlement(), metrics: metrics);

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-metrics-2", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.True(reply.Recovered);
        // One window read, one replay pass (the anchor already sits at the head) — the
        // recovery phases are separate instruments from the tick set.
        Assert.Equal(1, samples.Count("room.recovery.read"));
        Assert.Equal(1, samples.Count("room.recovery.replay"));
        // The resumed actor's first ticks land too — but under a loaded CI runner the
        // first commit can lag a fixed sleep, so poll with a deadline instead.
        var resumed = Task.Delay(TimeSpan.FromSeconds(30));
        while (samples.Count("room.tick.commit") < 1 && !resumed.IsCompleted)
            await Task.Delay(50);
        Assert.True(samples.Count("room.tick.commit") >= 1);
        Assert.Equal(0, samples.TaggedMeasurements);
    }

    /// <summary>The frame store with an injectable commit delay: the phase a test slows
    /// down without touching the module or the cadence.</summary>
    private sealed class ScriptedFrameStore(int commitDelayMs,
        LoadRecoveryReply? recoveryPlan = null) : FrameStore.FrameStoreClient
    {
        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(
            LoadRecoveryRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            if (recoveryPlan is null)
                throw new RpcException(new Status(StatusCode.NotFound, "no plan"));
            return new AsyncUnaryCall<LoadRecoveryReply>(Task.FromResult(recoveryPlan),
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
            CancellationToken cancellationToken = default)
        {
            var reply = Task.Run(async () =>
            {
                if (commitDelayMs > 0) await Task.Delay(commitDelayMs);
                return new FrameReceiptReply
                {
                    CommitId = request.CommitId, RoomId = request.RoomId,
                    Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
                };
            });
            return new AsyncUnaryCall<FrameReceiptReply>(reply, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutRecoverySnapshotAsync(
            PutRecoverySnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    /// <summary>Admissions confirm; suspensions would be a surprise but do not fail the actor.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoomTakeoverReply
                {
                    OwnerInstanceId = request.OwnerInstance, Status = "Active"
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> SuspendRoundAsync(SuspendRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply
                {
                    RoomId = request.RoomId, Status = "Suspended"
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    /// <summary>The lifecycle a lone actor needs: settling and suspending are no-ops.</summary>
    private sealed class IdleRuntime : IRoomLifecycle
    {
        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
