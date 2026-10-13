---
title: 接口参考（后台/管理端）
---

# 接口参考

本服务暴露给后台/管理端的全部接口：HTTP 端点、WS 事件、拆分模式 IPC 信封、配置面。
所有主张以源码为准，出处见各条 file:line。

## 鉴权

- **HTTP 端点（/health、/ready、/sessions、/matches、/faces、/metrics、/）无鉴权**，属内网假设；
  生产部署应置于反向代理后并限制来源（见 [部署与安全](./deployment.md) 反向代理一节）。
- 有鉴权的是两个接入面：`POST /ingress/{platform}`（平台回调验签，fail-closed）与 `WS /ws`
  （hello token 鉴权，fail-closed）。配置见 [配置参考](./config.md)。

## 端口（launchSettings 默认）

| 宿主 | HTTP | 出处 |
|---|---|---|
| `Chorus.Server`（单进程） | `5083` | `src/Chorus.Server/Properties/launchSettings.json:8` |
| `Chorus.Gateway` | `5168` | `src/Chorus.Gateway/Properties/launchSettings.json:8` |
| `Chorus.EngineHost` | `5067` | `src/Chorus.EngineHost/Properties/launchSettings.json:8` |

## HTTP 端点

### Chorus.Gateway（`src/Chorus.Gateway/Program.cs`）

| 方法 路径 | 用途 | 响应 | 出处 |
|---|---|---|---|
| `GET /` | 自述 | `{"name":"Chorus.Gateway","ok":true}` | `Program.cs:100` |
| `GET /health` | 存活探针 | 200 空 | `Program.cs:101` |
| `GET /faces` | listener face 计数器 JSON（ingress/ws 两面） | `FaceCounters`，见下 | `Program.cs:102`，字段 `Faces/FaceCounters.cs:64`、`:73` |
| `GET /metrics` | Prometheus 文本（`chorus_*`） | text | `Program.cs:103`，渲染 `Gateway.Core/Metrics/ChorusMetricsText.cs` |
| `GET /ready` | 就绪探针（仅拆分 Gateway） | 200 `{"ready":true,"engineSilenceMs":n}`；静默超窗 503 `{"ready":false,"engineSilenceMs":n}` | `Program.cs:110`，判定 `Gateway/GatewayIpcService.cs:32` |
| `POST /ingress/{platform}` | 平台事件入口（platform 角色） | 见「Ingress 错误码」 | `Program.cs:125`；`Gateway:Roles` 未选中该角色时端点不注册（404），`Program.cs:121` |
| `WS /ws` | 主播客户端（streamer 角色） | 见「WS 协议」 | `Program.cs:133`；同上角色门禁 `Program.cs:128` |

curl 示例（Gateway 在 5168）：

```bash
curl http://localhost:5168/health
curl http://localhost:5168/ready
curl http://localhost:5168/faces
curl http://localhost:5168/metrics
curl -X POST http://localhost:5168/ingress/test -H 'content-type: application/json' \
  -d '{"sessionId":"s1","eventId":"e1","userId":"u1","kind":"comment","text":"hi"}'
```

### Chorus.EngineHost（`src/Chorus.EngineHost/Program.cs`）

| 方法 路径 | 用途 | 响应 | 出处 |
|---|---|---|---|
| `GET /` | 自述 | `{"name":"Chorus.EngineHost","ok":true}` | `Program.cs:34` |
| `GET /health` | 存活探针 | 200 空 | `Program.cs:35` |
| `GET /metrics` | Prometheus 文本：会话诊断 + `chorus_process_cpu_percent`（worker CPU，首抓省略） | text | `Program.cs:36`；CPU 刻度 `EngineHost/ProcessCpuGauge.cs` |

```bash
curl http://localhost:5067/health
curl http://localhost:5067/metrics
```

### Chorus.Server（单进程，`src/Chorus.Server/Program.cs`）

