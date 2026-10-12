namespace Chorus.Engine.Session;

public sealed record SessionDiagnostics(
    string SessionId,
    long TickId,
    int TickDurationMs,
    long ServerTimeMs,
    long TotalIngestedEvents,
    long TotalAppliedEvents,
    long TotalMergedEvents,
    long TotalDroppedEvents,
    long TotalSnapshotsSent,
    long TotalResyncSnapshotsSent,
    long TotalAcksAccepted,
    long TotalAcksDuplicate,
    long TotalAcksStale,
    long TotalAcksInvalid,
    long LastTickProcessMs,
    IReadOnlyList<SessionClientDiagnostics> Clients
);

public sealed record SessionClientDiagnostics(
    string ClientId,
    long ConnectedTickId,
    long LastAckTickId,
    long LagTicks,
    long LastAckAgeMs,
    long ResyncCount,
    long LastResyncAgeMs
);
