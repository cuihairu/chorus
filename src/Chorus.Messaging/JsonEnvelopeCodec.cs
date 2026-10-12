using System.Buffers;
using System.Text.Json;
using Chorus.Protocol;

namespace Chorus.Messaging;

public static class JsonEnvelopeCodec
{
    public static byte[] Serialize(Envelope envelope)
        => JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.SerializerOptions);

    public static Envelope Deserialize(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        var doc = JsonDocument.ParseValue(ref reader);
        var env = doc.RootElement.Deserialize<Envelope>(ProtocolJson.SerializerOptions);
        if (env == null)
        {
            throw new InvalidOperationException("Failed to deserialize envelope.");
        }
        return env;
    }

    public static async ValueTask<Envelope> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var env = doc.RootElement.Deserialize<Envelope>(ProtocolJson.SerializerOptions);
        if (env == null)
        {
            throw new InvalidOperationException("Failed to deserialize envelope.");
        }
        return env;
    }
}

