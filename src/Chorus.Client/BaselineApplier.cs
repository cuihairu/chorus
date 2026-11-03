using Chorus.Rpc;

namespace Chorus.Client;

/// <summary>
/// Declares whether a module ships a complete delta contract: deltas that carry
/// enough information to rebuild the displayed state on top of the baseline.
/// A module with only effect events has no such contract and syncs by snapshots
/// alone (authoritative-sync 8.3).
/// </summary>
public enum DeltaContract
{
    /// <summary>Deltas carry the full incremental payload; the client chains them on the baseline.</summary>
    Full,

    /// <summary>No complete delta contract: every delta is refused and the module syncs by snapshots.</summary>
    SnapshotOnly
}

/// <summary>What the client must do with one sync message against its baseline.</summary>
public enum BaselineDecisionKind
{
    /// <summary>Replace the baseline with the snapshot payload atomically.</summary>
    ApplySnapshot,

    /// <summary>Apply the delta on top of the applied baseline.</summary>
    ApplyDelta,

    /// <summary>Record the round result; the room is terminal from now on.</summary>
    ApplyResult,

    /// <summary>Stale or duplicate: ignore the message, the baseline stays.</summary>
    Drop,

    /// <summary>
    /// The chain cannot continue on this baseline: request a full snapshot (or resubscribe
    /// at the decision's <see cref="BaselineDecision.TargetEpoch"/>).
    /// </summary>
    RebaselineRequired,

    /// <summary>Hard protocol violation: the stream itself is broken; the client must stop, not resubscribe.</summary>
    Invalid
}

/// <summary>Stable reason codes for drop, rebaseline and invalid decisions (asserted by name, not by string literal).</summary>
public static class BaselineReason
{
    public const string CrossEpoch = "CrossEpoch";
    public const string MissingBaseline = "MissingBaseline";
    public const string Gap = "Gap";
    public const string NoDeltaContract = "NoDeltaContract";
    public const string Resync = "Resync";
    public const string Duplicate = "Duplicate";
    public const string Regression = "Regression";
    public const string ProtocolRoomMismatch = "ProtocolRoomMismatch";
    public const string ProtocolModuleConfigMismatch = "ProtocolModuleConfigMismatch";
    public const string ProtocolDeltaInvariant = "ProtocolDeltaInvariant";
    public const string ProtocolTickRegression = "ProtocolTickRegression";
    public const string ProtocolVersionRange = "ProtocolVersionRange";
    public const string ProtocolEmptyMessage = "ProtocolEmptyMessage";
}

/// <summary>One sync decision plus why: the reason code for Drop/Rebaseline/Invalid and the epoch to resubscribe at for cross-epoch and resync.</summary>
public readonly record struct BaselineDecision(BaselineDecisionKind Kind, string? Reason = null, long? TargetEpoch = null)
{
    /// <summary>An apply decision (snapshot, delta or round result).</summary>
    public static BaselineDecision Apply(BaselineDecisionKind kind) => new(kind);

    /// <summary>A stale or duplicate message: ignore it, the baseline stays.</summary>
    public static BaselineDecision Drop(string reason) => new(BaselineDecisionKind.Drop, reason);

    /// <summary>The chain must restart: request a snapshot, optionally resubscribing at a new epoch.</summary>
    public static BaselineDecision Rebaseline(string reason, long? targetEpoch = null) =>
        new(BaselineDecisionKind.RebaselineRequired, reason, targetEpoch);

    /// <summary>A hard protocol violation: stop the stream, do not resubscribe.</summary>
    public static BaselineDecision Invalid(string reason) => new(BaselineDecisionKind.Invalid, reason);
}

/// <summary>A read-only copy of the applied baseline (-1 tick/version and no module identity before the first snapshot).</summary>
public readonly record struct BaselineStatus(
    string RoomId,
    long Epoch,
    long Tick,
    long StateVersion,
    string? ModuleVersion,
    string? ConfigHash,
    bool HasBaseline,
    bool Terminal);

/// <summary>
/// Client-side baseline contract for the authoritative sync stream (FIX-SYNC-01): the
/// client only advances state on a message that matches its room, epoch and baseline.
/// Duplicates and regressions drop, a version jump or a missing baseline asks for a full
/// snapshot, and a different epoch never chains onto the old baseline — epochs are
/// ownership generations, not ordered versions. The chain is keyed on state_version
/// alone; tick rides along for display (a version can advance while the tick pauses).
/// Single receive-loop use, no locks — mirrors FramePlayer.
/// </summary>
public sealed class BaselineApplier
{
    private readonly string _roomId;
    private readonly long _epoch;
    private readonly DeltaContract _deltaContract;
    private long _tick = -1;
    private long _stateVersion = -1;
    private string? _moduleVersion;
    private string? _configHash;
    private bool _hasBaseline;
    private bool _terminal;

