# TODO（按实现顺序）

## 0. 仓库基础

- [x] 建立 solution：`Cohort.sln`（当前目标框架为 `net9.0`；如需 LTS 可改为 `net8.0`）
- [x] 建立包结构（建议）：
  - `src/Cohort.Server`（HTTP + WebSocket + DI）
  - `src/Cohort.Engine`（Session actor、tick、buffer、backpressure）
  - `src/Cohort.Protocol`（消息与序列化）
  - `src/Cohort.Adapters.Abstractions`（平台适配器接口）
  - `src/Cohort.SampleGame`（最小示例 Game Module，用于验证帧同步链路）
  - `tests/...`（至少覆盖 tick/缓冲/降采样）
- [x] 加入 `docs/` 的目录结构与索引（已完成初稿）

## 1. Tick/帧同步（第一阶段：打通闭环，v1=权威状态快照）

- [x] 实现 `SessionActor`（单线程邮箱模型 + 固定 tick）
- [x] 实现 `AudienceEvent` 统一模型 + 稳定排序规则
- [x] 实现 `inputDelayTicks` 缓冲（按 tick 结算事件）
- [x] 实现 `maxEventsPerTick` + 合并/降采样策略（简单合并：同用户同类型累加）
- [x] 实现 WebSocket 协议：`hello/welcome/ack/snapshot`
- [x] 实现 per-client ack 跟踪与落后检测（`maxLagTicks`）与指标输出（`/sessions`）
- [x] 实现快照机制（全量 snapshot；序列化用 JSON）

## 2. 平台接入（开源只做接口与最小 stub）

- [x] `IPlatformEventMapper` / `IPlatformEventVerifier` 接口
- [x] `Platform Ingress`：HTTP endpoint 接收 event（示例 payload + 验签 stub）
- [x] 去重机制（基于 platformEventId + TTL）

## 3. Match/阵营对抗抽象

- [x] `Match` / `Faction` / `Session` 的关系建模
- [x] 多客户端订阅同一 `Match` 的状态（两个主播客户端看到同一局）
- [x] “观众加入阵营”的通用规则接口（弹幕口令/礼物映射）

## 3.5 通信与部署模式

- [x] 抽象 `MessageBus/ActorRef/Router`（支持 Monolith 与拆分）（分片交付：InProc/NamedPipe/UDS/TCP 四 bus；共享会话生命周期 `SessionRegistry`（a6eae7f）；streamer-ws 门面 `Faces/StreamerWsFace` + `IStreamerSessionRouter`/`IStreamerSessionBinding` 缝（e0501bc/3a10f9d），两处 `/ws` 各剩一行调用，Gateway parity 守卫 `StreamerWsGatewayParityTests`；platform-ingress 门面 `Faces/PlatformIngressFace` + `ISessionActorRef.TellAsync`（Accepted/Rejected/Unavailable 三态）/`ISessionActorRouter.Resolve` 路由表缝（0eac8ac）——Monolith `InProcSessionActorRouter` 复用共享 `SessionRegistry`（表=GetOrCreate），Gateway `IpcSessionActorRouter` 按 sessionId 发布 Engine IPC，未送达一律回滚 dedup 认领 + 503；守卫 `PlatformIngressFaceTests`/`SessionActorRefTests`/`GatewayIngressTellTests`）
- [x] 提供 InProc bus（进程内）
- [x] 提供 NamedPipe bus（本机 IPC 示例）
- [x] 提供 Unix Domain Socket bus（本机 IPC 示例）
- [x] 提供 TCP transport（localhost / 容器）
- [x] 提供 HTTP transport（远程 / 网关层）
- [x] 评估并规划共享内存 transport（MemoryMappedFile + FlatBuffers）（docs/design/shared-memory-transport.md：现状成本、方案轮廓、更便宜替代（eng→gw 按 tick 去重发布）、风险表、触发条件与 A/B/C 阶段；结论暂不实现）

