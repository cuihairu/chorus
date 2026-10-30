---
title: 协议参考
---


# 协议参考（v1）

## hello（客户端 -> 服务端）

```json
{"type":"hello","sessionId":"optional","clientId":"optional","token":"optional","protocolVersion":1}
```

字段说明：

- `sessionId`：客户端要加入的会话；可选
- `clientId`：客户端标识；可选；未提供时服务端可以分配一个
- `token`：接入凭证；可选；由服务端校验（见下）
- `protocolVersion`：线协议版本；可选；当前为 `1`（见下）

### hello 协议版本（自 2026-10-12）

`hello.protocolVersion` / `welcome.protocolVersion` 标识的是**线协议**版本（由服务端 `Chorus.Protocol` 的 `ProtocolVersionPolicy.Current` 决定），与 `clientVersion`（客户端自身构建号，自由字符串）无关：

- 默认（`null`/不发送）→ 视为旧客户端，**放行**（向后兼容，不强升级）；
- 发送且与服务端版本一致 → 放行；
- 发送且与服务端版本不一致 → 服务端先回 `error`（`protocol_mismatch: client=N server=M`），
  随后以 `PolicyViolation` 关闭连接，并计入 `/faces` 的
  `streamerWs.protocolMismatch`
  与 `chorus_ws_protocol_mismatch_total`；
- 版本校验发生在**鉴权之前**：版本不对的客户端不消耗鉴权决策。

### hello 鉴权（自 2026-10-12，fail-closed）

收到 hello 后，服务端校验 `token`（配置 `Faces:StreamerWs:AuthTokens`，支持 `token@unixMs过期时间`，
多个 token 以分号分隔）：

- token 缺失 / 未知 / 过期 → 服务端先回 `error`（`unauthorized: missing_token | invalid_token | token_expired`），
  随后以 `PolicyViolation` 关闭连接；
- 未配置任何 token 且未开启 `Faces:StreamerWs:AllowAnonymous` → 拒绝一切连接（fail-closed）；
- `AllowAnonymous` 只建议在开发配置（appsettings.Development.json）里显式开启。

hello 是单条 WS 文本消息；超过 `Faces:StreamerWs:MaxMessageBytes`（默认 64KiB）→ 以
`MessageTooBig` 关闭连接。ACK 超过 `Faces:StreamerWs:AckPerMinute`（默认 600/连接/分钟）→
静默丢弃并计入 `/faces` 的 `streamerWs.ackDropped`。

## welcome（服务端 -> 客户端）

```json
{
  "type":"welcome",
  "sessionId":"session-1",
  "clientId":"client-1",
  "tickDurationMs":66,
  "inputDelayTicks":2,
  "snapshotEveryTicks":1,
  "serverTimeMs":1710000000000,
  "protocolVersion":1
}
```

字段说明：

- `tickDurationMs`：权威 tick 周期
- `inputDelayTicks`：输入在统一结算前被调度到未来多少个 tick
- `snapshotEveryTicks`：周期快照频率
- `serverTimeMs`：服务端当前时间戳，客户端可用于漂移估计
- `protocolVersion`：服务端线协议版本；客户端应与自身实现版本比对，不一致时主动断开而不是猜测字段

客户端收到 `welcome` 后，应以它为权威节拍配置，而不是继续使用本地默认值。

## snapshot（服务端 -> 客户端）

```json
{
  "type":"snapshot",
  "sessionId":"session-1",
  "tickId":123,
  "serverTimeMs":1710000000123,
  "state": {},
  "forced":true,
  "reason":"lag",
  "targetClientId":"client-1",
  "clientLagTicks":9,
  "clientLastAckTickId":114,
  "frameEvents":[{"frameId":123,"eventId":"ev-1","kind":"comment","userId":"u-1"}],
  "v":1
}
```

字段说明：

- `tickId`：本快照对应的权威 tick
- `serverTimeMs`：快照生成时的服务端时间
- `state`：权威状态快照；v1 为 JSON
- `forced`：是否为强制重同步快照
- `reason`：强制原因；当前常见值有 `lag`（服务端基于 ACK 判断客户端落后而主动推送）与 `resync-gap`（客户端 `resync` 请求的回填帧窗口已被逐出，反馈最新帧）
- `targetClientId`：目标客户端；服务端只想让某个特定慢客户端立即对齐时使用
- `clientLagTicks`：服务端判断该客户端当前落后多少个 tick
- `clientLastAckTickId`：服务端看到该客户端最近的 ACK tick
- `frameEvents`：可选（自 2026-10-13，帧同步 P2）。自上次快照以来已应用的弹幕事件；每项的 `frameId` 是事件所应用的帧，`eventId` 是事件 ID（去重键），`kind` 是归一化事件类型（`comment` / `like` / `gift` / `share`），`userId` 是平台用户 ID。**没有事件时整个字段省略**（而不是空数组）——增量可选字段，不触发 `v` 抬升（见[版本策略](./versioning.md)）。旧客户端可忽略。
- `v`：快照 schema 版本（2026-10-13 起线上生效，见[版本策略](./versioning.md)）；从引擎快照透传，当前恒为 `1`；旧发送方省略时接收方按 `1` 读取。未知版本的处理语义（拒绝 + 强制重同步）将在设计首个 v2 schema 时定义。

