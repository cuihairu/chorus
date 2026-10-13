---
title: 协议参考
---

# 协议参考（v1）

## hello（client -> server）

```json
{"type":"hello","sessionId":"optional","clientId":"optional","token":"optional","protocolVersion":1}
```

字段说明：

- `sessionId`：客户端希望加入的会话，可选
- `clientId`：客户端标识，可选；未提供时可由服务端分配
- `token`：接入凭据，可选；服务端校验（见下）
- `protocolVersion`：线协议版本，可选；当前为 `1`（见下）

### hello 协议版本（2026-10-12 起）

`hello.protocolVersion` / `welcome.protocolVersion` 标识**线协议**版本（由服务端 `Chorus.Protocol`
的 `ProtocolVersionPolicy.Current` 决定），与 `clientVersion`（客户端自身构建号，自由字符串）无关：

- 缺省（`null`/不传）→ 视为旧版客户端，**放行**（向后兼容，不做硬升级）；
- 传了且等于服务端版本 → 放行；
- 传了且不等于服务端版本 → 服务端先回 `error`（`protocol_mismatch: client=N server=M`），
  随后以 `PolicyViolation` 关闭连接，并计入 `/faces` 的 `streamerWs.protocolMismatch`
  与 `chorus_ws_protocol_mismatch_total`；
- 版本校验在鉴权**之前**：版本不对的客户端不消耗鉴权判定。

### hello 鉴权（2026-10-12 起，fail-closed）

服务端在收到 hello 后校验 `token`（配置 `Faces:StreamerWs:AuthTokens`，支持 `token@unixMs过期时间`，
分号分隔多个）：

- token 缺失/未知/已过期 → 服务端先回 `error`（`unauthorized: missing_token | invalid_token | token_expired`），
  随后以 `PolicyViolation` 关闭连接；
- 未配置任何 token 且未开 `Faces:StreamerWs:AllowAnonymous` → 所有连接一律拒绝（fail-closed）；
- `AllowAnonymous` 仅建议在开发配置（appsettings.Development.json）里显式打开。

hello 为单条 WS 文本消息，超过 `Faces:StreamerWs:MaxMessageBytes`（默认 64KiB）→ 以
`MessageTooBig` 关闭连接。ACK 超过 `Faces:StreamerWs:AckPerMinute`（默认 600/连接/分钟）→
静默丢弃并计入 `/faces` 的 `streamerWs.ackDropped`。

## welcome（server -> client）

```json
{
  "type":"welcome",
  "sessionId":"session-1",
  "clientId":"client-1",
  "tickDurationMs":66,
  "inputDelayTicks":3,
  "snapshotEveryTicks":2,
  "serverTimeMs":1710000000000,
  "protocolVersion":1
}
```

字段说明：

- `tickDurationMs`：权威 tick 周期
- `inputDelayTicks`：输入被调度到未来多少 tick 后结算
- `snapshotEveryTicks`：周期快照频率
- `serverTimeMs`：服务端当前时间戳，可用于客户端做漂移估计
- `protocolVersion`：服务端线协议版本；客户端应比对其与自身实现版本，不一致时主动断开而不是猜字段

客户端在收到 `welcome` 后，应以其作为权威节拍配置，而不是继续沿用本地默认值。

## snapshot（server -> client）

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
  "v":1
}
```

字段说明：

- `tickId`：该快照对应的权威 tick
- `serverTimeMs`：快照生成时的服务端时间
- `state`：权威状态快照，v1 为 JSON
- `forced`：是否为强制重同步快照
- `reason`：强制原因，当前常见值为 `lag`
- `targetClientId`：目标客户端；当服务端只想让特定慢端立即对齐时使用
- `clientLagTicks`：服务端判定该客户端当前落后多少 tick
- `clientLastAckTickId`：服务端看到的该客户端最近 ACK tick
- `v`：快照 schema 版本（2026-10-13 起上线，见 [版本策略](./versioning.md)）；引擎快照透传，当前恒为 `1`；旧发送端缺省时接收端按 `1` 读入。未知版本的处理语义（拒绝 + 强制 resync）待首个 v2 schema 设计时定

语义说明：

- 普通周期快照用于稳定跟随和重连恢复
- 当 `forced=true && reason="lag"` 时，表示服务器判定客户端落后，需要立刻以该快照对齐
- 客户端收到强制快照后，不应继续长时间补历史帧，而应优先切换到该快照状态

## ack（client -> server）

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

- `lastAppliedTickId`：客户端已经真正应用完成的权威 tick
- `clientTimeMs`：可选，用于辅助漂移和排查

语义约束：

- ACK 只能在状态真正应用完成后发送
- ACK 不能表示“已经收到消息”
- ACK 不能表示“已入队，稍后应用”

如果客户端在快照还未应用完成前就发送 ACK，服务端会误判其已经跟上。

## 推荐实现约束

- 客户端正常跟随时，应周期性发送 ACK
- 轻度 lag 时，可做受控追赶
- 重度 lag 时，应等待或请求最新快照
- 客户端逻辑层应始终以 `tickId` 和权威快照为准

## 与当前服务端实现的对应关系

当前服务端实现中：

- `welcome` 对应 `ServerWelcome`
- `snapshot` 对应 `ServerSnapshot`
- `ack` 对应 `ClientAck`

其中服务端强制重同步所携带的关键上下文字段包括：

- `forced`
- `reason`
- `targetClientId`
- `clientLagTicks`
- `clientLastAckTickId`

客户端如果要做可解释的恢复日志，建议把这些字段一起记录下来。