## 3.6 拆分模式（Gateway + EngineHost）

- [x] 新增 `Cohort.Gateway` 与 `Cohort.EngineHost` 两个进程
- [x] IPC 双向通道（Gateway->Engine 命令，Engine->Gateway 快照）
- [x] 把会话参数从 EngineHost 下发到 Gateway（避免两边配置漂移）

## 4. 可观测性与压测

- [x] 指标：tick 耗时、事件数、丢弃/合并数、ack 延迟、落后 tick（b86f5a8 `/metrics` Prometheus 文本 + `SessionDiagnostics` 导出；后续 a5f23b8/be9f264 补 protocol_mismatch 与 engine_silence）
- [x] 日志结构化（最少：sessionId、tickId、clientId）（拒绝/回路/发送失败路径全带 sessionId 结构化字段；tickId 在快照发送失败与 tick 过载路径；见 docs/reference/observability.md 日志关键字段）
- [x] 提供一个本地压测脚本（模拟 N 个观众事件 + 2 个客户端）（578415b 独立 `Chorus.LoadTool`：IngressDriver 观众事件 + ClientSimulation 双客户端，采样 `/metrics` 输出 tick_overruns/errors/mailbox_depth；`LoadToolTests`）

## 5. 文档定稿

- [x] 把 `docs/spec.md` 定稿为“第一版可实现规格”（根据你确认的取舍：权威状态同步 vs 输入帧广播）（落在 `docs/design/spec.md`，0409cf2 定案权威状态同步 + 事件生命周期，a35417c 补 lifecycle）
- [x] 增加 `docs/protocol.md`（协议字段与示例）（落在 `docs/reference/protocol.md`，8a49c1b；7a1b64f 补版本语义）
- [x] 增加 `docs/ops.md`（部署与参数调优：tick/inputDelay/maxEvents 等）（落在 `docs/reference/operations.md` + `deployment.md` + `config.md` + `tuning.md`，8a49c1b）

## 6. 文档站（VuePress）

- [x] 建立 VuePress 文档站（`docs/.vuepress` + `npm run docs:build`）
- [x] 整理架构/帧同步/旧服分析文档到 `docs/`
- [x] GitHub Pages 自动部署（`docs.yml`）

## 7. CI / 覆盖率 / 发布

- [x] CI：build + test（`ci.yml`）
- [x] 覆盖率上传（Codecov）
- [x] Tag 自动 Release（`release.yml`）


---

## 8. 实现分析与改进建议（2026-10-12 clone 分析，按优先级）

**现状快照（读码核实，非推测）**
- 11 个 csproj、`src+tests` 约 4100 行 C#；只有 1 个测试工程 `tests/Cohort.Engine.Tests`（9 个文件：reducer / faction rule / MatchRegistry / mapper / SampleGame + InProc/NamedPipe/TCP/HTTP/UDS 五种 transport）。
- 拆分模式已跑通：Gateway（`/ws`、`/ingress/{platform}`）+ EngineHost（权威 tick）+ `Cohort.Messaging` 四种 IPC transport，`/health` 三处都有，**`/metrics` 一处没有**。
- `Ingress/EventDeduplicator.cs`、`Ingress/TestPlatformEventMapper.cs`、`WebSocketHelpers.cs` 在 `Cohort.Server` 与 `Cohort.Gateway` 各存一份（diff 除命名空间外一致）。

### P0 — 平台对接与安全（先做）

