# TODO

## 0. 仓库基础

- [x] 建立 solution 与包结构（11 csproj + 测试工程，`d32e1bc`）
- [x] 建立 docs/ 目录结构与索引（`3291add`）

## 1. Tick/帧同步

- [x] SessionActor 单线程邮箱 + 固定 tick（`8a49c1b`）
- [x] AudienceEvent 统一模型 + 稳定排序（`8a49c1b`）
- [x] inputDelayTicks 缓冲（`8a49c1b`）
- [x] maxEventsPerTick 合并/降采样（`8a49c1b`）
- [x] WebSocket 协议 hello/welcome/ack/snapshot（`8a49c1b`）
- [x] per-client ack 跟踪与落后检测（`499f01c`）
- [x] 快照机制（JSON 全量）（`96310c5`）

## 2. 平台接入

- [x] IPlatformEventMapper / IPlatformEventVerifier 接口（`8a49c1b`）
- [x] Platform Ingress HTTP endpoint（`8f18bde`）
- [x] 去重机制（EventDeduplicator，`78f2597`）

## 3. Match/阵营

- [x] Match / Faction / Session 关系建模（`8a49c1b`）
- [x] 多客户端订阅同一 Match（`8226727`）
- [x] 观众加入阵营规则接口（`8a49c1b`）

## 3.5 通信与部署模式

- [x] MessageBus/ActorRef/Router 抽象（`0eac8ac`）
- [x] InProc / NamedPipe / UDS / TCP / HTTP 五种 transport（`8f18bde`）
- [x] 共享会话生命周期 SessionRegistry（`a6eae7f`）
- [x] streamer-ws 门面合一（`e0501bc`/`3a10f9d`）
- [x] platform-ingress 门面合一（`0eac8ac`）
- [x] 广播快照按 tick 去重（`50f0045`，232 测试）

## 3.6 拆分模式

- [x] Chorus.Gateway 与 Chorus.EngineHost 两个进程（`8f18bde`）
- [x] IPC 双向通道（`8f18bde`）
- [x] 会话参数 EngineHost 下发 Gateway（`8f18bde`）

## 4. 可观测性与压测

- [x] /metrics Prometheus 文本（`b86f5a8`）
- [x] 结构化日志 sessionId/tickId/clientId（`7209d91`）
- [x] 本地压测工具 Chorus.LoadTool（`578415b`）
- [x] 峰值测量支持（`4ea4273`，242 测试）

## 5. 文档定稿

- [x] spec.md 第一版规格（`0409cf2`）
- [x] protocol.md 协议字段与示例（`7a1b64f`）
- [x] ops/deployment/config/tuning 文档（`8a49c1b`）

## 6. 文档站

- [x] VuePress 文档站（`docs/.vuepress/`）
- [x] GitHub Pages 自动部署（`docs.yml`）

## 7. CI / 覆盖率 / 发布

- [x] CI build + test（`ci.yml`）
- [x] 覆盖率上传 Codecov（`ci.yml`）
- [x] Tag 自动 Release（`release.yml`）

## 8. 实现分析与改进

### P0 平台对接与安全

- [x] 抖音平台对接（占位模式，`e2304b2`，172 测试）
- [x] 快手平台对接（占位模式，`e2304b2`，172 测试）
- [x] WebSocket 接入鉴权（`8f18bde`）
- [x] Ingress 真验签 fail-closed（`def21d2`）

### P1 正确性与容量

- [x] SessionActor 反压（`5592d62`）
- [x] 会话空闲回收（`362b435`）
- [x] MessageBus/ActorRef/Router 抽象（`0eac8ac`）

### P2 可观测性

- [x] /metrics Prometheus（`b86f5a8`）
- [x] 结构化日志（`7209d91`）
- [x] 本地压测脚本（`578415b`）

### P3 性能/测试/运维

- [x] 快照序列化按 tick 编码一次（`96310c5`）
- [x] Gateway/EngineHost e2e 测试（`92d633b`）
- [x] 协议版本化 protocolVersion（`a5f23b8`，166 测试）
- [x] Gateway readiness 探针 /ready（`be9f264`）

## 9. 外部评审采纳

### 阶段一：正确性与安全

- [x] 归并跨阵营串数据（`9ad322d`）
- [x] ACK 单调性与 Tick 上界（`499f01c`）
- [x] 生产默认拒绝未验证 Ingress（`def21d2`）
- [x] 去重原子化（`78f2597`）
- [x] Match 权威语义定性（`0409cf2`）

