namespace Chorus.Engine.Session;

public sealed record SessionConfig(
    int TickDurationMs = 100,
    int InputDelayTicks = 2,
    int SnapshotEveryTicks = 1,
    int MaxEventsPerTick = 200,
    int MaxLagTicks = 50,
    int ResyncCooldownMs = 2000
);