- [x] **抖音平台对接**：`Cohort.Adapters.Abstractions` 已有 `IPlatformEventMapper` / `IPlatformEventVerifier` / `IAudienceFactionRule`，但仓库里只有 `TestPlatformEventMapper`（拼装测试数据）与 `AllowAllPlatformEventVerifier`（无条件放行）。新增 `DouyinPlatformEventMapper` + `DouyinPlatformEventVerifier`：抖音开放平台直播事件回调接入（弹幕/礼物/点赞→`AudienceEvent`），回调验签与事件类型映射按官方文档实现；`/ingress/douyin` 路由挂上，去重继续走 `EventDeduplicator`（platformEventId+TTL）。（2026-10-12 完成：官方口径未能在线核实，验签/字段为明示的联调占位契约，见 `docs/reference/platform-douyin.md`；验签 fail-closed，配置注入密钥；顺带落地 platform-ingress face 限流/请求体上限/超时）
- [x] **快手平台对接**：同上，`KuaishouPlatformEventMapper` + `KuaishouPlatformEventVerifier`，`/ingress/kuaishou`；两家的鉴权材料（app secret / 回调加密）走配置，不硬编码。（2026-10-12 完成：验签与 AES 信封为**官方口径**（sha1Hex(body+Token) / kwaisign，AES-256-CBC IV=Key[0..16]，见 `docs/reference/platform-kuaishou.md` §1）；直播事件内层 JSON 官方文档未给，按明示占位契约，待联调替换；验签 fail-closed，Token/KEY 配置注入；msgId 作去重 EventId）
- [x] **两个平台的联调用例**：官方样例 payload 进仓做金测（签名正确通过、篡改拒绝、重复 event id 去重、未知事件类型不崩）。（2026-10-12 完成：`DouyinIngressEndpointTests`（占位契约口径）与 `KuaishouIngressEndpointTests`（官方 kwaisign+AES 信封口径，测试内构造密文）；四类金测齐全，TestHost HTTP 端到端）
- [x] **WebSocket 接入鉴权**：`hello` 目前无凭据，任何人可连 `/ws` 并收快照（`docs/design/spec.md:31` 把鉴权列为会话管理项但未实现）。加 token（服务端校验 + 过期），失败直接关连接。（2026-10-12 完成：`Faces:StreamerWs:AuthTokens`（`token@unixMs过期`，`;` 分隔）+ 缺失/未知/过期以 PolicyViolation 关连接；未配置且未开匿名 → fail-closed 全拒；demo 在 appsettings.Development.json 显式开 AllowAnonymous 并注明；测试 `StreamerWsAuthEndpointTests`）
- [x] **Ingress 真验签**：替换 `AllowAllPlatformEventVerifier` 为按平台的 HMAC 校验（stub 保留为显式的 `allowAll=true` 配置，且默认关闭）。（2026-10-12 完成：抖音/快手 named verifier 按 `Ingress:AllowAll`（默认 false）才注册兜底；缺验证器/密钥 → 401（fail-closed），评审 P0-4 验收测试 `IngressAllowAllEndpointTests`）

### P1 — 正确性与容量

