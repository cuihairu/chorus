---
title: 配置参考
---

# 配置参考

配置来源：appsettings.json 的 `Session` 段映射到 `Chorus.Engine.Session.SessionConfig`（未配置时使用下述默认值）。

当前默认值：

```text
tickDurationMs = 66
inputDelayTicks = 2
snapshotEveryTicks = 1
maxEventsPerTick = 200
maxLagTicks = 50
resyncCooldownMs = 2000
maxPendingEvents = 8000
eventEnqueueMaxWaitMs = 50
tickTimeoutMs = 250
maxSessions = 200
idleEvictAfterMs = 600000
maxSnapshotBytes = 1048576
```

关键参数：

- `tickDurationMs`
- `inputDelayTicks`
- `snapshotEveryTicks`
- `maxEventsPerTick`
- `maxLagTicks`
- `resyncCooldownMs`
- `maxPendingEvents` / `eventEnqueueMaxWaitMs`（阶段二过载保护）
- `tickTimeoutMs`（阶段二超时检测）
- `maxSessions` / `idleEvictAfterMs`（阶段二会话生命周期）
- `maxSnapshotBytes`（阶段三快照体积上限）

## 参数说明

### `tickDurationMs`

含义：

- 服务器权威逻辑 tick 周期，单位毫秒

默认值：

- `66`（约 15fps 逻辑推进；2026-10-12 与 frame-sync-best-practices 建议统一，最终值以压测为准）

影响：

- 值越小，同步越细，但服务端和客户端压力越大
- 值越大，系统更稳，但输入生效和恢复粒度更粗

建议：

- 直播互动场景通常建议落在 `50~66`，当前默认值 `66` 已与之统一，最终值以压测为准
- 结合 `tickTimeoutMs`（默认 250）观察 `lastTickProcessMs` 是否超限

### `inputDelayTicks`

含义：

- 输入被调度到未来多少 tick 后统一结算

默认值：

- `2`

影响：

- 值越大，越能吸收抖动和乱序
- 值越小，体感更快，但更容易受网络波动影响

### `snapshotEveryTicks`

含义：

- 周期快照发送频率

默认值：

- `1`

影响：

- `1` 表示每 tick 都会发送快照
- 值越大，带宽和序列化成本越低，但恢复会变慢

建议：

- 当前默认值适合开发期快速验证
- 线上通常需要结合快照体积和带宽成本调整到 `2~5`

### `maxEventsPerTick`

含义：

- 单 tick 最大进入权威逻辑的事件数

默认值：

- `200`

影响：

- 是防止“单帧爆炸”的安全阀
- 超限后会触发 reducer 的裁剪、合并或降采样效果

### `maxLagTicks`

含义：

- 客户端落后多少 tick 后触发强制快照恢复

默认值：

- `50`

影响：

- 值越大，系统对短期抖动更宽容
- 值越大，也意味着慢端会在错误状态停留更久

建议：

- 当前默认值偏保守，适合避免开发环境频繁 resync
- 线上通常要明显收紧，否则恢复不够积极

### `resyncCooldownMs`

含义：

- 同一客户端两次强制快照之间的最小间隔

默认值：

- `2000`

影响：

- 防止短时间内反复 resync
- 值太小会导致抖动式恢复
- 值太大又会让问题客户端长时间无法重新对齐

### `maxSnapshotBytes`

含义：

- 单条快照序列化后的体积上限，单位字节

默认值：

- `1048576`（1 MiB）

影响：

- 序列化后超限的快照**跳过发送**，并计入 `chorus_snapshots_oversize_total`
- 跳过后不会断开客户端；客户端侧的 ACK 水位与重连 resync 机制负责兜底
- 反复超限说明状态有界失控（如 `Dictionary<userId,...>` 只增不减），应先修状态体积而不是调大该值

建议：

- 结合 `snapshotEveryTicks` 与在线客户端数估算带宽：单客户端约 `snapshotBytes × 客户端数 / snapshotEveryTicks / tickDurationMs` 的持续下行
## Ingress 与 listener face（2026-10-12 起）

单体与 Gateway 同构。两类 listener face 配置分道（见 `docs/design/process-topology.md`），
计数器经 `GET /faces` 暴露。

| 键 | 默认 | 说明 |
|---|---|---|
| `Ingress:AllowAll` | `false` | **开发开关**：`true` 时注册未签名验签兜底（无真实验证器的平台回调被放行）。开启时启动日志打 ERROR 告警；仅建议在 appsettings.Development.json 显式打开，生产配置不带 |
| `Ingress:DedupTtlSeconds` | `600` | 事件去重 TTL（platformEventId） |
| `Ingress:FactionRules:CommentKeywords` | 空 | 弹幕口令 → 阵营映射 |
| `Ingress:FactionRules:GiftIds` | 空 | 礼物 ID → 阵营映射 |
| `Ingress:Douyin:AppSecret` | 无 | 抖音回调验签密钥；缺失时 `/ingress/douyin` 全部拒绝（fail-closed） |
| `Ingress:Douyin:SignatureHeader` | `X-Douyin-Signature` | 签名 header 名（待联调确认，见平台参考文档） |
| `Ingress:Kuaishou:VerifyToken` | 无 | 快手「消息验证Token」；缺失时 `/ingress/kuaishou` 全部拒绝（fail-closed） |
| `Ingress:Kuaishou:MsgEncryptKey` | 无 | 快手「消息加密解密KEY」（base64，32 字节）；缺失时事件无法解密（400） |
| `Ingress:Kuaishou:SignatureHeader` | `kwaisign` | 官方签名 header 名 |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | platform-ingress 面请求体上限，超限 413 |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | platform-ingress 面每来源每分钟上限，超限 429 |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | platform-ingress 面单请求预算，超时 504 |
| `Faces:StreamerWs:AuthTokens` | 无 | 主播 WS 接入 token 列表，`;` 分隔，每项 `token` 或 `token@unixMs过期时间`。**未配置且未开 AllowAnonymous → 所有 WS 连接拒绝（fail-closed）** |
| `Faces:StreamerWs:AllowAnonymous` | `false` | 开发开关：允许无 token 连接。开启时启动日志打 WARN；仅建议在 appsettings.Development.json 显式打开 |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | 单条 WS 消息上限，超限以 `MessageTooBig` 关闭连接 |
| `Faces:StreamerWs:AckPerMinute` | `600` | 每连接每分钟 ACK 上限，超限静默丢弃（计数 `/faces` → `streamerWs.ackDropped`） |

## IPC 与就绪（拆分模式）

`Ipc` 段映射到 `Chorus.Messaging.Ipc.IpcSettings`（端口/传输），就绪判定另有两个键：

| 键 | 默认 | 说明 |
|---|---|---|
| `Ipc:HeartbeatIntervalMs`（EngineHost） | `5000` | 引擎在 eng→gw 通道发 `eng.heartbeat` 的间隔；`0` 关闭 |
| `Ipc:ReadinessMaxSilenceMs`（Gateway） | `15000` | Gateway `GET /ready` 判 200 的最大静默窗口，超窗 503（fail-closed） |

详见 [部署与安全](./deployment.md) 的「拆分模式就绪探针」。

## 推荐阅读

- [参数调优](./tuning.md)
- [故障排查](./troubleshooting.md)
- [帧同步最佳实践](../design/frame-sync-best-practices.md)
- [平台对接参考：抖音](./platform-douyin.md)
- [平台对接参考：快手](./platform-kuaishou.md)
- [平台适配器契约](./adapter-contract.md)
- [部署与安全](./deployment.md)
- [版本策略与兼容性](./versioning.md)

