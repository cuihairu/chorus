using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The leaf's restatement of the server's JSON contract (EK-1): the exact options
    /// Chorus.Protocol.ProtocolJson uses, so the Envelope codec reproduces
    /// JsonEnvelopeCodec's wire byte-for-byte. The leaf restates them rather than
    /// referencing Chorus.Protocol — the Godot package never drags the server stack
    /// into a game, and the golden test (EK-2) pins the wire, not the reference.
    /// </summary>
    public static class GodotProtocolJson
    {
        public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
    }
}
