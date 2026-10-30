---
title: "对战：会话、帧同步与客户端接入"
---


# 对战：会话、帧同步与客户端接入

封装状态：**已封装**。对战能力由四个稳定接口构成：

| 板块 | 入口 | 说明 |
|---|---|---|
| 观众输入 | `POST /ingress/{platform}` | 弹幕/礼物 → `AudienceEvent`，唯一玩法输入面 |
| 主播侧渲染 | `WS /ws` | hello → welcome → snapshot 循环 |
| 客户端 SDK | `Chorus.Client.ChorusClient` | 连接/渲染/ack/resync 一体 |
| 服务端规则 | `IGameModule` / `IGameModuleFactory` | 宿主注册，引擎每 tick 调用 |

## 一分钟理解模型

- **没有"创建对局"API**：会话在首个观众事件或首个 WS hello 到达时隐式建立
  （`src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs:142`、`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:106`）。
  hello 不带 `sessionId` 时服务端生成 `s_{guid}`（`StreamerWsFace.cs:142-143`）。
- **服务端权威 tick**：默认每 66ms 一个逻辑帧（`src/Chorus.Engine/Session/SessionConfig.cs:4`），
  事件先缓冲、下一 tick 统一生效，`InputDelayTicks`（默认 2，`:5`）抗平台抖动。
- **客户端只管渲染**：每 tick 一帧快照（`SnapshotEveryTicks` 默认 1，`:6`）；应用完一帧回 `ack`，
  检测到缺帧发 `resync`，引擎从帧历史重放或推送强制快照对齐。

## 客户端接入：最小示例

用 SDK（推荐；消息语义已实现：乱序丢弃、幂等重放、ack 节流、resync 冷却）：

```csharp
using Chorus.Client;

var options = ChorusClientOptions.Default(
    wsUrl: "ws://localhost:5083/ws",
    sessionId: "s1",        // 首次可传空语义：见下裸协议说明
    clientId: "c1");

await using var client = await ChorusClient.ConnectAsync(
    options,
    onState: state => Console.WriteLine($"tick state: {state.GetRawText()}"));

// client.Completion      —— 连接关闭（服务器关闭/传输错误/Dispose）时完成
// client.FramesApplied   —— 已应用帧数
// client.LastAppliedFrameId / AcksSent / ResyncsSent —— 诊断
```

`ChorusClient.ConnectAsync` 签名在 `src/Chorus.Client/ChorusClient.cs:72`；连接完成
hello/welcome 握手，握手超时由 `HandshakeTimeout` 控制。**回调在接收循环上执行；不要阻塞它。**

`ChorusClientOptions` 参数（`ChorusClient.cs:12-34`）：

| 参数 | 类型 | 含义 |
|---|---|---|
| `WsUrl` | string | 完整 `/ws` 地址 |
| `SessionId` / `ClientId` | string | 会话与客户端身份 |
| `Token` | string? | hello 鉴权 token（`Faces:StreamerWs:AuthTokens`，fail-closed） |
| `AckEveryFrames` | int | 每 N 帧回一个 ack（默认 2；生产限 600 ack/分钟/连接） |
| `ResyncCooldown` | TimeSpan | 客户端 resync 冷却（引擎侧另有 `Session:ResyncRequestCooldownMs`，默认 1000ms） |
| `HandshakeTimeout` | TimeSpan | hello→welcome 预算 |

裸 WS 协议（不用 SDK 时）：JSON 文本帧；消息 `type` 常量在
`src/Chorus.Protocol/ProtocolTypes.cs:5-12`。

```json
// 客户端 → 服务器（首帧必须是 hello）
{"type":"hello","sessionId":"s1","clientId":"c1","token":"...","protocolVersion":1}

// 应用帧后（引擎按此计算 lag；超 MaxLagTicks 会收到 forced 快照对齐）
{"type":"ack","sessionId":"s1","clientId":"c1","lastAppliedTickId":42}

// 检测到帧缺口时（fromFrameId = 最后连续帧号）
{"type":"resync","sessionId":"s1","clientId":"c1","fromTickId":40}
```

