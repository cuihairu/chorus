using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chorus.Redesign.StreamerGateway.Messages;

/// <summary>
/// The single JSON configuration of the wire surface: web defaults (camelCase, no nulls) with
/// byte fields carried as base64 strings, matching the contract examples. Both directions use
/// this one options instance so a message that round-trips through the gateway is stable.
/// </summary>
public static class EdgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Optional facts stay absent from the wire when the gateway has none — a null field
        // and a missing field must not be distinguishable to the client.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
