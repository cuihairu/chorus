using System.Security.Cryptography;
using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-03: whether a committed frame carries the complete checkpoint is the
/// package's recovery mode, not a host-wide setting — a FullCheckpoint room checkpoints
/// every committed tick while a deterministic one still rides its manifest cadence —
/// and recovery follows the same mode: a FullCheckpoint room restores its head
/// checkpoint directly (replayed frames zero, frame hashes never consulted — its
/// module is non-deterministic by construction, so no replay could verify), and a room
/// whose head checkpoint is missing is refused by name and parked visibly instead of
/// being silently rebuilt from a stale snapshot.
/// </summary>
public sealed class RedesignModuleCheckpointUnitTests
{
    private const string LiveRoom = "room-ckpt-live";
    private const string DefaultRoom = "room-ckpt-default";

    [Fact]
    public async Task TheCheckpointCadenceFollowsThePackageMode()
    {
        var frames = new ScriptedFrameStore { Inbox = GiftEveryTick() };
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), frames,
            new ScriptedSettlement());

        // The live package (FullCheckpoint): every committed tick carries the complete
        // state in the frame transaction.
        var live = await runtime.PrepareRoomAsync(Room(LiveRoom, "pkg-live-1", "op-ckpt-live"));
        Assert.Equal("Running", live.Status);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitsCount(LiveRoom) >= 3)),
            "The live room never committed three frames.");
        foreach (var commit in frames.CommitsOf(LiveRoom))
        {
            Assert.True(commit.SnapshotPayload.Length > 0,
                $"The live room's tick {commit.PrevTick + 1} committed without its checkpoint.");
            Assert.Equal(1, commit.SnapshotSchemaVersion);
            Assert.Equal(32, commit.SnapshotHash.Length);
        }

        // The default package (DeterministicReplay): the first hundred ticks commit
        // bare frames — its cadence is the manifest's snapshot-every-ticks, unchanged.
        var deterministic = await runtime.PrepareRoomAsync(Room(DefaultRoom, "pkg-1", "op-ckpt-default"));
        Assert.Equal("Running", deterministic.Status);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitsCount(DefaultRoom) >= 3)),
            "The default room never committed three frames.");
        foreach (var commit in frames.CommitsOf(DefaultRoom))
            Assert.True(commit.SnapshotPayload.Length == 0,
                $"The default room's tick {commit.PrevTick + 1} carried a checkpoint off cadence.");
    }

    [Fact]
    public async Task AFullCheckpointRoomRecoversFromItsHeadCheckpointWithoutReplay()
    {
        // The frames below carry hashes that no replay could verify — garbage bytes a
        // DeterministicReplay recovery would treat as a refuted chain. A FullCheckpoint
        // room recovers anyway, from its head checkpoint alone, and reports zero
        // replayed frames: the live module's clock jitter means re-deriving those
        // frames is impossible by construction, so the recovery never attempts it.
        var live = RedesignModuleCatalog.Live.Load(RedesignModuleCatalog.Live.ManifestAt(25));
        var seeded = new LiveGameModule(live.Manifest, clock: () => 4L); // jitter 1, fixed
        var config = StartConfig();
        var tickOne = seeded.ApplyTick(seeded.CreateInitialState(config), 1,
            [new NormalizedInput("host-ckpt", """{"action":"boost","level":3}""")]);
        var tickTwo = seeded.ApplyTick(tickOne.State, 2, []);
        var headPayload = seeded.Serialize(tickTwo.State);

        var frames = new ScriptedFrameStore(() => LivePlan(seeded, config, headPayload, tickTwo.State.SchemaVersion));
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), frames,
            new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = LiveRoom, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1, TickMs = 25
        });

        Assert.True(reply.Recovered);
        Assert.Equal("Running", reply.Status);
        Assert.Equal(2L, reply.LastTick);
        Assert.Equal(2L, reply.SnapshotTick);
        Assert.Equal(0, reply.ReplayedFrames); // the no-replay proof
        Assert.Contains("resumes without replay", reply.Note, StringComparison.Ordinal);

        // The restored checkpoint became a running room: the actor commits onward from
        // the head it restored, still checkpointing every tick.
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.Commits.Any(
                commit => commit.RoomId == LiveRoom && commit.PrevTick == 2))),
            "The recovered live room never committed past the restored head.");
        var onward = frames.Commits.First(commit => commit.RoomId == LiveRoom && commit.PrevTick == 2);
        Assert.True(onward.SnapshotPayload.Length > 0);
    }

    [Fact]
    public async Task AFullCheckpointRoomWithoutAHeadCheckpointIsRefusedAndSuspended()
    {
        // The newest checkpoint is stale (tick 0 under a head at tick 3): replay cannot
        // close the gap for a non-deterministic module, so the recovery refuses by name
        // and parks the room visibly — never silently rebuilding from the stale state.
        var live = new LiveGameModule(RedesignModuleCatalog.Live.ManifestAt(25), clock: () => 4L);
        var initial = live.CreateInitialState(StartConfig());
        var payload = live.Serialize(initial);
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore(() => new LoadRecoveryReply
        {
            Found = true, RoomId = LiveRoom, Status = "Running", Epoch = 1,
            LastTick = 3, StateVersion = 4,
            TickMs = 25, ModuleVersion = "0.9.0", ConfigHash = "cfg-live-1",
            PackageHash = "pkg-live-1", RecoveryMode = RedesignGameModuleManifest.FullCheckpoint,
            Snapshots =
            {
                new RecoverySnapshotProto
                {
                    Tick = 0, SchemaVersion = initial.SchemaVersion,
                    Payload = ByteString.CopyFrom(payload),
                    PayloadHash = ByteString.CopyFrom(SHA256.HashData(payload)), Verified = true
                }
            },
            Participants =
            {
                new RoundParticipantStateProto
                {
                    ParticipantId = "host-ckpt", BindingId = "binding-host-ckpt", State = "Active"
                }
            }
        });
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = LiveRoom, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        Assert.Equal("Suspended", reply.Status);
        Assert.Contains("newest checkpoint is at tick 0", reply.Note, StringComparison.Ordinal);
        Assert.Contains("not the head tick 3", reply.Note, StringComparison.Ordinal);
        Assert.Contains("cannot replay to the head", reply.Note, StringComparison.Ordinal);
        Assert.EndsWith("the room is suspended.", reply.Note, StringComparison.Ordinal);
        // The suspension actually landed on the round store, named for this refusal.
        var suspension = Assert.Single(rounds.Suspensions);
        Assert.Equal(LiveRoom, suspension.RoomId);
        Assert.Contains("recovery failed", suspension.Reason, StringComparison.Ordinal);
        Assert.Null(runtime.FindRoom(LiveRoom));
    }

    /// <summary>The live plan the head-restore recovery runs: identity facts of the live
    /// package, a verified head checkpoint at the room's head tick, and frame hashes no
    /// replay could verify — recovery must not depend on them.</summary>
    private static LoadRecoveryReply LivePlan(LiveGameModule seeded, GameStartConfig config,
        byte[] headPayload, int schemaVersion)
    {
        var initial = seeded.CreateInitialState(config);
        var initialPayload = seeded.Serialize(initial);
        return new LoadRecoveryReply
        {
            Found = true, RoomId = LiveRoom, Status = "Running", Epoch = 1,
            LastTick = 2, StateVersion = 3,
            TickMs = 25, ModuleVersion = "0.9.0", ConfigHash = "cfg-live-1",
            PackageHash = "pkg-live-1", RecoveryMode = RedesignGameModuleManifest.FullCheckpoint,
            Snapshots =
            {
                new RecoverySnapshotProto
                {
                    Tick = 0, SchemaVersion = initial.SchemaVersion,
                    Payload = ByteString.CopyFrom(initialPayload),
                    PayloadHash = ByteString.CopyFrom(SHA256.HashData(initialPayload)), Verified = true
                },
                new RecoverySnapshotProto
                {
                    Tick = 2, SchemaVersion = schemaVersion,
                    Payload = ByteString.CopyFrom(headPayload),
                    PayloadHash = ByteString.CopyFrom(SHA256.HashData(headPayload)), Verified = true
                }
            },
            Frames =
            {
                new RecoveryFrameProto
                {
                    Tick = 1, InputEventIds = { "evt-boost" },
                    // Garbage by construction: a replay would refute the chain here.
                    StateHash = ByteString.CopyFrom(new byte[32])
                },
                new RecoveryFrameProto { Tick = 2, StateHash = ByteString.CopyFrom(new byte[32]) }
            },
            Deliveries =
            {
                new RecoveryDeliveryProto
                {
                    EventId = "evt-boost", BindingId = "binding-host-ckpt",
                    PayloadJson = """{"action":"boost","level":3}"""
                }
            },
            Participants =
            {
                new RoundParticipantStateProto
                {
                    ParticipantId = "host-ckpt", BindingId = "binding-host-ckpt", State = "Active"
                }
            }
        };
    }

    private static GameStartConfig StartConfig() => new(200, 777,
        [new GameParticipant("host-ckpt", GameStartConfig.RedFaction),
         new GameParticipant("guest-ckpt", GameStartConfig.BlueFaction)], 0);

    /// <summary>One gift for the host binding on every inbox read: input noise the live
    /// module ignores, steady enough to keep both rooms ticking.</summary>
    private static Func<ReadInboxReply> GiftEveryTick() => () => new ReadInboxReply
    {
        Items = { new InboxItemProto
        {
            EventId = "evt-gift", BindingId = "binding-host-ckpt",
            PayloadJson = """{"kind":"gift","amount":7}"""
        } }
    };

    private static PrepareRoomRequest Room(string roomId, string packageHash, string operationId) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = operationId, DurationTicks = 200, RandomSeed = 777, TickMs = 25,
        PackageHash = packageHash,
        Participants =
        {
            new GameParticipantProto
            {
                ParticipantId = "host-ckpt", BindingId = "binding-host-ckpt",
                Faction = GameStartConfig.RedFaction
            },
            new GameParticipantProto
            {
                ParticipantId = "guest-ckpt", BindingId = "binding-guest-ckpt",
                Faction = GameStartConfig.BlueFaction
            }
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

    /// <summary>The frame store: a scriptable inbox, a scriptable recovery plan, every
    /// commit request captured per room.</summary>
    private sealed class ScriptedFrameStore(Func<LoadRecoveryReply>? plan = null) : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly List<CommitFrameRequest> _commits = new();

        public Func<ReadInboxReply>? Inbox { get; set; }

        public IReadOnlyList<CommitFrameRequest> Commits
        {
            get { lock (_gate) return _commits.ToList(); }
        }

        public int CommitsCount(string roomId)
        {
            lock (_gate) return _commits.Count(commit => commit.RoomId == roomId);
        }

        public IReadOnlyList<CommitFrameRequest> CommitsOf(string roomId)
        {
            lock (_gate) return _commits.Where(commit => commit.RoomId == roomId).ToList();
        }

        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(LoadRecoveryRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(plan?.Invoke() ?? new LoadRecoveryReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(Inbox?.Invoke() ?? new ReadInboxReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits.Add(request);
            return new AsyncUnaryCall<FrameReceiptReply>(
                Task.FromResult(new FrameReceiptReply
                {
                    CommitId = request.CommitId, RoomId = request.RoomId,
                    Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
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

    /// <summary>Prepares and activations succeed; every suspension is captured.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private readonly List<SuspendRoundRequest> _suspensions = new();

        public IReadOnlyList<SuspendRoundRequest> Suspensions
        {
            get { lock (_gate) return _suspensions.ToList(); }
        }

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
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _suspensions.Add(request);
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Suspended" }),
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
