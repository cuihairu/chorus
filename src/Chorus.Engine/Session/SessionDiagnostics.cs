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
    IReadOnlyList<SessionClientDiagnostics> Clients,
    /// <summary>Events rejected because the bounded event lane was full (mergeable/cosmetic classes).</summary>
    long TotalDroppedOverflow = 0,
    /// <summary>Gifts rejected after the bounded wait - must-not-lose class, ingress should retry (Failed).</summary>
    long TotalDroppedGifts = 0,
    /// <summary>Ticks whose processing exceeded TickTimeoutMs (detection only, tick is not cancelled).</summary>
    long TotalTickOverruns = 0,
    /// <summary>Ticks that failed (e.g. GameModule threw); the actor survives and keeps ticking.</summary>
    long TotalTickErrors = 0,
    /// <summary>Current pending depth of the control and event lanes combined.</summary>
    int MailboxDepth = 0
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
