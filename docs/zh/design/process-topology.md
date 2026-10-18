# 进程拓扑：网关拆分与房间进程（设计）

> 本文保留 2026-10-12 的角色拆分设计；阶段状态已于 2026-10-19 按代码更新。当前运行路径是 Gateway + EngineHost，经可选配置的 ControlHost 共享房间权威；生产多机验收状态见 `todo.md` ROOM-07/09/11/12。

## 1. 提议与结论

提议形态四类角色：

1. **平台网关**：对接抖音 / 快手等直播平台（HTTPS 回调、开放平台长连接）；
2. **主播网关**：对接主播端与直播伴侣客户端（WebSocket、ACK、重同步；英文标识 `streamer-gw`）；
3. **房间管理进程**：房间注册、分配、健康、路由表；
4. **房间进程 × N**：每个房间的权威 tick 与 GameModule。

**结论：角色划分成立，但物理进程按「角色先分、进程后拆」推进，不一次拆成四个。**

## 2. 为什么角色划分成立

- **两个故障域**：平台侧突发洪峰、验签、限流与主播侧的鉴权、ACK、重同步互不相干，分开后一边被打爆不带走另一边；限流/验证/超时各配各的（对应评审 P0-4「Ingress 默认放行」与 P1-10「客户端消息边界」）。
- **调度与执行分离**：房间注册/分配/健康/路由归 manager，权威 tick 与玩法归 worker，跨机扩展不用重构。
- **已有底座**：`Chorus.Messaging` 的 `Envelope` + UDS/NamedPipe/TCP/HTTP 就是进程间通道；现有 Gateway ↔ EngineHost 拆分模式即「主播网关 ↔ worker」的原型。

## 3. 关键事实：瓶颈是事件吞吐，不是连接数

直播互动里**观众不直连本系统**（他们连的是抖音/快手），本系统连接数只有主播端与直播伴侣，很小；真正放大的是**事件量**（弹幕洪峰）。因此：

- **不采用一房一进程**：一个房间 = 一个 `SessionActor`，单 worker 进程承载数十到上百个房间；
- **房间管理进程在单机阶段是逻辑组件**（寄宿），不是必须的物理进程；
- 扩容依据是每秒事件数与 worker CPU，不是房间数。

## 4. 阶段与触发条件

| 阶段 | 形态 | 触发条件 | 状态（2026-10-13 逐条核对） |
|---|---|---|---|
| 一 | 单体内**代码分层**：`platform-ingress` 与 `streamer-ws` 两个 listener 共用一套门面实现（`PlatformIngressFace` / `StreamerWsFace`，验证/限流/超时/去重/门禁单点），同进程部署（单体仍是默认部署形态） | 平台接入批次 | **已完成**：todo §2 平台接入三项、§3 Match/阵营三项全勾；两个 listener 的门禁与投递均已合一（下两节） |
| 二 | **Gateway 角色拆分 + EngineHost**：platform-gw、streamer-gw 可独立部署；多房间由 EngineHost 承载 | Gateway 角色拆分与 IPC 接线 | **已接线**：多 Gateway 以 `Gateway:Id` 区分并由 EngineHost 扇出；此 IPC 角色拓扑不等于房间集群控制面。 |
| 三 | **ControlHost 权威下的多 Worker**：Gateway 按 owner/epoch 路由，EngineHost 报告 lease、恢复并持久提交 checkpoint | 容量证据及 ROOM-07/09/11/12 rollout gates | **控制面路径已接线，生产多机未验收**：房间分配、fencing、控制路由快照、checkpoint 和恢复均由 ControlHost/EngineHost/Gateway 配合完成。多机分区、迁移端到端、容量与同步复制仍是 open gates；具体状态以 `todo.md` 为准。旧 `RoomAffinity`/`RoomRouteTable` 不是该部署路径。 |


**streamer-ws 门面已合一（§3.5 路由抽象的第一片）**：两个 listener 的 `streamer-ws` 门禁此前是 Server/Gateway 两份漂移拷贝，现已抽为 `Chorus.Gateway.Core/Faces/StreamerWsFace` 一份，两宿主只在 `IStreamerSessionRouter` / `IStreamerSessionBinding` 缝上分叉：

- Monolith → `MonolithSessionRouter`：同进程 `SessionActor`，welcome 由 `SessionConfig` 生成；
- Gateway → `IpcSessionRouter`：经 Engine IPC 注册客户端，welcome 由引擎回传（2s 预算，超时 fail-closed）。

