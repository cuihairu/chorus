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
    int V = 1
);
