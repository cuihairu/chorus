using System.Text.Json.Serialization;

namespace Chorus.Redesign.StreamerGateway.Messages;

/// <summary>Hello (contracts 3.1): the first frame on every connection. The protocol version
/// gates everything that follows; the build and module versions are diagnostics.</summary>
public sealed record EdgeHello
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Hello;
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

/// <summary>LoginWithPlatformLaunch (contracts 3.1): the client's first authenticated move.
/// The client generated and stored the next resume secret before sending (3.3); it rides as
/// base64 and is never logged.</summary>
public sealed record EdgeLoginWithPlatformLaunch
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.LoginWithPlatformLaunch;
    public string? Platform { get; init; }
    public required string AppId { get; init; }
    public required string LaunchCredential { get; init; }
    public string? LoginAttemptId { get; init; }
    public byte[]? NextResumeSecret { get; init; }
}

/// <summary>Resume (contracts 3.1, 3.3): rotate onto a fresh session with a new secret. The
/// room facts are diagnostics only — the server's participation record decides.</summary>
public sealed record EdgeResume
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Resume;
    public required string ResumeOperationId { get; init; }
    public byte[]? ResumeCredential { get; init; }
    public byte[]? NextResumeSecret { get; init; }
    public required string PreviousSessionId { get; init; }
    public string? LastSeenRoomId { get; init; }
    public long? LastAppliedStateVersion { get; init; }
}

/// <summary>Logout (contracts 3.1): ends the session; the gateway closes the connection after
/// the revocation is durable. No downlink acknowledgement exists for it.</summary>
public sealed record EdgeLogout
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.Logout;
    public string? OperationId { get; init; }
}

/// <summary>LoginReady (contracts 3.2): the session facts the client keeps. The access
/// credential names the acting session for every later uplink.</summary>
public sealed record EdgeLoginReady
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.LoginReady;
    public required string StreamerId { get; init; }
    public required string SessionId { get; init; }
    public long SessionVersion { get; init; }
    public required string AccessCredential { get; init; }
    public long AccessExpiresAt { get; init; }
    public bool ResumeRegistered { get; init; }
}

/// <summary>SessionReplaced (contracts 3.2): another login or resume displaced this
/// connection; it arrives right before the gateway closes it.</summary>
public sealed record EdgeSessionReplaced
{
    [JsonPropertyName("type")]
    public string Type => EdgeTypes.SessionReplaced;
    public required string Reason { get; init; }
}
