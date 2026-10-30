---
title: API 参考（后端/管理侧）
---


# API 参考

本服务暴露给后端/管理侧的全部端点：HTTP 端点、WS 事件、分离模式 IPC 信封，以及配置面。
所有声明以源码为准；每条目附 `file:line` 引用。

## 认证

- **HTTP 端点（/health、/ready、/sessions、/matches、/leaderboard、/faces、/metrics、/）无认证**，依赖内网假设；
  生产部署应置于反向代理之后并限制来源（见[部署与安全](./deployment.md)中的反向代理一节）。
- 两个带认证的面：`POST /ingress/{platform}`（平台回调签名校验，fail-closed）与 `WS /ws`
  （hello token 认证，fail-closed）。配置见[配置参考](./config.md)。

## 端口（launchSettings 默认）

| 宿主 | HTTP | 来源 |
|---|---|---|
| `Chorus.Server`（单进程） | `5083` | `src/Chorus.Server/Properties/launchSettings.json:8` |
| `Chorus.Gateway` | `5168` | `src/Chorus.Gateway/Properties/launchSettings.json:8` |
| `Chorus.EngineHost` | `5067` | `src/Chorus.EngineHost/Properties/launchSettings.json:8` |

## HTTP 端点

### Chorus.Gateway（`src/Chorus.Gateway/Program.cs`）

| 方法 路径 | 用途 | 响应 | 来源 |
|---|---|---|---|
| `GET /` | 自描述 | `{"name":"Chorus.Gateway","ok":true}` | `Program.cs:123` |
| `GET /health` | 存活探针 | 200 空 | `Program.cs:124` |
| `GET /faces` | 监听面计数器 JSON（入口/ws 两面） | `FaceCounters`，见下 | `Program.cs:125`，字段 `Faces/FaceCounters.cs:67`，`:76` |
| `GET /metrics` | Prometheus 文本（`chorus_*`） | text | `Program.cs:126`，渲染 `Gateway.Core/Metrics/ChorusMetricsText.cs` |
| `GET /ready` | 就绪探针（仅分离 Gateway） | 200 `{"ready":true,"engineSilenceMs":n}`；超窗静默 503 `{"ready":false,"engineSilenceMs":n}` | `Program.cs:133`，决策 `Gateway/GatewayIpcService.cs:32` |
| `POST /ingress/{platform}` | 平台事件入口（platform 角色） | 见"Ingress 错误码" | `Program.cs:148`；`Gateway:Roles` 未选该角色时端点不注册（404），`Program.cs:144` |
| `WS /ws` | 主播客户端（streamer 角色） | 见"WS 协议" | `Program.cs:156`；同上的角色门 `Program.cs:151` |

网关不持有会话状态，故无 `/sessions` 或 `/leaderboard`（后者在 EngineHost / Server 上）。

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

| 方法 路径 | 用途 | 响应 | 来源 |
|---|---|---|---|
| `GET /` | 自描述 | `{"name":"Chorus.EngineHost","ok":true}` | `Program.cs:46` |
| `GET /health` | 存活探针 | 200 空 | `Program.cs:47` |
| `GET /sessions` | 会话/客户端诊断 JSON（每客户端落后/ack 详情；分离模式下的会话权威） | JSON | `Program.cs:52`（`c7846a6` 完成） |
| `GET /leaderboard/{scope}/{scopeId}` | 排行榜只读查询（scope/错误码见[排行榜](../guide/leaderboard.md)） | JSON | `Program.cs:53` |
| `GET /metrics` | Prometheus 文本：会话诊断 + `chorus_ipc_publish_failures_total` + `chorus_snapshot_bytes` + `chorus_process_cpu_percent`（worker CPU，首次采样省略） | text | `Program.cs:54`；CPU 仪表 `EngineHost/ProcessCpuGauge.cs` |

```bash
curl http://localhost:5067/health
curl http://localhost:5067/sessions
curl http://localhost:5067/metrics
```

### Chorus.Server（单进程，`src/Chorus.Server/Program.cs`）

