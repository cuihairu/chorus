namespace Chorus.Protocol.Messages;

public sealed record ServerError(
    string Type,
    string Message,
    string? Code = null
);

