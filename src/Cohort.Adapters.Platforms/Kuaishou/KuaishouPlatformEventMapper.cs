using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohort.Adapters.Abstractions;
using Cohort.Protocol.Models;

namespace Cohort.Adapters.Platforms;

/// <summary>
/// Maps Kuaishou event callbacks to <see cref="AudienceEvent"/>.
///
/// ENVELOPE: official per docs/saas/develop/api/encryptedMsg.html -
///   { "encryptedMsg": base64, "msgId": string, "componentAppId": string, "timestamp": ms }
/// where encryptedMsg = AES-256-CBC/PKCS7 over the event JSON, key = base64-decoded
/// 消息加密解密KEY (32 bytes), IV = first 16 key bytes. msgId is the official
/// unique message id and is used as the dedup EventId.
///
/// STATUS (2026-10-12): the inner event JSON layout for live events (danmaku /
/// gift / like) is NOT publicly documented - the placeholder inner contract
/// below mirrors the douyin adapter (event / session_id / user_id / content /
/// gift_*) and must be replaced at joint debugging.
/// TODO(joint-debugging): replace <see cref="MapInnerEvent"/> with the official
/// live event schema; swap session_id source for the official room mapping.
/// </summary>
public sealed class KuaishouPlatformEventMapper : IPlatformEventMapper
{
    private readonly string? _messageEncryptKey;

    public KuaishouPlatformEventMapper(string? messageEncryptKey)
    {
        _messageEncryptKey = string.IsNullOrWhiteSpace(messageEncryptKey) ? null : messageEncryptKey.Trim();
    }

    public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
    {
        if (!string.Equals(platform, PlatformNames.Kuaishou, StringComparison.OrdinalIgnoreCase))
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

            var msgId = TryGetString(root, "msgId");
            if (string.IsNullOrWhiteSpace(msgId))
            {
                return null;
            }

            var inner = TryDecryptMessage(root);
            if (inner == null)
            {
                return null;
            }

            using (inner)
            {
                var innerRoot = inner.RootElement;

                var sessionId = TryGetString(innerRoot, "session_id");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return null;
                }

                var kind = MapEventKind(TryGetString(innerRoot, "event"));

                return new AudienceEvent(
                    EventId: msgId,
                    Platform: PlatformNames.Kuaishou,
                    SessionId: sessionId,
                    UserId: TryGetString(innerRoot, "user_id") ?? "anonymous",
                    Kind: kind,
                    IngestTimeMs: ingestTimeMs,
                    Text: TryGetString(innerRoot, "content"),
                    GiftId: TryGetString(innerRoot, "gift_id"),
                    GiftCount: TryGetInt32(innerRoot, "gift_count"),
                    GiftValue: TryGetInt32(innerRoot, "gift_value"),
                    MatchId: TryGetString(innerRoot, "match_id"),
                    FactionId: TryGetString(innerRoot, "faction_id")
                );
            }
        }
    }

    private JsonDocument? TryDecryptMessage(JsonElement envelope)
    {
        if (_messageEncryptKey == null)
        {
            return null;
        }

        var encrypted = TryGetString(envelope, "encryptedMsg");
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(_messageEncryptKey);
        }
        catch (FormatException)
        {
            return null;
        }

        if (key.Length < 16)
        {
            return null;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = key.AsSpan(0, 16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var cipherText = Convert.FromBase64String(encrypted);
            var plain = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
            return JsonDocument.Parse(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            return null;
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
