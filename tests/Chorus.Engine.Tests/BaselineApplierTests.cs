using Chorus.Client;
using Chorus.Rpc;
using Google.Protobuf;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-01 acceptance (客户端只对匹配房间、epoch 和基线的增量推进状态): the
/// client baseline contract - the first snapshot establishes the baseline and
/// deltas chain on state_version alone, duplicates and regressions drop, a jump
/// or a missing baseline asks for a full snapshot, a different epoch never chains
/// onto the old baseline, and a module with no delta contract syncs by snapshots.
/// The state_version == base_version + 1 server invariant is a hard rule: a delta
/// outside it is a protocol error, and the invariant (not a base-vs-current
/// comparison) is what tells a duplicate delta apart from a missing baseline.
/// </summary>
public sealed class BaselineApplierTests
{
    private const string Room = "room-a";
    private const long Epoch = 2;

    [Fact]
    public void FirstSnapshotEstablishesTheBaseline()
    {
        var applier = NewApplier();

        Assert.False(applier.Status.HasBaseline);
        Assert.Equal(-1, applier.Status.Tick);
        Assert.Equal(-1, applier.Status.StateVersion);
        Assert.Null(applier.Status.ModuleVersion);
        Assert.Null(applier.Status.ConfigHash);
        Assert.False(applier.Status.Terminal);

        var decision = applier.Decide(Snapshot(Room, Epoch, tick: 0, version: 1));

        Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
        Assert.True(applier.Status.HasBaseline);
        Assert.Equal(0, applier.Status.Tick);
        Assert.Equal(1, applier.Status.StateVersion);
        Assert.Equal("mod-1", applier.Status.ModuleVersion);
        Assert.Equal("cfg-1", applier.Status.ConfigHash);
    }

    [Fact]
    public void ConsecutiveDeltasAdvanceTheBaseline()
    {
        var applier = NewApplier();
        Establish(applier);

        Assert.Equal(BaselineDecisionKind.ApplyDelta,
            applier.Decide(Delta(Room, Epoch, tick: 1, version: 2, baseVersion: 1)).Kind);
        Assert.Equal(1, applier.Status.Tick);
        Assert.Equal(2, applier.Status.StateVersion);

        Assert.Equal(BaselineDecisionKind.ApplyDelta,
            applier.Decide(Delta(Room, Epoch, tick: 2, version: 3, baseVersion: 2)).Kind);
        Assert.Equal(2, applier.Status.Tick);
        Assert.Equal(3, applier.Status.StateVersion);
    }

    [Fact]
    public void VersionOnlyDeltaAtTheSameTickApplies()
    {
        var applier = NewApplier();
        Establish(applier, tick: 5, version: 6);

        var decision = applier.Decide(Delta(Room, Epoch, tick: 5, version: 7, baseVersion: 6));

        Assert.Equal(BaselineDecisionKind.ApplyDelta, decision.Kind);
        Assert.Equal(5, applier.Status.Tick);
        Assert.Equal(7, applier.Status.StateVersion);
    }

    [Fact]
    public void DuplicateSnapshotDrops()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        var decision = applier.Decide(Snapshot(Room, Epoch, tick: 4, version: 5));