单进程形态多两个诊断端点；`/sessions`、`/matches` 只在此形态暴露（权威数据在引擎侧，拆分模式不转发）：

| 方法 路径 | 用途 | 出处 |
|---|---|---|
| `GET /` | 自述 | `Program.cs:102` |
| `GET /health` | 存活探针 | `Program.cs:103` |
| `GET /sessions` | 会话/客户端诊断 JSON（lag、ack、丢弃计数） | `Program.cs:104` |
| `GET /matches` | match 级聚合视图 JSON | `Program.cs:105` |
| `GET /faces` | 同 Gateway | `Program.cs:106` |
| `GET /metrics` | 同 Gateway | `Program.cs:109` |
| `POST /ingress/{platform}` | 同 Gateway | `Program.cs:115` |
| `WS /ws` | 同 Gateway | `Program.cs:120` |

```bash
curl http://localhost:5083/sessions
curl http://localhost:5083/matches
```

### /faces 字段

`GET /faces` 返回 `IngressFaceSnapshot` + `StreamerWsFaceSnapshot` 两段
（`src/Chorus.Gateway.Core/Faces/FaceCounters.cs:64`、`:73`）：

- ingress：`accepted` / `rateLimited` / `tooLarge` / `timedOut` / `unverified` / `duplicates` / `unmapped`
- streamerWs：`connections` / `unauthorized` / `protocolMismatch` / `messageTooLarge` / `ackAccepted` / `ackDropped`

## Ingress 请求与错误码

请求体由平台映射器解析为 `AudienceEvent`
（`src/Chorus.Protocol/Models/AudienceEvent.cs`，record 字段）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `eventId` | string | 是 | 平台事件 id，去重键（TTL 见 `Ingress:DedupTtlSeconds`） |
| `platform` | string | 映射器注入 | 来源平台标识 |
| `sessionId` | string | 是 | 会话/房间 id |
| `userId` | string | 是 | 观众 id |
| `kind` | enum | 是 | `comment` / `like` / `gift` |
| `ingestTimeMs` | long | 映射器注入 | 接入时间（unix ms） |
| `text` / `giftId` / `giftCount` / `giftValue` | 可选 | 否 | 按 kind 取用 |
| `matchId` / `factionId` | 可选 | 否 | 阵营归属提示 |

