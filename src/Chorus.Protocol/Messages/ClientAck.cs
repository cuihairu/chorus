namespace Chorus.Protocol.Messages;

public sealed record ClientAck(
    string Type,
    string SessionId,
    string ClientId,
    long LastAppliedTickId,
    long? ClientTimeMs = null
);

