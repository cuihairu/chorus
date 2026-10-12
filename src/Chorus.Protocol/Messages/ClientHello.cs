namespace Chorus.Protocol.Messages;

public sealed record ClientHello(
    string Type,
    string? SessionId = null,
    string? ClientId = null,
    string? Token = null,
    string? ClientVersion = null,
    int? ProtocolVersion = null
);

