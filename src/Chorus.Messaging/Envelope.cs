using System.Text.Json;

namespace Chorus.Messaging;

/// <summary>
/// IPC message envelope. <see cref="MessageId"/> identifies one delivery;
/// <see cref="SourceEventId"/> optionally carries the platform event id this
/// delivery was born from (correlation: ingress EventId → engine → snapshot).
/// </summary>
public sealed record Envelope(
    string Type,
    string MessageId,
    string SessionId,
    long CreatedTimeMs,
    JsonElement Body,
    string? SourceEventId = null,
    long? AuthorityEpoch = null
);
