using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Maps Douyin live callback payloads to <see cref="AudienceEvent"/>.
///
/// STATUS (2026-10-12): official event types and field names could not be
/// confirmed online (see docs/reference/platform-douyin.md). The envelope below
/// is the placeholder contract agreed for integration testing - it is NOT the
/// official Douyin schema:
///
///   event       (string)  "comment" | "gift" | "like" | anything else -> Unknown
///   event_id    (string)  required, stable id used for dedup; events without
///                         one are refused (unmapped) - a random substitute would
///                         let the same payload retry as different "new" events
///   timestamp   (number)  required, milliseconds; the verifier's freshness bound
///                         rides on it (signature-covered, M5)
///   session_id  (string)  required, selects the Chorus session
///   user_id     (string)  defaults to "anonymous"
///   content     (string)  comment text
///   gift_id / gift_count / gift_value    gift payload
///
/// Unknown event types map to <see cref="AudienceEventKind.Unknown"/> so the
/// pipeline can acknowledge and drop them instead of failing the callback.
///
/// TODO(joint-debugging): replace the field extraction below with the official
/// event names and payload layout once callback access is granted.
/// </summary>
public sealed class DouyinPlatformEventMapper : IPlatformEventMapper
{
    public string PlatformName => PlatformNames.Douyin;

    public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
    {
        if (!string.Equals(platform, PlatformNames.Douyin, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("session_id", out var sessionIdEl)
                || sessionIdEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(sessionIdEl.GetString()))
            {
                return null;
            }

            var sessionId = sessionIdEl.GetString()!;

            // event_id is the source-identity key every downstream dedup answers on:
            // without it the same payload would retry as distinct events, so refuse
            // (unmapped -> 400) instead of inventing a random stand-in.
            var eventId = TryGetString(root, "event_id");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return null;
            }

            var userId = TryGetString(root, "user_id");
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = "anonymous";
            }

            var kind = MapEventKind(TryGetString(root, "event"));

            return new AudienceEvent(
                EventId: eventId,
                Platform: PlatformNames.Douyin,
                SessionId: sessionId,
                UserId: userId,
                Kind: kind,
                IngestTimeMs: ingestTimeMs,
                Text: TryGetString(root, "content"),
                GiftId: TryGetString(root, "gift_id"),
                GiftCount: TryGetInt32(root, "gift_count"),
                GiftValue: TryGetInt32(root, "gift_value"),
                MatchId: TryGetString(root, "match_id"),
                FactionId: TryGetString(root, "faction_id")
            );
        }
    }

    private static AudienceEventKind MapEventKind(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return AudienceEventKind.Unknown;
        }

        return eventName.Trim().ToLowerInvariant() switch
        {
            "comment" => AudienceEventKind.Comment,
            "gift" => AudienceEventKind.Gift,
            "like" => AudienceEventKind.Like,
            _ => AudienceEventKind.Unknown,
        };
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static int? TryGetInt32(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
            : null;
    }
}
