---
title: "快速上手：帧同步接入"
---


# 快速上手：帧同步接入

本页给新项目**最小可运行**的接入步骤：一个 WebSocket 客户端 + 一个回调，按帧号渲染权威状态。

> **SDK 已交付（2026-10-14，帧同步 P3）**：`Chorus.Client.ChorusClient` 一次调用完成接入——hello/welcome 握手、按帧号渲染回调
> （`FramePlayer` 状态机，对重放/旧帧幂等丢弃）、应用后 ACK、自动 `resync` 拉取缺帧。端到端测试
> `ChorusClientTests`（3 例，含真 /ws）。
>
> 诚实边界：SDK 是**最小入口**（单连接、单回调、无自动重连——重连/退避策略留给
> 接入方；压测工具 `ClientSimulation` 演示完整形态）。协议见[协议参考](../reference/protocol.md)；帧同步
> 语义见[帧同步设计](../design/frame-sync.md)。

---

## 三行接入（SDK）

```csharp
await using var client = await ChorusClient.ConnectAsync(
    ChorusClientOptions.Default("ws://localhost:5083/ws", "s-1", "c-1"),
    state => RenderYourGame(state));   // 回调只收已应用的最新帧，按帧号严格递增
```

回调运行在接收循环上，**不要阻塞它**；`ChorusClientOptions` 可调 ACK 采样（`AckEveryFrames`，默认 2）与 `resync` 冷却（默认 2s）。

---

## 协议步骤（SDK 已替你做；裸协议接入时参考）

1. **连接** `ws://<host>/ws`（端口见[配置参考](../reference/config.md)；单进程默认 `http://localhost:5083`）。
2. **发 `hello`**：带 `sessionId` / `clientId` / `protocolVersion`。
3. **收 `welcome`**：读权威节拍配置（tick 参数）；别再守本地默认值。
4. **渲染循环**：收 `snapshot`，用 `tickId`（帧号）渲染 `state`；帧号 ≤ 已应用帧的直接丢弃（幂等；重放窗口不会渲染两遍）。
5. **发 `ack`**：在快照**真正应用后**才发 `lastAppliedTickId`（不是"收到"）。
6. **缺口检测**：帧号 > 已应用帧 + 1 → 丢帧了，发 `resync`（`fromFrameId` = 已应用帧 + 1）拉取窗口内每一帧。
7. **滞后处理**：收到 `forced=true && reason="lag"` 或 `reason="resync-gap"` 快照 → 立即对齐，不回填历史。
8. **一个回调**：`Action<JsonElement>` 处理 `state`；渲染层订阅它。

---

## 裸协议骨架（C#；语义参考实现见 `Chorus.Client`）

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
            // 4. 按帧号渲染；7. 强制快照在此对齐
            var frameId = doc.GetProperty("tickId").GetInt64();
            var forced = doc.TryGetProperty("forced", out var f) && f.GetBoolean();
            var state = doc.GetProperty("state");

            if (frameId <= lastAppliedFrameId) break;   // 幂等：丢弃旧帧与回放帧
            var gapFrom = lastAppliedFrameId >= 0 && frameId > lastAppliedFrameId + 1
                ? lastAppliedFrameId + 1 : -1;          // 6. 缺口检测
            lastAppliedFrameId = frameId;
            render(state);

            // 6. 拉取窗口内每一帧；forced 快照本身就是对齐结果，不要再拉
            if (gapFrom >= 0 && !forced)
            {
                await Send(ws, new { type = "resync", sessionId = "s-1", clientId = "c-1", fromFrameId = gapFrom });
            }

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

> 上例约 45 行且**未测试**（裸协议参考代码）；同一语义的**已测试**实现是
> `Chorus.Client`（`ChorusClient` = 握手 + 重放 + ACK 采样 + resync 冷却；`FramePlayerTests` 7 例 + `ChorusClientTests` 3 例），压测工具
> `ClientSimulation` 也用它。`forced` 分支与"lag > maxLagTicks 等重同步"的完整追赶策略见[帧同步设计](../design/frame-sync.md) §4.5。

---

## 关键约束

- **帧号 = `tickId`**：单调递增、随快照下发（`ServerSnapshot.cs:8`）。客户端按帧号排序渲染。
- **ACK 语义**：`lastAppliedTickId` = **真正应用**的帧号，不是"收到"（`protocol.md:121-130`）。
- **ACK 限流**：`Faces:StreamerWs:AckPerMinute` 默认 **600/连接/分钟**（`src/Chorus.Gateway.Core/Faces/StreamerWsFaceOptions.cs:21`），
  超限 ACK 被静默丢弃（`/faces` 的 `streamerWs.ackDropped` 计数）。默认 `tickDurationMs=66`（≈15.2 快照/分钟）时，
  每帧一个 ACK 约 909/分钟，**必撞限**——骨架应对 ACK 采样或调大该值。
- **强制快照**：`forced=true && reason="lag"` → 立即对齐、不回填历史（`protocol.md:104-105`）；`reason="resync-gap"` → 你的 `resync` 请求
  窗口已被逐出，本帧即最新对齐，**不要再拉**。
- **resync 限流**：两层（`Faces:StreamerWs:ResyncPerMinute` 默认 30/连接/分钟 + 引擎冷却 `Session:ResyncRequestCooldownMs`
  默认 1000ms）。只在真缺口时发一次；重放帧会被幂等丢弃。
- **幂等**：帧号 ≤ 已应用帧的快照直接丢弃。
- **鉴权**：`/ws` 默认 **fail-closed**——需要 `Faces:StreamerWs:AuthTokens` 或显式开启 `AllowAnonymous`（见[配置参考](../reference/config.md)）。

---

## 相关页面

- [帧同步设计](../design/frame-sync.md) —— 帧号语义 / 帧回填 / 追赶策略
- [协议参考](../reference/protocol.md) —— `hello` / `welcome` / `snapshot` / `ack` / `resync` 消息
- [API 参考](../reference/api.md) —— 后端/管理端点
- [版本策略](../reference/versioning.md) —— 兼容性规则
