---
title: 平台适配器契约
---


# 平台适配器契约与模板

接入一个新的直播平台 = 实现两个接口 + 注册 + 密钥配置。核心不感知任何平台私有的字段。

相关文档：

- [抖音接入](../reference/platform-douyin.md)
- [快手接入](../reference/platform-kuaishou.md)
- [B 站接入](../reference/platform-bilibili.md)
- [抖音小游戏接入](../reference/platform-douyin-mini-game.md)
- [快手小游戏接入](../reference/platform-kuaishou-mini-game.md)
- [部署与安全配置](../reference/deployment.md)

## 契约

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

## 入口处理顺序

`POST /ingress/{platform}` 顺序固定；接入方按该顺序理解失败语义：

1. 限流（`Faces:PlatformIngress:RateLimitPerMinute`，默认 1200/分钟，按来源 IP）→ **429**
2. 请求体上限（`Faces:PlatformIngress:MaxBodyBytes`，默认 64KB）→ **413**
3. 处理超时（`Faces:PlatformIngress:TimeoutMs`，默认 5000ms，覆盖整个读取+处理时长；客户端取消不计）→ **504**
4. 验签 → **401**（`Ingress:AllowAll` 默认 false；缺验证器同样 401）
5. 映射（遍历所有 `IPlatformEventMapper`；返回 null 交给下一个）→ 全都不匹配则 **400** `{"error":"unmapped_event"}`
6. 阵营规则补全（`Ingress:FactionRules:CommentKeywords` / `GiftIds`）
7. 去重（`Ingress:DedupTtlSeconds`，默认 600s，reserve/commit/rollback）→ 重复返回 `{"duplicated":true}` **200**
8. 进入 SessionActor 事件通道 → 通道满且超时 **503**（此时去重标记回滚，平台可重试）

指标：`chorus_ingress_*` / `chorus_events_*`，见[可观测性](../reference/observability.md)。

## 统一模型 `AudienceEvent`

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

- `eventId`：平台侧事件 ID；为空时 mapper 必须推导稳定 ID（去重依赖它）。
- `matchId` / `factionId`：可由 mapper 提供，也可由 `IAudienceFactionRule` 补全。
- 未知事件类型映射为 `Unknown`；**不要抛异常**。

## 模板：Mapper

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

## 模板：验签器（fail-closed）

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

要点：

- 密钥从配置读取，**绝不硬编码**。
- 比较用 `FixedTimeEquals` 常量时间，避免时序侧信道。
- 时间戳窗口与 nonce 防重放按平台官方文档实现；官方文档未给的字段一律按"待联调"处理。

## 注册

以 `Chorus.Server` / `Chorus.Gateway` 现有的抖音注册为模板（两个宿主同构）：

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

两个注意点：

- Router 按 `PlatformName`（大小写不敏感）路由，具名验证器优先于 fallback：即使 `Ingress:AllowAll=true`，抖音回调仍走 `DouyinPlatformEventVerifier`。
- Mapper 的遍历顺序 = 注册顺序。把"无条件吞掉所有 body"的 mapper 放最后，或让它先判断 `platform` 再决定是否接管。

## 验收测试（每个适配器必须通过）

1. 正确签名通过（200）
2. 篡改 body / 缺签名 / 错密钥 → 401
3. 重复 `eventId` → `{"duplicated":true}` 200
4. 未知事件类型 → 不崩溃，映射为 `Unknown`
5. 超大 body → 413
6. 超限流 → 429
7. 事件通道满（`Session:MaxPendingEvents`）→ 503 且去重标记回滚（平台重试可再进）

## 待联调字段标记约定

官方文档无法确认的字段一律按本约定处理，**不得虚构**：

```csharp
// TODO(待联调): 官方文档未给出 giftValue 字段，需沙箱联调确认后再启用
GiftValue: null,
```

- 不确定的字段名、枚举值、签名算法 → 保持接口与字段位置，值留空/`Unknown`，并在适配器文档的"待联调"章节列出。
- 密钥、回调 URL、平台 appKey 一律走配置，不进代码。
- 联调确认后回填字段，并把对应 `platform-*.md` 的"待联调"清单更新为已确认。
