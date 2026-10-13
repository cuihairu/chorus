using System.Globalization;
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Maps B站 open-live wss 事件（blivedm <c>models/open_live.py</c>，✅ 原文核实）
/// 到 <see cref="AudienceEvent"/>。P1 弹幕 + P2 礼物/进场/房间生命周期：
///   DANMU_MSG                          → comment（data.open_id / data.msg / msg_id）
///   SEND_GIFT / SEND_GIFT_V2（灰度）  → gift（data.gift_id / data.gift_num / data.price；
///                                        V2 新格式容忍：字段缺失走默认值，不丢事件不崩）
///   INTERACT_WORD_V2 / LIVE_ROOM_ENTER → enter（data.open_id）
///   LIVE_START / LIVE_END             → roomLifecycle（Text=live_start / live_end）
///   其余（SUPER_CHAT_MESSAGE / GUARD_BUY / USER_TOAST_MSG_V2 等）→ Unknown
///   （不崩，管线确认后丢弃；平台协议仍在演进，见设计文档 §1.2）
///
/// STATUS (2026-10-14): 事件类型与字段名按 blivedm open_live 模型原文实现；
/// 若联调发现字段有出入，替换本文件的字段抽取即可，管道不动。
///
/// 会话绑定：open-live 事件不带 session_id，房间 → 会话走配置
/// （Ingress:Bilibili:SessionId，单绑定；多房间映射属 §14 控制面）。
/// </summary>
public sealed class BilibiliPlatformEventMapper : IPlatformEventMapper
{
    private readonly string? _sessionId;

    public BilibiliPlatformEventMapper(string? sessionId) => _sessionId = Normalize(sessionId);

    public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
    {
        if (!string.Equals(platform, PlatformNames.Bilibili, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_sessionId == null)
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var cmd = TryGetString(root, "cmd");
            var data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
                ? dataEl
                : default;

            var eventId = TryGetString(root, "msg_id");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                // 缺 msg_id 时合成 guid，去重不会重复（与 DouyinPlatformEventMapper 同约定）。
                eventId = $"{PlatformNames.Bilibili}:{Guid.NewGuid():N}";
            }

            var userId = UserId(data);
            AudienceEvent New(AudienceEventKind kind, string? text = null, string? giftId = null,
                int? giftCount = null, int? giftValue = null) =>
                new(
                    EventId: eventId,
                    Platform: PlatformNames.Bilibili,
                    SessionId: _sessionId,
                    UserId: userId,
                    Kind: kind,
                    IngestTimeMs: ingestTimeMs,
                    Text: text,
                    GiftId: giftId,
                    GiftCount: giftCount,
                    GiftValue: giftValue,
                    MatchId: null,
                    FactionId: null);

            // gift_id 在 open-live 是 int（blivedm 原文核实），按数字/字符串双读容忍。
            var giftId = TryGetInt32(data, "gift_id") is { } gid
                ? gid.ToString(CultureInfo.InvariantCulture)
                : TryGetString(data, "gift_id");

            return cmd switch
            {
                "DANMU_MSG" => New(AudienceEventKind.Comment, text: TryGetString(data, "msg")),
                "SEND_GIFT" or "SEND_GIFT_V2" => New(
                    AudienceEventKind.Gift,
                    giftId: giftId,
                    giftCount: TryGetInt32(data, "gift_num"),
                    giftValue: TryGetInt32(data, "price")),
                "INTERACT_WORD_V2" or "LIVE_ROOM_ENTER" => New(AudienceEventKind.Enter),
                "LIVE_START" => New(AudienceEventKind.RoomLifecycle, text: "live_start"),
                "LIVE_END" => New(AudienceEventKind.RoomLifecycle, text: "live_end"),
                _ => New(AudienceEventKind.Unknown),
            };
        }
    }

    private static string UserId(JsonElement data)
    {
        var userId = TryGetString(data, "open_id");
        return string.IsNullOrWhiteSpace(userId) ? "anonymous" : userId;
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;

    private static int? TryGetInt32(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var value)
                ? value
                : null;

    private static string? Normalize(string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
}