### 阶段二：稳定性与过载

- [x] 有界邮箱 + 事件缓冲（`5592d62`）
- [x] Tick 去阻塞发送（`df71d26`）
- [x] Tick 调度单调时钟 + 超时（`362b435`）
- [x] Session 生命周期管理（`362b435`）

### 阶段三：可观测性与恢复

- [x] chorus_* 指标（`b86f5a8`）
- [x] 快照版本与大小上限（`768e668`）
- [x] 客户端重同步闭环（`8226727`）
- [x] IPC 断连/重启/回退（`d5d2d64`）
- [x] 独立压测工具（`578415b`）

### 阶段四：发布与生态

- [x] Cohort → Chorus 命名收尾（`8f18bde`）
- [x] 最小游戏模块示例与适配器模板（`8a49c1b`）
- [x] 部署/安全/故障排查/版本策略文档（`8a49c1b`）
- [x] Gateway:Roles 角色打包（`15a6dad`）
- [x] RoomAffinity + RoomRouteTable（`be2eff7`）
- [x] 快照 schema 版本上线（`2d157fa`）
- [x] CPU 峰值测量仪器（`4ea4273`）

## 10. 进程拓扑

- [ ] 阶段三多 worker：等单 worker 峰值 CPU > 60% 或单机房间数达数百量级（运行期数据，仓内无法自证）
- [x] 恢复半边：IGameModule 快照恢复契约落地（`062cc68`，11 测试）：方案 A（回写入口进 `IGameModule`，必选成员）+ 线协议 JSON 输入 + fail-closed。`RestoreState(version, JsonElement)` 必选 = 编译期强制每模块回答「怎么重建」；单点 `GameModuleRestorer` 在 actor 工厂内执行（`Create` 后、`SessionActor` 前），两处出生点共用 → parity by construction；未知版本/超 `MaxSnapshotBytes`/模块异常一律拒绝启动，`SessionRegistry.StartFailures` 可观测，不留半状态 actor；无生产接线（两 Program.cs 不注册默认恢复源，null = 今天行为不变）

## 11. 帧同步方案

方案见 docs/design/frame-sync.md，接入见 docs/guide/quickstart-frame-sync.md。

- [x] P1 帧号语义落地（`a7fe3c6`，7 测试）：帧号=tickId 文档化；客户端按帧号回放状态机 `Chorus.LoadTool.FramePlayer` 落地并接入压测工具（gap→resync）；`state.frameEvents` 约定废弃（被 P2 信封字段取代）
- [x] P2 帧历史与补帧（`758a6a5`，18 测试）：帧历史 ring buffer + `resync` 拉取消息全链路 + 快照 `frameEvents` 信封字段（additive，不 bump v）
- [x] P3 客户端 SDK（`213155b`，10 测试）：`Chorus.Client.ChorusClient` 三行接入（握手+按帧号回调+ACK 抽样+resync 冷却），真实 /ws e2e

## 12. B 站弹幕源适配器

调研与方案见 docs/design/bilibili-danmaku-adapter.md。结论：部分支持。

- [x] P1 open-live 接入 + 弹幕归一化 + 断线重连 + /ingress/bilibili（`ff8399c`，34 测试）：金测（鉴权/篡改/去重/未知不崩）+ e2e（弹幕→帧→客户端快照）；签名/帧协议按 blivedm 原文核实
- [x] P2 礼物/进场/房间生命周期归一化（含 SEND_GIFT_V2 灰度）（`8f5d5be`，40 测试）：金测覆盖各类型映射 + V2 容忍 + e2e 礼物→帧；SUPER_CHAT/GUARD_BUY/USER_TOAST 待定项顺延
- [x] P3 反向写入/互动指令：决议为明确不做（见「明确不做」段，无官方通道）

## 13. 文档一致性

- [x] 文档-代码一致性审计（2026-10-13，41 条修复，见 docs/审计-文档一致性.md）
- [x] 总体架构与 Code Agent 实施规范（2026-10-13，80 项审计，见 docs/design/overall-architecture.md）
- [x] 架构图进文档（`89d2275`）
- [x] API 参考文档（`67daedc`）
- [x] README 双语互链（`f692034`）
- [x] 每日构建 workflow（`56e5f29`）
- [x] .NET 10 迁移（`d32e1bc`，242 测试）

## 14. 控制面（架构审计确认缺失）