- [x] **`SessionActor` 反压**：`SessionActor.cs:19` 是 `Channel.CreateUnbounded`（无界邮箱）——慢消费/事件洪峰时内存无上界，与 §1 勾选的「backpressure」不符。改有界邮箱 + 溢出策略（按 `maxEventsPerTick` 已有的合并/丢弃口径），并把 `SessionDiagnostics` 的 dropped 计数接出去。（5592d62：事件道有界 `Wait` + 礼物单次有限等待，溢出分 overflow/gift 计数入 `SessionDiagnostics`；控制命令道保持无界不丢；`SessionActorBackpressureTests`）
- [x] **会话空闲回收**：`SessionManager` 只有 `DisposeAsync`，没有空闲超时/驱逐；长跑会话与僵尸 session 会一直占 tick。加 idle timeout + 主动驱逐（并同步给 Gateway 注销）。（362b435：`SessionConfig.IdleEvictAfterMs`（600s 默认）+ `SweepIdleAsync` 定期清扫 + 容量满时 idlest-first 驱逐（Server 端）；拆分模式同款在 `EngineIpcService.MaybeEnforceSessionLifecycle`，只驱逐无客户端会话故无需 Gateway 注销；`SessionManagerLifecycleTests`）
- [x] **合并重复代码**：把 `Ingress/*` 与 `WebSocketHelpers.cs` 从 Server/Gateway 抽到共享工程（`Cohort.Gateway.Core` 或并入 `Cohort.Protocol`），两处 `#if`/拷贝改为一次引用；补单测只写一份。（`WebSocketHelpers.cs`、`Faces/IngressFaceGuard.cs`/`IngressRateLimiter.cs`/`IngressFaceOptions.cs` 均只在 `Chorus.Gateway.Core` 一份，Server/Gateway 皆工程引用（e2304b2 起），无 `#if`/拷贝；单测 `IngressFaceGuardTests` 单份）
- [x] **`MessageBus/ActorRef/Router` 抽象**（§3.5）与进程内路由表：拆分模式不再各写一套。（此前 a6eae7f 会话生命周期合一、e0501bc/3a10f9d streamer-ws 门面合一；本轮（0eac8ac）：`Faces/PlatformIngressFace` 单点实现两个 `/ingress` 端点（限流/体积上限/超时/验签/映射/去重后恰好一次 Tell），投递语义 `ISessionActorRef.TellAsync` 三态——Accepted / Rejected=有界邮箱满（仅 Monolith）/ Unavailable=IPC 传输断（仅 Gateway）——`ISessionActorRouter.Resolve(sessionId)` 进程内路由表：Monolith `InProcSessionActorRouter` 直接复用共享 `SessionRegistry.GetOrCreate`（容量上限/idlest 驱逐/空闲清扫内建），Gateway `IpcSessionActorRouter` 发布 Engine IPC、真表在引擎侧；未送达回滚 dedup 认领 + 503 保证平台重试是全新投递，响应 tick 字段仅 Monolith ref 暴露；`MatchRegistry` 随门面迁入 `Chorus.Gateway.Core.Faces`，拆分模式传 null（房间统计归引擎）；守卫 `PlatformIngressFaceTests`（8 用例）/`SessionActorRefTests`（4 用例）/`GatewayIngressTellTests`（引擎下线 503×2））

### P2 — 可观测性（§4 全空）

- [x] `/metrics`（Prometheus 文本格式）：tick 耗时直方图、事件 ingested/applied/merged/dropped、ack 延迟、落后 tick 客户数、快照与重同步数——`SessionDiagnostics` 已经在收集这些字段，只差导出。（b86f5a8：Server/Gateway `/metrics`，29 项含 face 计数器与 ack 延迟/落后 tick 分布；`MetricsEndpointTests`）
- [x] 结构化日志：sessionId / tickId / clientId 三件套进日志（现在是裸 `Console`/`ILogger` 文案）。（7209d91：拒绝/回路/快照发送失败路径全带 sessionId 结构化字段，tickId 在快照发送失败与 tick 过载路径；见 docs/reference/observability.md 日志关键字段；同 §4 该条）
- [x] 本地压测脚本：N 个观众事件 + 2 个主播客户端，输出 tick 抖动与丢弃率（放 `scripts/`）。（578415b 独立 `Chorus.LoadTool` 工程，同 §4 压测条目；tick 抖动经 tick_overruns/errors 采样观测）

### P3 — 性能 / 测试 / 运维