单进程形态多一个诊断端点；`/matches` 仅此形态暴露（权威 `MatchRegistry` 在单进程）。`/sessions` 由分离模式的 EngineHost 暴露（见上）；`/leaderboard` 两种形态都在会话宿主上：

| 方法 路径 | 用途 | 来源 |
|---|---|---|
| `GET /` | 自描述 | `Program.cs:141` |
| `GET /health` | 存活探针 | `Program.cs:142` |
| `GET /sessions` | 会话/客户端诊断 JSON（落后、ack、丢弃计数） | `Program.cs:143` |
| `GET /matches` | 比赛级聚合视图 JSON | `Program.cs:144` |
| `GET /leaderboard/{scope}/{scopeId}` | 排行榜只读查询（见[排行榜](../guide/leaderboard.md)） | `Program.cs:145` |
| `GET /faces` | 同 Gateway | `Program.cs:146` |
| `GET /metrics` | 同 Gateway | `Program.cs:149` |
| `POST /ingress/{platform}` | 同 Gateway | `Program.cs:160` |
| `WS /ws` | 同 Gateway | `Program.cs:165` |

```bash
curl http://localhost:5083/sessions
curl http://localhost:5083/matches
```

### /faces 字段

`GET /faces` 返回两个区块，`IngressFaceSnapshot` + `StreamerWsFaceSnapshot`
（`src/Chorus.Gateway.Core/Faces/FaceCounters.cs:67`，`:76`）：

- ingress：`accepted` / `rateLimited` / `tooLarge` / `timedOut` / `unverified` / `duplicates` / `unmapped`
- streamerWs：`connections` / `unauthorized` / `protocolMismatch` / `messageTooLarge` / `ackAccepted` / `ackDropped` / `resyncDropped` / `snapshotDropped`

## Ingress 请求与错误码

请求体由平台映射器解析为 `AudienceEvent`
（`src/Chorus.Protocol/Models/AudienceEvent.cs`，record 字段）：

| 字段 | 类型 | 必填 | 描述 |
|---|---|---|---|
| `eventId` | string | 是 | 平台事件 id，去重键（TTL 见 `Ingress:DedupTtlSeconds`） |
| `platform` | string | 映射器注入 | 源平台标识 |
| `sessionId` | string | 是 | 会话/房间 id |
| `userId` | string | 否 | 观众 id（映射器默认 `anonymous`） |
| `kind` | enum | 是 | `comment` / `like` / `gift` |
| `ingestTimeMs` | long | 映射器注入 | 摄取时间（unix ms） |
| `text` / `giftId` / `giftCount` / `giftValue` | 可选 | 否 | 按 kind 取值 |
| `matchId` / `factionId` | 可选 | 否 | 阵营分配提示 |

