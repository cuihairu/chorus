---
title: 快速接入帧同步
---

# 快速接入帧同步

本文给新项目**最小可跑**的接入步骤：一个 WebSocket 客户端 + 一个回调，按帧号渲染权威状态。

> **诚实声明**：仓内**没有客户端 SDK**（src 全是 server 侧）。下文示例是**直接对接 WS 协议**的最小骨架，**未实测**（本轮未执行）。协议本身见 [协议参考](../reference/protocol.md) 与 [接口参考](../reference/api.md)；帧同步语义见 [帧同步方案](../design/frame-sync.md)。SDK 封装是 P3（计划中）。

---

## 接入步骤（7 步）

1. **连** `ws://<host>/ws`（端口见 [配置参考](../reference/config.md)，单进程默认 `http://localhost:5083`）。
2. **发 `hello`**：带 `sessionId` / `clientId` / `protocolVersion`。
3. **收 `welcome`**：读权威节拍配置（tick 参数），不要沿用本地默认。
4. **渲染循环**：收 `snapshot`，按 `tickId`（帧号）渲染 `state`。
5. **发 `ack`**：快照**真正应用完成后**发 `lastAppliedTickId`（不是「已收到」）。
6. **落后处理**：收 `forced=true && reason="lag"` 快照 → 立即对齐，不补历史。
7. **一个回调**：`Action<JsonElement>` 处理 `state`，渲染层订阅它。

---

## 最小可跑骨架（C#，未实测）

```csharp
// 依赖：System.Net.WebSockets（.NET 10 内置）
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://localhost:5083/ws"), CancellationToken.None);

// 2. hello
await Send(ws, new { type = "hello", sessionId = "s-1", clientId = "c-1", protocolVersion = 1 });

long lastAppliedFrameId = -1;   // 已应用的权威帧号
var render = new Action<JsonElement>(state =>
{
    // 7. 你的渲染逻辑：按帧号插值渲染 state
    Console.WriteLine($"frame={lastAppliedFrameId} state={state}");
});

var buffer = new byte[64 * 1024];
while (ws.State == WebSocketState.Open)
{
    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
    if (result.MessageType == WebSocketMessageType.Close) break;

    var doc = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(buffer, 0, result.Count));
    if (!doc.TryGetProperty("type", out var t)) continue;

    switch (t.GetString())
    {
        case "welcome":
            // 3. 读权威节拍（示例只打日志）
            Console.WriteLine($"welcome: {doc}");
            break;

        case "snapshot":
            // 4. 按帧号渲染；6. 强制快照立即对齐
            var frameId = doc.GetProperty("tickId").GetInt64();
            var forced = doc.TryGetProperty("forced", out var f) && f.GetBoolean();
            var state = doc.GetProperty("state");

            if (frameId <= lastAppliedFrameId) break;   // 幂等：丢弃旧帧
            lastAppliedFrameId = frameId;
            render(state);

            // 5. 应用完成后 ACK
            await Send(ws, new { type = "ack", sessionId = "s-1", clientId = "c-1", lastAppliedTickId = frameId });
            break;
    }
}

static async Task Send(ClientWebSocket ws, object payload)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}
```

> 上例约 40 行，**未实测**（未在本轮执行）。`forced` 分支与「落后 > maxLagTicks 等 resync」的完整追帧策略见 [帧同步方案](../design/frame-sync.md) §4.5。

---

## 关键约束

- **帧号 = `tickId`**：单调递增，随快照下发（`ServerSnapshot.cs:8`）。客户端按帧号排序与渲染。
- **ACK 语义**：`lastAppliedTickId` = 已**真正应用完成**的帧号，不是「已收到」（`protocol.md:121-130`）。
- **ACK 频控**：`Faces:StreamerWs:AckPerMinute` 默认 **600/连接/分钟**（`src/Chorus.Gateway.Core/Faces/StreamerWsFaceOptions.cs:21`），超限静默丢弃（`/faces` 的 `streamerWs.ackDropped` 计数）。按默认 `tickDurationMs=66`（≈15.2 快照/分钟）每帧回一个 ACK 约 909/分钟，**会撞上上限**——骨架应抽样发 ACK 或调高该值。
- **强制快照**：`forced=true && reason="lag"` → 立即对齐，不补历史（`protocol.md:104-105`）。
- **幂等**：帧号 ≤ 已应用的快照直接丢弃。
- **鉴权**：`/ws` 默认**fail-closed**——需 `Faces:StreamerWs:AuthTokens` 或显式开 `AllowAnonymous`（[配置参考](../reference/config.md)）。

---

## 相关页面

- [帧同步方案](../design/frame-sync.md) — 帧号语义 / 补帧 / 追帧策略
- [协议参考](../reference/protocol.md) — `hello` / `welcome` / `snapshot` / `ack` 消息
- [接口参考](../reference/api.md) — 后台/管理端接口
- [版本策略](../reference/versioning.md) — 兼容性规则
