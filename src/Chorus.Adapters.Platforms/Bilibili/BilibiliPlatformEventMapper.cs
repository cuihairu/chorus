using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Maps B站 open-live wss 事件（blivedm <c>models/open_live.py</c>，✅ 原文核实）
/// 到 <see cref="AudienceEvent"/>。P1 只归一化弹幕 DANMU_MSG：
///   data.open_id → userId（空 → "anonymous"，未登录 open_id=0 也不推断身份）
///   data.msg     → Text
///   msg_id       → EventId（缺 → 合成 guid，保持去重稳定）
/// 未知 cmd → <see cref="AudienceEventKind.Unknown"/>（不崩，管线确认后丢弃；
/// 与 DouyinPlatformEventMapper 同约定——平台协议仍在演进，见设计文档 §1.2）。
///
/// STATUS (2026-10-14): 事件类型与字段名按 blivedm open_live 模型原文实现；
/// 若联调发现字段有出入，替换本文件的字段抽取即可，管道不动。
///
/// 会话绑定：open-live 事件不带 session_id，房间 → 会话走配置
/// （Ingress:Bilibili:SessionId，P1 单绑定；多房间映射属 §14 控制面）。
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

            var kind = MapKind(cmd);
            if (kind == AudienceEventKind.Unknown)
            {
                return new AudienceEvent(
                    EventId: eventId,
                    Platform: PlatformNames.Bilibili,
                    SessionId: _sessionId,
                    UserId: "anonymous",
                    Kind: kind,
                    IngestTimeMs: ingestTimeMs,
                    Text: null,
                    GiftId: null,
                    GiftCount: null,
                    GiftValue: null,
                    MatchId: null,
                    FactionId: null);
            }

            var userId = TryGetString(data, "open_id");
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = "anonymous";
            }

            return new AudienceEvent(
                EventId: eventId,
                Platform: PlatformNames.Bilibili,
                SessionId: _sessionId,
                UserId: userId,
                Kind: kind,
                IngestTimeMs: ingestTimeMs,
                Text: TryGetString(data, "msg"),
                GiftId: null,
                GiftCount: null,
                GiftValue: null,
                MatchId: null,
                FactionId: null);
        }
    }

    private static AudienceEventKind MapKind(string? cmd)
        => string.Equals(cmd, "DANMU_MSG", StringComparison.OrdinalIgnoreCase)
            ? AudienceEventKind.Comment
            : AudienceEventKind.Unknown;

    private static string? TryGetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;

    private static string? Normalize(string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
}
