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
- [x] SessionRegistry dispose 幂等（`6f9efca`，699 测试）：CI 实证关停竞态 `ObjectDisposedException`——拆分引擎 `EngineIpcService : BackgroundService`，`BackgroundService.DisposeAsync` 在 execute 任务未完成时会二次调 `StopAsync`，`_registry.DisposeAsync()` 二次执行命中已 dispose 的 `_sweepCts`；加 `Interlocked.Exchange` 守卫使二次 dispose 变 no-op（五种 transport bus 均已幂等、`base.StopAsync` 自带 `_executeTask` 空守卫，dispose 链路闭合；单进程 `SessionManager : IAsyncDisposable` 非 BackgroundService，无此风险）
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
- [x] LoadTool 入口事件签名支持（PATROL1015/KX9，`da20c2e`/`3da56e1`）：`--sign <secret>`（旗标）与 `CHORUS_LOAD_SIGN_SECRET` / `CHORUS_LOAD_DOUYIN_SECRET`（别名，环境变量优先）对 douyin 占位 HMAC-SHA256 与快手官方 sha1Hex(body+token) 签出精确请求体，其余平台拒绝签名；启动未签名显性告警，结束零 accepted 走 stderr ALARM 且退出码 1（无测试/文档/CI 依赖该退出码，安全），401 从笼统 other 分桶为 unauthorized；`IngressSigner`（Sign/CanSign）与 `IngressDiagnostics` 抽成可单测单元（替换首稿 `ZeroAcceptedAlarm`），`docs/guide/load-testing.md` 同步。阶段三门控数据由此可在仓内复现
- [x] Engine→Gateway IPC 发布失败可观测（`8e87a47`，694 测试）：EngineHost 侧补齐 `chorus_ipc_publish_failures_total`（快照 best-effort 发布失败计数并告警、欢迎发布失败计数并 rethrow），与 Gateway 侧同指标名构成双向覆盖；此前 `EngineBusSessionClient.SafePublishAsync` 静默吞掉失败、`RenderEngine` 无该指标（observability.md 已同步）
- [x] 快照体积可观测（`f301aa3`，696 测试）：`SessionDiagnostics.LastSnapshotBytes` 记录每 session 最近一次下发快照的线字节数（`/sessions` 自动暴露），`ChorusMetricsText` 增 `chorus_snapshot_bytes` gauge（跨 session 取最大）；此前 tuning.md 记为「未暴露」的调优信号缺口已闭合（tuning.md/observability.md 已同步）
- [x] 会话驱逐可观测（PATROL1014）：`SessionRegistry` 两条驱逐路径（容量驱逐 `EvictIdlestLocked` / 空闲清扫 `SweepIdleAsync`）此前零计数——`chorus_sessions` gauge 下降无法区分正常结束与被驱逐；增 `IdleEvictions`/`CapacityEvictions` 累计计数（Interlocked），`ChorusMetricsText` 增 `chorus_session_idle_evictions_total` / `chorus_session_capacity_evictions_total`，注册表级三计数收拢为 `SessionRegistryCounters` 记录（`RenderServer`/`RenderEngine` 各接 `registry` 参数，替换裸 `sessionStartFailures`），Server/EngineHost 接线；observability.md 已同步，`SessionRegistryTests` 两例加计数断言 + `Renderer_SessionRegistryCounters_EmittedOnBothSurfaces` 守卫
- [x] 会话启动失败可观测（PATROL1014）：`SessionRegistry.StartFailures`（快照恢复 fail-closed / 模块构造抛异常时拒绝启动）此前仅进程内可见——失败会话无 actor 诊断、`/sessions` 为空、`/metrics` 无该计数，属观测黑洞；`ChorusMetricsText` 增 `chorus_session_start_failures_total`（`RenderServer`/`RenderEngine` 各接参），Server 传 `sessions.StartFailures`、EngineHost 传 `engine.StartFailures`（`EngineIpcService` 新增该属性）；observability.md 已同步，`Renderer_SessionStartFailures_EmittedOnBothSurfaces` 守卫 + 两处 e2e 断言
- [x] 帧同步 resync 路径指标导出（PATROL1014）：frame-sync P2 的 resync 计数（`TotalResyncRequests/Replays/WindowMisses/Throttled`）与 `FrameHistoryBytes` 此前仅在 `/sessions` 可见、未进 `/metrics`；`ChorusMetricsText` 增 `chorus_resync_requests_total` / `chorus_resync_replays_total` / `chorus_resync_window_misses_total`（窗口缺失→帧历史过小的调优信号）/ `chorus_resync_throttled_total` + `chorus_frame_history_bytes` gauge（跨 session 取最大）；observability.md 指标清单已同步，`Renderer_ResyncCounters_SumAcrossSessions` / `Renderer_FrameHistoryBytes_MaxAcrossSessions` 两例守卫
- [x] 网关 resync 限流丢弃可观测（P1014D）：`FaceCounters.WsResyncDropped`（`/ws` resync 固定窗口限流丢弃，frame-sync.md:165 已记载但从未进指标）是指标覆盖对账（SessionDiagnostics 34 字段 + FaceCounters 14 计数器 vs `/metrics` 43 指标名）找出的最后一个未导出计数；`ChorusMetricsText` 增 `chorus_ws_resync_dropped_total`（RenderServer/RenderGateway 门面段），与引擎侧 `chorus_resync_throttled_total` 互补（丢弃发生在到达引擎之前）；`Renderer_WsResyncDropped_EmittedOnFaceSurfaces` 守卫两表面
- [x] EngineHost 诊断端点可用性（`c7846a6`，698 测试）：新增 `GET /sessions`（拆分模式下的会话权威，补 deployment.md 表 ❌/未暴露 缺口，**已代决(巡检 R2)**——部署表标 ❌ 是「未暴露」的缺口而非有意排除，引擎已是会话权威，暴露只读诊断端点与既有 `/health`、`/metrics` 同信任模型）；顺带修复 latent bug：EngineHost 只 `AddHostedService<EngineIpcService>()`（仅注册 `IHostedService`），`/metrics` 直接注入 `EngineIpcService` 时 DI 解析失败 → **一直 500**，改 `AddSingleton` + `AddHostedService(sp => ...)` 后恢复；补 `ProgramMarker.cs` 让测试可 `WebApplicationFactory<Chorus.EngineHost.Program>` 托管 EngineHost（首次有测试覆盖其 HTTP 面，deployment/api/observability 已同步）

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
- [x] 文档站构建与部署（`docs.yml`：`npm ci` + `vuepress build` + Pages；2026-10-14 修复 npm override 非法键导致的红灯 `127b0d5`，pin 纪律见 docs/guide/getting-started.md `f3ce94a`）
- [x] docs.yml 触发过滤修正（`0b092f3`）：原 `paths-ignore`（抄自 ci.yml，那里对「纯文档不编译」正确）使 build+deploy 文档站的 workflow 反被纯文档提交跳过——最近 14 提交中 7 个纯文档提交全部未触发 Pages 重建，`docs/.vuepress/` 配置改坏 CI 也不拦；改为 `paths` 包含站点真实输入（`docs/**` + `package.json`/lock + workflow 自身，站点不读 src/），代码与台账提交不再触发冗余文档构建
- [x] 单客户端出站队列积压导出（`6d738b7`，审计 B3「待导出」闭合）：`OutboundClientQueue.Dropped` 新增 `clientQueueDropped`（per-client）/ `totalClientQueueDropped`（session 汇总）+ 指标 `chorus_client_queue_dropped_total`。顺带修掉一个潜伏计数缺陷：capacity-1 槽原用 `DropOldest`，`TryWrite` 在丢弃旧项后仍返回 true（仅 writer 完成时返回 false），原 `_dropped` 分支实为死代码、真实替换从未计数；改用 `FullMode.Wait` + 显式自逐出（交付语义与 DropOldest 一致，逐出可精确计数，`BoundedChannelOptions` 无公开的丢弃回调可用）
- [x] 负载工具与传输层测试覆盖补齐（PATROL1015，`4247128`/`fe94464`/`dcea9ce`/`eb6c592`，729 测试）：LoadTool 全仓最低（22%）——`IngressDriver` 计数器补 Interlocked（并行 producer 丢更新缺陷）+ 状态分桶/失败/会话轮转测试；`ClientSimulation` 加 `connect` 传输缝（镜像 ChorusClient 惯例）+ 真实 /ws e2e（hello/welcome/快照/ACK/断连重连）+ 修复重连退避 `Task.Delay` 在 try 外的取消泄漏；`LoadToolOptions` 旗标/环境变量/错误路径全补。`ChorusMessaging`：NamedPipe 收发链路（原仅测 dispose）、IPC 工厂各 transport 选择与配置校验、信封编解码失败路径。`ChorusClient`：帧跳号 gap→resync 拉取（`Session:SnapshotEveryTicks=2` 注入确定性 gap）+ 回放帧幂等丢弃。顺带修复既存 flake：`WireSnapshotFrameEventsTests` 读帧助手遇非 snapshot 帧即退出循环（`1f63956`）。`Program.cs`（顶层语句入口胶水）不测
- [x] Bilibili 适配层覆盖补齐（PATROL1015，`21b0292`，752 测试）：`BilibiliOpenLiveClient` 78.4%→94.9%——心跳双循环存活、非 2xx 心跳不停摆、auth 回包坏 JSON 计失败重试、app_start 各失败形态（code!=0/5xx/wss_link null）退避重试、数字型 game_id 流向 app/end、ingress 拒绝/断连计 PostFailures、app/end 失败忽略；修复 `DisposeAsync` 非幂等（显式 + `await using` 双路径触发 `ObjectDisposedException`，补 Interlocked 守卫 + 双 dispose 回归测试，同 SessionRegistry 纪律）。`BilibiliIngressBridgeHostedService` 15.4%→100%：六件套 `IsConfigured` 门禁（缺一/坏 AppId 全 false）、Start/Stop 对 client 的委派、`Create` 配置装配与缺配置报错。剩余未覆盖均为传输故障注入 catch 块，不伪造
- [x] 网关/引擎热路径覆盖补齐（PATROL1015，`9a5ea2e`，771 测试，行覆盖 89.82%→91.77%）：`IngressRateLimiter` 71.9%→100%（窗口滚动、限流钳位、空/空源键落同桶、10k 键满表清扫只移除闲置窗口）；`WsSessionClient` 66.7%→100%（编码快路径、sessionId 过滤、未开连接跳过、发送取消与传输错误静默吞掉）；`OutboundClientQueue` 73.6%→88%（replay 空帧/顺序直送/失败中止/门等待期取消、pump 传输错误后继续送帧）+ 修复 `SendReplayAsync` 的 `_replayGate.WaitAsync` 落在 try 外——取消令牌会把 OCE 抛给 fire-and-forget 调用方变成未观察任务异常（违背该方法注释契约，同 `ClientSimulation` 取消泄漏同类）；`SessionActor` 90.5%→96.7%（非周期 tick 上的强制重同步分支、空 SessionId 守卫、Start 幂等、同名客户端重连替换后旧连接停收）。剩余未覆盖：`OutboundClientQueue` 的防御性 catch 与「重试写抢占」并发竞态分支（不可确定性复现）、礼物事件 `EventEnqueueMaxWaitMs` 满队等待分支、`PrintUsage`/`--help`（`Environment.Exit` 杀测试宿主）
- [x] 控制面纯逻辑守卫与分支覆盖补齐（PATROL1015，`36ac347`，824 测试，行覆盖 91.77%→92.58%，10 个目标文件未覆盖行清零）：`RoomAuthorityRegistry`（空白 roomId/workerId fail-closed、空白 roomId 或 epoch 非正数的路由判定、两阶段三入口 epoch<1 的 `ArgumentOutOfRangeException`）；`RoomLifecycle`（`AllowedActions` 与 `CanTransition` 逐状态交叉核对的反向索引一致性，防合法动作清单多漏出构造不出来的菜单）；`DataCommandIdempotencyTracker`（ttl<=0 / capacity<=0 构造期拒绝）/ `EventDeduplicator`（ttl<=0）/ `FanOutMessageBus`（空子总线构造期拒绝、子总线取消原样上抛不被 `AggregateException` 吞掉）/ `TcpMessageBus`（空白 host、端口越界、`ResolveListenAddress` 主机名/`0.0.0.0`/`*`/`::`/字面 IP 各自落到可监听地址、StartServer 幂等）/ `Matchmaker`（空白 mode、机器人 ParticipantId 重复即拒绝）；`BilibiliPlatformEventVerifier` fail-closed 全分支（错平台、平台名大小写不敏感、缺凭据与空白凭据归一化为缺失、空 body/头）；`ScriptedBotStrategy`/`BotRuntime`（空 botId/participantId、非法导出态、策略返回 null 计错、缺身份动作隔离不拖垮整轮 Tick）；`DefaultAudienceEventReducer`（评论与进场类 passthrough 不参与合并、按预算截断且截断计入 dropped 的会计）
- [x] 拆分模式与平台守卫覆盖补齐（PATROL1015/KX9，`45b5d4c`/`9463c13`/`1d7a53c`/`83567ff`，877 测试，行覆盖 92.58%→93.44%）：`IpcAckResyncLoopTests` 补齐定向快照分发链路（修复三处测试基建缺陷：出站总线 warm-up 缺失、测试侧竞争服务器抢走网关监听的 socket 文件、录制 socket 未按协议 camelCase 解码致 TickId 恒 0）；`IpcSessionRouter` 注册失败与 welcome 2s 超时两 fail-visible 臂（错误连接发 ServerError 不静默不挂住）+ binding ack/resync IPC 转发；`IpcSessionActorRef` 取消令牌原样重抛（Tell 取消≠引擎不可用）+ SessionId/TickId 暴露；`PlatformCapabilityRegistry`（Count 计数与整行替换幂等、TryGet 命中返回同一引用且平台名大小写不敏感、未注册 false/null）；`KuaishouPlatformEventMapper`（空白 session_id、空白 encryptedMsg、非 base64 密钥 FormatException、可解码但不足 16 字节、空白 event 名归一化 Unknown）；`SnapshotIntegrity.Compute(null)`/`TrySave(null)`/空白 payload/`Load` 与 `Delete` 空白 roomId 构造期拒绝