- [x] 平台账号唯一绑定：`(Platform, PlatformAccountId) → Room` 存储 + 唯一性约束 + 换绑流程（`244a16f`，15 测试）：`PlatformBindingRegistry` 内存实现（冲突 fail-visible 不隐式换绑、换绑/解绑 CAS 防并发双绑与迟到解绑、并发绑恰好一个赢）；持久化属 §15，生产接线随控制面路由落地
- [x] 身份映射：platform identity → streamer/player 映射（`b5b5993`，15 测试）：`PlatformIdentityRegistry`（Streamer/Player 身份表，冲突 fail-visible 不隐式覆盖、换绑/解绑 CAS、同身份重映射幂等、并发绑恰好一个赢）；主播 WS 认证当前是共享 token，接线随控制面落地
- [x] Room 生命周期状态机：Created/Waiting/Active/Idle/Sleeping/Migrating/Recovering/Closing/Destroyed（`6c89316`，27 测试）：`RoomLifecycleTransitions` 纯跃迁表 + `RoomLifecycleRegistry` 每房间状态（非法跃迁 fail-visible、状态不撕裂）；审计 §5.1 五约束逐条落地（Destroyed 只经 Closing 到达、无驱逐出口——容量淘汰不等同房间销毁、Sleeping 非终态且经 Recovering 恢复、Migrating 必经 Recovering 且可中止、终态记录才可 Forget）；持久化属 §15
- [x] Worker 注册与健康管理（`8212d18`，14 测试）：`WorkerRegistry`（注册/幂等重注册、心跳单调不回退、健康窗口判定、快照有序）；健康是观察——过期 worker 仅从健康集排除、不自动移除（摘除是显式控制决策，边界满窗口按过期 fail-closed）；`SortedWorkerIds()` 直接喂 `RoomAffinity.Assign`（阶段三代码半边，接线等运行期数据门控）
- [x] 平台能力注册（平台支持的事件类型/版本/权限）（`a322b91`，12 测试）：`PlatformCapabilityRegistry`（事件类型集合 + Ingest/Writeback 方向标志，未注册平台 fail-closed）；默认值与已落地映射器对齐（B 站四类型只读、抖音/快手占位三类型），Server/Gateway Program.cs 播种；B 站 Writeback 恒 false（P3 决议）

## 15. 数据面（架构审计确认缺失）

