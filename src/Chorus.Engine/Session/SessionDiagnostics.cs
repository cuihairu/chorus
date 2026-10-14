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
    int MailboxDepth = 0,
    /// <summary>Snapshots skipped because the serialized state exceeded MaxSnapshotBytes (stage-3).</summary>
    long TotalSnapshotsOversize = 0,
    /// <summary>Client-initiated resync requests received (frame-sync P2, before cooldown filtering).</summary>
    long TotalResyncRequests = 0,
    /// <summary>Resync requests answered from the frame history window (frame-sync P2).</summary>
    long TotalResyncReplays = 0,
    /// <summary>Resync requests whose window start was missing - answered with latest + forced (frame-sync P2).</summary>
    long TotalResyncWindowMisses = 0,
    /// <summary>Resync requests rejected by the per-client cooldown (frame-sync P2).</summary>
    long TotalResyncThrottled = 0,
    /// <summary>Current frame-history footprint in encoded bytes (frame-sync P2).</summary>
    long FrameHistoryBytes = 0,
    /// <summary>Encoded wire size of the most recent snapshot this session sent (tuning: snapshot-size signal; 0 = none yet).</summary>
    long LastSnapshotBytes = 0,
    /// <summary>Snapshots evicted from clients' latest-wins outbound slots before the transport picked them up (audit B3 backlog signal).</summary>
    long TotalClientQueueDropped = 0
);

public sealed record SessionClientDiagnostics(
    string ClientId,
    long ConnectedTickId,
    long LastAckTickId,
    long LagTicks,
    long LastAckAgeMs,
    long ResyncCount,
    long LastResyncAgeMs,
    /// <summary>Latest-wins slot evictions for this client (audit B3 backlog signal).</summary>
    long ClientQueueDropped = 0
);