- [x] 快照序列化：现为 JSON 全量快照（§1），每 tick 对多客户端重复序列化。改为按 tick 序列化一次复用 + `ArrayPool` 租借；评估 MessagePack/FlatBuffers（`docs/design/frame-sync.md` 已提）。（96310c5：`WireSnapshot` 按 tick 编码一次，广播路径全客户端共享同一字节；`ISessionClient` 增 `SendSnapshotAsync(WireSnapshot)` DIM，WS 传输直写字节；验收 `WireSnapshotTests` 两用例；MessagePack/FlatBuffers 结论暂不引入见 `docs/reference/tuning.md` 快照序列化成本节。`ArrayPool` 未做——单次编码后字节立即共享只读使用，无高频分配点可池化。）
- [x] 测试缺口：Gateway/EngineHost 的 WS→IPC→Engine 端到端、Ingress HTTP 面、IPC 断连重连与降级（UDS→NamedPipe/TCP 回退）目前**零测试**；这三条是拆分模式的主干路径。（WS→IPC→Engine：92d633b `SplitModeEndToEndTests` 真 UDS 双通道 + 真 WS 客户端 hello/welcome/快照/ingress 事件全链路；Ingress HTTP 面：`IngressFaceEndpointTests`/`DouyinIngressEndpointTests`/`KuaishouIngressEndpointTests`/`IngressAllowAllEndpointTests`；IPC 断连重连与降级：d5d2d64 `IpcResilienceTests`）
- [x] 协议版本化：`hello/welcome` 无版本字段，后续加字段会破坏旧客户端。在 `Cohort.Protocol` 加 `protocolVersion`，welcome 回显，不匹配明确拒绝。（2026-10-12，commit a5f23b8：`ProtocolVersionPolicy.Current`=1 + `ClientHello.ProtocolVersion?` 可选 + `ServerWelcome.ProtocolVersion` 回显（三端构造点同步）；缺省=旧客户端放行，不匹配 `protocol_mismatch` 拒绝且**先于鉴权**；计数 `streamerWs.protocolMismatch` + `chorus_ws_protocol_mismatch_total`（指标 28→29）；验收 `ProtocolVersionEndpointTests` 4 例；顺带修复 3 个既有 flaky 测试（AckFlood 未跳过交织快照、Resync 无锁枚举 List、Oversize 轮询提前退出）——修后全量 166/166 连续 10 轮全绿；docs：protocol.md 新增版本语义、versioning.md 缺口更新为「单值检查非区间协商」、observability.md 补指标行）
- [x] 运维：split 模式下 Gateway 应有对 EngineHost 的 readiness（现在只有各自 `/health` 常返 200）；补 compose 健康检查与优雅停机（先把在跑的 session 快照落盘）。（2026-10-12，commit be9f264：**readiness 半落地**——EngineHost 每 `Ipc:HeartbeatIntervalMs`=5000 在 eng→gw 发 `eng.heartbeat`（哨兵 sessionId `__heartbeat`，0=关闭，发布失败仅 LogDebug 不崩循环）；Gateway 收任何引擎消息刷新静默计时，`GET /ready` 在 `Ipc:ReadinessMaxSilenceMs`=15000 内 200 / 否则 503 fail-closed（从未收到也 503），新指标 `chorus_ipc_engine_silence_ms` gauge（从未收到=-1）；验收 `GatewayReadinessTests` 5 例（首消息前 503 / 新鲜消息 200 / 过窗 503 / 指标导出 / 心跳真发 UDS e2e）；docs：deployment.md 就绪探针节+端点表、config.md IPC 表、observability.md 指标行。**快照落盘不做**——与「明确不做：无持久化」边界冲突，compose 健康检查写法已由 `/ready`+`/health` 覆盖）


## 9. 外部评审采纳（Chorus，2026-10-12）

> 来源与全文：[docs/design/review-2026-10-12-chatgpt.md](./docs/design/review-2026-10-12-chatgpt.md)。按用户指令：先更新文档，再按阶段执行。**每项落地必须同步该文末尾的「落地跟踪」与本表**。

