using System.Globalization;
using System.Text.Json;

namespace Chorus.ExampleGame;

/// <summary>
/// FIX-INPUT-08/09, the configured mapping from the platform's normalized event — the
/// PlatformGateway's AudienceEvent JSON, camelCase with kind as its stable numeric code
/// (the enum doc fixes the code values) — to this module family's command shape. A gift
/// with a stated value scores its recipient's faction that value
/// (<c>{"kind":"gift","amount":value}</c>); a like carries its click count
/// (<c>{"kind":"like","count":n}</c>), read from the event's <c>text</c> field, which the
/// audience reducer's window merge fills with the merged click count — so one physical click
/// is never mistakenly reported as one callback, and a merged batch states how many clicks it
/// stands for (a like with no stated count is one click). Comments and enters take no
/// command. A payload that is not an AudienceEvent shape (already a module command, the demo
/// path, or a client command) passes through untouched, so a host under this translation still
/// plays raw command deliveries exactly as before. Pure JSON to JSON: no IO, no module state,
/// no platform dependency.
/// </summary>
public sealed class AudienceEventModuleInputTranslator : IRoomInputTranslator
{
    // The wire contract the gateway writes for every platform event (the AudienceEvent
    // record of Chorus.Protocol): these four are always present on a platform payload and
    // never on a module command. Parsing the documented shape without the type keeps this
    // package dependency-free — the module family never takes a platform reference.
    private const int CommentKind = 1; // AudienceEventKind.Comment
    private const int LikeKind = 2;    // AudienceEventKind.Like
    private const int GiftKind = 3;    // AudienceEventKind.Gift
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
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.Number)
                return null;
            switch (kind.GetInt32())
            {
                case GiftKind:
                    if (!root.TryGetProperty("giftValue", out var value)
                        || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var giftValue))
                        return null; // a gift without a stated value scores nothing, deterministically
                    return JsonSerializer.Serialize(new { kind = "gift", amount = giftValue });
                case LikeKind:
                    // FIX-INPUT-09: the reducer's window merge states the merged click count in
                    // text; a single unmerged like states nothing and stands for one click.
                    return JsonSerializer.Serialize(new { kind = "like", count = LikeCount(root) });
                default:
                    return null; // a platform event this family takes no command from (comments)
            }
        }
    }

    /// <summary>The click count one like event stands for: the merged window's stated count
    /// when <c>text</c> is a positive integer, otherwise one physical click. Never negative,
    /// never zero — a like is at least one click, and the module's tally stays honest.</summary>
    private static int LikeCount(JsonElement root)
    {
        if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            return 1;
        return int.TryParse(text.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count > 0 ? count : 1;
    }
}