- [x] DataCommand 契约 + 幂等 + 可靠性分级（`56fd01e`，32 测试）：新工程 `src/Chorus.Data`（零依赖，供未来独立 Data Worker 进程）；`DataReliabilityLevel` 四级 + `DataReliabilityPolicy` 五项语义逐条对齐审计 §8.1（未定义级别 fail-closed，不猜默认策略）；`DataCommands.Validate` fail-visible 返回违规字段名（§8.3 关键数据不静默丢），`Level` 无默认值漏设编译期失败；`DataCommandIdempotencyTracker` 原子幂等台账（TryBegin/Commit/Rollback，沿用 EventDeduplicator 纪律，失败绝不被误记为已处理 + TTL + 容量安全阀）；`DataCommandOutbox` 有界 FIFO（计数准入容量硬上限、满载按分级分派：统计 Dropped 计数可观测/其余 RejectedFull、envelope 带 EnqueuedAtMs 供排队延迟可观测）；排行榜/持久化/统计属后续项
- [x] Data Player + 游戏记录 Repository（`fb6f0fe`，29 测试）：`PlayerRecord`/`PlayerRepository` 对齐审计 §8.1 玩家资料四要求——版本号守卫（只接受 current+1，旧值回写/跳版本 fail-visible，last-writer-wins 被挡）、并发写恰好一赢（CAS）、幂等（同版本同内容重放 = no-op 成功，同版本不同内容 = 冲突）、查询一致性（读总返回已提交最新版本；资料更新合并键值不洗掉其他命令的键）；`GameRecord`/`GameRecordRepository` 追加式不可改（同 RecordId 重放同内容幂等 no-op、不同内容/RoomId 冲突不覆盖历史）、顺序由生产者负责（不校验 sequence 单调）、查询按 sequence 升序 + 游标严格大于 + 无 sequence 记录稳定追加序；持久化边界由 DataCommandOutbox/业务流水提供，生产接线属后续项
- [x] 排行榜子系统：RoomRank / MatchRank / PeriodRank / GlobalRank / PlatformRank（`b6ff76e`，33 测试）：`LeaderboardScope` 四作用域（Room/Match/Period/Global）共用 `LeaderboardStore`——替换式投影（UpsertScore，房间/单局权威状态直投、天然幂等；只暴露引擎侧，客户端不能自报分 §9.2）、幂等增量（AddDelta 按 sourceId 先占锚点再改分，重复事件不重复加分、并发同源恰好一次、未知参与者从 0 起算、负增量可纠错）、确定性并列（分数降序 + 同分 participantId 字典序 + 竞赛名次 1,2,2,4）、最终冻结（Freeze 即封榜，写入 fail-visible 拒绝、查询恒返冻结时刻名次、重复冻结幂等）、周期榜按 scope key 携带周期实现跨周期自然重置、查询与更新分离（Query 返回独立快照）；`PlatformLeaderboardRegistry` + `IPlatformLeaderboardAdapter` 适配器注册（未注册平台 fail-closed 返回 false/null，空作用域/参与者抛错；B 站无官方写回通道不内置实现）；平台延迟聚合/缓存/查询 API 属后续项
- [x] 持久化层：DB 选型 + 快照持久化 + 恢复（`d5232cf`，21 测试）：按审计口径落地为**后端可插拔契约层**——`ISnapshotBackend`（存储组件按实际需求选择、不硬编码选型，审计 §186 行；初期不引外部 DB 依赖，真实 DB 接线随生产需求出现）+ `InMemorySnapshotBackend` 参考实现 + `SnapshotStore`；快照记录 `StateSnapshot` 携带 Version/SchemaVersion/Checksum，**版本单调守卫**（旧写者迟到快照绝不覆盖新状态：TrySave 返 false / Save 抛 InvalidOperationException，同版本重放幂等拒绝）、**完整性校验**（SHA-256 前 8 字节大端 long，写入侧重算不信调用方自报、读取时校验，损坏记录抛 `SnapshotIntegrityViolationException` 而非当作「未持久化」触发错误重建）；状态重建（恢复的执行半边）已随 §10 恢复契约落地（`062cc68`）
- [x] 统计与分析管道（`d9c7f6b`，14 测试）：`AnalyticsPipeline` 从 DataCommandOutbox 批量消费（DrainOnce 有界批处理），按 CommandType 注册提取器产出 `StatSample` 派生统计；幂等（同 CommandId 绝不重复计数，DuplicateCount 可观测）、普通统计可丢弃可重建（Reset 即重建入口，§8.1；不丢失级别走 outbox RejectedFull 不经过本管道）、可观测不致命（未知 CommandType/提取器异常/空提取结果跳过并计 SkippedCount，统计故障不拖垮引擎）、每个指标自动带 `*` 跨 scope 聚合键；离线/窗口等复杂聚合与生产接线属后续项

## 16. 匹配与机器人（架构审计确认缺失）

- [x] Matchmaker：匹配策略 + 参与者分配（`839e6ec`，19 测试）：`MatchmakingPolicy`（模式/最小最大人数/填充模式，等真人模式必须给等待上限否则构造期拒绝）、`Matchmaker`（FIFO 先到先得、一局最多 MaxParticipants 超额留队、真人凑满 Min 即开局、凑不满按规则 1 配置行事：等真人超时填充或立即填充、补齐到 Min 不 padding 到 Max）、重复排队 fail-visible、开局消耗队列不二次组局、机器人填充委托 `IBotParticipantFactory` 并在组局边界校验产出（数量/独立 ParticipantId/非空 BotId，规则 2 强制，残缺产出 InvalidOperationException）；时间由调用方传入不读墙钟；Match/Room 创建绑定属调用方（审计 §6 ParticipantSet 之后流程）
- [x] Bot 工厂 + Bot Participant + 统一输入队列（`b0a04cf`，21 测试）：`DefaultBotParticipantFactory`（进程内单调序号生成独立 ParticipantId/BotId，规则 2；机器人是参与者层实体，非虚构主播账号、非伪造 WS 客户端）+ `BotAction` 纯数据记录（无委托/回调/状态引用，策略只能产出动作，规则 3 队列半边；显式 TargetTick + bot 内 Sequence 幂等锚点，规则 5）+ `BotActionQueue` 统一输入队列（规则 4：与平台事件/主播操作进同一条确定性输入流——总序 TargetTick 升序 + 同 Tick BotId 字典序 + bot 内 Sequence，排序只依赖动作字段与提交顺序无关；同 (BotId,Sequence) 重放 fail-visible 拒绝计入 DuplicateCount；容量硬上限满载 RejectedFullCount 可观测不静默丢）
- [x] 机器人策略配置与动作（`505ca5f`，18 测试）：`BotStrategyConfig` 声明式配置（策略 id/cooldown/键值参数，构造期校验）+ `BotWorldView` 只读世界视图（不可变数据快照——当前 Tick/下一可用动作 Tick/房间事实，规则 3 视图半边）+ `IBotStrategy` 契约（观察只读视图产出 BotAction，不携带任何改状态能力，规则 3；ExportState 可恢复 / 返回 null 声明可重建，规则 6 迁移两半边）+ `ScriptedBotStrategy` 参考实现（脚本顺序逐条产出、bot 内序号单调、游标导出恢复、Restore(null) 从头重放）+ `BotRuntime`（按 Tick 驱动全部策略提交统一输入队列，规则 4；单策略崩溃只影响该 bot 计 StrategyErrorsCount 可观测、注册按参与者幂等替换）；真实游戏策略与迁移接线属后续项