语义：

- 正常周期快照用于稳定跟随与重连恢复
- `forced=true && reason="lag"` 表示服务端判定客户端落后，需要立即对齐到该快照
- `forced=true && reason="resync-gap"` 表示客户端 `resync` 请求的起始帧已不在引擎窗口内；该快照即当前最新帧，客户端应直接对齐，不再回填历史
- 收到强制快照后，客户端不应再长时间回填历史帧，应优先切换到该快照的状态

## ack（客户端 -> 服务端）

```json
{
  "type":"ack",
  "sessionId":"session-1",
  "clientId":"client-1",
  "lastAppliedTickId":123,
  "clientTimeMs":1710000000999
}
```

字段说明：

- `lastAppliedTickId`：客户端真正应用完成的权威 tick
- `clientTimeMs`：可选，用于辅助漂移估计与排障

语义约束：

- ACK 只能在状态真正应用完成后发送
- ACK 不能表示"收到了消息"
- ACK 不能表示"已排队，稍后应用"

若客户端在快照尚未应用完时就发 ACK，服务端会误判它已追上。

## resync（客户端 -> 服务端）

自 2026-10-13（帧同步 P2）。客户端发现缺帧或重连后，按帧号拉取回填帧窗口：

```json
{
  "type":"resync",
  "sessionId":"session-1",
  "clientId":"client-1",
  "fromFrameId":123
}
```

字段说明：

- `fromFrameId`：客户端已应用的最后一帧帧号；服务端反馈 `[fromFrameId, 当前 tickId]` 窗口内的快照序列
- `sessionId` / `clientId`：绑定字段，用于校验并定位目标客户端（`clientId` 未知的请求计数后忽略）

服务端响应语义（两种，互斥）：

1. **窗口命中** → 通过直发通道按帧号顺序反馈窗口内的每一帧快照（不走 latest-wins 队列，因此不会被后续快照挤掉）。客户端按 `tickId` 幂等应用：帧号 ≤ 已应用帧的直接丢弃。重放与周期快照可能在传输中交错；客户端必须按 `tickId` 排序，而不是按到达顺序。
2. **窗口未命中**（起始帧已被逐出，或会话已关闭帧历史）→ 反馈单条最新快照，`forced=true` 且 `reason="resync-gap"`；客户端对齐最新帧，放弃回填历史。

限流（两层）：

- WS 面固定窗口：每连接每分钟 `Faces:StreamerWs:ResyncPerMinute`（默认 30）；超限请求丢弃并计入 `/faces` 的 `streamerWs.resyncDropped`
- 引擎按客户端冷却：`Session:ResyncRequestCooldownMs`（默认 1000ms）；冷却内的重复请求计入 `TotalResyncThrottled` 后直接返回

服务端不额外发"resync 已受理"回执：客户端以收到的快照序列作为响应。重试规则：

- 发送 `resync` 后，约定预算内**完全没有**收到快照 → 重试
- 收到 `forced=true` 快照（`reason="resync-gap"` 或 `reason="lag"`）→ 已对齐，**不要重试**
- 收到非强制快照且仍在跳帧 → 可再发一次 `resync`；重放帧因帧号 ≤ 已应用帧会被幂等丢弃，重发是安全的

## 客户端实现约束建议

- 客户端正常跟随时应周期性发送 ACK
- 轻微落后时可接受有控制的追赶
- 严重落后时应等待或请求最新快照
- 客户端逻辑层应始终以 `tickId` 与权威快照为准

## 与当前服务端实现的对应关系

在当前服务端实现中：

- `welcome` 对应 `ServerWelcome`
- `snapshot` 对应 `ServerSnapshot`
- `ack` 对应 `ClientAck`
- `resync` 对应 `ClientResync`（帧同步 P2）

服务端强制重同步携带的关键上下文字段有：

- `forced`
- `reason`
- `targetClientId`
- `clientLagTicks`
- `clientLastAckTickId`

客户端若想产出可解读的恢复日志，建议一并记录这些字段。
