using System.Text.Json;

namespace Chorus.ExampleGame;

/// <summary>
/// FIX-INPUT-08, the configured mapping from the platform's normalized event — the
/// PlatformGateway's AudienceEvent JSON, camelCase with kind as its stable numeric code
/// (the enum doc fixes the code values) — to this module family's command shape. A gift
/// with a stated value scores its recipient's faction that value
/// (<c>{"kind":"gift","amount":value}</c>); kinds this family takes no command from — likes
/// and comments are FIX-INPUT-09's own line — yield no command. A payload that is not an
/// AudienceEvent shape (already a module command, the demo path, or a client command) passes
/// through untouched, so a host under this translation still plays raw command deliveries
/// exactly as before. Pure JSON to JSON: no IO, no module state, no platform dependency.
/// </summary>
public sealed class AudienceEventModuleInputTranslator : IRoomInputTranslator
{
    // The wire contract the gateway writes for every platform event (the AudienceEvent
    // record of Chorus.Protocol): these four are always present on a platform payload and
    // never on a module command. Parsing the documented shape without the type keeps this
    // package dependency-free — the module family never takes a platform reference.
    private const int GiftKind = 3; // AudienceEventKind.Gift, code value fixed by the enum.
    private static readonly string[] WireMarkers = ["eventId", "platform", "sessionId", "userId"];

    public string? Translate(string payloadJson)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payloadJson); }
        catch (JsonException) { return payloadJson; } // not a platform payload: a command already
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return payloadJson;
            foreach (var marker in WireMarkers)
                if (!root.TryGetProperty(marker, out _))
                    return payloadJson; // a module command, not a platform event shape
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.Number
                || kind.GetInt32() != GiftKind)
                return null; // a platform event this family takes no command from (likes: INPUT-09)
            if (!root.TryGetProperty("giftValue", out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var giftValue))
                return null; // a gift without a stated value scores nothing, deterministically
            return JsonSerializer.Serialize(new { kind = "gift", amount = giftValue });
        }
    }
}
