---
title: Platform Adapter Contract
---


# Platform Adapter Contract and Templates

Onboarding a new live-streaming platform = implementing two interfaces + registration + secret configuration. The core is unaware of any platform-specific private fields.

Related docs:

- [Douyin Integration](../reference/platform-douyin.md)
- [Kuaishou Integration](../reference/platform-kuaishou.md)
- [Bilibili Integration](../reference/platform-bilibili.md)
- [Douyin Mini Game Integration](../reference/platform-douyin-mini-game.md)
- [Kuaishou Mini Game Integration](../reference/platform-kuaishou-mini-game.md)
- [Deployment and Security Configuration](../reference/deployment.md)

## Contract

```csharp
namespace Chorus.Adapters.Abstractions;

// 原始 body -> 统一 AudienceEvent。返回 null 表示"不处理该平台/该类型"。
public interface IPlatformEventMapper
{
    AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs);
}

// 验签/验时间戳。生产默认 fail-closed：未通过即 401。
public interface IPlatformEventVerifier
{
    bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers);
}

// 具名验证器：Ingress 按 PlatformName 路由；平台没有注册验证器时直接拒绝。
public interface IPlatformNamedEventVerifier : IPlatformEventVerifier
{
    string PlatformName { get; }
}

// 阵营规则：未显式带 factionId 时按弹幕口令/礼物 ID 补全。
public interface IAudienceFactionRule
{
    AudienceEvent Apply(AudienceEvent e);
}
```

## Ingress Processing Order

`POST /ingress/{platform}` has a fixed order; integrators understand the failure semantics in this order:

1. Rate limiting (`Faces:PlatformIngress:RateLimitPerMinute`, default 1200/minute, by source IP) → **429**
2. Request body limit (`Faces:PlatformIngress:MaxBodyBytes`, default 64KB) → **413**
3. Processing timeout (`Faces:PlatformIngress:TimeoutMs`, default 5000ms, covers the whole read+process duration; client cancellation does not count) → **504**
4. Signature verification → **401** (`Ingress:AllowAll` defaults to false; missing verifier also gives 401)
5. Mapping (iterate over all `IPlatformEventMapper`; null return goes to the next) → if none match **400** `{"error":"unmapped_event"}`
6. Faction rule completion (`Ingress:FactionRules:CommentKeywords` / `GiftIds`)
7. Deduplication (`Ingress:DedupTtlSeconds`, default 600s, reserve/commit/rollback) → duplicates return `{"duplicated":true}` **200**
8. Enter the SessionActor event lane → lane full with timeout **503** (at this point the dedup marker rolls back, and the platform can retry)

Metrics: `chorus_ingress_*` / `chorus_events_*`, see [Observability](../reference/observability.md).

## Unified Model `AudienceEvent`

```csharp
public sealed record AudienceEvent(
    string EventId,
    string Platform,
    string SessionId,
    string UserId,
    AudienceEventKind Kind,   // Unknown=0, Comment=1, Like=2, Gift=3
    long IngestTimeMs,
    string? Text = null,
    string? GiftId = null,
    int? GiftCount = null,
    int? GiftValue = null,
    string? MatchId = null,
    string? FactionId = null
);
```

- `eventId`: the platform-side event id; when empty, the mapper must derive a stable id (deduplication relies on it).
- `matchId` / `factionId`: can be provided by the mapper, or completed by `IAudienceFactionRule`.
- Unknown event types map to `Unknown`; **do not throw exceptions**.

## Template: Mapper

```csharp
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace MyAdapters;

public sealed class MyPlatformMapper : IPlatformEventMapper
{
    public const string Platform = "myplatform";

    public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
    {
        if (!string.Equals(platform, Platform, StringComparison.OrdinalIgnoreCase))
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
            return null; // Ingress 返回 400
        }

        using (doc)
        {
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
            eventId ??= $"{Platform}:{ingestTimeMs}:{Guid.NewGuid():N}";

            var userId = root.TryGetProperty("userId", out var userIdEl) ? userIdEl.GetString() : null;
            userId ??= "anonymous";

            return new AudienceEvent(
                EventId: eventId,
                Platform: Platform,
                SessionId: sessionId,
                UserId: userId,
                Kind: MapKind(root),
                IngestTimeMs: ingestTimeMs,
                Text: root.TryGetProperty("text", out var t) ? t.GetString() : null,
                GiftId: root.TryGetProperty("giftId", out var g) ? g.GetString() : null,
                GiftCount: root.TryGetProperty("giftCount", out var gc) && gc.TryGetInt32(out var gcn) ? gcn : null,
                GiftValue: root.TryGetProperty("giftValue", out var gv) && gv.TryGetInt32(out var gvn) ? gvn : null);
        }
    }

    private static AudienceEventKind MapKind(JsonElement root)
    {
        // 未知类型 -> Unknown，绝不抛异常
        return root.TryGetProperty("kind", out var kindEl)
               && kindEl.ValueKind == JsonValueKind.String
               && Enum.TryParse(kindEl.GetString(), true, out AudienceEventKind parsed)
            ? parsed
            : AudienceEventKind.Unknown;
    }
}
```

