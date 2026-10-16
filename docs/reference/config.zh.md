---
title: 配置参考
---


<p align="center">
  <a href="./config.md">English</a> | 简体中文
</p>

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
frameHistoryBytes = 8388608
resyncRequestCooldownMs = 1000
maxResyncReplayTicks = 128
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

- 同一客户端两次**服务端推送**强制快照之间的最小间隔（引擎按 ACK 水位判定客户端落后后主动推送）

默认值：

- `2000`

影响：

- 防止短时间内反复 resync
- 值太小会导致抖动式恢复
- 值太大又会让问题客户端长时间无法重新对齐
- **与 `resyncRequestCooldownMs`（客户端主动拉）互不复用**，见下

### `frameHistoryBytes`

2026-10-13 起（帧同步 P2）。

含义：

- per-session 帧历史 ring buffer 的字节上限，保存最近的 wire 快照供客户端 `resync` 按帧号补帧

默认值：

- `8388608`（8 MiB/session）

影响：

- 按字节封顶、驱逐最旧帧；刚加入的帧不会被驱逐（单帧超整容量时保留为唯一帧）
- 补帧窗口起点早于最旧保留帧 → 视为未命中（回吐最新快照 + `forced=true`/`reason="resync-gap"`），不做部分回放
- 内存上界 = 该值 × `maxSessions`（200）≈ 最坏 1.6 GiB，生产压测后按实测快照字节调整
- `0` 关闭帧历史：所有 `resync` 请求走 `resync-gap` 对齐路径

### `resyncRequestCooldownMs`

2026-10-13 起（帧同步 P2）。

含义：

- 同一客户端两次**主动拉取**（`resync` 消息）之间的最小间隔，引擎层 per-client 冷却

默认值：

- `1000`

影响：

- 冷却内重复请求计入 `TotalResyncThrottled` 后直接返回
- 另有传输层固定窗口 `Faces:StreamerWs:ResyncPerMinute`，两层独立生效

### `maxResyncReplayTicks`

2026-10-15 起（churn 修复 ③，重放限界）。

含义：

- 单次客户端 `resync` 请求最多重放的帧数；快照携带全量状态，窗口超过该值时直接回吐一条
  `forced=true`（`reason="resync-gap"`）最新快照对齐，不做逐帧重放

默认值：

- `128`（约 8.4 秒的 66ms 帧）

影响：

- 每次重放的 eng→gw 消息量封顶（2026-10-15 门控压测：重放洪流 × 快照分发曾把接收循环压死，见 `todo.md:156`）
- 超限计入 `chorus_resync_replay_capped_total`（与窗口未命中 `chorus_resync_window_misses_total` 分开计数）
- `0` 完全关闭重放：所有 `resync` 请求都走 `resync-gap` 单帧对齐

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
| `Ingress:Bilibili:AccessKeyId` / `AccessKeySecret` | 无 | B 站 open-live 签名凭据（主播本人授权，不进仓不进日志）；缺失时 `/ingress/bilibili` 全部拒绝（fail-closed） |
| `Ingress:Bilibili:AppId` | 无 | open-live 项目 app_id；桥启动前置 |
| `Ingress:Bilibili:RoomOwnerAuthCode` | 无 | 主播身份鉴别码（app_start 用）；桥启动前置 |
| `Ingress:Bilibili:SessionId` | 无 | 房间 → chorus 会话绑定（P1 单绑定，多房间属控制面 todo §14）；缺失时事件映射失败（400） |
| `Ingress:Bilibili:IngressUrl` | 无 | 桥转发目标（如 `http://127.0.0.1:5083/ingress/bilibili`）；四件套+此键齐全才启动桥（`BilibiliIngressBridgeHostedService`） |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | platform-ingress 面请求体上限，超限 413 |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | platform-ingress 面每来源每分钟上限，超限 429 |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | platform-ingress 面单请求预算，超时 504 |
| `Faces:StreamerWs:AuthTokens` | 无 | 主播 WS 接入 token 列表，`;` 分隔，每项 `token` 或 `token@unixMs过期时间`。**未配置且未开 AllowAnonymous → 所有 WS 连接拒绝（fail-closed）** |
| `Faces:StreamerWs:AllowAnonymous` | `false` | 开发开关：允许无 token 连接。开启时启动日志打 WARN；仅建议在 appsettings.Development.json 显式打开 |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | 单条 WS 消息上限，超限以 `MessageTooBig` 关闭连接 |
| `Faces:StreamerWs:AckPerMinute` | `600` | 每连接每分钟 ACK 上限，超限静默丢弃（计数 `/faces` → `streamerWs.ackDropped`） |
| `Faces:StreamerWs:ResyncPerMinute` | `30` | 每连接每分钟 `resync` 补帧请求上限，超限丢弃（计数 `/faces` → `streamerWs.resyncDropped`；2026-10-13 帧同步 P2） |

## Gateway 角色打包（拆分模式，三进程形态）

| 键 | 默认 | 说明 |
|---|---|---|
| `Gateway:Roles`（Gateway） | `all` | 该 Gateway 进程承载的 listener 角色：`all`（一个进程两个 listener，默认）、`platform`（只映射 `/ingress`，即 platform-gw）、`streamer`（只映射 `/ws`，即 streamer-gw）。未选中角色的端点不注册（404），两个 listener 的门面实现共用一份（`PlatformIngressFace` / `StreamerWsFace`），无代码分叉；`/health`、`/faces`、`/metrics`、`/ready` 不受角色影响。非法值启动即抛（fail-fast）。单体形态不受此键影响。见 [进程拓扑](../design/process-topology.zh.md) §4 |
| `Gateway:Id`（Gateway） | 空 | 本网关标识（仅 ASCII 字母/数字/`-`）：后缀本进程的 eng→gw 通道名，使多个网关可共享同一 EngineHost 与同一 socket 目录。空 = 旧单网关通道名（向后兼容）。非法字符启动即抛。见 [拆分模式](../guide/split-mode.zh.md) 多网关一节 |

## IPC 与就绪（拆分模式）

`Ipc` 段映射到 `Chorus.Messaging.Ipc.IpcSettings`（端口/传输），心跳/就绪/扇出另有三个键：

| 键 | 默认 | 说明 |
|---|---|---|
| `Ipc:HeartbeatIntervalMs`（EngineHost） | `5000` | 引擎在 eng→gw 通道发 `eng.heartbeat` 的间隔；`0` 关闭 |
| `Ipc:ReadinessMaxSilenceMs`（Gateway） | `15000` | Gateway `GET /ready` 判 200 的最大静默窗口，超窗 503（fail-closed） |
| `Ipc:GatewayIds`（EngineHost） | 空 | 逗号分隔的网关标识列表（如 `platform-gw,streamer-gw`）；引擎对每个 id 各建一条 eng→gw 客户端并逐条扇出发布。空 = 旧单网关通道 |

详见 [部署与安全](./deployment.zh.md) 的「拆分模式就绪探针」。

## 推荐阅读

- [参数调优](./tuning.zh.md)
- [故障排查](./troubleshooting.zh.md)
- [帧同步最佳实践](../design/frame-sync-best-practices.zh.md)
- [平台对接参考：抖音](./platform-douyin.zh.md)
- [平台对接参考：快手](./platform-kuaishou.zh.md)
- [平台适配器契约](./adapter-contract.zh.md)
- [部署与安全](./deployment.zh.md)
- [版本策略与兼容性](./versioning.zh.md)

