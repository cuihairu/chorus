using Chorus.Protocol;

namespace Chorus.Protocol.Messages;

public sealed record ServerWelcome(
    string Type,
    string SessionId,
    string ClientId,
    int TickDurationMs,
    int InputDelayTicks,
    int SnapshotEveryTicks,
    long ServerTimeMs,
    // Defaulted so an older EngineHost deserializing a newer Gateway's relayed
    // welcome (or vice versa) still binds; hosts pass ProtocolVersionPolicy.Current.
    int ProtocolVersion = ProtocolVersionPolicy.Current
);

