---
title: 配置参考
---


# 配置参考

配置来源：appsettings.json 的 `Session` 节映射到 `Chorus.Engine.Session.SessionConfig`（未配置时使用下列默认值）。

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
- `maxSnapshotBytes`（阶段三快照大小限制）

## 参数说明

### `tickDurationMs`

含义：

- 服务端权威逻辑 tick 周期，单位毫秒

默认值：

- `66`（约 15fps 的逻辑推进；2026-10-12 与 frame-sync-best-practices 建议统一，最终值以压测为准）

影响：

- 越小同步越细，但对服务端和客户端压力越大
- 越大系统越稳，但输入生效与恢复的粒度变粗

建议：

- 直播互动场景通常建议落在 `50~66`；当前默认 `66` 已与之统一，最终值以压测为准
- 结合 `tickTimeoutMs`（默认 250）一起看 `lastTickProcessMs` 是否超限

### `inputDelayTicks`

含义：

- 输入在统一结算前被调度到未来多少个 tick

默认值：

- `2`

影响：

- 越大越能吸收抖动与乱序
- 越小感知越快，但更易受网络波动影响

### `snapshotEveryTicks`

含义：

- 周期快照的发送频率

默认值：

- `1`

影响：

- `1` 表示每 tick 发一次快照
- 越大带宽与序列化开销越低，但恢复变慢

建议：

- 当前默认适合开发期快速验证
- 生产通常按快照大小与带宽成本调到 `2~5`

### `maxEventsPerTick`

含义：

- 每 tick 进入权威逻辑的事件数上限

默认值：

- `200`

影响：

- 作为"单帧爆炸"的安全阀
- 超限会触发 reducer 的裁剪、合并或降采样效果

### `maxLagTicks`

含义：

- 客户端落后多少个 tick 后触发强制快照恢复

默认值：

- `50`

影响：

- 越大系统越容忍短期抖动
- 越大也意味着慢客户端在错误状态停留更久

建议：

- 当前默认偏保守，适合开发环境避免频繁重同步
- 生产通常需要明显收紧，否则恢复不够激进

### `resyncCooldownMs`

含义：

- 同一客户端两次**服务端推送**强制快照的最小间隔（引擎基于 ACK 水位判定客户端落后而主动推送）

默认值：

- `2000`

影响：

- 防止短时间内反复重同步
- 太小会形成恢复振荡
- 太大会让问题客户端长时间无法重新对齐
- 与 `resyncRequestCooldownMs`（客户端主动拉取）**不共用**，见下

### `frameHistoryBytes`

自 2026-10-13（帧同步 P2）。

含义：

- 每会话帧历史环形缓冲的字节上限，保存近期线快照供客户端 `resync` 按帧号回填

默认值：

- `8388608`（8 MiB/会话）

影响：

- 按字节封顶、逐出最旧帧；刚加入的帧不被逐出（单帧超过整个容量时，该帧作为唯一帧保留）
- 回填窗口起点早于最旧保留帧 → 按未命中处理（返回最新快照 + `forced=true`/`reason="resync-gap"`），不做部分重放
- 内存上界 = 该值 × `maxSessions`（200）≈ 最坏 1.6 GiB；生产压测后按实测快照字节调整
- `0` 关闭帧历史：所有 `resync` 请求走 `resync-gap` 对齐路径

### `resyncRequestCooldownMs`

自 2026-10-13（帧同步 P2）。

含义：

- 同一客户端两次**主动拉取**（`resync` 消息）的最小间隔，引擎层按客户端冷却

默认值：

- `1000`

影响：

- 冷却内的重复请求计入 `TotalResyncThrottled` 后直接返回
- 此外还有传输层固定窗口 `Faces:StreamerWs:ResyncPerMinute`；两层各自独立生效

### `maxResyncReplayTicks`

自 2026-10-15（抖动修复 ③，重放上界）。

含义：

- 单个客户端 `resync` 请求最多重放的帧数；快照携带完整状态，窗口超过该值时直接返回单条
  `forced=true`（`reason="resync-gap"`）最新快照对齐，不逐帧重放

默认值：

- `128`（约 8.4 秒的 66ms 帧）

影响：