### 阶段一：正确性与安全边界（最高优先）
- [x] 归并跨阵营串数据：`DefaultAudienceEventReducer` 分组键补 `SessionId/MatchId/FactionId`（点赞 `(S,M,F,U)`、礼物 `(S,M,F,U,GiftId)`），混合阵营计数守恒用例（2026-10-12，commit 9ad322d，验收 `Reduce_MixedFactions_ConservesPerFactionCounts`）
- [x] ACK 单调性与 Tick 上界：拒绝负数/未来 Tick/倒退，绑定 Client 身份，分类计数（2026-10-12，commit 499f01c，验收 `SessionActorAckTests` 6 例；顺带修复 run loop 在命令驱动 drain 后因 PeriodicTimer 并发等待异常而静默死亡的 bug）
- [x] 生产默认拒绝未验证 Ingress：`AllowAllPlatformEventVerifier` 退为显式 dev 开关，缺验证器即拒（2026-10-12，commit def21d2：`Ingress:AllowAll` 默认 false + 响亮告警 + demo 配置显式开启并注明、生产配置保持关闭；验收 `IngressAllowAllEndpointTests`）
- [x] 去重原子化：`EventDeduplicator.TryMark` 改原子占位；IPC 发布失败不得被记成重复（投递状态 Received/Accepted/Applied/Failed）（2026-10-12，commit 78f2597：reserve/commit/rollback 原子协议 + token 守卫 + TTL 清扫 + 容量安全阀，两端 ingress 发布失败即 Rollback；验收 `EventDeduplicatorTests` 13 例含 32 并发同 id 恰一赢家；生命周期与四态落 `docs/design/spec.md` §3.6）
- [x] Match 权威语义定性：展示聚合 vs 权威对抗状态（结论写进 `docs/design/spec.md`，不先上 MatchActor）（2026-10-12，commit 0409cf2：spec.md §2.3——MatchStateStore 定性为展示级共享聚合，权威裁决需单 Match 定序点（MatchActor 或收敛到单 SessionActor），触发条件=公平对抗/结算需求出现）

### 阶段二：稳定性与过载保护
- [x] 有界邮箱 + 事件缓冲（Session 总量/每 Tick/最长等待），控制命令与观众事件分道，礼物类不可静默丢弃（2026-10-12，commit 5592d62：控制道无界/事件道 `MaxPendingEvents`=8000 有界；礼物满时按 `EventEnqueueMaxWaitMs`=50ms 有界等待后判 Failed 可重试、其余立即丢弃计数；验收 `SessionActorBackpressureTests`）
- [x] 权威 Tick 去阻塞发送：每客户端有界出站队列/最新快照覆盖，慢客户端不拖累 Tick（含 Gateway `DispatchSnapshotAsync`）（2026-10-12，commit df71d26：Engine `OutboundClientQueue` 容量 1 DropOldest + 泵；Gateway `GatewayClientConnection` 同构最新覆盖 + 惰性泵，`DispatchSnapshotAsync` 只入队；验收 `SessionActorOutboundTests`——阻塞窗口 ticks 照常推进、backlog 坍缩为在途+最新）
- [x] Tick 调度：单调时钟截止 + 超时策略，统一 `TickDurationMs` 默认值与文档建议（2026-10-12，commit 362b435：`PeriodicTimer` 单调驱动 + `TickTimeoutMs`=250 超限计数（检测不取消）；默认值 100→66 与 frame-sync-best-practices 统一，config.md/appsettings 三端同步；验收 `SlowTick_OverrunCounted`）
- [x] Session 生命周期：空闲回收、上限、异常隔离（GameModule 抛异常不得静默退出 Actor）（2026-10-12，commit 362b435：SessionManager `MaxSessions`=200 最久未见驱逐 + `IdleEvictAfterMs`=600s 客户端空闲回收 + 维护循环；EngineHost 同策略惰性执行；异常隔离在 5592d62（tick 异常计数、actor 存活）；验收 `SessionManagerLifecycleTests` + `GameModule_Throws_ActorSurvivesAndCountsErrors`）

