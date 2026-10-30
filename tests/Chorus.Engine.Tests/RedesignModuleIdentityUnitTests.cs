using System.Security.Cryptography;
using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-01, the contract leg: the room's identity is a persisted fact, never a
/// caller opinion. A recovery that restates a different tick, or a room whose package,
/// module version or checkpoint mode this host cannot load, is refused by name — the room's
/// original tick and module survive a takeover or admin retry unchanged, and the contract
/// names the missing-package and incompatible-module errors instead of silently rebuilding
/// the module from hardcoded literals.
/// </summary>
public sealed class RedesignModuleIdentityUnitTests
{
    [Fact]
    public async Task ARecoveryCanLeaveTheCadenceUnspecifiedAndUseThePersistedOne()
    {
        // 0 means "the room's persisted cadence": the contract path stays open to callers
        // that do not know the cadence, and the recovery runs the room at its own tick.
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => Plan()), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 0
        });

        Assert.True(reply.Recovered);
        Assert.Equal("Running", reply.Status);
        Assert.Equal(0L, reply.LastTick);
        Assert.Equal(0, reply.ReplayedFrames);
    }

    [Fact]
    public async Task ARecoveryThatRestatesADifferentTickIsRefused()
    {
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => Plan(tickMs: 25)), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 10
        });

        Assert.False(reply.Recovered);
        Assert.Equal("Running", reply.Status);
        Assert.Contains("does not match the room's persisted cadence 25 ms", reply.Note, StringComparison.Ordinal);
        Assert.Contains("cannot change a room's tick", reply.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoomUnderAnUnknownPackageIsRefusedAsMissing()
    {
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => Plan(packageHash: "pkg-unknown")), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        Assert.Contains("pkg-unknown", reply.Note, StringComparison.Ordinal);
        Assert.Contains("missing or unknown", reply.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoomUnderAnIncompatibleModuleVersionIsRefused()
    {
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => Plan(moduleVersion: "2.0.0")), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        Assert.Contains("2.0.0", reply.Note, StringComparison.Ordinal);
        Assert.Contains("incompatible", reply.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoomUnderAnUnknownRecoveryModeIsRefused()
    {
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => Plan(recoveryMode: "MysteryMode")), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        Assert.Contains("MysteryMode", reply.Note, StringComparison.Ordinal);
        Assert.Contains("not a known mode", reply.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoomUnderAnIncompatibleRecoveryModeIsRefused()
    {
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => Plan(recoveryMode: RedesignGameModuleManifest.FullCheckpoint)),
            new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.False(reply.Recovered);
        Assert.Contains(RedesignGameModuleManifest.FullCheckpoint, reply.Note, StringComparison.Ordinal);
        Assert.Contains("is not the mode package", reply.Note, StringComparison.Ordinal);
        Assert.Contains("incompatible", reply.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuelRoomRecoversThroughTheDuelModuleNotTheDefault()
    {
        // FIX-MODULE-02: a pkg-duel-1 room's replay only verifies under the duel rules —
        // the gift in tick 1 scores nothing there, while the default module would have
        // moved red by 7 and committed a different state hash. Recovered=true proves the
        // registry loaded the package the room persisted, not the example module.
        var duel = RedesignModuleCatalog.Duel.Load(RedesignModuleCatalog.Duel.ManifestAt(25));
        var initial = duel.CreateInitialState(new GameStartConfig(200, 777,
            [new GameParticipant("host-id", GameStartConfig.RedFaction),
             new GameParticipant("guest-id", GameStartConfig.BlueFaction)], 0));
        var tickOne = duel.ApplyTick(initial, 1,
            [new NormalizedInput("host-id", """{"kind":"gift","amount":7}""")]);
        Assert.Empty(tickOne.Effects); // the gift is noise to a duel — the default would score
        var payload = duel.Serialize(initial);
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(),
            new ScriptedFrameStore(() => new LoadRecoveryReply
            {
                Found = true, RoomId = "room-id-1", Status = "Running", Epoch = 1,
                LastTick = 1, StateVersion = 2,
                TickMs = 25, ModuleVersion = "0.9.0", ConfigHash = "cfg-duel-1",
                PackageHash = "pkg-duel-1", RecoveryMode = RedesignGameModuleManifest.DeterministicReplay,
                Snapshots =
                {
                    new RecoverySnapshotProto
                    {
                        Tick = 0, SchemaVersion = initial.SchemaVersion,
                        Payload = ByteString.CopyFrom(payload),
                        PayloadHash = ByteString.CopyFrom(SHA256.HashData(payload)), Verified = true
                    }
                },
                Frames = { new RecoveryFrameProto
                {
                    Tick = 1, InputEventIds = { "evt-gift" },
                    StateHash = ByteString.CopyFrom(duel.ComputeStateHash(tickOne.State))
                } },
                Deliveries = { new RecoveryDeliveryProto
                {
                    EventId = "evt-gift", BindingId = "binding-host-id",
                    PayloadJson = """{"kind":"gift","amount":7}"""
                } },
                Participants =
                {
                    new RoundParticipantStateProto
                    {
                        ParticipantId = "host-id", BindingId = "binding-host-id", State = "Active"
                    }
                }
            }), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-id-1", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1, TickMs = 25
        });

        Assert.True(reply.Recovered);
        Assert.Equal("Running", reply.Status);
        Assert.Equal(1, reply.LastTick);
        Assert.Equal(1, reply.ReplayedFrames);
    }

    /// <summary>A recoverable tick-0-at-head plan with the room's persisted identity, the
    /// caller's fields selectable so each contract violation can be scripted alone.</summary>
    private static LoadRecoveryReply Plan(int tickMs = 25, string moduleVersion = "1.0.0",
        string configHash = "cfg-1", string packageHash = "pkg-1",
        string recoveryMode = "DeterministicReplay")
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "example-factions", "1.0.0", 25, 100, RedesignGameModuleManifest.DeterministicReplay));
        var initial = module.CreateInitialState(new GameStartConfig(200, 777,
            [new GameParticipant("host-id", GameStartConfig.RedFaction),
             new GameParticipant("guest-id", GameStartConfig.BlueFaction)], 0));
        var payload = module.Serialize(initial);
        return new LoadRecoveryReply
        {
            Found = true, RoomId = "room-id-1", Status = "Running", Epoch = 1,
            LastTick = 0, StateVersion = 1,
            TickMs = tickMs, ModuleVersion = moduleVersion, ConfigHash = configHash,
            PackageHash = packageHash, RecoveryMode = recoveryMode,
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
                    ParticipantId = "host-id", BindingId = "binding-host-id", State = "Active"
                }
            }
        };
    }

    /// <summary>The frame store answering the scripted plan; the tick-0-at-head recovery
    /// pins the head snapshot and resumes the actor, so the commit and inbox calls answer
    /// plain successes to keep the background loop benign.</summary>
    private sealed class ScriptedFrameStore(Func<LoadRecoveryReply> plan) : FrameStore.FrameStoreClient
    {
        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(LoadRecoveryRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(plan()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutRecoverySnapshotAsync(
            PutRecoverySnapshotRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply { Tick = request.Tick, Verified = true }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    /// <summary>The fenced activation answers Active for the named owner.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoomTakeoverReply
            {
                OwnerInstanceId = request.OwnerInstance, Status = "Active"
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
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
