using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// Whether a module ships a complete delta contract: deltas that carry enough
    /// information to rebuild the displayed state on top of the baseline. A module with
    /// only effect events has no such contract and syncs by snapshots alone. Mirrors the
    /// server-side <c>DeltaContract</c> (FIX-SYNC-01).
    /// </summary>
    public enum DeltaContractMode
    {
        /// <summary>Deltas carry the full incremental payload; the client chains them on the baseline.</summary>
        Full = 0,

        /// <summary>No complete delta contract: every delta is refused and the module syncs by snapshots.</summary>
        SnapshotOnly = 1
    }

    /// <summary>What the client must do with one sync frame against its baseline. The names
    /// and meanings mirror the server-side baseline contract (FIX-SYNC-01) member for member —
    /// the parity test pins them, so the Unity applier port (FIX-U3D-09) answers exactly
    /// what the reference applier answers.</summary>
    public enum SyncDecisionKind
    {
        /// <summary>Replace the baseline with the snapshot payload atomically.</summary>
        ApplySnapshot = 0,

        /// <summary>Apply the delta on top of the applied baseline.</summary>
        ApplyDelta = 1,

        /// <summary>Record the round result; the room is terminal from now on.</summary>
        ApplyResult = 2,

        /// <summary>Stale or duplicate: ignore the frame, the baseline stays.</summary>
        Drop = 3,

        /// <summary>The chain cannot continue on this baseline: request a full snapshot, or
        /// resubscribe at the decision's target epoch.</summary>
        RebaselineRequired = 4,

        /// <summary>Hard protocol violation: the stream itself is broken; the client must stop, not resubscribe.</summary>
        Invalid = 5
    }

    /// <summary>Stable reason codes for drop, rebaseline and invalid decisions, mirroring the
    /// server-side reason set name for name.</summary>
    public static class SyncReasons
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

    /// <summary>One sync decision plus why: the reason code for drop, rebaseline and invalid
    /// decisions and the epoch to resubscribe at for cross-epoch and resync.</summary>
    public struct SyncDecision
    {
        public SyncDecision(SyncDecisionKind kind, string? reason = null, long? targetEpoch = null)
        {
            Kind = kind;
            Reason = reason;
            TargetEpoch = targetEpoch;
        }

        public SyncDecisionKind Kind { get; set; }
        public string? Reason { get; set; }
        public long? TargetEpoch { get; set; }

        /// <summary>An apply decision (snapshot, delta or round result).</summary>
        public static SyncDecision Apply(SyncDecisionKind kind) => new SyncDecision(kind);

        /// <summary>A stale or duplicate frame: ignore it, the baseline stays.</summary>
        public static SyncDecision Drop(string reason) => new SyncDecision(SyncDecisionKind.Drop, reason);

        /// <summary>The chain must restart: request a snapshot, optionally resubscribing at a new epoch.</summary>
        public static SyncDecision Rebaseline(string reason, long? targetEpoch = null) =>
            new SyncDecision(SyncDecisionKind.RebaselineRequired, reason, targetEpoch);

        /// <summary>A hard protocol violation: stop the stream, do not resubscribe.</summary>
        public static SyncDecision Invalid(string reason) => new SyncDecision(SyncDecisionKind.Invalid, reason);
    }

    /// <summary>A read-only copy of the applied baseline (tick/version −1 and no module
    /// identity before the first snapshot).</summary>
    public struct BaselineStatus
    {
        public string RoomId { get; set; }
        public long Epoch { get; set; }
        public long Tick { get; set; }
        public long StateVersion { get; set; }
        public string? ModuleVersion { get; set; }
        public string? ConfigHash { get; set; }
        public bool HasBaseline { get; set; }
        public bool Terminal { get; set; }
    }

    /// <summary>
    /// The Unity side's sync applier (FIX-U3D-09 implements it, porting the server-side
    /// reference applier's ordered rules): decide each stream frame against the room,
    /// epoch and baseline before any state moves. Game code applies the payload only on
    /// the apply decisions; rebaseline asks the SDK to resubscribe; invalid ends the stream.
    /// </summary>
    public interface ISyncApplier
    {
        /// <summary>Decides one frame against the current baseline without moving it.</summary>
        SyncDecision Decide(SyncFrame frame);

        /// <summary>A read-only copy of the baseline as of the last decided frame.</summary>
        BaselineStatus Status { get; }
    }
}
