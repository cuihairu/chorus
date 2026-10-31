using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-04, the unit leg: the upgrade window. A 1.1.0 package ships beside the
/// 1.0.0 default and both resolve under their own hash, so a room persisted under the
/// old arithmetic still replays under the old package while the new arithmetic would
/// refute those very frames (the 1.0.0 boost rule scores 10 × level plus its carried
/// seed's draw, the 1.1.0 rule is a flat 12 × level — the first boosted tick's state
/// hash parts ways). And a host that dropped the old package from its own runtime set
/// refuses the old room by name, on prepare and on recovery, with no actor and no data
/// plane round-trip — the cluster default is not silently substituted for the dropped
/// version, which is exactly what would produce a wrong frame.
/// </summary>
public sealed class RedesignModuleUpgradeUnitTests
{
    private const string OldRoom = "room-upgrade-old";

    [Fact]
    public async Task TheOldRoomReplaysUnderTheOldPackageWhileTheNewArithmeticRefutesItsFrames()
    {
        // A 1.0.0 chain with one boost at tick 1: the frame hashes are the real module's,
        // so the replay verifies them honestly — no asserted constants.
        var chain = new V1BoostChain();
        var frames = new ScriptedFrameStore(chain.Plan);
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), frames,
            new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = OldRoom, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1, TickMs = 25
        });

        Assert.True(reply.Recovered, reply.Note);
        Assert.Equal("Running", reply.Status);
        Assert.Equal(2L, reply.LastTick);
        Assert.Equal(0L, reply.SnapshotTick); // replayed from the tick-0 anchor
        Assert.Equal(2, reply.ReplayedFrames);
        Assert.Contains("Replayed 2 frames", reply.Note, StringComparison.Ordinal);

        // The upgrade evidence: the very frames that verified under the 1.0.0 rules
        // would not verify under the 1.1.0 rules — the same initial state and the same
        // boost produce different state hashes, so a host that wrongly loaded the new
        // module for an old room could not even replay it, let alone re-derive it.
        var v1 = RedesignModuleCatalog.Default.Load(RedesignModuleCatalog.Default.ManifestAt(25));
        var v2 = RedesignModuleCatalog.DefaultNext.Load(RedesignModuleCatalog.DefaultNext.ManifestAt(25));
        var config = V1BoostChain.StartConfig();
        var boost = new NormalizedInput("host-up", """{"action":"boost","level":3}""");
        var underV1 = v1.ApplyTick(v1.CreateInitialState(config), 1, [boost]);
        var underV2 = v2.ApplyTick(v2.CreateInitialState(config), 1, [boost]);
        Assert.NotEqual(
            Convert.ToHexString(v1.ComputeStateHash(underV1.State)),
            Convert.ToHexString(v2.ComputeStateHash(underV2.State)));
    }

    [Fact]
    public async Task AHostThatDroppedTheOldPackageRefusesItByNameOnPrepare()
    {
        // The upgrade host staged only the new version (plus the other modules): the old
        // package is gone from its runtime set, and the refusal names it against the set
        // the host actually ships.
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(),
            shippedPackages: [RedesignModuleCatalog.DefaultNext, RedesignModuleCatalog.Duel,
                RedesignModuleCatalog.Live]);

        var error = await Assert.ThrowsAsync<ModulePackageException>(
            () => runtime.PrepareRoomAsync(Room(packageHash: "pkg-1", operationId: "op-old")));
        // The refusal names the old package and lists the set this host ships — the new
        // version is there, the old one is not, so nothing could be misread as shipped.
        Assert.Contains($"package \"pkg-1\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("pkg-1-v2, pkg-duel-1, pkg-live-1", error.Message, StringComparison.Ordinal);
        // Refused before any data-plane work and no actor exists — nothing a retry could
        // mistake for a started room.
        Assert.Equal(0, rounds.PrepareCalls);
        Assert.Equal(0, frames.CommitCount);
        Assert.Null(runtime.FindRoom(OldRoom));

        // The same host still prepares the new version cleanly: the upgrade shipped.
        var fresh = await runtime.PrepareRoomAsync(Room(packageHash: "pkg-1-v2", operationId: "op-new"));
        Assert.Equal("Running", fresh.Status);
        Assert.Equal(1, rounds.PrepareCalls);
    }

    [Fact]
    public async Task AnEmptyHashIsNotSubstitutedWhenTheClusterDefaultIsDropped()
    {
        // Empty names the cluster default — the pin the coordinator writes. On a host
        // that dropped the default package, the empty pin must refuse too: silently
        // substituting the new version for an old room is the wrong-frame bug, not a
        // convenience. The refusal names the default package explicitly.
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), new ScriptedFrameStore(),
            new ScriptedSettlement(),
            shippedPackages: [RedesignModuleCatalog.DefaultNext, RedesignModuleCatalog.Duel,
                RedesignModuleCatalog.Live]);

        var error = await Assert.ThrowsAsync<ModulePackageException>(
            () => runtime.PrepareRoomAsync(Room(packageHash: "", operationId: "op-empty")));
        Assert.Contains(GameModuleIdentity.PackageHash, error.Message, StringComparison.Ordinal);
        Assert.Contains("pkg-1-v2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatDroppedTheOldPackageRefusesTheOldRoomsRecoveryByItsName()
    {
        // The recovery refusal is the same resolution, in the recovery path: the old
        // room's persisted package is named back against the host's shipped set, the
        // room stays put (no wrong frames, no substitute module). The plan is read once —
        // that is how the recovery learns the room's package — but no frame is replayed
        // or committed under a substitute module.
        var frames = new ScriptedFrameStore(new V1BoostChain().Plan);
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), frames,
            new ScriptedSettlement(),
            shippedPackages: [RedesignModuleCatalog.DefaultNext, RedesignModuleCatalog.Duel,
                RedesignModuleCatalog.Live]);

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = OldRoom, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        // The room's persisted package is named back against the host's shipped set — the
        // old version absent from it — and no frame under a substitute module can exist.
        Assert.Contains($"package \"{GameModuleIdentity.PackageHash}\"", reply.Note, StringComparison.Ordinal);
        Assert.Contains("pkg-1-v2, pkg-duel-1, pkg-live-1", reply.Note, StringComparison.Ordinal);
        Assert.Equal(1, frames.LoadRecoveryCalls);
        Assert.Equal(0, frames.CommitCount);
        Assert.Null(runtime.FindRoom(OldRoom));
    }

    private static PrepareRoomRequest Room(string packageHash, string operationId) => new()
    {
        RoomId = OldRoom, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = operationId, DurationTicks = 200, RandomSeed = 777, TickMs = 25,
        PackageHash = packageHash,
        Participants =
        {
            new GameParticipantProto
            {
                ParticipantId = "host-up", BindingId = "binding-host-up",
                Faction = GameStartConfig.RedFaction
            },
            new GameParticipantProto
            {
                ParticipantId = "guest-up", BindingId = "binding-guest-up",
                Faction = GameStartConfig.BlueFaction
            }
        }
    };

    /// <summary>A real 1.0.0 chain: the initial state, a boost at tick 1 and an empty
    /// tick 2, every frame hash the actual module computes — so the replay verification
    /// is earned, not asserted. The plan restates the room's persisted identity
    /// (FIX-MODULE-01) so the identity check admits it.</summary>
    private sealed class V1BoostChain
    {
        public LoadRecoveryReply Plan { get; }

        public V1BoostChain()
        {
            var v1 = RedesignModuleCatalog.Default.Load(RedesignModuleCatalog.Default.ManifestAt(25));
            var config = StartConfig();
            var initial = v1.CreateInitialState(config);
            var boost = new NormalizedInput("host-up", """{"action":"boost","level":3}""");
            var tickOne = v1.ApplyTick(initial, 1, [boost]);
            var tickTwo = v1.ApplyTick(tickOne.State, 2, []);
            Plan = new LoadRecoveryReply
            {
                Found = true, RoomId = OldRoom, Status = "Running", Epoch = 1,
                LastTick = 2, StateVersion = 3, TickMs = 25,
                ModuleVersion = GameModuleIdentity.ModuleVersion,
                ConfigHash = GameModuleIdentity.ConfigHash,
                PackageHash = GameModuleIdentity.PackageHash,
                RecoveryMode = GameModuleIdentity.RecoveryMode,
                Snapshots =
                {
                    new RecoverySnapshotProto
                    {
                        Tick = 0, SchemaVersion = initial.SchemaVersion,
                        Payload = ByteString.CopyFrom(v1.Serialize(initial)),
                        PayloadHash = ByteString.CopyFrom(v1.ComputeStateHash(initial)),
                        Verified = true
                    }
                },
                Frames =
                {
                    new RecoveryFrameProto
                    {
                        Tick = 1, InputEventIds = { "evt-boost" },
                        StateHash = ByteString.CopyFrom(v1.ComputeStateHash(tickOne.State)),
                        ModuleVersion = GameModuleIdentity.ModuleVersion,
                        ConfigHash = GameModuleIdentity.ConfigHash
                    },
                    new RecoveryFrameProto
                    {
                        Tick = 2, StateHash = ByteString.CopyFrom(v1.ComputeStateHash(tickTwo.State)),
                        ModuleVersion = GameModuleIdentity.ModuleVersion,
                        ConfigHash = GameModuleIdentity.ConfigHash
                    }
                },
                Deliveries =
                {
                    new RecoveryDeliveryProto
                    {
                        EventId = "evt-boost", BindingId = "binding-host-up",
                        PayloadJson = """{"action":"boost","level":3}"""
                    }
                },
                Participants =
                {
                    new RoundParticipantStateProto
                    {
                        ParticipantId = "host-up", BindingId = "binding-host-up", State = "Active"
                    }
                }
            };
        }

        public static GameStartConfig StartConfig() => new(200, 777,
            [new GameParticipant("host-up", GameStartConfig.RedFaction),
             new GameParticipant("guest-up", GameStartConfig.BlueFaction)], 0);
    }

    /// <summary>The frame store: a scriptable recovery plan (counted), an empty inbox and
    /// every commit request captured.</summary>
    private sealed class ScriptedFrameStore(LoadRecoveryReply? plan = null) : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly List<CommitFrameRequest> _commits = new();
        private int _loadRecoveryCalls;

        public int LoadRecoveryCalls { get { lock (_gate) return _loadRecoveryCalls; } }
        public int CommitCount { get { lock (_gate) return _commits.Count; } }

        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(LoadRecoveryRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _loadRecoveryCalls++;
            return new AsyncUnaryCall<LoadRecoveryReply>(Task.FromResult(plan ?? new LoadRecoveryReply()),
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
        private int _prepareCalls;

        public int PrepareCalls { get { lock (_gate) return _prepareCalls; } }

        public IReadOnlyList<SuspendRoundRequest> Suspensions
        {
            get { lock (_gate) return _suspensions.ToList(); }
        }

        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _prepareCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Preparing" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

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