- [x] 覆盖率台账口径校准与热路径/边界连续补齐（PATROL1015，`9089261`/`1eb95f2`/`eab97d5`/`defcc2e`/`e8ad20e`/`e972f7a`/`2f056c4`，877→953 测试，行覆盖 93.44%→95.81%）：同口径重新校准（collector = ci.yml 的 `XPlat Code Coverage`，`(file,line)` 去重后取最大 `hits`，`hits>0` 记命中；之前 91.64% 的口径错在用 `count` 属性、未按 `(file,line)` 去重，且漏了 `<class>` 的 `filename` 属性兜底），同口径基线 771→91.77% / 824→92.58% / 877→93.44% / 931→95.25% / 938→95.51% / 953→95.81%
  - **测试逼出两个真实缺陷并已修复**：`GatewayClientConnection.DisposeAsync` 非幂等——显式 `DisposeAsync` + `await using` 双路径第二次调用在已 Dispose 的 CTS 上抛 `ObjectDisposedException`（补 Interlocked 守卫 + 双 dispose 回归测试，同 `SessionRegistry`/`BilibiliOpenLiveClient` 纪律）；`PlatformIngressFace` 的 tell 抛异常（引擎故障/取消）不回滚 dedup 租约——平台重试被误答 `200 duplicated` 而事件从未投递（静默丢事件，补 tell 专属回滚包装，outcome 各分支语义不变）
  - 逐块补齐：网关面（`PlatformIngressFace` 504/503 两臂 + dedup 回滚契约、`StreamerWsFace` 坏 JSON hello 拒绝/超大消息关闭/重同步限流丢帧计数/客户端中途断开安静收尾、`WebSocketHelpers` 超体积异常）；会话层守卫（`SessionActor` 直投 ingest 与兼容包装器、超预算 tick 的点赞合并 + 截断会计、4096 帧事件信封裁剪后状态完好、`FrameHistory` 窗口起点晚于最新帧即 miss、`ModuleRestore` 版本号低于 1 fail-visible 且不触碰模块、`SessionRegistry` 后台清扫回路 + dispose 安静收尾）；拆分模式 IPC（`EngineIpcService` 满容量的摊销清扫走网关客户端探针——无探针则空闲会话永不清、活会话断开清 per-client 槽、信封 SourceEventId 与内层事件不一致只记日志不阻断投递、事件车道满载计 overflow 不静默丢）；Bilibili 客户端（二次 `Start()` 只跑一个循环、connect 被循环令牌取消走取消臂干净停机、ws/游戏心跳各自的传输故障只停该循环不拖垮会话、app/end 传输层抛异常忽略、app_start 的 `game_id` 非字符串/数字型 fail-visible 退避且永不落地 app/end）；客户端 SDK（`ChorusClient` 脚本化 WebSocket 桩经 `connect:` 接缝覆盖接收循环全边——OCE 注入走空闲超时 `continue`、服务端 Close 帧走 `WebSocketException`→`return` 非故障收尾、非快照帧跳过、坏 JSON 快照忽略后下帧照常、`ShouldSendResync` 零冷却恒真 vs 冷却内限流、ack/resync 发送失败静默吞掉不拖垮循环、握手超时 dispose 连接并抛异常）；平台协议与适配器边（`BilibiliOpenLiveProtocol` 坏包头/截断头/超长头 `yield break`、`ProtoVerNormal` 原样返回、未知版本号 fail-visible 抛异常；`BilibiliSigner` 空 body/空 headers 拒绝、缺 Authorization 头拒绝、空白 Authorization 拒绝；`TestPlatformEventMapper` 缺 sessionId/空白 sessionId 返回 null、数字型 kind 按枚举解析、未知字符串 kind 回退 Comment；`DouyinRankUploadAdapter` 坏 JSON 响应走 catch 计 Rejections；`AnalyticsPipeline` `maxCommands<1` 抛 `ArgumentOutOfRangeException`、提取器返回 null 跳过计数）
  - **剩余未覆盖已逐条定性，不再追数**：进程内不可达的入口胶水（`Program.cs` 顶层语句/`Main`：LoadTool 101 行、Server 11 行、Gateway 6 行——只被子进程执行，collector 只统计测试宿主进程）；需真实端点才能跑的负载工具客户端循环（`ClientSimulation` 28 行，需活 WS 网关）；**internal 类无 InternalsVisibleTo**（`LengthPrefixedStream` 26-39 三个 IOException 抛异常臂——`internal static`，测试项目无法直接访问，间接经 `NamedPipeMessageBus` 覆盖但不可定向注入坏帧）；**空 catch 块无 IL 语句可插桩**（`GatewayClientConnection` 88-90/113-115、`SessionActor` 144-146/206-208、`SessionRegistry` 214/226-228/247-249、传输层各 catch 头 4 行×3、`StreamerWsFace` 239）；**缓存的身份 lambda**（`MatchRegistry` 68 行 `id => id` 恒 0，兄弟构造行 4 hits，非代码缺陷）；**不可达防御码**（`EngineIpcService` 187 出站总线空判/123 干净退出尾、`Bilibili` 82-88 StopAsync 的 `_loop` 永故障 catch 臂/197-199 WhenAll 的 OCE 臂（两个心跳循环各自吞 OCE，WhenAll 不可能抛）、`SessionActor` 234/360、`SessionRegistry` 152）；**定时竞态**（`SessionActor` 250-252 四六七个 tick 等待无一次命中 `when` 过滤、`EngineIpcService` 141-143 心跳 OCE 臂——取消多落在 `PublishAsync` 里被通用 catch 吞掉后走 `while` 条件退出、`Bilibili` 123 同类 catch 头）；**固定超时**（`Bilibili` 281 auth 回复 10s 超时返回 false，不可注入且 10s/测试不可接受）；**真实网络**（`Bilibili` 239-241 未注入 connect 时的真 `ClientWebSocket` 分支、`ChorusClient` 129-130 `DefaultConnectAsync` 成功路径需可信 TLS WS 端点而内存 TestServer 用自签证书、`LoadTool` 67-69 `--help` 臂调 `Environment.Exit` 会杀死测试宿主）

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

