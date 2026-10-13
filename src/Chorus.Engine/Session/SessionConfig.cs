namespace Chorus.Engine.Session;

public sealed record SessionConfig(
    int TickDurationMs = 66,
    int InputDelayTicks = 2,
    int SnapshotEveryTicks = 1,
    int MaxEventsPerTick = 200,
    int MaxLagTicks = 50,
    int ResyncCooldownMs = 2000,
    /// <summary>Bounded audience-event lane capacity (session-wide total, review stage-2).</summary>
    int MaxPendingEvents = 8000,
    /// <summary>Extra wait for must-not-lose events (gifts) when the event lane is full; 0 drops immediately.</summary>
    int EventEnqueueMaxWaitMs = 50,
    /// <summary>Tick processing overrun threshold for diagnostics; 0 disables the check.</summary>
    int TickTimeoutMs = 250,
    /// <summary>Max live sessions per process; 0 disables the cap (review stage-2).</summary>
    int MaxSessions = 200,
    /// <summary>Client-less sessions idle longer than this are evicted (review stage-2).</summary>
    int IdleEvictAfterMs = 600_000,
    /// <summary>Serialized state payload cap per snapshot; larger states skip the send and count as oversize. 0 disables (stage-3).</summary>
    int MaxSnapshotBytes = 1_048_576,
    /// <summary>Frame-history ring-buffer byte cap per session (frame-sync P2). Snapshots evict
    /// oldest once the buffer exceeds this; 0 disables history (and client resync answers). </summary>
    int FrameHistoryBytes = 8_388_608,
    /// <summary>Per-client cooldown for client-initiated resync requests (frame-sync P2);
    /// extra requests inside the window are ignored. 0 disables throttling.</summary>
    int ResyncRequestCooldownMs = 1000
);
