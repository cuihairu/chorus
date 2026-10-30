# 进程拓扑：Gateway 拆分与房间进程（设计）

> 状态：设计于 2026-10-12 定稿（用户提案 + 评审交叉核验）。这是一个**目标形态与节奏**；实现随 todo.md 各期推进；物理拆分要到触发条件满足后才落地。


> 当前部署：使用 **Chorus.PlatformGateway**（`/ingress/{platform}`）与
> **Chorus.StreamerGateway**（`/ws`）作为独立可执行。两者都用
> `Chorus.Gateway.Runtime` 库；互不依赖对方可执行，也不用 `Gateway:Roles`。
> 见[独立网关部署](../guide/independent-gateways.md)。
> 下面合并的 `Chorus.Gateway` 与角色切换示例仅为兼容保留。

## 1. 提案与结论

提案形态有四个角色：

1. **平台网关**：对接抖音/快手等直播平台（HTTPS 回调、开放平台长连接）；
2. **主播网关**：对接主播客户端与直播伴侣客户端（WebSocket、ACK、重同步；英文标识 `streamer-gw`）；
3. **房间管理进程**：房间注册、分配、健康、路由表；
4. **房间进程 × N**：每房间的权威 tick 与 GameModule。

**结论：角色划分成立，但物理进程按"先角色后进程"推进，而不是一步拆成四个。**

## 2. 为什么角色划分成立

- **两个故障域**：平台侧突发洪峰、签名校验、限流与主播侧认证、ACK、重同步互不相关；拆分后一侧被压垮不会拖垮另一侧；限流/校验/超时各自独立配置（对应评审 P0-4"Ingress 默认放行"与 P1-10"客户端消息边界"）。
- **调度与执行分离**：房间注册/分配/健康/路由归管理器，权威 tick 与玩法归 worker；跨机扩展不需要重构。
- **既有基础**：`Chorus.Messaging` 的 `Envelope` + UDS/NamedPipe/TCP/HTTP 是进程间通道；既有 Gateway ↔ EngineHost 分离模式正是"主播网关 ↔ worker"的原型。

## 3. 关键事实：瓶颈是事件吞吐，不是连接数

在直播互动中，**观众不直接连接本系统**（他们连抖音/快手）；本系统的连接数只有主播客户端与直播伴侣，很小；真正被放大的是**事件量**（弹幕洪峰）。因此：

- **不采用一房一进程**：一房一个 `SessionActor`，单 worker 进程承载几十到几百个房间；
- **单机阶段房间管理进程是逻辑组件**（宿主内），不是必要物理进程；
- 扩展依据是每秒事件数与 worker CPU，不是房间数。

## 4. 阶段与触发条件

| 阶段 | 形态 | 触发条件 | 状态（2026-10-13 逐项核对） |
|---|---|---|---|
| 一 | **单体内的代码分层**：`platform-ingress` 与 `streamer-ws` 两个监听共享一套 face 实现（`PlatformIngressFace` / `StreamerWsFace`，校验/限流/超时/去重/闸门的单点），部署在同一进程（单体仍是默认部署形态） | 平台接入批次 | **已完成**：todo §2 平台接入三项、§3 比赛/阵营三项全部勾选；两监听的闸门与投递已统一（下两节） |
| 二 | **四角色三进程**：platform-gw、streamer-gw、room-worker（承载多房间）；房间所有权由 ControlHost 协调 | Gateway 角色拆分与控制面房间目录工作 | **已接线；生产验证仍 open**：一个 Gateway 进程可承载 `platform` 或 `streamer` 角色，独立 platform-gw / streamer-gw / EngineHost 进程可互通。ControlHost 现在拥有房间分配、worker 租约、epoch、持久检查点与路由快照；见 §3 与 §7 的当前多 worker 路径。下方更早的 RoomManager/MatchRegistry 提案保留为历史设计语境，不是部署设计。 |
| 三 | 独立 Worker 进程，部署时再加机器；当前 ControlHost 设计不需要独立 RoomManager 进程 | 容量/负载证据，或运营上的横向扩展需求；生产激活还需 ROOM-07/09/11/12 门槛 | **控制面多 worker 路由已接线；生产横向扩展仍未验证。** ControlHost 按活跃 Worker 容量分配房间；Gateway 消费新 owner/epoch 路由快照；EngineHost 恢复并持久提交被围栏的检查点。原 `RoomAffinity`/`RoomRouteTable` 原型保留为库路径，而部署控制模式用 `RoomControlStore` 与 `ControlRoutingSnapshot`。PostgreSQL 16+ 主库/同步备库存储与初始 100 房间 SLO 在 `docs/architecture/room-control-storage.md` 中选定；provider 容量、复制与独立机器故障测试仍是 todo.md §19 ROOM-07/09/11/12 的发布门槛。 |

