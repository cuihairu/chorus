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
    long? ClientLastAckTickId = null
);