响应与错误码（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs`）：

| 状态 | 条件 | 出处 |
|---|---|---|
| 200 `{ok,sessionId,eventId,tick?}` | 受理；`tick` 仅单进程形态（引擎 tick 经 IPC 走 welcome/snapshot） | `:128`、`:129` |
| 200 `{ok:true,duplicated:true,...}` | 重复事件（TTL 内同 `eventId`） | `:101` |
| 400 `{error:"unmapped_event",platform}` | 未知事件类型 | `:93` |
| 401 | 验签失败/缺验证器/缺密钥（fail-closed） | `:76` |
| 413 | 请求体超 `Faces:PlatformIngress:MaxBodyBytes` | `:68` |
| 429 | 超 `Faces:PlatformIngress:RateLimitPerMinute` | `:58` |
| 503 | 引擎侧投递失败（回滚去重，平台重试即全新投递）；或事件道满 | `:119`、`:138` |
| 504 | 单请求超 `Faces:PlatformIngress:TimeoutMs` | `:134` |

## WS 协议（/ws）

JSON 文本帧。消息定义在 `src/Chorus.Protocol/Messages/`：

### 客户端 → 服务器

| 消息 | 字段 | 出处 |
|---|---|---|
| `hello` | `sessionId?`、`clientId?`、`token?`、`clientVersion?`、`protocolVersion?`（缺省=旧客户端放行，不匹配拒） | `ClientHello.cs:3` |
| `ack` | `sessionId`、`clientId`、`lastAppliedTickId`、`clientTimeMs?` | `ClientAck.cs:3` |

### 服务器 → 客户端

| 消息 | 字段 | 出处 |
|---|---|---|
| `welcome` | `sessionId`、`clientId`、`tickDurationMs`、`inputDelayTicks`、`snapshotEveryTicks`、`serverTimeMs`、`protocolVersion` | `ServerWelcome.cs:5` |
| `snapshot` | `sessionId`、`tickId`、`serverTimeMs`、`state`（玩法 JSON）、`forced?`、`reason?`、`targetClientId?`、`clientLagTicks?`、`clientLastAckTickId?`、`v`（schema 版本，缺省读 1） | `ServerSnapshot.cs:5`（`v` 在 `:20`） |
| `error` | `message`、`code?`——`Invalid hello` / `First message must be hello` / 协议版本原因 / `unauthorized: {reason}` / `Engine unavailable` / `Engine welcome timeout` | `ServerError.cs:3`；六处发出点 `StreamerWsFace.cs:97`、`:114`、`:124`、`:134`、`IpcSessionRouter.cs:39`、`:52` |

字段语义、`v` 版本策略与客户端状态机见 [协议参考](./protocol.md) 与 [版本策略](./versioning.md)。

### 连接关闭原因（close status + reason）

| 关闭 | 触发 | 出处（`StreamerWsFace.cs`） |
|---|---|---|
| `MessageTooBig` "hello too large" | hello 超 `Faces:StreamerWs:MaxMessageBytes` | `:90` |
| `NormalClosure` "invalid hello" | hello 解析失败 | `:96` |
| `NormalClosure` "first message must be hello" | 首帧不是 hello | `:113` |
| `PolicyViolation`（协议版本原因） | `protocolVersion` 不匹配，先于鉴权 | `:123` |
| `PolicyViolation` `unauthorized: {missing_token\|invalid_token\|token_expired}` | hello 鉴权失败（fail-closed） | `:133` |
| `MessageTooBig` "message too large" | 后续消息超限 | `:169` |

## IPC 信封（拆分模式内部接口，非后台面）

引擎与网关之间经 `Envelope`（JSON，`sessionId` 为序键）通信，消息类型常量
`src/Chorus.Messaging/Ipc/IpcMessageTypes.cs:3`：

| 方向 | 类型 | 载荷 | 出处 |
|---|---|---|---|
| gw→eng | `gw.connect` / `gw.disconnect` | `GatewayConnectCmd` / `GatewayDisconnectCmd` | `Gateway/GatewayIpcService.cs:96`、`:112` |
| gw→eng | `gw.ack` | `GatewayAckCmd`（`lastAppliedTickId`、`clientTimeMs`） | `GatewayIpcService.cs:117` |
| gw→eng | `gw.audienceEvent` | `AudienceEvent`（带 `SourceEventId` 关联） | `EngineHost/EngineIpcService.cs:236` |
| eng→gw | `eng.welcome` | `ServerWelcome` | `EngineIpcService.cs:205` |
| eng→gw | `eng.snapshot` | `ServerSnapshot`（含 `v`） | `EngineIpcService.cs`（WireSnapshot 编码 `Engine/Session/WireSnapshot.cs`） |
| eng→gw | `eng.heartbeat` | `EngineHeartbeatCmd`（哨兵 sessionId `__heartbeat`） | `EngineIpcService.cs:130` |

通道命名与多网关扇出：`Ipc:GatewayIds` + `Gateway:Id`（见 [通信抽象](../design/ipc.md) 通道命名一节）。

## 配置面

全部配置键、默认值与 fail-closed 语义见 [配置参考](./config.md)；就绪探针配置见
[部署与安全](./deployment.md) 拆分模式一节。

## 相关页面

- [协议参考](./protocol.md)（字段与示例、版本语义）
- [可观测性](./observability.md)（`chorus_*` 指标清单、日志关键字段、关联 ID）
- [平台适配器契约](./adapter-contract.md)（`/ingress/{platform}` 8 步处理顺序与验签模板）
