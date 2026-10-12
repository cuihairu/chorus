using Chorus.Protocol.Models;

namespace Chorus.Adapters.Abstractions;

public sealed class KeywordGiftAudienceFactionRule : IAudienceFactionRule
{
    private readonly IReadOnlyDictionary<string, string> _commentKeywords;
    private readonly IReadOnlyDictionary<string, string> _giftIds;

    public KeywordGiftAudienceFactionRule(
        IReadOnlyDictionary<string, string>? commentKeywords = null,
        IReadOnlyDictionary<string, string>? giftIds = null)
    {
        _commentKeywords = Normalize(commentKeywords);
        _giftIds = Normalize(giftIds);
    }

    public AudienceEvent Apply(AudienceEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.FactionId))
        {
            return e;
        }

        string? factionId = e.Kind switch
        {
            AudienceEventKind.Comment => ResolveFromComment(e.Text),
            AudienceEventKind.Gift => ResolveFromGift(e.GiftId),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(factionId)
            ? e
            : e with { FactionId = factionId };
    }

    private string? ResolveFromComment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return _commentKeywords.TryGetValue(text.Trim(), out var factionId) ? factionId : null;
    }

    private string? ResolveFromGift(string? giftId)
    {
        if (string.IsNullOrWhiteSpace(giftId))
        {
            return null;
        }

        return _giftIds.TryGetValue(giftId.Trim(), out var factionId) ? factionId : null;
    }

    private static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            normalized[key.Trim()] = value.Trim();
        }

        return normalized;
    }
}