- [x] 阶段三多 worker：等单 worker 峰值 CPU > 60% 或单机房间数达数百量级（2026-10-15 已实测单 worker 门控数据：14 核裸机无 cgroup 配额；300 房间 + 20k rps 签名事件 → `process_cpu_percent` 峰值 15.09、`events_applied_total` 39349、`snapshots_sent_total` 1611127、`tick_overruns` 378、`tick_errors` 0、驱动 sent=69029 ok=69028；800 房间 + 30k rps → 峰值 15.26（与 300 房间持平）、`events_applied_total` 30851、`tick_overruns` 349、驱动 sent=70112 ok=70110。即 ~2.14 核当量，距 60% 触发线（8.4 核）约 4 倍余量，房间数从 300→800 峰值不变说明数百房间远未逼近单 worker 瓶颈：条款 1 未触发，条款 2 虽可满足但由 CPU 数据看属产品决策而非负载所迫，维持未勾等用户拍板）。注意：该数据当时用仓外带签名的驱动压出（LoadTool 签名缺口已由 `3da56e1` 闭合，现可用 `--sign` 仓内复现）。仓内复现（R1015D，2026-10-15 晚，拆分 Gateway+EngineHost + `--sign` 驱动，800 房间 / 30k rps 目标）暴露两处缺陷并修复（各带新单测）：① LoadTool eventId 互撞——producer 各自编号 + 线程游标，97% 事件在去重面被判重吞掉（CPU 测量随之无意义）→ 共享驱动级序号 + ordinal 游标 + `--rate` 正名 `EventsPerSecondTotal`；② 网关 welcome waiter 泄漏——2s 超时后不释放，同 (session,client) 再连必撞「waiter already exists」，重连风暴自维持（单 120s 跑 185k 失败连接、gw->eng 淹没在注册churn）→ `TryCancelWelcomeWaiter` + finally 确定性清理。但修复后仍取不到可信门控数据，**新阻塞**：gw→eng 事件投递在 800 房间下塌陷——churn-free 对照（`--clients 1 --disconnect-every 0`）100% 受理零 503，引擎却只应用 ~5%（受理 114,138 / 应用 5,569，停跑后排空仅 ~290/s）；默认 churn 组 t≈25s 起通道堵死、86% 503（应用 307/5,387）。引擎侧 `Event lane full`=0、`ingested≈applied`、`mailbox_depth`=0 → 丢在接收路径不在引擎内；两组 resync 均 ~1,387/s（零断连也 83,240 次）——引擎单一 gw→eng 接收循环被 resync 重放吃满，事件/控制面无公平性。修复方向：resync 重放限界 + 接收循环事件/控制公平（另注：旧进程优雅关闭按路径 unlink socket 会删掉新进程刚绑定的同名文件——重启竞态，control 首轮全 503 即此因，非负载）。修复进展（FIREPASS20261015，2026-10-15）：根因找到并修复——引擎 `GetOrCreateSession` 的 sweep 触发在 `Count>=MaxSessions` 时恒真（800=800），**每条消息**都 fire-and-forget 一个 sweep，且 sweep 探针 `_hasClients` 对每 session 线性扫 `_clients.Keys`（O(N²)/sweep）→ 接收循环被 ~2k sweep/s × 64 万次比较 + `_lock` 护航饿死。修复：`SweepGate`（30s 惯例，cap 时收紧到 1s，MaxSessions≤0 永不收紧）+ `_clientCountBySession` O(1) 探针（`SweepGateTests` 5 用例 + `EngineSweepTests` 2 用例：cap 下 1s 节奏回收 dead session、剩余客户端保活）。**验证**：800 房间 churn-free 复跑投递恢复 `ingested==applied==61376` 零损失（修复前 111,890 受理只到 23,114）；但门控数据被推翻——引擎峰值 CPU **79.88**（≈11.2 核当量，tickMs 22，overruns 105/60s），旧「15.26/4× 余量」结论是在被 sweep 饿死的引擎上测出的假象，**条款 1（单 worker 峰值 CPU>60%）在 800 房 + 800 WS 客户端 + ~1k events/s 实投下已观测触发**（仍维持未勾等用户拍板）。**仍余阻塞（churn 场景）**：默认 3 客户端/房（2,400 WS）+ 断连重连下事件投递再次冻结——resync 风暴 4,217/s（零断连也有 1,387/s，客户端 gap 检测放大）× 每次重放 ~13 帧 → eng→gw 快照洪流 21.5k msg/s 超过单 socket/单锁/单线程串行分发的上限 → 引擎 `GatewayConnect` 处理器内联 await welcome 发布（同一饱和总线单锁）→ 接收循环秒级阻塞 → 事件摄取冻结（accepted 21,733 送达 ~0，无 503 无 waiter 冲突，mailbox 0，ticks 仍走）。修复需两件：welcome 发布出接收循环（有界队列）+ 网关快照分发并发化/慢客户端丢弃策略 + resync 重放限界——架构级，非本轮单条目可闭合。修复进展（R1015E，2026-10-15）：三件套第一件已落地——welcome 发布移出接收循环：新增 `WelcomePublisher`（有界 Channel 4096；`TryEnqueue` 永不同步阻塞接收循环，队满或发布失败计入 `chorus_ipc_publish_failures_total` 并可见于 `/metrics`），`GatewayConnect` 处理器改为入队，内联 `PublishRequiredAsync` 删除。测试先行：`WelcomePublisherTests` 4 用例（顺序交付 / 队满丢弃且不阻塞调用方 / 发布异常上报后继续 / token 取消收束）+ `EngineWelcomeDeliveryTests` 1 用例（真实 UDS，eng→gw 侧 listen-accept 后永不读制造内核缓冲积压，20k `GatewayConnect` 风暴后追加 1 条 audience event；断言 `TotalIngestedEvents>=1`、`TotalTickErrors==0`、`PublishFailures>0`——旧内联发布下接收循环卡在 welcome 写入上，该断言必失败）。**churn 复跑验证**（800 房 ×3 客户端 = 2,400 WS，`--disconnect-every 20`，30k rps 签名事件，60s；复跑环境还需 `Faces__StreamerWs__AllowAnonymous`，否则每个 hello 被 `missing_token` 拒、churn 根本到不了引擎）：eng→gw `snapshots_sent_total` 2.65M、客户端 resync 313k、重连 25,488（20s 节奏的正常 churn，非拒绝风暴）；**事件投递解冻——网关受理 45,287 → 引擎 `ingested==applied==45,287`，零 503、`events_dropped`=0、`mailbox_depth`=0、`tick_errors`=0、overruns 11/60s、tickMs 12、峰值 CPU 27.6**（修复前同场景 accepted 21,733 送达 ~0）。代价转移到设计上该承担的位置：22,777 次发布失败 = 13,021 `eng.welcome`（有界丢弃 → 客户端 welcome 预算超时转重连，与 25,488 重连吻合）+ 9,756 `eng.snapshot`（丢帧由 resync 补齐）。**仍余阻塞（churn 场景，架构级，非本轮单条目可闭合）**：② 网关快照分发并发化 / 慢客户端丢弃策略——eng→gw 仍是单 socket + 单 `_clientLock` + 单线程串行分发，2.65M 快照下丢 9,756 帧、客户端 313k 次 resync；③ resync 重放限界——本轮 101,294 次 resync 请求触发 27,568 次重放、网关另拒 114,155 次，重放总量无请求级上限，与快照洪流互相放大。修复进展（PATROL015，2026-10-15）：重放扇出已限界——`SessionConfig.MaxResyncReplayTicks`（默认 128 ≈ 8.4s @66ms；`0` = 关闭重放）给单次 resync 请求的重放条数封顶，超限落入既有强制快照路径（`forced=true`/`reason="resync-gap"`），请求级扇出从此有上界；超限另立计数 `chorus_resync_replay_capped_total`（与窗口未命中分开计），经 `SessionDiagnostics.TotalResyncReplayCapped` 进 `/metrics`（Server / EngineHost 共用同一 renderer）。测试先行：`SessionResyncReplayCapTests` 4 用例（小窗口正常重放 / 超 cap 只答 1 条强制快照且不夹带旧帧 / cap=0 全走强制 / 双渲染面输出 7=3+4）。**仍余阻塞（churn 场景，架构级）**：② 网关快照分发并发化 / 慢客户端丢弃策略未动——eng→gw 仍单 socket + 单 `_clientLock` + 单线程串行分发，本条只封了重放扇出侧。修复进展（PATROL015，2026-10-16）：② 的丢弃策略半边落地了可观测性——排查发现策略本身早已在位（`GatewayClientConnection` 容量 1 latest-wins 槽 + 逐连接 pump + `_sendLock`，2026-10-15 前的仓内 R1015E 记录早于该实现），但计数是死代码：槽用 `BoundedChannelFullMode.DropOldest`，满槽时 `TryWrite` 仍返回 true 并静默丢弃旧帧，`_dropped` 只在槽已 complete（已 Dispose）时计数、且无任何读取方 → churn 下恒读 0，正是它该报警的场景。修复：改 `FullMode.Wait` + 显式可数驱逐（先 `Reader.TryRead` 取走旧帧再重写，重写再失败则新帧自身计入），镜像引擎侧 `OutboundClientQueue` 的同一取舍（该文件注释已记录 DropOldest 无回调的同一坑）；驱逐点直接镜像进进程级 `FaceCounters`，重连掉的连接不丢历史。新暴露面：`chorus_ws_snapshot_dropped_total`（`/metrics`，仅拆分模式有意义，单进程恒 0，其丢弃计 `chorus_client_queue_dropped_total`）+ `/faces` → `streamerWs.snapshotDropped`；`docs/reference/api.md` 顺带补上此前遗漏的 `resyncDropped` 字段（代码 7 个、文档只写 6 个）。测试先行：`GatewayClientConnectionTests` +2（gated socket 使 pump 卡在首帧、槽被占时第二次写入可数，断言只送 tick 1/3、`Dropped==1`、面计数 `1`；槽空不计数）+ `GatewaySnapshotDropMetricsTests` 3（面快照字段 / 渲染面输出 / 零值仍输出该 counter）。修复进展（PATROL016，2026-10-16）：**拆分角色拓扑首测打通**——LoadTool 新增 `--ws-url`（env `CHORUS_LOAD_WS_URL`；缺省由 `--url` 派生 http→ws / https→wss + `/ws`，行为不变），ingress 与 `/ws` 可指向不同进程，此前仓内驱动无法测 `Gateway:Roles=platform`/`streamer` 分进程拓扑（`LoadToolTests` +4：派生/尾斜杠/裸主机/显式覆盖）。拆分复跑（platform-gw 只挂 ingress、streamer-gw 只挂 `/ws`、`Ipc:GatewayIds` 扇出共用一个 EngineHost；800 房 ×3 客户端 = 2,400 WS，30k rps 目标，60s churn）：网关受理 17014 → **25515**（1.5×，证实双面共进程争用真实存在），`ingested==applied` 零损失零 503、`tick_errors`=0、引擎峰值 CPU 24.5（单网关 30.2）、`chorus_ws_snapshot_dropped_total` 5668（单网关 17377）；但仍远未到 30k 目标。**驱动端根因定位**（三条实验）：① 同拓扑 churn 关断（`--disconnect-every 0`，800 客户端 + 128 producers）20s 投 94,125 事件 = **4706 rps**、`ingested==applied`、tickMs 3、overruns 0——引擎远未饱和；② 全量 2400 客户端时驱动自身被自己的客户端模拟拖死（resync 206k 次/60s、重连 24k），ingress 仅 ~365 rps——LoadTool 单进程既当客户端又当驱动是入口瓶颈，非服务器侧饱和；③ producers 8→128 仅 1.6×（churn 下 546→862 rps），同步 request-response 驱动上限 = producers/RTT。结论：30k rps 量级需驱动端改造（异步发送 / 多进程驱动），服务器侧无饱和证据。顺带修复驱动 pacing 截断：`IngressDriver` 的 `1000 / producerRps` 整除在 ≥1000 events/s/producer 时静默归零——`--rate` 全程失效、历次「30k rps」实为 blast 模式（`RatePacer` 绝对时刻表替代：分数间隔保留、落后即追赶发 Zero；`--rate 100` 10s 实跑 1001 事件贴合目标；`LoadToolTests` +3）。附带产品修复（本轮全量回归暴露）：`SessionRegistry.SweepIdleAsync` 的 idle 驱逐计数原先在受害者 `DisposeAsync` 之后才累加——map 移除先发生，`chorus_sessions` 已降而 `chorus_session_idle_evictions_total` 仍平，抓取器可见不一致对；计数移进锁内决策点（与 `EvictIdlestLocked` 同序），新确定性用例 `SweepIdle_CountsEvictionBeforeVictimDisposalCompletes`（gated send 卡 pump→卡 DisposeAsync→Count=0 时计数必须已可见）；另 3 处 resync 重放限界测试时序加固（latest-wins 槽可吃掉单发强制应答→冷却关断下重发至落地；gap 锚点改最早送达帧，`current-7` 可早于首个存档帧落入 window-miss；`Diagnostics` 为每 tick 刷新快照，计数断言改 UntilAsync）。**仍余阻塞（churn 场景，架构级）**：② 网关快照分发并发化未动——eng→gw 仍单 socket + 单进程单线程串行分发（拆分复跑 `snapshots_sent` 1.28M/60s、`ipc_publish_failures` 47010 ≈ 3.7%，均为该路径压力），LoadTool 驱动能力改造（异步/多进程）与之同属架构级、待用户拍板优先级。复核与修复进展（PATROL1016，2026-10-16）：逐条复核上轮遗留——① 条款 1 的「已观测触发（79.88）」证据作废：那是 R1015D 时代 welcome 发布仍内联在接收循环里测出的畸形值，WelcomePublisher 落地后同场景实测 27.6（churn 800 房×3 客户端，FIREPASS）/ 24.5（拆分）/ 30.2（单网关）（PATROL016），距 60% 线（8.4 核）~2.1 倍余量 → **条款 1 修复后未触发**，回到未触发判定；② eng→gw 单 socket 串行分发、③ LoadTool 驱动改造两项仍等用户拍板优先级，解锁条件不变。**另注的重启竞态已代决修复**（产品缺陷，独立于阶段三拍板，设计决策记入 docs/design/ipc.md「socket 文件生命周期」）：根因不是旧代码的显式 `File.Delete` 而是 BCL——.NET 6 起 `Socket.Dispose` 无条件 unlink `UnixDomainSocketEndPoint` 绑定的路径（dotnet/runtime#52103，无 opt-out），旧进程 dispose 按路径 unlink 必砍新进程重绑的同名文件；修复：服务端 bind 改自定义 `RawUnixEndPoint`（不进 BCL 删除跟踪，sockaddr 序列化布局照抄 BCL），路径删除权回归总线，`DisposeAsync` 时按 bind 身份（CreationTimeUtc=st_ctime）守卫删除——身份未知或文件已换主一律不删，残留由下次 StartServer 删-绑兜底；删除点从 accept 循环尾部挪进 `DisposeAsync`（带取消 token 的 `Task.Run` 任务可能从未被调度，循环尾部守卫会整体跳过）。测试先行：`UnixDomainSocketMessageBusTests` +2（正常关闭删自己的路径 / 竞态关闭不删新进程的路径且新进程继续服务），bus+IPC 选择性门禁 65/65 三连绿。修复进展（FW1016D，2026-10-16，代决授权下开工 ②）：② 的传输层半边落地——瓶颈定位为每消息 I/O 次数而非带宽（写侧 2 次 WriteAsync+Flush、读侧每帧 2 次独立 ReadAsync、JSON 序列化压在 `_clientLock` 临界区；快照路径已广播去重每 session 每 tick 一条，无更粗合并点）→ 读侧新增 `FrameReader`（带缓冲流水线：一次 `Read` 可解多帧；帧超缓冲时 payload 直读入目标数组、缓冲不随帧长增长；UDS/TCP/NamedPipe 三传输共用），写侧小帧 ≤16KiB 合并单次写（接收端是字节流看不到写边界，故不影响分帧；大帧仍两次写，避免为省一次 syscall 复制整份 payload），UDS 发布侧把序列化移出临界区。线协议字节与失败语义不变（干净 EOF= null / 截断 header 或 payload=IOException / 负长度与 >32MiB 拒绝），对端零感知。测试先行：`LengthPrefixedStreamTests` 10 用例（3 帧只需 2 次读 vs 旧式 7 次；小帧单次写、大帧两次写；单字节涓流；16B 缓冲跨帧回收；200KB 大帧跨缓冲；截断/非法长度抛错）。门禁：帧层 10/10 + 传输/IPC 消费者 76/76。决策 D8（已代决，含回滚面）。② 剩余半边（多 socket 按 session 分片、写合并泵）需门控复测取新基线再定参数，未开工；LoadTool 驱动改造（异步/多进程）待办。条目本身仍维持未勾：修复后门控峰值 24.5（拆分）/30.2（单网关），距 60% 触发线 8.4 核有 2 倍以上余量，无负载触发；多 worker 的代码零件（RoomAffinity/RoomRouteTable/WorkerRegistry/Epoch）已在，生产接线等运行期门控数据。修复进展（FW1016E，2026-10-16，**② 缺的门控基线已取：D8 前后同拓扑同参数 A/B**）：两树分别 `dotnet build -c Debug -o` 到独立目录，EngineHost 5067 + platform-gw 5168（roles=platform）+ streamer-gw 5178（roles=streamer）三进程，`Ipc:GatewayIds=platform-gw,streamer-gw`、`Session:MaxSessions=1000`，三项前置（`Ingress:Douyin:AppSecret`+`--sign`、`Faces:PlatformIngress:RateLimitPerMinute=2000000`、`Faces:StreamerWs:AllowAnonymous=true`）全设，杀进程→等真退出→`rm -f /tmp/chorus/*.sock`→启动。① **churn-free 对照（800 房 ×1 客户端、`--disconnect-every 0`、`--producers 128`、60s）——工作量匹配（送达 107,794 vs 108,085，差 0.3%），为本轮主证据**：`ipc_publish_failures` 4,324→1,425（−67%；按 `snapshots_sent` 折算 0.69%→0.21%）、`snapshots_sent` 627,242→686,333（+11.0%）、引擎 `client_queue_dropped` 458→12（−97%）、网关 `ws_snapshot_dropped` 132→6（−95%）、引擎峰值 CPU 30.32→27.08（−10.7%，14 核 4.25→3.79 核）、**无计划重连 4,775→1,678（−65%，客户端侧稳定性收益）**、`ingested==applied` 双侧零损失（base 105,691/105,585 差 106；d8 105,969 全等）、零 503/429/401、`maxLagTicks` 13 双侧不变、错误类别一致（`Welcome publish queue is full` + 取消，无新异常类型）；**唯一恶化项 `tick_overruns`（`TickTimeoutMs=250`）13→38（+2.9×，占 ~72.7 万次 tick 的 0.005%）**。② **churn 组（800 房 ×3 客户端 = 2,400 WS、`--disconnect-every 20`、60s）——驱动侧工作量不匹配（送达 53,105 vs 32,657、重连 37,743 vs 32,119），仅作旁证**：`ipc_publish_failures` 58,229→45,442（−22%；4.22%→3.30%）、`ws_snapshot_dropped` 16,610→8,213（−51%；每重连 0.44→0.26）、峰值 CPU 27.68→21.67、`tick_overruns` 1→15。**tick_overruns 恶化的结构定位**（未插桩计时，仅代码结构确认）：tick 体内快照广播是 await 的（`SessionActor.cs:636/671/703`），经 `EngineBusSessionClient.SendSnapshotAsync` → UDS `PublishAsync` 在单一 `_clientLock` 上串行写单一 socket；D8 每帧 I/O 变便宜后交付量 +11%，同一 socket 的写阻塞暴露面随之上升（两组同向）。合并写缓冲的每帧分配（`WriteFrameAsync` 对 ≤16KiB 帧 `new byte[4+n]`，实测快照均值 `chorus_snapshot_bytes` 2,748 B ≈ 11.4k 次/s 短生命周期分配）不构成 250ms 级停摆来源，未单独归因。**② 的解锁条件由此明确**：残余成本不在带宽也不在每帧 I/O 次数（D8 已取走大半），而在「tick 内联 await 发布」与「单 socket 串行扇出」这一对。**② 裁决（代决，维持暂缓，理由从「缺基线」更新为「有基线且触发线未达」）**：CPU 触发线未达——三组峰值 21.67–30.32%（3.0–4.25 核）vs 60% 线 8.4 核，2.0–2.8 倍余量；写合并泵会把 `PublishAsync` 失败语义从同步变异步（`snapshots_sent_total` 从「已写」变「已入队」、`SafePublishAsync`/`WelcomePublisher` 的取舍需重排、welcome 延迟预算改动），而其目标工作负载（30k rps）仍被 LoadTool 单进程驱动卡住（本轮 churn-free 128 producers 仅 1,797 rps；churn 下上限 ~862 rps）——在该场景不可达之前实现，等于为不可达负载改关键路径语义。→ ②（多 socket 按 session 分片 / 写合并泵）与 LoadTool 驱动改造（异步发送 / 多进程驱动）**继续并列暂缓**，等 CPU 触发或驱动改造后复测；本轮不实现。**方法学注记**：驱动吞吐本身随机器负载波动（本轮 churn-free 1,797 rps vs R1015D 记录 4,706 rps），绝对速率不可跨轮比较，只能比较同轮 A/B 的归一化计数（每 snapshots_sent、每重连）。条目本身仍维持未勾，理由不变：修复后门控峰值 27.08（churn-free，D8 后）/30.32（churn-free，D8 前）vs 8.4 核触发线，2 倍以上余量，无负载触发；多 worker 代码零件已在，生产接线等运行期门控数据（2026-10-17 收尾 LoadTool 多进程驱动： 父进程均分  并为每分片生成唯一  event/client id 前缀，消除跨进程 eventId 去重互撞与同 (session,client) waiter 冲突；分片速率/参数隔离/CLI 校验和事件前缀均有单测，双语压测文档同步）
- [x] 阶段三多 worker Gateway 接线（R1024PATROL，1015 测试）：`Ipc:WorkerId` 后缀 EngineHost 的 gw→eng 通道；Gateway 的 `Ipc:WorkerIds` 建 per-worker 客户端，connect/event/ack/resync/disconnect 全信封按 `RoomAffinity` 固定路由；真实双 EngineHost + Gateway UDS 守卫快照与观众事件都落到 affinity worker，同时补 `SessionActor.DisposeAsync` 并发幂等守卫与双语文档/config；空配置保持 legacy 单 worker 行为。原未勾门控条目继续等待负载/产品拍板
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

