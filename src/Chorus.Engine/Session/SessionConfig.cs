namespace Chorus.Engine.Session;

public sealed record SessionConfig(
    int TickDurationMs = 100,
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
    int TickTimeoutMs = 250
);