    public BaselineApplier(string roomId, long epoch, DeltaContract deltaContract)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("A baseline needs a room id.", nameof(roomId));
        }

        if (epoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "The epoch must not be negative.");
        }

        _roomId = roomId;
        _epoch = epoch;
        _deltaContract = deltaContract;
    }

    /// <summary>A copy of the current baseline (-1 tick/version before the first snapshot).</summary>
    public BaselineStatus Status => new(_roomId, _epoch, _tick, _stateVersion,
        _moduleVersion, _configHash, _hasBaseline, _terminal);

    /// <summary>
    /// Decides one sync message against the baseline. Apply decisions advance the
    /// baseline; drop, rebaseline and invalid decisions leave it untouched.
    /// </summary>
    public BaselineDecision Decide(SyncMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Snapshot is { } snapshot)
        {
            return DecideSnapshot(snapshot);
        }

        if (message.Delta is { } delta)
        {
            return DecideDelta(delta);
        }

        if (message.RoundResult is { } result)
        {
            return DecideResult(result);
        }

        if (message.ResyncRequired is { } resync)
        {
            return DecideResync(resync);
        }

        return BaselineDecision.Invalid(BaselineReason.ProtocolEmptyMessage);
    }

    private BaselineDecision DecideSnapshot(SnapshotMessage snapshot)
    {
        if (snapshot.RoomId != _roomId)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolRoomMismatch);
        }

        if (snapshot.Epoch != _epoch)
        {
            return BaselineDecision.Rebaseline(BaselineReason.CrossEpoch, snapshot.Epoch);
        }

        if (snapshot.StateVersion < 1 || snapshot.Tick < 0)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolVersionRange);
        }

        if (_terminal)
        {
            return BaselineDecision.Drop(BaselineReason.Duplicate);
        }

        if (!_hasBaseline)
        {
            return ApplySnapshot(snapshot);
        }

        if (snapshot.ModuleVersion != _moduleVersion || snapshot.ConfigHash != _configHash)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolModuleConfigMismatch);
        }

        if (snapshot.StateVersion > _stateVersion && snapshot.Tick < _tick)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolTickRegression);
        }

        if (snapshot.StateVersion < _stateVersion)
        {
            return BaselineDecision.Drop(BaselineReason.Regression);
        }

        if (snapshot.StateVersion == _stateVersion)
        {
            return BaselineDecision.Drop(BaselineReason.Duplicate);
        }

        return ApplySnapshot(snapshot);
    }

    private BaselineDecision DecideDelta(DeltaMessage delta)
    {
        if (delta.RoomId != _roomId)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolRoomMismatch);
        }

        if (delta.Epoch != _epoch)
        {
            return BaselineDecision.Rebaseline(BaselineReason.CrossEpoch, delta.Epoch);
        }

        if (delta.StateVersion < 1 || delta.Tick < 0)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolVersionRange);
        }

        if (_terminal)
        {
            return BaselineDecision.Drop(BaselineReason.Duplicate);
        }

        if (_deltaContract == DeltaContract.SnapshotOnly)
        {
            return BaselineDecision.Rebaseline(BaselineReason.NoDeltaContract);
        }

        if (!_hasBaseline)
        {
            return BaselineDecision.Rebaseline(BaselineReason.MissingBaseline);
        }

        if (delta.ModuleVersion != _moduleVersion || delta.ConfigHash != _configHash)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolModuleConfigMismatch);
        }

        // The server commits stateVersion = base + 1 and tick = lastTick + 1 in one
        // transaction (RedesignFrameStore): a delta outside the invariant is corruption,
        // not a gap. The invariant also collapses base-vs-current — a duplicate delta
        // (base = current-1) lands in the version == current drop below instead of being
        // misread as a missing baseline.
        if (delta.StateVersion != delta.BaseVersion + 1)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolDeltaInvariant);
        }

        if (delta.StateVersion < _stateVersion)
        {
            return BaselineDecision.Drop(BaselineReason.Regression);
        }

        if (delta.StateVersion == _stateVersion)
        {
            return BaselineDecision.Drop(BaselineReason.Duplicate);
        }

        if (delta.StateVersion > _stateVersion + 1)
        {
            return BaselineDecision.Rebaseline(BaselineReason.Gap);
        }

        // Exactly current + 1: a lower tick is impossible from a valid commit, and a
        // version-only pause boundary (tick unchanged) is legal.
        if (delta.Tick < _tick)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolTickRegression);
        }

        _tick = delta.Tick;
        _stateVersion = delta.StateVersion;
        return BaselineDecision.Apply(BaselineDecisionKind.ApplyDelta);
    }

    private BaselineDecision DecideResult(RoundResultMessage result)
    {
        if (result.RoomId != _roomId)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolRoomMismatch);
        }

        if (result.Epoch != _epoch)
        {
            return BaselineDecision.Rebaseline(BaselineReason.CrossEpoch, result.Epoch);
        }

        if (result.StateVersion < 1)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolVersionRange);
        }

        if (_terminal)
        {
            return BaselineDecision.Drop(BaselineReason.Duplicate);
        }

        if (_hasBaseline && result.StateVersion < _stateVersion)
        {
            return BaselineDecision.Drop(BaselineReason.Regression);
        }

        // A bare round result is a real flow for a released room that answered a correct
        // subscriber (RoomRuntime.LateSubscriberAnswer): the terminal marker applies even
        // with no state baseline behind it.
        _tick = result.Tick;
        _stateVersion = result.StateVersion;
        _terminal = true;
        return BaselineDecision.Apply(BaselineDecisionKind.ApplyResult);
    }

    private BaselineDecision DecideResync(ResyncRequiredMessage resync)
    {
        if (resync.RoomId != _roomId)
        {
            return BaselineDecision.Invalid(BaselineReason.ProtocolRoomMismatch);
        }

        return BaselineDecision.Rebaseline(BaselineReason.Resync, resync.CurrentEpoch);
    }

    private BaselineDecision ApplySnapshot(SnapshotMessage snapshot)
    {
        _tick = snapshot.Tick;
        _stateVersion = snapshot.StateVersion;
        _moduleVersion = snapshot.ModuleVersion;
        _configHash = snapshot.ConfigHash;
        _hasBaseline = true;
        return BaselineDecision.Apply(BaselineDecisionKind.ApplySnapshot);
    }
}
