using System.Text.Json.Serialization;

namespace Chorus.Redesign.StreamerGateway.Messages;

/// <summary>Hello (contracts 3.1): the first frame on every connection. The protocol version
/// gates everything that follows; the build and module versions are diagnostics.</summary>
public sealed record EdgeHello
{
    public int ProtocolVersion { get; init; }
    public string? ClientBuild { get; init; }
    public IReadOnlyList<string> SupportedModuleVersions { get; init; } = [];
}

/// <summary>Ping: proves the connection is reachable, nothing more (contracts 3.1). The
/// gateway answers with a pong and makes no session inference from either frame.</summary>
public sealed record EdgePing;

/// <summary>Pong: the ping reply with the server clock, for the client's own lag math.</summary>
public sealed record EdgePong
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Pong;
    public long ServerTimeMs { get; init; }
}

/// <summary>Error (contracts 1, 3.2): a visible refusal. Codes come from
/// <see cref="EdgeErrorCodes"/>; the message is short and safe to show.</summary>
public sealed record EdgeError
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Error;
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? OperationId { get; init; }
}