响应与错误码（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs`）：

| 状态 | 条件 | 来源 |
|---|---|---|
| 200 `{ok,sessionId,eventId,tick?}` | 已接受；`tick` 仅单进程形态（引擎 tick 经 welcome/快照走 IPC） | `:141-143` |
| 200 `{ok:true,duplicated:true,...}` | 重复事件（TTL 内同一 `eventId`） | `:101` |
| 400 `{error:"unmapped_event",platform}` | 未知事件类型 | `:93` |
| 401 | 签名校验失败 / 缺校验器 / 缺密钥（fail-closed） | `:76` |
| 413 | 请求体超 `Faces:PlatformIngress:MaxBodyBytes` | `:68` |
| 429 | 超 `Faces:PlatformIngress:RateLimitPerMinute` | `:58` |
| 503 | 引擎侧投递失败（去重已回滚；平台重试是一次全新投递）；或事件通道满 | `:133`，`:152` |
| 504 | 单请求超 `Faces:PlatformIngress:TimeoutMs` | `:148` |

## WS 协议（/ws）

JSON 文本帧。消息定义在 `src/Chorus.Protocol/Messages/`：

### 客户端 → 服务端

| 消息 | 字段 | 来源 |
|---|---|---|
| `hello` | `sessionId?`、`clientId?`、`token?`、`clientVersion?`、`protocolVersion?`（默认按旧版客户端放行；不匹配则拒绝） | `ClientHello.cs:3` |
| `ack` | `sessionId`、`clientId`、`lastAppliedTickId`、`clientTimeMs?` | `ClientAck.cs:3` |

### 服务端 → 客户端

| 消息 | 字段 | 来源 |
|---|---|---|
| `welcome` | `sessionId`、`clientId`、`tickDurationMs`、`inputDelayTicks`、`snapshotEveryTicks`、`serverTimeMs`、`protocolVersion` | `ServerWelcome.cs:5` |
| `snapshot` | `sessionId`、`tickId`、`serverTimeMs`、`state`（玩法 JSON）、`forced?`、`reason?`、`targetClientId?`、`clientLagTicks?`、`clientLastAckTickId?`、`v`（schema 版本，缺省按 1） | `ServerSnapshot.cs:5`（`v` 在 `:20`） |
| `error` | `message`、`code?`——`Invalid hello` / `First message must be hello` / 协议版本原因 / `unauthorized: {reason}` / `Engine unavailable` / `Engine welcome timeout` | `ServerError.cs:3`；六个发送点 `StreamerWsFace.cs:97`，`:114`，`:124`，`:134`，`IpcSessionRouter.cs:39`，`:52` |

字段语义、`v` 版本策略与客户端状态机见[协议参考](./protocol.md)与[版本策略](./versioning.md)。

### 连接关闭原因（关闭状态 + reason）

| 关闭 | 触发 | 来源（`StreamerWsFace.cs`） |
|---|---|---|
| `MessageTooBig` "hello too large" | hello 超 `Faces:StreamerWs:MaxMessageBytes` | `:90` |
| `NormalClosure` "invalid hello" | hello 解析失败 | `:96` |
| `NormalClosure` "first message must be hello" | 首帧不是 hello | `:113` |
| `PolicyViolation`（协议版本原因） | `protocolVersion` 不匹配，认证之前 | `:123` |
| `PolicyViolation` `unauthorized: {missing_token\|invalid_token\|token_expired}` | hello 认证失败（fail-closed） | `:133` |
| `MessageTooBig` "message too large" | 后续消息超限 | `:169` |

## IPC 信封（分离模式内部接口，非后端面）

引擎与网关经 `Envelope`（JSON，按 `sessionId` 排序）通信；消息类型常量
`src/Chorus.Messaging/Ipc/IpcMessageTypes.cs:3`：

| 方向 | 类型 | 载荷 | 来源 |
|---|---|---|---|
| gw→eng | `gw.connect` / `gw.disconnect` | `GatewayConnectCmd` / `GatewayDisconnectCmd` | `Gateway/GatewayIpcService.cs:96`，`:112` |
| gw→eng | `gw.ack` | `GatewayAckCmd`（`lastAppliedTickId`、`clientTimeMs`） | `GatewayIpcService.cs:117` |
| gw→eng | `gw.audienceEvent` | `AudienceEvent`（带 `SourceEventId` 关联） | `EngineHost/EngineIpcService.cs:236` |
| eng→gw | `eng.welcome` | `ServerWelcome` | `EngineIpcService.cs:205` |
| eng→gw | `eng.snapshot` | `ServerSnapshot`（含 `v`） | `EngineHost/EngineBusSessionClient.cs:49-59`（直接构造 `ServerSnapshot` 并发布；`WireSnapshot` 仅用于引擎内广播 WS 路径） |
| eng→gw | `eng.heartbeat` | `EngineHeartbeatCmd`（哨兵 sessionId `__heartbeat`） | `EngineIpcService.cs:130` |

通道命名与多网关扇出：`Ipc:GatewayIds` + `Gateway:Id`（见[消息抽象](../design/ipc.md)的通道命名一节）。

## 配置面

全部配置键、默认值与 fail-closed 语义见[配置参考](./config.md)；就绪探针配置见[部署与安全](./deployment.md)的分离模式一节。

## 相关页面

- [协议参考](./protocol.md)（字段与示例、版本语义）
- [可观测性](./observability.md)（`chorus_*` 指标清单、日志关键字段、关联 ID）
- [平台适配器契约](./adapter-contract.md)（`/ingress/{platform}` 八步处理顺序与签名校验模板）
