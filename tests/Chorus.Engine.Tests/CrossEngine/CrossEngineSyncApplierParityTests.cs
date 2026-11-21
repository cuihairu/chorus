using Chorus.GodotSdk;
using Xunit;
using UnityDeltaContractMode = Chorus.UnitySdk.DeltaContractMode;
using UnitySyncApplier = Chorus.UnitySdk.SyncApplier;
using UnitySyncDecision = Chorus.UnitySdk.SyncDecision;
using UnitySyncDecisionKind = Chorus.UnitySdk.SyncDecisionKind;
using UnitySyncFrame = Chorus.UnitySdk.SyncFrame;
using UnitySyncFrameKind = Chorus.UnitySdk.SyncFrameKind;
using UnitySyncReasons = Chorus.UnitySdk.SyncReasons;

namespace Chorus.Engine.Tests.CrossEngine;

/// <summary>
/// The applier half of the cross-engine consistency core (EK-2): the Unity SDK's
/// <c>SyncApplier</c> and its Godot port must answer the same decision on the same
/// frame — same kind, same reason, same target epoch, same baseline after — so one
/// protocol serves both engines. The frames here are the constructed ladder rungs
/// (apply, drop, rebaseline, invalid), not the golden payloads: the wire bytes are
/// pinned by the envelope facts; this pins the decision semantics.
/// </summary>
public sealed class CrossEngineSyncApplierParityTests
{
    private const string Room = "room-1";
    private const long Epoch = 3;