        Assert.Equal(BaselineDecisionKind.Drop, decision.Kind);
        Assert.Equal(BaselineReason.Duplicate, decision.Reason);
        Assert.Equal(5, applier.Status.StateVersion);
    }

    [Fact]
    public void RegressedSnapshotDrops()
    {
        var applier = NewApplier();
        Establish(applier, tick: 6, version: 7);

        var decision = applier.Decide(Snapshot(Room, Epoch, tick: 5, version: 6));

        Assert.Equal(BaselineDecisionKind.Drop, decision.Kind);
        Assert.Equal(BaselineReason.Regression, decision.Reason);
        Assert.Equal(7, applier.Status.StateVersion);
    }

    [Fact]
    public void ForwardSnapshotRebaselines()
    {
        var applier = NewApplier();
        Establish(applier, tick: 2, version: 3);

        var decision = applier.Decide(Snapshot(Room, Epoch, tick: 9, version: 10));

        Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
        Assert.Equal(9, applier.Status.Tick);
        Assert.Equal(10, applier.Status.StateVersion);
    }

    [Fact]
    public void FirstDeltaWithoutABaselineRebaselines()
    {
        var applier = NewApplier();

        var decision = applier.Decide(Delta(Room, Epoch, tick: 1, version: 2, baseVersion: 1));

        Assert.Equal(BaselineDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(BaselineReason.MissingBaseline, decision.Reason);
        Assert.False(applier.Status.HasBaseline);
    }

    [Fact]
    public void DuplicateDeltaDropsNotRebaselines()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        // The invariant (version == base + 1) makes base == current - 1 a duplicate,
        // not a missing baseline.
        var decision = applier.Decide(Delta(Room, Epoch, tick: 4, version: 5, baseVersion: 4));

        Assert.Equal(BaselineDecisionKind.Drop, decision.Kind);
        Assert.Equal(BaselineReason.Duplicate, decision.Reason);
        Assert.Equal(5, applier.Status.StateVersion);
    }

    [Fact]
    public void RegressedDeltaDrops()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        var decision = applier.Decide(Delta(Room, Epoch, tick: 2, version: 3, baseVersion: 2));

        Assert.Equal(BaselineDecisionKind.Drop, decision.Kind);
        Assert.Equal(BaselineReason.Regression, decision.Reason);
        Assert.Equal(5, applier.Status.StateVersion);
    }

    [Fact]
    public void GappedDeltaRebaselines()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        // A real gap carries a valid invariant (version = base + 1) whose base sits
        // ahead of the client baseline: the client at v5 missed v6, so the next delta
        // it sees is base 6 / version 7.
        var decision = applier.Decide(Delta(Room, Epoch, tick: 6, version: 7, baseVersion: 6));

        Assert.Equal(BaselineDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(BaselineReason.Gap, decision.Reason);
        Assert.Equal(5, applier.Status.StateVersion);
    }

    [Fact]
    public void DeltaInvariantViolationIsInvalid()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        var decision = applier.Decide(Delta(Room, Epoch, tick: 5, version: 6, baseVersion: 3));

        Assert.Equal(BaselineDecisionKind.Invalid, decision.Kind);
        Assert.Equal(BaselineReason.ProtocolDeltaInvariant, decision.Reason);
    }

    [Fact]
    public void CrossEpochSnapshotRebaselinesBothDirections()
    {
        var applier = NewApplier();
        Establish(applier, tick: 0, version: 1);

        var higher = applier.Decide(Snapshot(Room, Epoch + 1, tick: 0, version: 1));
        Assert.Equal(BaselineDecisionKind.RebaselineRequired, higher.Kind);
        Assert.Equal(BaselineReason.CrossEpoch, higher.Reason);
        Assert.Equal(Epoch + 1, higher.TargetEpoch);

        var lower = applier.Decide(Snapshot(Room, Epoch - 1, tick: 0, version: 1));
        Assert.Equal(BaselineDecisionKind.RebaselineRequired, lower.Kind);
        Assert.Equal(BaselineReason.CrossEpoch, lower.Reason);
        Assert.Equal(Epoch - 1, lower.TargetEpoch);

        // The old baseline is untouched: epochs never chain, in either direction.
        Assert.Equal(Epoch, applier.Status.Epoch);
        Assert.Equal(1, applier.Status.StateVersion);
    }

    [Fact]
    public void CrossEpochDeltaRebaselines()
    {
        var applier = NewApplier();
        Establish(applier, tick: 0, version: 1);

        var decision = applier.Decide(Delta(Room, Epoch + 3, tick: 1, version: 2, baseVersion: 1));

        Assert.Equal(BaselineDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(BaselineReason.CrossEpoch, decision.Reason);
        Assert.Equal(Epoch + 3, decision.TargetEpoch);
    }

    [Fact]
    public void ForeignOrEmptyMessagesAreInvalid()
    {
        var applier = NewApplier();

        var wrongRoomSnapshot = applier.Decide(Snapshot("room-b", Epoch, tick: 0, version: 1));
        Assert.Equal(BaselineDecisionKind.Invalid, wrongRoomSnapshot.Kind);
        Assert.Equal(BaselineReason.ProtocolRoomMismatch, wrongRoomSnapshot.Reason);

        var wrongRoomDelta = applier.Decide(Delta("room-b", Epoch, tick: 1, version: 2, baseVersion: 1));
        Assert.Equal(BaselineDecisionKind.Invalid, wrongRoomDelta.Kind);
        Assert.Equal(BaselineReason.ProtocolRoomMismatch, wrongRoomDelta.Reason);

        var empty = applier.Decide(new SyncMessage());
        Assert.Equal(BaselineDecisionKind.Invalid, empty.Kind);
        Assert.Equal(BaselineReason.ProtocolEmptyMessage, empty.Reason);
    }

    [Fact]
    public void SnapshotOnlyContractRejectsDelta()
    {
        var applier = NewApplier(contract: DeltaContract.SnapshotOnly);
        Establish(applier, tick: 0, version: 1);

        var decision = applier.Decide(Delta(Room, Epoch, tick: 1, version: 2, baseVersion: 1));

        Assert.Equal(BaselineDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(BaselineReason.NoDeltaContract, decision.Reason);
        Assert.Equal(1, applier.Status.StateVersion);
    }

    [Fact]
    public void SnapshotOnlyContractStillAppliesSnapshots()
    {
        var applier = NewApplier(contract: DeltaContract.SnapshotOnly);

        var decision = applier.Decide(Snapshot(Room, Epoch, tick: 0, version: 1));

        Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
        Assert.True(applier.Status.HasBaseline);
    }

    [Fact]
    public void ResyncRequiredRebaselines()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        var decision = applier.Decide(Resync(Room, currentEpoch: 7));

        Assert.Equal(BaselineDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(BaselineReason.Resync, decision.Reason);
        Assert.Equal(7, decision.TargetEpoch);
        Assert.Equal(5, applier.Status.StateVersion);
    }

    [Fact]
    public void ModuleOrConfigMismatchIsInvalid()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        var moduleMismatch = applier.Decide(Snapshot(Room, Epoch, tick: 5, version: 6, moduleVersion: "mod-2"));
        Assert.Equal(BaselineDecisionKind.Invalid, moduleMismatch.Kind);
        Assert.Equal(BaselineReason.ProtocolModuleConfigMismatch, moduleMismatch.Reason);

        var configMismatch = applier.Decide(Delta(Room, Epoch, tick: 5, version: 6, baseVersion: 5, configHash: "cfg-2"));
        Assert.Equal(BaselineDecisionKind.Invalid, configMismatch.Kind);
        Assert.Equal(BaselineReason.ProtocolModuleConfigMismatch, configMismatch.Reason);
    }

    [Fact]
    public void RoundResultIsTerminal()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);

        var decision = applier.Decide(Result(Room, Epoch, tick: 100, version: 6));

        Assert.Equal(BaselineDecisionKind.ApplyResult, decision.Kind);
        Assert.True(applier.Status.Terminal);
        Assert.Equal(100, applier.Status.Tick);
        Assert.Equal(6, applier.Status.StateVersion);
    }

    [Fact]
    public void RoundResultWithoutABaselineAppliesTerminal()
    {
        var applier = NewApplier();

        // A released room answers a correct subscriber with a bare round result
        // (RoomRuntime.LateSubscriberAnswer): no snapshot is behind it.
        var decision = applier.Decide(Result(Room, Epoch, tick: 90, version: 9));

        Assert.Equal(BaselineDecisionKind.ApplyResult, decision.Kind);
        Assert.True(applier.Status.Terminal);
        Assert.False(applier.Status.HasBaseline);
        Assert.Equal(90, applier.Status.Tick);
        Assert.Equal(9, applier.Status.StateVersion);
    }

    [Fact]
    public void StaleRoundResultDrops()
    {
        var applier = NewApplier();
        Establish(applier, tick: 6, version: 7);

        var decision = applier.Decide(Result(Room, Epoch, tick: 5, version: 6));

        Assert.Equal(BaselineDecisionKind.Drop, decision.Kind);
        Assert.Equal(BaselineReason.Regression, decision.Reason);
        Assert.False(applier.Status.Terminal);
        Assert.Equal(7, applier.Status.StateVersion);
    }

    [Fact]
    public void DeltaAfterTerminalDrops()
    {
        var applier = NewApplier();
        Establish(applier, tick: 4, version: 5);
        Assert.Equal(BaselineDecisionKind.ApplyResult, applier.Decide(Result(Room, Epoch, tick: 100, version: 6)).Kind);

        var decision = applier.Decide(Delta(Room, Epoch, tick: 101, version: 7, baseVersion: 6));

        Assert.Equal(BaselineDecisionKind.Drop, decision.Kind);
        Assert.Equal(BaselineReason.Duplicate, decision.Reason);
        Assert.Equal(6, applier.Status.StateVersion);
    }

    [Fact]
    public void TickRegressionIsInvalid()
    {
        var applier = NewApplier();
        Establish(applier, tick: 10, version: 10);

        var delta = applier.Decide(Delta(Room, Epoch, tick: 9, version: 11, baseVersion: 10));
        Assert.Equal(BaselineDecisionKind.Invalid, delta.Kind);
        Assert.Equal(BaselineReason.ProtocolTickRegression, delta.Reason);

        var snapshot = applier.Decide(Snapshot(Room, Epoch, tick: 9, version: 11));
        Assert.Equal(BaselineDecisionKind.Invalid, snapshot.Kind);
        Assert.Equal(BaselineReason.ProtocolTickRegression, snapshot.Reason);
    }

    [Fact]
    public void FirstSnapshotAtVersionZeroIsInvalid()
    {
        var applier = NewApplier();

        var versionZero = applier.Decide(Snapshot(Room, Epoch, tick: 0, version: 0));
        Assert.Equal(BaselineDecisionKind.Invalid, versionZero.Kind);
        Assert.Equal(BaselineReason.ProtocolVersionRange, versionZero.Reason);

        var negativeTick = applier.Decide(Snapshot(Room, Epoch, tick: -1, version: 1));
        Assert.Equal(BaselineDecisionKind.Invalid, negativeTick.Kind);
        Assert.Equal(BaselineReason.ProtocolVersionRange, negativeTick.Reason);
    }

    [Fact]
    public void ConstructorRejectsABlankRoomOrNegativeEpoch()
    {
        Assert.Throws<ArgumentException>(() => new BaselineApplier("", Epoch, DeltaContract.Full));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaselineApplier(Room, -1, DeltaContract.Full));
    }

    private static BaselineApplier NewApplier(long epoch = Epoch, DeltaContract contract = DeltaContract.Full) =>
        new(Room, epoch, contract);

    private static void Establish(BaselineApplier applier, long tick = 0, long version = 1)
    {
        var decision = applier.Decide(Snapshot(Room, applier.Status.Epoch, tick, version));
        Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
    }

    private static SyncMessage Snapshot(string roomId, long epoch, long tick, long version,
        string moduleVersion = "mod-1", string configHash = "cfg-1") => new()
    {
        Snapshot = new SnapshotMessage
        {
            RoomId = roomId, Epoch = epoch, Tick = tick, StateVersion = version,
            ModuleVersion = moduleVersion, ConfigHash = configHash,
            StatePayload = ByteString.Empty, StateSchemaVersion = 1
        }
    };

    private static SyncMessage Delta(string roomId, long epoch, long tick, long version, long baseVersion,
        string moduleVersion = "mod-1", string configHash = "cfg-1") => new()
    {
        Delta = new DeltaMessage
        {
            RoomId = roomId, Epoch = epoch, Tick = tick, StateVersion = version,
            BaseVersion = baseVersion, ModuleVersion = moduleVersion, ConfigHash = configHash
        }
    };

    private static SyncMessage Result(string roomId, long epoch, long tick, long version) => new()
    {
        RoundResult = new RoundResultMessage
        {
            RoomId = roomId, Epoch = epoch, Tick = tick, StateVersion = version, ResultJson = "{}"
        }
    };

    private static SyncMessage Resync(string roomId, long currentEpoch) => new()
    {
        ResyncRequired = new ResyncRequiredMessage { RoomId = roomId, CurrentEpoch = currentEpoch, Reason = "test" }
    };
}
