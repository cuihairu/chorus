namespace Chorus.Protocol.Messages;

public sealed record ServerPong(
    string Type,
    long ServerTimeMs
);