    public static TheoryData<string, Func<Scene>> Scenes()
    {
        TheoryData<string, Func<Scene>> data = new();
        data.Add("first-snapshot-applies", () => new Scene(
            null, Snapshot(), UnitySyncDecisionKind.ApplySnapshot, null));
        data.Add("duplicate-snapshot-drops", () => new Scene(
            Snapshot(), Snapshot(), UnitySyncDecisionKind.Drop, UnitySyncReasons.Duplicate));
        data.Add("regression-snapshot-drops", () => new Scene(
            Snapshot(), Snapshot(tick: 1, version: 1), UnitySyncDecisionKind.Drop, UnitySyncReasons.Regression));
        data.Add("cross-epoch-rebaselines", () => new Scene(
            Snapshot(), Snapshot(epoch: 9, version: 3, tick: 6), UnitySyncDecisionKind.RebaselineRequired, UnitySyncReasons.CrossEpoch));
        data.Add("foreign-room-invalid", () => new Scene(
            Snapshot(), Snapshot(roomId: "room-2", version: 3, tick: 6), UnitySyncDecisionKind.Invalid, UnitySyncReasons.ProtocolRoomMismatch));
        data.Add("module-change-invalid", () => new Scene(
            Snapshot(), Snapshot(version: 3, tick: 6, module: "module-2"), UnitySyncDecisionKind.Invalid, UnitySyncReasons.ProtocolModuleConfigMismatch));
        data.Add("version-jump-tick-regression-invalid", () => new Scene(
            Snapshot(), Snapshot(version: 10, tick: 1), UnitySyncDecisionKind.Invalid, UnitySyncReasons.ProtocolTickRegression));
        data.Add("chained-delta-applies", () => new Scene(
            Snapshot(), Delta(version: 3, tick: 6, @base: 2), UnitySyncDecisionKind.ApplyDelta, null));
        data.Add("duplicate-delta-drops", () => new Scene(
            Snapshot(), Delta(version: 2, tick: 6, @base: 1), UnitySyncDecisionKind.Drop, UnitySyncReasons.Duplicate));
        data.Add("stale-delta-drops", () => new Scene(
            Snapshot(), Delta(version: 1, tick: 1, @base: 0), UnitySyncDecisionKind.Drop, UnitySyncReasons.Regression));
        data.Add("gap-delta-rebaselines", () => new Scene(
            Snapshot(), Delta(version: 10, tick: 20, @base: 9), UnitySyncDecisionKind.RebaselineRequired, UnitySyncReasons.Gap));
        data.Add("delta-invariant-invalid", () => new Scene(
            Snapshot(), Delta(version: 4, tick: 6, @base: 2), UnitySyncDecisionKind.Invalid, UnitySyncReasons.ProtocolDeltaInvariant));
        data.Add("delta-tick-regression-invalid", () => new Scene(
            Snapshot(), Delta(version: 3, tick: 4, @base: 2), UnitySyncDecisionKind.Invalid, UnitySyncReasons.ProtocolTickRegression));
        data.Add("round-result-terminates", () => new Scene(
            Snapshot(), Result(version: 3, tick: 7), UnitySyncDecisionKind.ApplyResult, null));
        data.Add("resync-rebaselines", () => new Scene(
            null, Resync(epoch: 4), UnitySyncDecisionKind.RebaselineRequired, UnitySyncReasons.Resync));
        data.Add("missing-baseline-rebaselines", () => new Scene(
            null, Delta(version: 2, tick: 1, @base: 1), UnitySyncDecisionKind.RebaselineRequired, UnitySyncReasons.MissingBaseline));
        data.Add("empty-frame-invalid", () => new Scene(
            null, new Step(UnityKind: f => new UnitySyncFrame(), GodotKind: f => new SyncFrame()),
            UnitySyncDecisionKind.Invalid, UnitySyncReasons.ProtocolEmptyMessage));
        return data;
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void BothAppliersAnswerTheSameDecisionAndBaseline(string name, Func<Scene> scene)
    {
        var s = scene();
        var unity = RunUnity(s);
        var godot = RunGodot(s);

        // The two engines' SyncDecisionKind are distinct enum types; parity is the
        // member name, so compare the names (a renamed member fails here).
        Assert.Equal(unity.Kind.ToString(), godot.Kind.ToString());
        Assert.Equal(unity.Reason, godot.Reason);
        Assert.Equal(unity.TargetEpoch, godot.TargetEpoch);
        // The baselines agree after the scene played.
        Assert.Equal(unity.Status.Tick, godot.Status.Tick);
        Assert.Equal(unity.Status.StateVersion, godot.Status.StateVersion);
        Assert.Equal(unity.Status.HasBaseline, godot.Status.HasBaseline);
        Assert.Equal(unity.Status.Terminal, godot.Status.Terminal);
        Assert.Equal(unity.Status.ModuleVersion, godot.Status.ModuleVersion);
        Assert.Equal(unity.Status.ConfigHash, godot.Status.ConfigHash);
        // And the expected decision is pinned on the reference itself, so parity cannot
        // pass by both sides drifting the same way.
        Assert.Equal(s.ExpectedKind, unity.Kind);
        Assert.Equal(s.ExpectedReason, unity.Reason);
    }

    [Fact]
    public void ASnapshotOnlyModuleRefusesDeltasOnBothEngines()
    {
        var unityApplier = new UnitySyncApplier(Room, Epoch, UnityDeltaContractMode.SnapshotOnly);
        var godotApplier = new SyncApplier(Room, Epoch, DeltaContractMode.SnapshotOnly);
        var unityFirst = unityApplier.Decide(ToUnity(Snapshot()));
        var godotFirst = godotApplier.Decide(ToGodot(Snapshot()));
        Assert.Equal(unityFirst.Kind.ToString(), godotFirst.Kind.ToString());

        var unityDelta = unityApplier.Decide(ToUnity(Delta(version: 3, tick: 6, @base: 2)));
        var godotDelta = godotApplier.Decide(ToGodot(Delta(version: 3, tick: 6, @base: 2)));

        Assert.Equal(UnitySyncDecisionKind.RebaselineRequired, unityDelta.Kind);
        Assert.Equal(unityDelta.Kind.ToString(), godotDelta.Kind.ToString());
        Assert.Equal(unityDelta.Reason, godotDelta.Reason);
        Assert.Equal(UnitySyncReasons.NoDeltaContract, unityDelta.Reason);
    }

    // ------------------------------------------------------------------
    // The scene: an optional baseline step, then one decided step.
    // ------------------------------------------------------------------

    public sealed record Step(
        Func<int, UnitySyncFrame> UnityKind,
        Func<int, SyncFrame> GodotKind);

    public sealed record Scene(
        Step? Baseline,
        Step? Decide,
        UnitySyncDecisionKind ExpectedKind,
        string? ExpectedReason);

    public sealed record Outcome(
        UnitySyncDecisionKind Kind,
        string? Reason,
        long? TargetEpoch,
        Chorus.UnitySdk.BaselineStatus Status);

    private static Outcome RunUnity(Scene scene)
    {
        var applier = new UnitySyncApplier(Room, Epoch, UnityDeltaContractMode.Full);
        if (scene.Baseline is { } baseline)
        {
            applier.Decide(baseline.UnityKind(0));
        }
        if (scene.Decide is null) throw new InvalidOperationException("a scene decides one step.");
        var decision = applier.Decide(scene.Decide.UnityKind(0));
        return new Outcome(decision.Kind, decision.Reason, decision.TargetEpoch, applier.Status);
    }

    public sealed record GodotOutcome(
        SyncDecisionKind Kind,
        string? Reason,
        long? TargetEpoch,
        BaselineStatus Status);

    private static GodotOutcome RunGodot(Scene scene)
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);
        if (scene.Baseline is { } baseline)
        {
            applier.Decide(baseline.GodotKind(0));
        }
        if (scene.Decide is null) throw new InvalidOperationException("a scene decides one step.");
        var decision = applier.Decide(scene.Decide.GodotKind(0));
        return new GodotOutcome(decision.Kind, decision.Reason, decision.TargetEpoch, applier.Status);
    }

    // ------------------------------------------------------------------
    // Frame builders: the same frame, once per namespace.
    // ------------------------------------------------------------------

    private static Step Snapshot(string roomId = Room, long epoch = Epoch, long tick = 5,
        long version = 2, string module = "module-1", string config = "cfg-1") => new(
        _ => new UnitySyncFrame
        {
            Kind = UnitySyncFrameKind.Snapshot,
            Snapshot = new Chorus.UnitySdk.SnapshotFrame
            {
                RoomId = roomId, Epoch = epoch, Tick = tick, StateVersion = version,
                ModuleVersion = module, ConfigHash = config,
            },
        },
        _ => new SyncFrame
        {
            Kind = SyncFrameKind.Snapshot,
            Snapshot = new Chorus.GodotSdk.SnapshotFrame
            {
                RoomId = roomId, Epoch = epoch, Tick = tick, StateVersion = version,
                ModuleVersion = module, ConfigHash = config,
            },
        });

    private static Step Delta(long version, long tick, long @base) => new(
        _ => new UnitySyncFrame
        {
            Kind = UnitySyncFrameKind.Delta,
            Delta = new Chorus.UnitySdk.DeltaFrame
            {
                RoomId = Room, Epoch = Epoch, Tick = tick, StateVersion = version,
                BaseVersion = @base, ModuleVersion = "module-1", ConfigHash = "cfg-1",
            },
        },
        _ => new SyncFrame
        {
            Kind = SyncFrameKind.Delta,
            Delta = new Chorus.GodotSdk.DeltaFrame
            {
                RoomId = Room, Epoch = Epoch, Tick = tick, StateVersion = version,
                BaseVersion = @base, ModuleVersion = "module-1", ConfigHash = "cfg-1",
            },
        });

    private static Step Result(long version, long tick) => new(
        _ => new UnitySyncFrame
        {
            Kind = UnitySyncFrameKind.RoundResult,
            RoundResult = new Chorus.UnitySdk.RoundResultFrame
            {
                RoomId = Room, Epoch = Epoch, Tick = tick, StateVersion = version,
            },
        },
        _ => new SyncFrame
        {
            Kind = SyncFrameKind.RoundResult,
            RoundResult = new Chorus.GodotSdk.RoundResultFrame
            {
                RoomId = Room, Epoch = Epoch, Tick = tick, StateVersion = version,
            },
        });

    private static Step Resync(long epoch) => new(
        _ => new UnitySyncFrame
        {
            Kind = UnitySyncFrameKind.ResyncRequired,
            ResyncRequired = new Chorus.UnitySdk.ResyncRequiredFrame
            {
                RoomId = Room, CurrentEpoch = epoch, Reason = "peer",
            },
        },
        _ => new SyncFrame
        {
            Kind = SyncFrameKind.ResyncRequired,
            ResyncRequired = new Chorus.GodotSdk.ResyncRequiredFrame
            {
                RoomId = Room, CurrentEpoch = epoch, Reason = "peer",
            },
        });

    private static UnitySyncFrame ToUnity(Step step) => step.UnityKind(0);

    private static SyncFrame ToGodot(Step step) => step.GodotKind(0);
}
