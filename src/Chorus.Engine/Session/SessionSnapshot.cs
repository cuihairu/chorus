using Chorus.Protocol.Messages;

namespace Chorus.Engine.Session;

public sealed record SessionSnapshot(
    string SessionId,
    long TickId,
    long ServerTimeMs,
    object State,
    bool? Forced = null,
    string? Reason = null,
    string? TargetClientId = null,
    long? ClientLagTicks = null,
    long? ClientLastAckTickId = null,
    /// <summary>Snapshot schema version (stage-3); clients reject unknown versions and resync.</summary>
    int V = 1,
    /// <summary>Per-event frame attribution (frame-sync P2): events applied since the
    /// previous snapshot, each stamped with the tickId it was applied on.
    /// Null = field omitted on the wire (additive optional, no v bump).</summary>
    IReadOnlyList<FrameEvent>? FrameEvents = null
);