## Template: Signature Verifier (fail-closed)

```csharp
public sealed class MyPlatformVerifier : IPlatformNamedEventVerifier
{
    private readonly string _appSecret;

    public MyPlatformVerifier(string appSecret)
    {
        _appSecret = appSecret ?? string.Empty;
    }

    public string PlatformName => MyPlatformMapper.Platform;

    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        // 缺密钥 -> 拒绝（fail-closed），不降级、不报警
        if (_appSecret.Length == 0)
        {
            return false;
        }

        if (!headers.TryGetValue("x-my-signature", out var signature) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = ComputeSignature(rawBody);
        return CryptographicOperations.FixedTimeEquals(
            SHA256(rawBody.UTF8Bytes()),
            FromBase64(signature));
    }
}
```

Key points:

- Secrets are read from configuration; **never hard-coded**.
- Use `FixedTimeEquals` for constant-time comparison to avoid timing side channels.
- Timestamp windows and nonce anti-replay are implemented per the platform's documentation; fields not specified in the official documentation are treated as "pending joint debugging".

## Registration

Use the existing Douyin registration in `Chorus.Server` / `Chorus.Gateway` as the template (the two hosts are isomorphic):

```csharp
// 1. 具名验证器：先注册具体类型，再以 IPlatformNamedEventVerifier 暴露给 Router 收集
builder.Services.AddSingleton(sp => new MyPlatformVerifier(
    secret: sp.GetRequiredService<IConfiguration>()["Ingress:MyPlatform:AppSecret"]));
builder.Services.AddSingleton<IPlatformNamedEventVerifier>(sp => sp.GetRequiredService<MyPlatformVerifier>());

// 2. Router 是已有注册，无需改动：它把 GetServices<IPlatformNamedEventVerifier>()
//    按 PlatformName 收进字典，再带 AllowAllPlatformEventVerifier 作 fallback。
//    AllowAll 只在 Ingress:AllowAll=true 时注册（默认 false → 不注册 → 未知平台 401）。

// 3. 映射器（Ingress 遍历全部 IPlatformEventMapper，返回 null 交给下一个）
builder.Services.AddSingleton<IPlatformEventMapper, MyPlatformMapper>();
```

Two things to note:

- The Router routes by `PlatformName` (case-insensitive), and a named verifier takes precedence over the fallback: even if `Ingress:AllowAll=true`, Douyin callbacks still go through `DouyinPlatformEventVerifier`.
- Mapper iteration order = registration order. Put a mapper that "unconditionally swallows all bodies" last, or have it check `platform` first and then decide whether to take over.

## Acceptance Tests (every adapter must pass)

1. Correct signature passes (200)
2. Tampered body / missing signature / wrong secret → 401
3. Duplicate `eventId` → `{"duplicated":true}` 200
4. Unknown event type → no crash, mapped to `Unknown`
5. Oversized body → 413
6. Rate limit exceeded → 429
7. Event lane full (`Session:MaxPendingEvents`) → 503 and dedup marker rolls back (platform retry can re-enter)

## Convention for Pending Joint Debugging Field Markers

Fields that cannot be confirmed by official documentation are always handled per this convention; **do not fabricate**:

```csharp
// TODO(待联调): 官方文档未给出 giftValue 字段，需沙箱联调确认后再启用
GiftValue: null,
```

- Uncertain field names, enum values, or signature algorithms → keep the interface and field position, leave values empty/`Unknown`, and list them in the "Pending Joint Debugging" section of the adapter doc.
- Secrets, callback URLs, and platform appKeys all go through configuration, not code.
- After joint debugging confirms them, backfill the fields and update the "Pending Joint Debugging" list in the corresponding `platform-*.md` to confirmed.