- 封顶单次重放的 eng→gw 消息量（2026-10-15 门禁压测：重放洪峰 × 快照分发曾击穿过接收循环，见 `todo.md:156`）
- 超限计入 `chorus_resync_replay_capped_total`（与窗口未命中的 `chorus_resync_window_misses_total` 分开计数）
- `0` 完全关闭重放：所有 `resync` 请求走 `resync-gap` 单帧对齐

### `maxSnapshotBytes`

含义：

- 单条序列化快照的大小上限，单位字节

默认值：

- `1048576`（1 MiB）

影响：

- 序列化后超限的快照**跳过发送**并计入 `chorus_snapshots_oversize_total`
- 跳过不会断开客户端；由客户端侧 ACK 水位与重连重同步机制兜底
- 反复超限说明状态有界性失控（如 `Dictionary<userId,...>` 只增不减）；先修状态大小，而不是调大该值

建议：

- 用 `snapshotEveryTicks` 与在线客户端数估算带宽：每客户端约 `snapshotBytes × clientCount / snapshotEveryTicks / tickDurationMs` 的持续下行

## 入口与监听面（自 2026-10-12）

单机与 Gateway 同构。两类监听面配置走不同路径（见 `docs/design/process-topology.md`），
计数器经 `GET /faces` 暴露。

| 键 | 默认值 | 描述 |
|---|---|---|
| `Ingress:AllowAll` | `false` | **开发开关**：`true` 时注册无签名的验签 fallback（没有真实验证器的平台回调也放行）。开启时启动日志打印 ERROR 警告；只建议在 appsettings.Development.json 里显式开启，生产配置不包含它 |
| `Ingress:DedupTtlSeconds` | `600` | 事件去重 TTL（platformEventId） |
| `Ingress:FactionRules:CommentKeywords` | 空 | 弹幕关键词 → 阵营映射 |
| `Ingress:FactionRules:GiftIds` | 空 | 礼物 ID → 阵营映射 |
| `Ingress:Douyin:AppSecret` | 无 | 抖音回调验签密钥；缺失时 `/ingress/douyin` 拒绝一切（fail-closed） |
| `Ingress:Douyin:SignatureHeader` | `X-Douyin-Signature` | 签名头名（待联调确认，见平台参考文档） |
| `Ingress:Kuaishou:VerifyToken` | 无 | 快手"消息验证Token"；缺失时 `/ingress/kuaishou` 拒绝一切（fail-closed） |
| `Ingress:Kuaishou:MsgEncryptKey` | 无 | 快手"消息加密解密KEY"（base64，32 字节）；缺失时事件无法解密（400） |
| `Ingress:Kuaishou:SignatureHeader` | `kwaisign` | 官方签名头名 |
| `Ingress:Bilibili:AccessKeyId` / `AccessKeySecret` | 无 | B 站开放平台签名凭证（主播个人授权，永不进仓不进日志）；缺失时 `/ingress/bilibili` 拒绝一切（fail-closed） |
| `Ingress:Bilibili:AppId` | 无 | 开放平台项目 app_id；桥启动前提 |
| `Ingress:Bilibili:RoomOwnerAuthCode` | 无 | 主播身份鉴权码（app_start 使用）；桥启动前提 |
| `Ingress:Bilibili:SessionId` | 无 | 房间 → chorus 会话绑定（P1 单绑定；多房间归属控制面 todo §14）；缺失时事件映射失败（400） |
| `Ingress:Bilibili:IngressUrl` | 无 | 桥转发目标（如 `http://127.0.0.1:5083/ingress/bilibili`）；四件套加该键齐备时桥才启动（`BilibiliIngressBridgeHostedService`） |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | platform-ingress 面请求体上限；超限 413 |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | platform-ingress 面按来源每分钟限额；超限 429 |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | platform-ingress 面单请求预算；超时 504 |
| `Faces:StreamerWs:AuthTokens` | 无 | 主播 WS 接入 token 列表，`;` 分隔，每项为 `token` 或 `token@unixMs过期时间`。**未配置且未开启 AllowAnonymous → 所有 WS 连接拒绝（fail-closed）** |
| `Faces:StreamerWs:AllowAnonymous` | `false` | 开发开关：允许无 token 连接。开启时启动日志打印 WARN；只建议在 appsettings.Development.json 里显式开启 |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | 单条 WS 消息上限；超限以 `MessageTooBig` 关闭连接 |
| `Faces:StreamerWs:AckPerMinute` | `600` | 每连接每分钟 ACK 限额；超限静默丢弃（经 `/faces` → `streamerWs.ackDropped` 计数） |
| `Faces:StreamerWs:ResyncPerMinute` | `30` | 每连接每分钟 `resync` 回填请求限额；超限丢弃（经 `/faces` → `streamerWs.resyncDropped` 计数；2026-10-13 帧同步 P2） |