hello 鉴权（fail-closed）、协议版本门禁、消息体积上限、ACK 速率限制、断连清理全部单点实现，两处 `/ws` 端点各只剩一行门面调用。回归守卫：`StreamerWsAuthEndpointTests` / `ProtocolVersionEndpointTests`（Server）与 `StreamerWsGatewayParityTests`（Gateway 侧同口径 close status、错误载荷与 `streamerWs` 计数器，e0501bc）。

**platform-ingress 门面与 Tell 缝已合一（§3.5 ActorRef/Tell 语义与进程内路由表）**：两个 listener 的 `/ingress` 端点（限流/体积上限/超时/验签/映射/去重/投递）抽为 `Chorus.Gateway.Core/Faces/PlatformIngressFace` 一份，投递语义落在共享的 `ISessionActorRef.TellAsync`（三态 `SessionTellOutcome`：Accepted / Rejected = 有界邮箱满 / Unavailable = 传输断）与 `ISessionActorRouter.Resolve(sessionId)` 路由表上：

- Monolith → `InProcSessionActorRouter`：`SessionRegistry.GetOrCreate` 即路由表（容量上限 + idlest 驱逐 + 空闲清扫都在共享生命周期里），Rejected 即邮箱满；
- Gateway → `IpcSessionActorRouter`：把事件按 sessionId 为序键发布到 Engine IPC，真正的表在引擎侧，transport 故障映射为 Unavailable。

两种未送达走同一重试口径：回滚 dedup 认领 + 503，平台重试即全新投递。响应体按拓扑保真：tick 字段只有 Monolith ref 暴露（引擎 tick 只随 welcome/snapshot 走）；`MatchRegistry` 随门面迁入 `Chorus.Gateway.Core.Faces`，拆分模式传 null（房间统计归引擎）。回归守卫：`PlatformIngressFaceTests`（门面 8 用例：Accepted 的 tick 字段 / Rejected 与 Unavailable 回滚 503 / 去重短路先行于 Tell / 未映射 400 / 未验签 401）、`SessionActorRefTests`（进程内 ref 直测：入箱 / 同会话同 actor / 邮箱满 Rejected / TickId 暴露）、`GatewayIngressTellTests`（引擎下线时 Gateway 连续 503 可重试）。

**RoomManager 历史提案（未采用）**：旧方案曾考虑在 Worker 侧增加 `RoomManager` 并把 `MatchRegistry` 改成房间目录。当前共享目录与权威由 `Chorus.ControlHost` 的 `RoomControlStore` 提供；Gateway 消费 owner/epoch 路由快照，EngineHost 报告 Worker lease 并恢复房间。`MatchRegistry` 仍是本地诊断聚合，RoomManager 独立进程没有实现，也不是当前部署所需组件。

## 5. 拆分前必须先解决的两个前置问题

### 5.1 房间事件的单点排序（单写者不变式）

同一房间全部输入必须在唯一 actor 内排序（现单消费者邮箱即此职责）。拆进程靠**房间粘性**保证，不允许两处同时写同一房间。

### 5.2 恢复路径（恢复半边）

房间恢复通过 ControlHost checkpoint 接线：EngineHost 读取已提交 tick 的 snapshot 并调用 `IGameModule.RestoreState`，owner/epoch fence 防止旧 Worker 写入；SQLite 与 PostgreSQL provider 均有集成覆盖。独立机器故障接管和迁移期间的端到端收敛尚未验收；详细存储决策见 [snapshot-restore.md](./snapshot-restore.md) 与 [房间存储架构](../../architecture/room-control-storage.md)。

### 5.3 跨进程路由形态（路由表）

当前接线：ControlHost 维护 `房间 → WorkerId + WorkerInstance + Epoch` 权威记录；Gateway 周期刷新路由快照，并对事件目标使用 owner/epoch 对应的 IPC 通道；EngineHost 报告 lease、checkpoint 与连接状态。快照失效时路由 fail-closed。它提供代码路径，不代表跨主机分区或生产容量已验收。

## 6. 明确不做

- 不上分布式 Match 集群；
- 不引入 Kafka / RabbitMQ 等外部消息平台；
- 不把平台私有适配逻辑放进核心；
- 单体保持最简单、最易调试的默认部署形态。

> 「当前最需要的是边界清楚、故障可控、结果正确，而不是更多组件。」——外部评审 §10，本设计同此口径。
