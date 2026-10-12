using System.Text.Json;

namespace Chorus.Messaging;

public sealed record Envelope(
    string Type,
    string MessageId,
    string SessionId,
    long CreatedTimeMs,
    JsonElement Body
);