```json
// 服务器 → 客户端
{"type":"welcome","sessionId":"s1","clientId":"c1","tickDurationMs":66,"inputDelayTicks":2,"snapshotEveryTicks":1,"serverTimeMs":1760000000000,"protocolVersion":1}
{"type":"snapshot","sessionId":"s1","tickId":42,"serverTimeMs":1760000000066,"state":{...},"v":1,"frameEvents":null}
```

## 响应结构

消息 record 定义在 `src/Chorus.Protocol/Messages/`：

| 消息 | 字段 | 出处 |
|---|---|---|
| `welcome` | `sessionId`、`clientId`、`tickDurationMs`、`inputDelayTicks`、`snapshotEveryTicks`、`serverTimeMs`、`protocolVersion` | `ServerWelcome.cs:5-14` |
| `snapshot` | `sessionId`、`tickId`、`serverTimeMs`、`state`（玩法 JSON）、`forced?`、`reason?`、`targetClientId?`、`clientLagTicks?`、`clientLastAckTickId?`、`v`（schema 版本）、`frameEvents?`（逐事件归属） | `ServerSnapshot.cs:5-24` |
| `error` | `message`、`code?` | `ServerError.cs` |

字段语义与版本策略见[协议参考](../reference/protocol.md)与[版本策略](../reference/versioning.md)。

## 错误码与拒绝行为

WS 层失败大多表现为**关闭连接**而非错误码（`src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs`）：

| 触发 | 行为 | 出处 |
|---|---|---|
| hello 超消息上限 | 关闭 `MessageTooBig` "hello too large" | `:94` |
| hello 解析失败 | 关闭 `NormalClosure` "invalid hello" | `:100` |
| 首帧不是 hello | 关闭 `NormalClosure` "first message must be hello" | `:117` |
| hello 鉴权失败（fail-closed） | `error` `unauthorized: {reason}` 后关闭 | `:138` |
| 后续消息超限 | 关闭 `MessageTooBig` "message too large" | `:175` |
| 引擎不可用 / welcome 超时（拆分模式） | `error` `Engine unavailable` / `Engine welcome timeout` | `src/Chorus.Gateway/IpcSessionRouter.cs:44`、`:57` |

握手成功后只接受 `ack` / `resync` / `ping`（`StreamerWsFace.cs:182-237`）。

## 服务端：定义对战规则

对战规则实现 `IGameModule`（`src/Chorus.Engine/Abstractions/IGameModule.cs:6-24`），
宿主注册工厂（`src/Chorus.Server/Program.cs:17`、`src/Chorus.EngineHost/Program.cs:11`）：

| 成员 | 职责 |
|---|---|
| `Name` | 玩法标识（快照里的 `game` 字段） |
| `ApplyEvents(tickId, events)` | 每 tick 消费这批观众事件、推进玩法状态 |
| `GetStateSnapshot()` | 产出快照 `state`（每 tick 全量发给客户端；控制大小） |
| `RestoreState(version, state)` | 会话出生时从权威快照恢复；未知版本直接抛（fail-closed） |

参考实现见 `src/Chorus.SampleGame/SampleGameModule.cs`（Name `:32`、状态结构 `users`/`totals`/`top` `:125-138`）。
怎么写、有哪些反模式见[游戏模块](./game-module.md)。

## 边界（不做什么）

- **没有创建/加入对局的 HTTP API**：会话隐式建立；显式"开局/加入"的产品语义未定
  （匹配落位见[匹配](./matchmaking.md)的边界说明）。
- **客户端写不了玩法数据**：WS 上只有 `ack`/`resync`/`ping`；玩法输入只能来自平台入口（把滥用面压到最小）。
- **玩法类型进程级固定**：一个宿主一个 `IGameModuleFactory`；不支持按请求选玩法。
- **网关不持会话状态**（拆分模式）：重连到另一网关副本不影响会话，但重连语义由客户端实现。
- **不持久化**：进程重启对局即终止（见[架构总览](../architecture.md)决策 5）。

## 相关页面

- [快速上手](./getting-started.md) / [帧同步最佳实践](../design/frame-sync-best-practices.md)
- [客户端重同步状态机](../design/client-resync-state-machine.md)
- [API 参考](../reference/api.md)（错误码表）
