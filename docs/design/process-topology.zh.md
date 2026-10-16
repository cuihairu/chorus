<p align="center">
  <a href="./process-topology.md">English</a> | 简体中文
</p>

# 进程拓扑：网关拆分与房间进程（设计）

> 状态：2026-10-12 设计定稿（用户提议 + 评审对照）。属**目标形态与节奏**，实现随 todo.md 阶段推进；未达触发条件前不落地物理拆分。

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
| 二 | **四角色、三进程**：platform-gw、streamer-gw、room-worker（承载多房间）；`RoomManager` 作为逻辑组件寄宿 worker 侧，由 `MatchRegistry` 升级为房间注册 + 路由表 | todo.md 阶段一正确性五项完成 | **已触发（5/5：9ad322d、499f01c、def21d2、78f2597、0409cf2），进行中**：进程可拆已交付（`Gateway:Roles`，15a6dad）——一个 Gateway 进程可只承载 `platform` 或 `streamer` 角色，platform-gw / streamer-gw / room-worker（EngineHost）三进程成为配置选项，门面零复制；三进程接线已通（5c5d25c，2026-10-13：eng→gw 通道按 `Gateway:Id` 后缀区分、引擎按 `Ipc:GatewayIds` 扇出，守卫 `ThreeProcessEndToEndTests`：platform-gw 注入的事件出现在 streamer-gw 的 WS 客户端）；房间侧尚未升级（见下「RoomManager 何时上」） |
| 三 | room-worker 独立多进程/多机，room-manager 才单独成进程（房间粘性 + 快照恢复） | 单 worker 高峰 CPU > 60%，或单机房间数达数百量级 | **未满足，差 2 条**：①单 worker 高峰 CPU 无数据（`Chorus.LoadTool` 冒烟是功能验证，量级数百事件，不是峰值测量）；②单机房间数无数百量级部署数据。两条都是运行期指标，仓内无法自证，留待生产/长期压测峰值数据（仪器已备：`chorus_process_cpu_percent` worker 进程 CPU 刻度 + LoadTool 运行期采样报峰值，见 todo §4 峰值测量项）。**不依赖数据的代码半边已先行交付（be2eff7，2026-10-13）**：`RoomAffinity`（FNV-1a 稳定散列 + 排序 worker 集合的纯函数映射——同一房间在同一 worker 集合下恒得同一 worker，§5.1 单写者不变式）+ `RoomRouteTable`（整体换图的共享路由表，room-manager 推送、双网关同读，§5.3），守卫 `RoomAffinityTests` 7 例；**暂无生产接线**，消费者（room-manager 发布路由、网关预路由）等阶段三触发再接。恢复半边（§5.2）不被这两条数据门控，其 `IGameModule` 恢复契约已拍板并落地（`062cc68`，2026-10-14），生产接线仍等阶段三触发 |


**streamer-ws 门面已合一（§3.5 路由抽象的第一片）**：两个 listener 的 `streamer-ws` 门禁此前是 Server/Gateway 两份漂移拷贝，现已抽为 `Chorus.Gateway.Core/Faces/StreamerWsFace` 一份，两宿主只在 `IStreamerSessionRouter` / `IStreamerSessionBinding` 缝上分叉：

- Monolith → `MonolithSessionRouter`：同进程 `SessionActor`，welcome 由 `SessionConfig` 生成；
- Gateway → `IpcSessionRouter`：经 Engine IPC 注册客户端，welcome 由引擎回传（2s 预算，超时 fail-closed）。

hello 鉴权（fail-closed）、协议版本门禁、消息体积上限、ACK 速率限制、断连清理全部单点实现，两处 `/ws` 端点各只剩一行门面调用。回归守卫：`StreamerWsAuthEndpointTests` / `ProtocolVersionEndpointTests`（Server）与 `StreamerWsGatewayParityTests`（Gateway 侧同口径 close status、错误载荷与 `streamerWs` 计数器，e0501bc）。

**platform-ingress 门面与 Tell 缝已合一（§3.5 ActorRef/Tell 语义与进程内路由表）**：两个 listener 的 `/ingress` 端点（限流/体积上限/超时/验签/映射/去重/投递）抽为 `Chorus.Gateway.Core/Faces/PlatformIngressFace` 一份，投递语义落在共享的 `ISessionActorRef.TellAsync`（三态 `SessionTellOutcome`：Accepted / Rejected = 有界邮箱满 / Unavailable = 传输断）与 `ISessionActorRouter.Resolve(sessionId)` 路由表上：

