using System.Globalization;
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Maps B站 open-live wss 事件（blivedm <c>models/open_live.py</c>，✅ 原文核实）
/// 到 <see cref="AudienceEvent"/>。P1 弹幕 + P2 礼物/进场/房间生命周期 + 深化（2026-11-14）：
///   DANMU_MSG                          → comment（data.open_id / data.msg / msg_id）
///   SEND_GIFT / SEND_GIFT_V2（灰度）  → gift（data.gift_id / data.gift_num / data.price；
///                                        V2 新格式容忍：字段缺失走默认值，不丢事件不崩）
///   INTERACT_WORD_V2 / LIVE_ROOM_ENTER → enter（data.open_id）
///   LIVE_START / LIVE_END             → roomLifecycle（Text=live_start / live_end）
///   SUPER_CHAT_MESSAGE                → superChat（留言文本 data.message，网页弹幕形态
///                                        data.msg 容忍回退；金额 data.price；付费留言。
///                                        **留言文本与金额字段名未获官方物料确认**，双字段回退
///                                        是假设性兼容处理；
///                                        撤销语义 SUPER_CHAT_MESSAGE_DELETE 不可表达→Unknown）
///   GUARD_BUY / USER_TOAST_MSG_V2     → guardBuy（guard_level→GiftId、num→GiftCount、
///                                        price→GiftValue；TOAST 同义容忍缺字段）
///   LIVE_OPEN_PLATFORM_INTERACTION_END → roomLifecycle（Text=interaction_end，连线玩法收束）
///   其余 → Unknown（不崩，管线确认后丢弃；平台协议仍在演进，见设计文档 §1.2）
///
/// STATUS (2026-11-14): 事件**名**（cmd）与弹幕/礼物/进场字段名按 blivedm open_live 模型
/// 原文实现；2026-11-14 新增的醒目留言/上舰字段名（data.message 与 data.msg 双回退、
/// guard_level/num/price）**未获官方物料确认**，属假设性兼容处理，以 blivedm 模型为对照
/// 基准。若联调发现字段有出入，替换本文件的字段抽取即可，管道不动。
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

            // msg_id 是全部下游来源去重的应答键：缺失即拒绝映射（端点 400 unmapped_event），
            // 不合成随机占位——随机 id 会让同一报文的每次重试都绕过去重。
            var eventId = TryGetString(root, "msg_id");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return null;
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
                // 留言文本字段名未在官方物料中锁定：open-live 载荷用 message，网页弹幕形态用
                // msg——按 SEND_GIFT_V2 灰度容忍先例两个都取，联调确定后收窄其一即可。
                "SUPER_CHAT_MESSAGE" => New(
                    AudienceEventKind.SuperChat,
                    text: TryGetString(data, "message") ?? TryGetString(data, "msg"),
                    giftValue: TryGetInt32(data, "price")),
                "GUARD_BUY" or "USER_TOAST_MSG_V2" => New(
                    AudienceEventKind.GuardBuy,
                    giftId: TryGetInt32(data, "guard_level") is { } guardLevel
                        ? guardLevel.ToString(CultureInfo.InvariantCulture)
                        : null,
                    giftCount: TryGetInt32(data, "num"),
                    giftValue: TryGetInt32(data, "price")),
                "LIVE_OPEN_PLATFORM_INTERACTION_END" => New(
                    AudienceEventKind.RoomLifecycle, text: "interaction_end"),
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