## 17. 小游戏平台适配（架构审计确认缺失）

- [x] 抖音小游戏 SDK + 登录/身份映射 + 实时通信 + 排行榜适配（`3dcd658`，55 测试，[patrol1224]）：适配层契约按官方公开文档落地——`MiniGamePlatformCapabilities` 能力表防编造（`tt.login`/开放数据域/云存储/游戏排行 API 名已核实；服务端 code2session 与容器内 WebSocket 未找到公开资料(待核)）；`HttpMiniGameLoginExchange` 配置驱动 fail-closed；`MiniGameIdentityBinder` 身份映射（确定性派生、冲突 fail-visible）；`MiniGameLeaderboardProjector` 排行投影（能力门控、不重排）；凭据/真机联调待外部（见 docs/reference/platform-douyin-mini-game.md）
- [x] 快手小游戏 SDK + 登录/身份映射 + 实时通信 + 排行榜适配（`3dcd658`，[patrol1224]）：公开文档仅覆盖小程序，小游戏全部能力未找到公开资料(待核)——能力表全待核、`Supports` 恒 false、排行榜投影恒 null；已核实的小程序 code2Session 官方形态（`oauth2/mp/code2session`）作为配置参照落地；平台入口确认/凭据/真机待外部（见 docs/reference/platform-kuaishou-mini-game.md）

## 18. 迁移与恢复（架构审计确认缺失）

- [x] 快照持久化 + 状态恢复 + 事件补齐（持久化 `d5232cf` + 状态恢复 `062cc68` + 事件补齐 = 恢复完成前不投递任何事件（gate）+ 客户端补帧 `758a6a5`；历史事件重放属 spec「明确不做」）
- [x] Epoch / 路由版本 + 旧写者隔离（`2415c92`，21 测试）：`RoomAuthorityRegistry` 落地审计 §11.1 保证 1/2/3/4/7——两阶段接管（TryBeginTakeover 冻结旧写者=停止权威写入的明确机制；TryCompleteTakeover 恢复完成后换主且 Epoch+1、旧写者彻底出局；TryAbortTakeover 恢复失败回滚旧写者不伪装接管成功）、迁移期间人人无权威（新 Worker 恢复完成前不接权威输入）、过期路由识别（IsRouteCurrent 按 epoch 判定，迟到旧接管请求 fail-visible 拒绝）、未 Begin 直接 Complete 的单方面夺权被拒、重复注册 fail-visible 不隐式换主、CAS 防并发双注册；保证 5（事件恢复）与 IGameModule 恢复契约同门控（§10），保证 6 已由 SnapshotStore 落地（§15）；生产接线随阶段三多 worker（等运行期门控）
- [x] Worker 故障接管测试（`f4775df`，6 测试，[patrol1224]）：控制面故障接管编排端到端（跨 WorkerRegistry + RoomAuthorityRegistry）——过期 worker 仅从健康集排除驱动接管决策；旧写者冻结、换主 Epoch+1、接管中止回滚不伪装成功、重复/迟到接管 fail-visible、旧 epoch 路由被拒、故障恢复的旧写者不自动回授权威

## 明确不做

- B 站 P3 反向写入/互动指令：当前无官方通道，需平台明确允许后再评估
- 不引入分布式 Match 集群、Kafka/RabbitMQ、增量快照/回滚同步/确定性重放
- 不把平台私有适配放进核心
- 不发展成账号/运营/支付/编辑器综合平台
- 单体保持默认部署形态
- 帧同步不做回放/录播对齐（无持久化）