### 阶段三：可观测性与恢复
- [x] `chorus_*` 指标（28 项：tick_duration/lag、ingress/applied/merged/dropped/duplicates、mailbox_depth、ack_lag、snapshot_send/resync/oversize、ws、ipc_publish_failures）+ 事件关联 ID（2026-10-12，commit b86f5a8 三端 `/metrics`；commit 68f29a9 `Envelope.SourceEventId` 贯穿 ingress→Engine（不一致打 `Correlation mismatch` warning）+ `docs/reference/observability.md` 指标清单/关联链/抓取示例；验收 `CorrelationTests` 3 例）
- [x] 快照版本与大小上限、应用失败路径、客户端重同步闭环（2026-10-12，commit 768e668：`V=1` schema 版本 + `MaxSnapshotBytes` 跳过发送并计数，超限即走 resync 兜底；commit 8226727：`SessionActorResyncTests.NoAck_TriggersForcedResync_ThenAckClearsLag` 验证停发 ACK→强制 resync→补发 ACK 回到非强制的完整闭环）
- [x] IPC 断连/重启/回退（UDS↔NamedPipe 协商）验证（2026-10-12，commit d5d2d64：服务器重启后客户端惰性重连下次 publish 即恢复；publish 期间服务器宕机必须显式失败（上行即 `chorus_ipc_publish_failures_total`，绝不静默丢弃）；Auto 在 Linux 选 UDS，UDS 无法 bind 时回退 NamedPipe；验收 `IpcResilienceTests` 3 例）
- [x] 独立压测工具（多 Session × 观众事件 × 变速客户端 × 随机断连）（2026-10-12，commit 578415b：`src/Chorus.LoadTool` 控制台工具，`--sessions/--rate/--clients/--ack-mix(fast|normal|slow)/--disconnect-every/--dup-rate/--gift-rate/--match`，双平台 payload（`test` 未签名演示链路 / `douyin` 映射器形状），结束时抓 `--engine-metrics`；验收 `LoadToolTests` 6 例 + 对真实 Server 冒烟（475 发/472 受理/241 判重/808 快照/89 ACK/9 重连，引擎侧 applied=235、snapshots=1201）；文档 `docs/guide/load-testing.md`）

