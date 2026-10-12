using System.Text.Json;
using Chorus.Protocol;

namespace Chorus.Messaging.Ipc;

public static class EnvelopeFactory
{
    public static Envelope Create<T>(string type, string sessionId, T body, string? sourceEventId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var elem = JsonSerializer.SerializeToElement(body, ProtocolJson.SerializerOptions);
        return new Envelope(
            Type: type,
            MessageId: $"m_{Guid.NewGuid():N}",
            SessionId: sessionId,
            CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body: elem,
            SourceEventId: sourceEventId
        );
    }

    public static T DeserializeBody<T>(Envelope envelope)
    {
        var obj = envelope.Body.Deserialize<T>(ProtocolJson.SerializerOptions);
        if (obj == null)
        {
            throw new InvalidOperationException($"Failed to deserialize body to {typeof(T).Name}");
        }
        return obj;
    }
}

