using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Development/simulator fixture mapper. Unlike a real platform mapper it resolves the event
/// kind by <b>name reflection over <see cref="AudienceEventKind"/></b> (string or numeric), so it can
/// synthesise any event shape a test needs. That dynamic word surface widens whenever a new
/// member is appended to the enum — by construction, not by a per-member decision — and an
/// unresolvable name still falls back to <see cref="AudienceEventKind.Comment"/>. It is never a
/// production surface (see the platform integration matrix: dev fixtures must stay out of
/// production hosts).
/// </summary>
public sealed class TestPlatformEventMapper : IPlatformEventMapper
{
    public string PlatformName => PlatformNames.Test;

    public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
    {
        if (!string.Equals(platform, PlatformNames.Test, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("sessionId", out var sessionIdEl))
        {
            return null;
        }

        var sessionId = sessionIdEl.GetString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var eventId = root.TryGetProperty("eventId", out var eventIdEl) ? eventIdEl.GetString() : null;
        eventId ??= $"test:{Guid.NewGuid():N}";

        var userId = root.TryGetProperty("userId", out var userIdEl) ? userIdEl.GetString() : null;
        userId ??= "anonymous";

        AudienceEventKind kind = AudienceEventKind.Comment;
        if (root.TryGetProperty("kind", out var kindEl))
        {
            if (kindEl.ValueKind == JsonValueKind.String && Enum.TryParse(kindEl.GetString(), ignoreCase: true, out AudienceEventKind parsed))
            {
                kind = parsed;
            }
            else if (kindEl.ValueKind == JsonValueKind.Number && kindEl.TryGetInt32(out var kindNum))
            {
                kind = (AudienceEventKind)kindNum;
            }
        }

        string? text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
        string? giftId = root.TryGetProperty("giftId", out var giftIdEl) ? giftIdEl.GetString() : null;
        int? giftCount = root.TryGetProperty("giftCount", out var giftCountEl) && giftCountEl.TryGetInt32(out var gc) ? gc : null;
        int? giftValue = root.TryGetProperty("giftValue", out var giftValueEl) && giftValueEl.TryGetInt32(out var gv) ? gv : null;

        var matchId = root.TryGetProperty("matchId", out var matchIdEl) ? matchIdEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(matchId))
        {
            matchId = null;
        }

        var factionId = root.TryGetProperty("factionId", out var factionIdEl) ? factionIdEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(factionId))
        {
            factionId = null;
        }

        return new AudienceEvent(
            EventId: eventId,
            Platform: PlatformNames.Test,
            SessionId: sessionId,
            UserId: userId,
            Kind: kind,
            IngestTimeMs: ingestTimeMs,
            Text: text,
            GiftId: giftId,
            GiftCount: giftCount,
            GiftValue: giftValue,
            MatchId: matchId,
            FactionId: factionId
        );
    }
}
