namespace Chorus.Protocol.Models;

public enum AudienceEventKind
{
    Unknown = 0,
    Comment = 1,
    Like = 2,
    Gift = 3,
    // 码值稳定：追加成员不改既有编号（wire 上 FrameEvent.Kind 走字符串）。
    Enter = 4,          // 观众进场（B 站 INTERACT_WORD_V2 / LIVE_ROOM_ENTER）
    RoomLifecycle = 5, // 房间开播/下播（LIVE_START / LIVE_END）；控制面动作属 §14
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