## Gateway 角色打包（拆分模式，三进程形态）

| 键 | 默认值 | 描述 |
|---|---|---|
| `Gateway:Roles`（Gateway） | `all` | 该 Gateway 进程承载的监听角色：`all`（单进程双监听，默认）、`platform`（只挂 `/ingress`，即 platform-gw）、`streamer`（只挂 `/ws`，即 streamer-gw）。未选角色的端点不注册（404）；两个监听的面实现共用一份（`PlatformIngressFace` / `StreamerWsFace`），无代码分叉；`/health`、`/faces`、`/metrics`、`/ready` 不受角色影响。非法值启动即抛（fail-fast）。单机形态不受该键影响。见[进程拓扑](../design/process-topology.md) §4 |
| `Gateway:Id`（Gateway） | 空 | 本网关标识（仅 ASCII 字母/数字/`-`）：作为本进程 eng→gw 通道名的后缀，从而多个网关可共享同一 EngineHost 与同一 socket 目录。空 = 旧单网关通道名（向后兼容）。非法字符启动即抛。见[拆分模式](../guide/split-mode.md)的多网关章节 |

## IPC 与就绪（拆分模式）

`Ipc` 节映射到 `Chorus.Messaging.Ipc.IpcSettings`（端口/传输）；心跳/就绪/扇出另有三个键：

| 键 | 默认值 | 描述 |
|---|---|---|
| `Ipc:HeartbeatIntervalMs`（EngineHost） | `5000` | 引擎在 eng→gw 通道上发 `eng.heartbeat` 的间隔；`0` 关闭 |
| `Ipc:ReadinessMaxSilenceMs`（Gateway） | `15000` | Gateway `GET /ready` 判 200 的最大静默窗；超窗 503（fail-closed） |
| `Ipc:GatewayIds`（EngineHost） | 空 | 网关标识逗号分隔列表（如 `platform-gw,streamer-gw`）；引擎按 id 各建一条 eng→gw 客户端并按 id 扇出发布。空 = 旧单网关通道 |
| `Ipc:EngToGwShards`（EngineHost + Gateway） | `1` | 每网关的 eng→gw socket 数。引擎把每个会话的帧发布到其会话 id 哈希到的分片（稳定 FNV-1a，因此一个会话恒在一个 socket 上、按会话有序）；网关每分片宿一个服务端 socket，汇合回单一分发循环。`1` 保持旧通道与端点名逐字节一致；`>1` 与 `Ipc:GatewayIds` 组合（分片 × 网关 socket 数）且要求 socket 传输——TCP/HTTP 在端点构造时 fail fast |
| `Ipc:WorkerId`（EngineHost） | 空 | 本 worker 标识（仅 ASCII 字母/数字/`-`）：作为其 gateway→engine 通道名的后缀以支持多 worker。空 = 旧单 worker 通道名。非法字符在端点构造时失败 |
| `Ipc:WorkerIds`（Gateway） | 空 | 无 ControlHost 旧模式的静态 worker 列表；按 `RoomAffinity` 原型路由。ControlHost 模式改为从 owner/epoch 路由快照选 worker |

详见[部署与安全](./deployment.md)的"拆分模式就绪探针"章节。

## 延伸阅读

- [参数调优](./tuning.md)
- [故障排查](./troubleshooting.md)
- [帧同步最佳实践](../design/frame-sync-best-practices.md)
- [平台接入参考：抖音](./platform-douyin.md)
- [平台接入参考：快手](./platform-kuaishou.md)
- [平台适配器契约](./adapter-contract.md)
- [部署与安全](./deployment.md)
- [版本策略与兼容性](./versioning.md)
