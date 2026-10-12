namespace Chorus.Protocol.Messages;

public sealed record ServerWelcome(
    string Type,
    string SessionId,
    string ClientId,
    int TickDurationMs,
    int InputDelayTicks,
    int SnapshotEveryTicks,
    long ServerTimeMs
);

