using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The Envelope codec (EK-1): reproduces JsonEnvelopeCodec's wire byte-for-byte
    /// on the same inputs. The cross-engine golden tests (EK-2) pin this against the
    /// reference codec — a change to either side's field naming, null handling or body
    /// passthrough that is not mirrored here turns the golden set red. This is the
    /// 防漂移 core: the Godot SDK is not allowed to drift from the server's framing.
    /// </summary>
    public static class GodotEnvelopeCodec
    {
        public static byte[] Serialize(GodotEnvelope envelope)
            => JsonSerializer.SerializeToUtf8Bytes(envelope, GodotProtocolJson.SerializerOptions);

        public static GodotEnvelope Deserialize(ReadOnlySpan<byte> bytes)
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            var doc = JsonDocument.ParseValue(ref reader);
            var env = doc.RootElement.Deserialize<GodotEnvelope>(GodotProtocolJson.SerializerOptions);
            if (env == null)
            {
                throw new InvalidOperationException("Failed to deserialize envelope.");
            }
            return env;
        }

        public static async ValueTask<GodotEnvelope> DeserializeAsync(Stream stream,
            CancellationToken cancellationToken)
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var env = doc.RootElement.Deserialize<GodotEnvelope>(GodotProtocolJson.SerializerOptions);
            if (env == null)
            {
                throw new InvalidOperationException("Failed to deserialize envelope.");
            }
            return env;
        }
    }
}