- [x] 文档-代码一致性审计（2026-10-13，41 条修复，见 docs/audit-doc-consistency.zh.md）
- [x] 文档站双语配对（2026-10-16 执行单：根目录轮 `acf9664` + 子目录轮 R1016E）：docs/ 41 对全部配对——根目录 4 篇（architecture / decisions / audit-doc-consistency / README 首页）+ 子目录 37 篇（design 14 / guide 8 / reference 15）。口径 `X.md`=英文、`X.zh.md`=中文，两份顶部均有语言切换器；子目录中文原文全部 `git mv` 转入 `.zh.md`（历史保留），英文版为新写真实译本（命令/路径/代码块/专有名词原样保留）；`.vuepress/config.js` 侧栏 39 条目无需改动（英文页占原路径），连带重写 37 篇内文互链与根目录 4 篇指向子目录的链接。例外（按执行单「1000 行以上不硬翻」红线）：`docs/design/overall-architecture.md`（1478 行）只做英文桩页——章节索引 + 「暂无译版」标注 + 回链中文原文。连带修复既有死链：`docs/design/snapshot-restore.md` 的 `./versioning.md` 原指向同目录不存在文件、应为 `../reference/versioning.md`（HEAD 上即死链，两语言版同修）；7 处 frontmatter 标题含未加引号的冒号导致 VuePress 构建失败（`YAMLException: incomplete explicit mapping pair`），已加引号。门禁：`npm run docs:build` 通过（server 37.71s / client 46.49s + 静态渲染成功），82 个 docs 文件 0 死链。剩余缺口：overall-architecture 英文全译（1478 行豁免，待有真实翻译产能再做）；todo.md 内文其余 doc 引用仍写 `.md`（现解析为英文版，内容等价、非死链，未逐条改写以免 `file:line` 类引用行号漂移）
- [x] 总体架构与 Code Agent 实施规范（2026-10-13，80 项审计，见 docs/design/overall-architecture.zh.md；`.md` 为「暂无译版」英文桩页）
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