- Monolith → `InProcSessionActorRouter`：`SessionRegistry.GetOrCreate` 即路由表（容量上限 + idlest 驱逐 + 空闲清扫都在共享生命周期里），Rejected 即邮箱满；
- Gateway → `IpcSessionActorRouter`：把事件按 sessionId 为序键发布到 Engine IPC，真正的表在引擎侧，transport 故障映射为 Unavailable。

两种未送达走同一重试口径：回滚 dedup 认领 + 503，平台重试即全新投递。响应体按拓扑保真：tick 字段只有 Monolith ref 暴露（引擎 tick 只随 welcome/snapshot 走）；`MatchRegistry` 随门面迁入 `Chorus.Gateway.Core.Faces`，拆分模式传 null（房间统计归引擎）。回归守卫：`PlatformIngressFaceTests`（门面 8 用例：Accepted 的 tick 字段 / Rejected 与 Unavailable 回滚 503 / 去重短路先行于 Tell / 未映射 400 / 未验签 401）、`SessionActorRefTests`（进程内 ref 直测：入箱 / 同会话同 actor / 邮箱满 Rejected / TickId 暴露）、`GatewayIngressTellTests`（引擎下线时 Gateway 连续 503 可重试）。

**RoomManager 何时上（阶段二的房间侧，当前刻意不做）**：设计原定的「`RoomManager` 寄宿 worker 侧 + `MatchRegistry` 升级为房间注册 + 路由表」今天有两个空实现风险——单 worker 下「房间 → worker」映射恒为 1:1，而路由表已由 worker 侧共享的 `SessionRegistry` 承担（容量上限 / idlest 驱逐 / 空闲清扫，`SessionRegistryTests`）；`MatchRegistry` 也仍是展示级聚合（spec.md §2.3 定性）。因此阶段二先只做「进程可拆」这一半（`Gateway:Roles`），房间注册 / 健康 / 路由下推等阶段三（多 worker）触发时再建，避免投机组件。

## 5. 拆分前必须先解决的两个前置问题

### 5.1 房间事件的单点排序（单写者不变式）

同一房间全部输入必须在唯一 actor 内排序（现单消费者邮箱即此职责）。拆进程靠**房间粘性**保证，不允许两处同时写同一房间。

### 5.2 恢复路径（恢复半边）

房间进程重启需有权威快照可回。契约缺口已闭合：`IGameModule` 现有必选回写入口 `RestoreState(version, JsonElement)`（2026-10-14 拍板方案 A 并落地，`062cc68`）——恢复输入取线协议 JSON（`version` 透传线协议 `ServerSnapshot.v`），单点 `GameModuleRestorer` 在 actor 工厂内执行（`Create` 后、`SessionActor` 前），恢复完成前无任何 tick/ingress 事件到达模块，恢复失败 fail-closed 拒绝会话启动（`SessionRegistry.StartFailures` 可观测，不留半状态 actor）；两处出生点共用同一执行器 → parity by construction。当前无生产接线（恢复源默认 null = 全新启动，行为不变）；**尚未闭合的半边**是「权威快照从哪来」——持久化介质选型与写盘时机（契约层已备 `ISnapshotBackend`/`SnapshotStore` 与 `ISnapshotRecoverySource` 两个缝），随阶段三触发再接线。契约选项、权衡与实现详见 [snapshot-restore.md](./snapshot-restore.zh.md)。

### 5.3 跨进程路由形态（路由表）

沿用现有模式推广：manager 维护 `房间 → worker` 映射并推送给网关；网关据此选择到 worker 的通道（或走 worker 的反向通道），主播网关与平台网关共用同一张路由表。

## 6. 明确不做

- 不上分布式 Match 集群；
- 不引入 Kafka / RabbitMQ 等外部消息平台；
- 不把平台私有适配逻辑放进核心；
- 单体保持最简单、最易调试的默认部署形态。

> 「当前最需要的是边界清楚、故障可控、结果正确，而不是更多组件。」——外部评审 §10，本设计同此口径。