### 阶段四：发布与生态
- [x] **Cohort → Chorus 命名收尾**（仓库已改名）：`Chorus.sln`、各 `Chorus.*` 工程与命名空间、项目引用、CI 工作流、文档站标题、启动命令、README 徽章；不得纯文本替换，改完全量构建+测试（2026-10-12 完成，提前于原「最后一批」计划按用户指令执行：sln/csproj/目录/命名空间/项目引用/CI（sln 引用 + VUEPRESS_BASE /chorus/）/文档站与参考文档内标识/README（标题、徽章指向 cuihairu/chorus、新 logo 64x64、鉴权默认拒绝说明）/IPC 默认值 /tmp/chorus、pipe 前缀 chorus；未做盲替换——评审归档保持原文，替换点=标识符 `Cohort.` 与显式列出的配置值；全量构建 0 error + 110/110 测试通过。注：todo 前文与历史小节的 `Cohort.*` 字样为历史记录不改）
- [ ] 进程拓扑：按 [process-topology.md](./docs/design/process-topology.md) 的阶段表推进（先代码分层 → 四角色三进程 → 触发条件满足再拆 room-manager）（2026-10-13 逐条核对触发条件：阶段一（代码分层）**已满足并已完成**——§2 平台接入三项、§3 Match/阵营三项全勾，两 listener 门禁/投递已合一；阶段二（四角色三进程）**触发条件已满足**（阶段一正确性五项 5/5：9ad322d、499f01c、def21d2、78f2597、0409cf2），已开工——`Gateway:Roles` 角色打包（15a6dad）：一个 Gateway 进程可只承载 `platform` / `streamer` 角色，platform-gw / streamer-gw / room-worker 三进程成为配置选项，门面零复制，未选中端点 404，非法值 fail-fast，验收 `GatewayRolesTests` 4 例；房间侧 `RoomManager` / `MatchRegistry` 升级待阶段三多 worker 触发（单 worker 下映射恒 1:1，路由表已是 worker 侧共享 `SessionRegistry`），理由与节奏写入 design §4「RoomManager 何时上」；阶段三**未满足，差 2 条**：①单 worker 高峰 CPU > 60% 无数据（LoadTool 冒烟数百事件为功能验证非峰值测量）②单机房间数无数百量级部署数据——两条均运行期指标，仓内无法自证。文档同步：§4 阶段表加状态列、config.md 增 `Gateway:Roles`、deployment.md 增三进程形态）
- [x] 最小游戏模块示例、平台适配器模板与接入契约（2026-10-12，commit 8a49c1b：`docs/guide/game-module.md`（`IGameModule` 契约 + `BattleGameModule` 最小示例 + 仓内参考实现 `src/Chorus.SampleGame` 的共享 `MatchStateStore` 与有界 top-N 快照 + 调用模型红线 + 反模式表）；`docs/reference/adapter-contract.md`（四抽象 + `/ingress/{platform}` 8 步处理顺序与 429/413/504/401/400/200-duplicated/503 语义 + `AudienceEvent` 完整 positional record + `TryParse` 防御式 mapper 模板 + `FixedTimeEquals` fail-closed 验签器模板 + 真实 DI 注册写法 + 7 项验收测试 + 「待联调」标记约定））
- [x] 部署/安全/故障排查文档与版本策略（2026-10-12，commit 8a49c1b：`docs/reference/deployment.md`（单进程 vs 拆分形态 + 构建运行 + 端点端口表 + 12 项安全配置默认值与 fail-closed 矩阵 + WS token 格式 + 反向代理 + 资源生命周期 + 内存态重启语义 + 拆分容器化）+ `docs/reference/versioning.md`（SemVer 0.x 策略 + 「正式发布只由维护者显式给定版本号触发」+ 五个兼容性面 + 兼容性判定表 + 无持久化=无迁移 + 升级回滚顺序；诚实标注两个缺口：`hello` 无 `protocolVersion`、快照 `V` 未上线）；`docs/reference/config.md` 补 `maxSnapshotBytes = 1048576` 默认值与说明；`docs/.vuepress/config.js` 侧边栏登记 4 个新页面））

### 十一类故障验证测试清单
- [x] 归并（混合阵营/Match/超限计数）✅ `DefaultAudienceEventReducerTests`（9ad322d）· 去重并发 ✅ `EventDeduplicatorTests` 32 并发恰一赢家（78f2597）· 投递失败重试 ✅ `Rollback_PublishFailure_AllowsPlatformRetry`（78f2597）· Tick 过载 ✅ `SessionActorBackpressureTests.Flood_*`+`SlowTick_OverrunCounted`（5592d62/362b435）· 慢客户端 ✅ `SessionActorOutboundTests`（df71d26）· ACK 安全 ✅ `SessionActorAckTests`（499f01c）· 生命周期 ✅ `SessionManagerLifecycleTests`（362b435）· 安全（未验证事件/超大请求体/非法 WS）✅ Kuaishou/Douyin/IngressAllowAll/StreamerWsAuth e2e（e2304b2/8f18bde/def21d2）
- [x] Match 并发 · 快照恢复 ✅ `MatchConcurrencyTests.ConcurrentEvents_ConserveTotalsAcrossSessions`（两 Session 同 match 400 并发投递无丢失、faction→session 归属正确，commit 8226727）· `SessionActorResyncTests.NoAck_TriggersForcedResync_ThenAckClearsLag`（停发 ACK 触发强制 resync、补发 ACK 后快照回到非强制，8226727）

### 明确不做（边界）
- 不引入分布式 Match 集群、Kafka/RabbitMQ、增量快照/回滚同步/确定性重放；不把平台私有适配放进核心；不发展成账号/运营/支付/编辑器综合平台；单体保持默认部署形态。