- [x] 抖音小游戏 SDK + 登录/身份映射 + 实时通信 + 排行榜适配（`3dcd658`，55 测试，[patrol1224]；服务端 OpenAPI 三项 2026-10-14 官方核实并落地 `41ceaeb`，21 测试）：适配层契约按官方公开文档落地——`MiniGamePlatformCapabilities` 能力表防编造（`tt.login`/开放数据域/云存储/游戏排行 API 名已核实；服务端 code2Session 已核实正文并落地专项实现 `DouyinAccessTokenProvider`/`DouyinMiniGameAuthClient`/`DouyinRankUploadAdapter`（接 `PlatformLeaderboardRegistry`），能力表 `ServerSessionExchange` 升级 VerifiedOfficial；容器内 WebSocket 维持待核——服务端 OpenAPI 菜单已见全、无直播观众事件推送通道，不伪造推送客户端）；`HttpMiniGameLoginExchange` 配置驱动 fail-closed；`MiniGameIdentityBinder` 身份映射（确定性派生、冲突 fail-visible）；`MiniGameLeaderboardProjector` 排行投影（能力门控、不重排）；凭据/真机联调待外部（见 docs/reference/platform-douyin-mini-game.md 与 platform-douyin.md §2）
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
- [ ] 跨机多房间架构图整理与核对（2026-10-17 用户令）：产出 docs/architecture/multi-machine-rooms.md，要求：
  1) 架构图用 mermaid（本仓文档已有 mermaid 用法，沿用），覆盖：房间、节点、跨机通信、路由/寻址、状态同步、故障切换
  2) 图中每个组件必须对应真实代码，给 file:line；代码里没有的组件**不许画上去**，若设计上需要则单独标注「未实现」
  3) 核对现状：逐个组件确认实现状态（已实现/部分/未实现），给证据
  4) 发现图与代码不一致处，**以代码为准**，并在文档里记录差异
  5) 构建通过（pnpm docs:build 或对应脚本），贴输出
  6) 完成后勾掉本项并 commit+push，回报 hash、组件数、已实现/未实现计数
硬要求：禁止自造切换链接；中文写真中文禁机翻；glob 禁 node_modules/dist/build
- [ ] 文档页三处修正（2026-10-17 用户令）：
  1) logo 缩小至少一半（现在过大）——按 .vitepress/config.* 中 themeConfig.logo 的尺寸设置改；站点 logo 与 docs 内页同时核对
  2) 中英文切换缺失——必须用 vitepress 官方 locales 方案（root=en + zh，各自独立 nav/sidebar），**禁止自造切换链接**
  3) GitHub 链接改为默认带图标版本——用 .vitepress/config 里 socialLinks 的官方写法（icon: 'github'），不要用纯文字链接
  完成后构建通过（pnpm docs:build 或对应脚本）贴输出，勾掉并 commit+push，回报 hash 与三处的 file:line
硬要求：中文写真中文禁机翻；glob 禁 node_modules/dist/build
