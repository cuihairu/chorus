namespace Chorus.Protocol.Models;

public enum AudienceEventKind
{
    Unknown = 0,
    Comment = 1,
    Like = 2,
    Gift = 3,
}

public sealed record AudienceEvent(
    string EventId,
    string Platform,
    string SessionId,
    string UserId,
    AudienceEventKind Kind,
    long IngestTimeMs,
    string? Text = null,
    string? GiftId = null,
    int? GiftCount = null,
    int? GiftValue = null,
    string? MatchId = null,
    string? FactionId = null
);
