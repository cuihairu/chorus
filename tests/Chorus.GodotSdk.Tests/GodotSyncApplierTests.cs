using Chorus.GodotSdk;
using Xunit;

namespace Chorus.GodotSdk.Tests;

/// <summary>
/// The Godot applier's ordered decision ladder (EK-1): a port of the Unity SDK's
/// SyncApplier, so the rules are the reference rules name for name. Each fact pins one
/// rung of the ladder — apply, drop, rebaseline and invalid each fire for exactly the
/// reason the reference answers, and nothing but an apply moves the baseline. The
/// cross-engine test (EK-2) runs the Unity and Godot appliers on the same frames and
/// pins them equal; this lane is the ladder's own ground truth.
/// </summary>
public sealed class GodotSyncApplierTests
{
    private const string Room = "room-1";
    private const long Epoch = 3;

    [Fact]
    public void AFirstSnapshotAppliesAndStampsTheBaseline()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(SnapshotLike());

        Assert.Equal(SyncDecisionKind.ApplySnapshot, decision.Kind);
        Assert.Null(decision.Reason);
        var status = applier.Status;
        Assert.True(status.HasBaseline);
        Assert.False(status.Terminal);
        Assert.Equal(5, status.Tick);
        Assert.Equal(2, status.StateVersion);
        Assert.Equal("module-1", status.ModuleVersion);
        Assert.Equal("cfg-1", status.ConfigHash);
    }

    [Fact]
    public void ADuplicateSnapshotDropsAndKeepsTheBaseline()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike());

        Assert.Equal(SyncDecisionKind.Drop, decision.Kind);
        Assert.Equal(SyncReasons.Duplicate, decision.Reason);
        Assert.Equal(5, applier.Status.Tick);
        Assert.Equal(2, applier.Status.StateVersion);
    }

    [Fact]
    public void ADuplicateStateVersionDropsEvenWhenTheTickDiffers()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike(tick: 99));

        Assert.Equal(SyncDecisionKind.Drop, decision.Kind);
        Assert.Equal(SyncReasons.Duplicate, decision.Reason);
    }

    [Fact]
    public void ARegressionSnapshotDrops()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike(stateVersion: 1, tick: 1));

        Assert.Equal(SyncDecisionKind.Drop, decision.Kind);
        Assert.Equal(SyncReasons.Regression, decision.Reason);
    }

    [Fact]
    public void AVersionJumpWithATickRegressionIsInvalid()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike(stateVersion: 10, tick: 1));

        Assert.Equal(SyncDecisionKind.Invalid, decision.Kind);
        Assert.Equal(SyncReasons.ProtocolTickRegression, decision.Reason);
    }

    [Fact]
    public void ACrossEpochSnapshotRebaselinesToTheNewEpoch()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike(epoch: 9));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.CrossEpoch, decision.Reason);
        Assert.Equal(9, decision.TargetEpoch);
        // The baseline stays untouched: a rebaseline does not move it.
        Assert.Equal(Epoch, applier.Status.Epoch);
        Assert.Equal(2, applier.Status.StateVersion);
    }

    [Fact]
    public void AForeignRoomIsInvalid()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike(roomId: "room-2"));

        Assert.Equal(SyncDecisionKind.Invalid, decision.Kind);
        Assert.Equal(SyncReasons.ProtocolRoomMismatch, decision.Reason);
    }

    [Fact]
    public void AModuleOrConfigChangeIsInvalid()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(SnapshotLike(moduleVersion: "module-2"));

        Assert.Equal(SyncDecisionKind.Invalid, decision.Kind);
        Assert.Equal(SyncReasons.ProtocolModuleConfigMismatch, decision.Reason);
    }

    [Fact]
    public void AVersionOutOfRangeIsInvalid()
    {
        var applier = NewApplierWithBaseline();

        Assert.Equal(SyncDecisionKind.Invalid,
            applier.Decide(SnapshotLike(stateVersion: 0)).Kind);
        Assert.Equal(SyncDecisionKind.Invalid,
            applier.Decide(SnapshotLike(tick: -1)).Kind);
    }

    [Fact]
    public void AnEmptyFrameIsInvalid()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(new SyncFrame());

        Assert.Equal(SyncDecisionKind.Invalid, decision.Kind);
        Assert.Equal(SyncReasons.ProtocolEmptyMessage, decision.Reason);
    }

    [Fact]
    public void AMissingBaselineRebaselinesBeforeTheFirstDelta()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(DeltaLike(stateVersion: 2, tick: 1, baseVersion: 1));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.MissingBaseline, decision.Reason);
    }

    [Fact]
    public void ASnapshotOnlyModuleRefusesEveryDelta()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.SnapshotOnly);
        applier.Decide(SnapshotLike());

        var decision = applier.Decide(DeltaLike(stateVersion: 3, tick: 6, baseVersion: 2));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.NoDeltaContract, decision.Reason);
    }

    [Fact]
    public void TheNextChainDeltaAppliesAndAdvancesTheBaseline()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(DeltaLike(stateVersion: 3, tick: 6, baseVersion: 2));

        Assert.Equal(SyncDecisionKind.ApplyDelta, decision.Kind);
        Assert.Equal(6, applier.Status.Tick);
        Assert.Equal(3, applier.Status.StateVersion);
    }

    [Fact]
    public void AGapRebaselinesRatherThanApplying()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(DeltaLike(stateVersion: 10, tick: 20, baseVersion: 9));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.Gap, decision.Reason);
        // The baseline never moved: the gap asked for a snapshot.
        Assert.Equal(2, applier.Status.StateVersion);
    }

    [Fact]
    public void AStaleDeltaDrops()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(DeltaLike(stateVersion: 1, tick: 1, baseVersion: 0));

        Assert.Equal(SyncDecisionKind.Drop, decision.Kind);
        Assert.Equal(SyncReasons.Regression, decision.Reason);
    }

    [Fact]
    public void ADeltaOutsideTheBasePlusOneInvariantIsInvalid()
    {
        var applier = NewApplierWithBaseline();

        // stateVersion 4 but base 2: corruption, not a gap.
        var decision = applier.Decide(DeltaLike(stateVersion: 4, tick: 6, baseVersion: 2));

        Assert.Equal(SyncDecisionKind.Invalid, decision.Kind);
        Assert.Equal(SyncReasons.ProtocolDeltaInvariant, decision.Reason);
    }

    [Fact]
    public void ARoundResultTerminatesTheRoom()
    {
        var applier = NewApplierWithBaseline();

        var decision = applier.Decide(ResultLike(stateVersion: 3, tick: 7));

        Assert.Equal(SyncDecisionKind.ApplyResult, decision.Kind);
        Assert.True(applier.Status.Terminal);
        // A terminal room refuses every later frame, even a snapshot.
        Assert.Equal(SyncDecisionKind.Drop,
            applier.Decide(SnapshotLike(stateVersion: 4, tick: 8)).Kind);
    }

    [Fact]
    public void ASuspendedFrameEndsTheStreamForTheGameNotTheApplier()
    {
        // The park notice is the edge wire's lane frame: the applier never sees it, so
        // an applier fed an empty frame after it is an invalid empty message — the game
        // owns the suspended frame.
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);
        Assert.Equal(SyncDecisionKind.Invalid,
            applier.Decide(new SyncFrame { Kind = SyncFrameKind.Suspended }).Kind);
    }

    [Fact]
    public void AResyncRebaselinesAtTheCurrentEpoch()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(new SyncFrame
        {
            Kind = SyncFrameKind.ResyncRequired,
            ResyncRequired = new ResyncRequiredFrame { RoomId = Room, CurrentEpoch = 4, Reason = "peer" },
        });

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.Resync, decision.Reason);
        Assert.Equal(4, decision.TargetEpoch);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static SyncApplier NewApplierWithBaseline()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);
        var decision = applier.Decide(SnapshotLike());
        Assert.Equal(SyncDecisionKind.ApplySnapshot, decision.Kind);
        return applier;
    }

    private static SyncFrame SnapshotLike(string roomId = Room, long epoch = Epoch,
        long tick = 5, long stateVersion = 2, string moduleVersion = "module-1",
        string configHash = "cfg-1") => new SyncFrame
    {
        Kind = SyncFrameKind.Snapshot,
        Snapshot = new SnapshotFrame
        {
            RoomId = roomId,
            Epoch = epoch,
            Tick = tick,
            StateVersion = stateVersion,
            ModuleVersion = moduleVersion,
            ConfigHash = configHash,
            StatePayload = new byte[] { 1, 2, 3 },
            StateSchemaVersion = 1,
        },
    };

    private static SyncFrame DeltaLike(string roomId = Room, long epoch = Epoch,
        long tick = 1, long stateVersion = 1, long baseVersion = 0,
        string moduleVersion = "module-1", string configHash = "cfg-1") => new SyncFrame
    {
        Kind = SyncFrameKind.Delta,
        Delta = new DeltaFrame
        {
            RoomId = roomId,
            Epoch = epoch,
            Tick = tick,
            StateVersion = stateVersion,
            BaseVersion = baseVersion,
            ModuleVersion = moduleVersion,
            ConfigHash = configHash,
        },
    };

    private static SyncFrame ResultLike(string roomId = Room, long epoch = Epoch,
        long tick = 1, long stateVersion = 1) => new SyncFrame
    {
        Kind = SyncFrameKind.RoundResult,
        RoundResult = new RoundResultFrame
        {
            RoomId = roomId,
            Epoch = epoch,
            Tick = tick,
            StateVersion = stateVersion,
            ResultJson = "{\"winner\":\"red\"}",
        },
    };
}