**streamer-ws 面已统一（§3.5 路由抽象的第一块）**：两个监听此前是 Server/Gateway 两份漂移的 `streamer-ws` 闸门，现提取为 `Chorus.Gateway.Core/Faces/StreamerWsFace` 单份，仅 `IStreamerSessionRouter` / `IStreamerSessionBinding` 缝在两个宿主间分叉：

- Monolith → `MonolithSessionRouter`：进程内 `SessionActor`，welcome 由 `SessionConfig` 生成；
- Gateway → `IpcSessionRouter`：经 Engine IPC 注册客户端，welcome 由引擎返回（2s 预算，超时 fail-closed）。

hello 认证（fail-closed）、协议版本门、消息大小上限、ACK 限流、断线清理都在单点实现，两个 `/ws` 端点各只留一行 face 调用。回归守卫：`StreamerWsAuthEndpointTests` / `ProtocolVersionEndpointTests`（Server）与 `StreamerWsGatewayParityTests`（Gateway 侧同口径关闭状态、错误载荷与 `streamerWs` 计数，e0501bc）。

**platform-ingress 面与 Tell 缝已统一（§3.5 ActorRef/Tell 语义与进程内路由表）**：两个监听的 `/ingress` 端点（限流/大小上限/超时/签名校验/映射/去重/投递）提取为 `Chorus.Gateway.Core/Faces/PlatformIngressFace` 单份，投递语义落到共享的 `ISessionActorRef.TellAsync`（三态 `SessionTellOutcome`：Accepted / Rejected = 有界信箱满 / Unavailable = 传输断开）与 `ISessionActorRouter.Resolve(sessionId)` 路由表：

- Monolith → `InProcSessionActorRouter`：`SessionRegistry.GetOrCreate` 是路由表（容量上限 + 空闲清扫；达容量时拒绝新建 Actor 而非驱逐既有房间），Rejected 指信箱满；
- Gateway → `IpcSessionActorRouter`：按 sessionId 顺序的会话键把事件发布到 Engine IPC；真实表在引擎侧，传输失败映射为 Unavailable。

两种未投递情形走同一重试口径：回滚去重声明 + 503；平台重试是一次全新投递。响应体忠于拓扑：tick 字段仅由 Monolith 引用暴露（引擎 tick 只随 welcome/快照走）；`MatchRegistry` 随 face 移入 `Chorus.Gateway.Core.Faces`，分离模式传 null（房间统计属于引擎）。回归守卫：`PlatformIngressFaceTests`（face 8 例：Accepted tick 字段 / Rejected 与 Unavailable 回滚 503 / 去重在 Tell 前短路 / 未映射 400 / 未签名 401）、`SessionActorRefTests`（进程内引用直测：信箱 / 同会话同 actor / 信箱满 Rejected / TickId 暴露）、`GatewayIngressTellTests`（引擎宕机时 Gateway 返回连续可重试 503）。

**历史 RoomManager 提案（非部署设计）**：更早提案在 Worker 上放一个 `RoomManager`，把 `MatchRegistry` 升级为房间目录。当前实现改用 ControlHost 作为共享房间目录与权威，配 `RoomControlStore` 与 `ControlRoutingSnapshot`；`MatchRegistry` 仍是本地诊断聚合。未引入独立 RoomManager 进程。

## 5. 拆分前必须解决的两个前置

### 5.1 房间事件的单点排序（单写者不变量）

同一房间的所有输入必须在唯一 actor 内排序（当前单消费者信箱正是这个职责）。进程拆分依赖**房间亲和**保证这一点；不允许两处同时写同一房间。

### 5.2 恢复路径（恢复半侧）

房间进程重启需要一个权威快照回退。`IGameModule.RestoreState(version, JsonElement)` 与 `GameModuleRestorer` 提供模块恢复；EngineHost 经 `ControlCheckpointRestorer` 读 ControlHost 检查点，tick 提交被当前 owner 实例与 epoch 围栏。PostgreSQL 16+ 被选为生产多 worker 拓扑的存储且其 provider 已接入；SQLite 仍是开发/单主机默认。容量、同步复制与独立进程/网络故障验收在 todo.md §19 ROOM-07/09/11/12 保持 open。下方更早的内存 `ISnapshotBackend` 讨论描述的是前 ControlHost 设计。

### 5.3 跨进程路由形态（路由表）

部署模式用 ControlHost 维护 `room → WorkerId + WorkerInstance + Epoch`；Gateway 与 EngineHost 刷新本地路由快照，过期或与 owner epoch 不符时 fail-closed。主播网关与平台网关消费同一权威。旧 RoomManager 推送的表仍是概念备选，不在当前路径中。

## 6. 明确不做

- 无分布式 Match 集群；
- 无 Kafka / RabbitMQ 或其他外部消息平台；
- 平台私有适配器逻辑不进核心；
- 单体仍是最简单、最易调试的默认部署形态。

> "当前最需要的是清晰的边界、可控的故障与正确的结果，而不是更多组件。"——外部评审 §10；本设计遵循同一口径。
