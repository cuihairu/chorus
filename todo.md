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
- [x] 测试套与源码分析器告警清零（P1020I，2026-10-20，`c9288ef`/`0187f25`，定向 242 测试通过）：起点 = 全量 build 51 处告警（xUnit1031×29 测试内阻塞等待、xUnit2031×7 Where+Assert.Single、xUnit2013×6 集合计数断言、xUnit2002×2 值类型 NotNull、xUnit2029×1 Empty+Where、CS8602/8604/8766×4 测试可空 + CS8602/8604×2 源码）。测试侧 19 文件改 `await`/规范断言（含 22 个 `void`→`async Task` 测试方法），容量基准真实跑 5s 验证两处 await 转换（4,301 checkpoints、0 失败、walBytesDelta 出数）；PG 走临时 docker 容器且必须 `max_connections=300`——默认 100 时 pool=100 挤掉 teardown 的 admin 连接，复现已记录的 SQLSTATE 53300（ROOM-11 账本「误配 max_connections=100 的初跑」同款，非新缺陷）。源码侧两处 null-forgiving：`EventDeduplicator` 容量驱逐 `soonestEntry!`（与 soonestKey 同分支赋值，守卫已含 key 非空判定）+ `SessionManager` 会话探针 `_registry!`（lambda 在 ctor 自引用待赋值字段，运行期由注释既证）。`dotnet build Chorus.sln` 分析器/CS 告警 0；剩余 NU1510 为包传递引用提示（包治理类，非本轮）。批量替换时误将 `Ingress_MalformedBodyWithValidSignature_Unmapped` 一并改 async（无 await 会触发 CS1998），当即回退

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

> 2026-10-18 复核：同机 UDS 固定 Worker 集合的 Gateway 路由已完成；跨机寻址、动态扩缩容和生产故障接管尚未完成。剩余工作统一见 §19，历史压测记录不作为生产闭环完成的依据。

- [ ] **BLOCKED (2026-10-19, 2026-10-20 复核维持): multi-worker production closure awaits ROOM-11 capacity/replication and ROOM-12 independent-host validation.** 阶段三多 worker 生产闭环：同机固定 Worker 集合路由已完成；剩余跨机寻址、动态扩缩容、生产故障接管统一见 §19，生产接线（RoomAffinity/RoomRouteTable/WorkerRegistry/Epoch 零件已在仓）等运行期门控数据。触发条款：① 单 worker 峰值 CPU > 60%（14 核裸机＝8.4 核）——**2026-10-20 已观测触发**；② 单机房间数达数百量级（800 房实测峰值不变，属产品决策非负载所迫）。② 多 socket 按 session 分片半已实现（2026-10-20，`ShardedMessageBus`/`FanInMessageBus` + `Ipc:EngToGwShards`，默认 1＝旧通道与端点名字节一致，可选打开）：测试先行，单测 + 真实 UDS 端到端覆盖每分片会话投递、per-session 亲和、合并顺序、命名与 fail-fast（26 新测试，1109/1109）。同树门控 A/B 未证明 CPU 改善（见下方证据），**裁决修正（已代决）**：分片保留为默认关闭的选项（默认 1＝旧通道与端点名字节一致），触发条款 1 不视为已解决；下一轮方向见下方结构性结论；LoadTool 多进程驱动已完成并补齐 session 分片（2026-10-20）。**tick body 的 JSON 序列化次数半已推进（2026-10-20 晚，见下方证据）**：① `ServerSnapshot.State` `JsonElement`→`object`，`WireSnapshot.Encode` 与 `EngineBusSessionClient` 不再先 `SerializeToElement` 再序列化（状态 2 遍→1 遍、中间 DOM 分配全消），快照体积门改复用 `Utf8JsonWriter` 过 `Stream.Null` 量长度（不再每 tick 分配整份 state byte[]）；② 混合 tick 路径（周期快照 tick 上有掉队客户端）复用同一条 broadcast 编码给帧历史 + 全部未强制客户端（每 tick 编码 4→2，实测进程级分配 35,269→16,050 B/tick＝2.20×）。**写合并泵＝实测无收益空间**（见下方证据：热路径无可减的传输写，4.0 次/tick 两树不变），delta 编码未动（需 wire v2 + 客户端同步 + `IGameModule` 契约扩展，属协议决策待拍板）。生产闭环本身仍以 §19 ROOM-11/12 外部验收为门槛。复核（2026-10-22）：`coding` 1.5 TiB/620 GiB 可用、45 GiB 内存在本机 VM；`dev` 是另一台物理机但 2 vCPU、1.6 GiB、根盘 40 GiB 且 0 可用；PVE 仍约 105.7 GiB thin-pool 可用、12 GiB 内存，继续不满足 ROOM-11/12 容量与独立故障域。
  证据（2026-10-20 门控复测）：`docker info` → 29.1.3/coding 单机；拓扑＝EngineHost 5067 + platform-gw 5168 + streamer-gw 5178（Debug、`Ipc:GatewayIds` 双网关），压测三前置 `Ingress:Douyin:AppSecret`+`--sign`、`Faces:PlatformIngress:RateLimitPerMinute=2000000`、`Faces:StreamerWs:AllowAnonymous=true`（缺一即零流量假阴性）；`chorus_process_cpu_percent` 口径＝整机核数占比（`ProcessCpuGauge.Percent`，全核忙=100）。复测：session 未切分（每分片重建整套 800 房、扇出×N）churn 800 房×12WS 峰值 65.71%、干净复跑 81.9%；补齐 session 分片（`OwnsShardIndex`＝slot%n，`LoadToolTests` 32/32）后 churn-free 800×1 受理 2,557 rps/峰值 53.88%、churn 800×3（2,400 WS 正确分布）受理 931 rps/峰值 100%/tickMs 42/overruns 72/零受理错误。→ 条款 1 成立；驱动绝对速率不可跨轮比较，本轮结论只依据同轮多次峰值越线。历史修复走 git 史（`3da56e1` LoadTool `--sign`、SweepGate、WelcomePublisher、resync 重放限界、RawUnixEndPoint 重启竞态、D8 帧层读缓冲+小帧合并，各有配套单测）。
  证据（2026-10-20 分片 A/B，同树 Debug、同驱动参数、每轮整链重启排空）：churn 800 房×3 WS（2,400 WS）、`--rate 30000 --producers 128 --driver-processes 4 --duration 120`。shards=1：受理 209,004、快照 2,647,279、tickMs 峰值 22-69、overruns 76、publish 失败 133,030（占快照 5.0%）、CPU 峰值 46.21-86.96；shards=4：受理 197,114、快照 2,863,003、tickMs 峰值 28-131、overruns 2、publish 失败 102,214（3.6%）、CPU 峰值 47.77-100。稳态 churn-free（800 房×1、零断连）反变差：shards=1 tickMs 峰值 44、publish 失败 411；shards=4 tickMs 峰值 177（4×）、失败 799。publish 失败体是 `OutboundClientQueue` 断连竞态的 `OperationCanceledException`（非 socket 错误），基线同样存在。→ 分片保持交付正确性（零受理错误、8 个分片 socket 命名正确、每个分片索引的会话都收到 welcome/快照），但**未证明 CPU 改善、稳态 tick 反变差 4×**；CPU 绝对值跨轮不可比（同轮内单进程采样窗口也差 2-4×）。稳态 churn-free 同参数对照（工作量近似：快照 1,424,086 vs 1,397,663、事件 207,131 vs 201,752）：shards=1 tickMs 峰值 44/overruns 126/publish 失败 411；shards=4 tickMs 峰值 177/overruns 206/失败 799——工作量没多，稳态反而变差，非「B 干得多」的假象。
  结构性结论（2026-10-20，2363afd 后核查，**改写 ② 的下一步**）：`chorus_tick_duration_ms` 取自 `SessionActor.OnTickAsync` 的 Stopwatch，覆盖整个 tick body（事件归并、模块 apply、状态快照 + JSON 编码、帧历史）；而快照出站走 `SessionActor:344` 包装的 `OutboundClientQueue`，其 `SendSnapshotAsync` 是**单槽 latest-wins 的 TryWrite，同步返回、绝不 await 传输**（`OutboundClientQueue:61-77`）。即 tick 从不等待 socket，且每客户端至多 1 个待写快照——**按 session 分片 socket 在结构上不可能降低 tick CPU**（churn 组的 overruns 76→2 更可能是机器负载噪声）。因此触发条款 1（单 worker 峰值 CPU > 60%）的下一轮方向不是再加 socket 分片，而是：① 降低 tick body 本身的 CPU（状态快照编码/delta、JSON 序列化次数——**序列化次数半 2026-10-20 晚已推进，见下方证据；delta 编码未动**）；② 写合并泵半要证明的是**减少写入次数**而非并行度（latest-wins 已把每客户端写入折叠到 1），否则无收益；③ 需隔离主机才能排除共享 14 核箱上其他容器的 CPU 干扰——那正是 §19 ROOM-11/12 的外部门槛。
  证据（2026-10-20 晚 tick body 序列化次数，opt-in `CHORUS_RUN_SNAPSHOT_ENCODE_BENCH=1`）：`WireSnapshotEncodeBenchmarkTests` 同一 run 内对照新路径与保留原样的旧路径（100 玩家状态 + 4 帧事件 + 25 键输入表，线载荷 6,004 B，5 run × 20,000 次）：新 62,991 ns/op、每编码分配 6,584 B；旧 155,883 ns/op、25,656 B——**2.47× 快、3.90× 少分配**。同一测试在未改动树上跑：两条路径读数差 3%（ns）/0.1%（分配），口径可信。线兼容守卫：`Encode_StateWrittenInline_MatchesLegacyDomPath_ByteForByte` 对 4 种状态 × 3 种 `Forced` 逐字节比对旧编码（含 unicode/空对象/空数组/显式 null 成员）；`Encode_RoundTrip_StateStillDeserializesAsJsonElement` 证明读侧运行时形状不变（IPC 两端与模块恢复都靠它）；`EngineBusSessionClientTests.Snapshot_StateJson_RidesTheBusUnchanged` 锁 eng→gw 信封内 state JSON；`SessionActorSnapshotTests` 新增体积门两侧判定（256 KiB 状态超 64 KiB 探针走一次分配回退仍被拒、1 KiB 状态在 64 KiB 门内放行）。改动后全量 1115/1115（原 1109 + 6）。体积门仍用默认序列化选项量长度（与线上协议的 Web 选项口径不同，属既有差异、本轮未改语义）。
  证据（2026-10-20 深夜 ②混合 tick 编码复用 + 写合并泵裁决，opt-in 同上）：混合路径守卫 `MixedTick_NonForcedClientsShareBroadcastBytes`（2 个 5ms 应答 + 1 个永不应答客户端，MaxLagTicks=5）：改前红（同一 tick 两个未强制客户端各拿一份独立编码），改后绿（`Assert.Same` 同一 `WireSnapshot` 实例 + forced 载荷带 `targetClientId`/`clientLagTicks`、broadcast 载荷无）。A/B 录数器 `RecordMixedTickAllocation_ThreeBroadcastOneForced`（3 应答 + 1 永不应答、`ResyncCooldownMs=0` 使每 tick 都是混合态、TickDurationMs=1、100 玩家 ~6 KiB 状态，进程级 `GC.GetTotalAllocatedBytes(precise:true)` 增量）：改前（src 停在 0cb4eb5）35,269 B/tick（ticks=1,316、sent=5,264＝4.0/tick）→ 改后 16,050 B/tick（ticks=1,964、sent=7,856＝4.0/tick）＝**2.20× 少分配，每 tick 编码 4→2，发送次数两树不变**。**写合并泵如实裁决＝未达标（无可减传输写）**：`OutboundClientQueue.SendSnapshotAsync` 单槽 latest-wins TryWrite 每客户端至多 1 待写（`OutboundClientQueue:61-77`）、Gateway 侧 `GatewayClientConnection` 同槽、eng→gw 总线每 session 每 tick 已只发 1 信封（`EngineBusSessionClient` broadcast-owner 去重）——热路径传输写/tick＝K 且每条都是必要交付，两树实测 4.0/tick 不变；再减写只能靠 skip-unchanged/delta＝协议决策待拍板，仓内无可动杠杆。改动后全量 1117/1117（1115 + 2）。
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

> 以下勾选表示已完成的库级构件；生命周期、Worker 健康和权威房间目录的生产接线仍待 §19 完成。

- [x] 平台账号唯一绑定：`(Platform, PlatformAccountId) → Room` 存储 + 唯一性约束 + 换绑流程（`244a16f`，15 测试）：`PlatformBindingRegistry` 内存实现（冲突 fail-visible 不隐式换绑、换绑/解绑 CAS 防并发双绑与迟到解绑、并发绑恰好一个赢）；持久化属 §15，生产接线随控制面路由落地
- [x] 身份映射：platform identity → streamer/player 映射（`b5b5993`，15 测试）：`PlatformIdentityRegistry`（Streamer/Player 身份表，冲突 fail-visible 不隐式覆盖、换绑/解绑 CAS、同身份重映射幂等、并发绑恰好一个赢）；主播 WS 认证当前是共享 token，接线随控制面落地
- [x] Room 生命周期状态机：Created/Waiting/Active/Idle/Sleeping/Migrating/Recovering/Closing/Destroyed（`6c89316`，27 测试）：`RoomLifecycleTransitions` 纯跃迁表 + `RoomLifecycleRegistry` 每房间状态（非法跃迁 fail-visible、状态不撕裂）；审计 §5.1 五约束逐条落地（Destroyed 只经 Closing 到达、无驱逐出口——容量淘汰不等同房间销毁、Sleeping 非终态且经 Recovering 恢复、Migrating 必经 Recovering 且可中止、终态记录才可 Forget）；持久化属 §15
- [x] Worker 注册与健康管理（`8212d18`，14 测试）：`WorkerRegistry`（注册/幂等重注册、心跳单调不回退、健康窗口判定、快照有序）；健康是观察——过期 worker 仅从健康集排除、不自动移除（摘除是显式控制决策，边界满窗口按过期 fail-closed）；`SortedWorkerIds()` 直接喂 `RoomAffinity.Assign`（阶段三代码半边，接线等运行期数据门控）
- [x] 平台能力注册（平台支持的事件类型/版本/权限）（`a322b91`，12 测试）：`PlatformCapabilityRegistry`（事件类型集合 + Ingest/Writeback 方向标志，未注册平台 fail-closed）；默认值与已落地映射器对齐（B 站四类型只读、抖音/快手占位三类型），Server/Gateway Program.cs 播种；B 站 Writeback 恒 false（P3 决议）

## 15. 数据面（架构审计确认缺失）

> 快照条目的完成范围为可插拔契约、内存后端及顺序调用检查；尚无持久后端生产接线，并发保存也未实现原子版本/epoch 守卫。原有“旧写者绝不覆盖”表述不能作为分布式保证，修复见 §19 ROOM-06。

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

> 以下为历史库级交付记录：注册表状态变化不等于真实旧 Actor 停止写入，恢复 gate 与客户端补帧不等于快照后的事件可靠恢复。生产隔离、持久化、事件确认与真实故障测试见 §19；当前不宣称自动故障接管可用。

- [x] 快照持久化 + 状态恢复 + 事件补齐（持久化 `d5232cf` + 状态恢复 `062cc68` + 事件补齐 = 恢复完成前不投递任何事件（gate）+ 客户端补帧 `758a6a5`；历史事件重放属 spec「明确不做」）
- [x] Epoch / 路由版本 + 旧写者隔离（`2415c92`，21 测试）：`RoomAuthorityRegistry` 落地审计 §11.1 保证 1/2/3/4/7——两阶段接管（TryBeginTakeover 冻结旧写者=停止权威写入的明确机制；TryCompleteTakeover 恢复完成后换主且 Epoch+1、旧写者彻底出局；TryAbortTakeover 恢复失败回滚旧写者不伪装接管成功）、迁移期间人人无权威（新 Worker 恢复完成前不接权威输入）、过期路由识别（IsRouteCurrent 按 epoch 判定，迟到旧接管请求 fail-visible 拒绝）、未 Begin 直接 Complete 的单方面夺权被拒、重复注册 fail-visible 不隐式换主、CAS 防并发双注册；保证 5（事件恢复）与 IGameModule 恢复契约同门控（§10），保证 6 已由 SnapshotStore 落地（§15）；生产接线随阶段三多 worker（等运行期门控）
- [x] Worker 故障接管测试（`f4775df`，6 测试，[patrol1224]）：控制面故障接管编排端到端（跨 WorkerRegistry + RoomAuthorityRegistry）——过期 worker 仅从健康集排除驱动接管决策；旧写者冻结、换主 Epoch+1、接管中止回滚不伪装成功、重复/迟到接管 fail-visible、旧 epoch 路由被拒、故障恢复的旧写者不自动回授权威

## 19. 房间管理与横向扩展整改计划（2026-10-18）

审查依据：[房间管理与横向扩展架构审查](docs/architecture/multi-machine-rooms.md)。保留现有 Gateway / Engine / GameModule 分层，以不同房间分配给不同 Worker 为扩展单位。§14/§15/§18 的库级勾选不替代以下生产验收。

2026-10-18 实施进展：容量满时拒绝新 Actor、可选业务空房清理默认关闭；ControlHost + SQLite 持久房间目录、节点租约、按容量选择 Worker、连接代次登记、可靠输入 inbox、epoch/tick fenced checkpoint、两阶段迁移 API、独立 Worker/Gateway advertised endpoint 已落地。待处理事件、去重回执和 checkpoint 拆为独立表，避免每次事件重写房间全量 JSON。ROOM-01～06、08、10 已按下方测试和接线验收关闭；ROOM-07 的真实 Actor 旧写者拒写已补测，但真实网络分区/暂停进程恢复仍须独立多进程故障注入；ROOM-09 的控制面迁移/恢复/中止状态流已覆盖，真实分区恢复验收依赖 ROOM-07；ROOM-11 缺经确认的生产规模/RPO/RTO 和部署后端负载环境；ROOM-12 缺独立多机环境与可记录的资源预算。ROOM-10 新增 1s 路由快照刷新，GatewayConnect 的 owner/epoch/generation 校验改为新鲜快照 fail-closed，已缓存房间不再每秒重复拉 checkpoint；路由变更传播仍需在多 Worker/Gateway 环境完成端到端验收。

### 第一阶段：房间准入与状态保留

- [x] **ROOM-01 / P0：容量不足时保护现有房间（审查 R01）**（ControlStore/SessionRegistry/SessionManager 容量拒绝与保留现有房间测试，拒绝计数进 `/metrics`）。替换 `SessionRegistry` 满容量直接淘汰 Actor 的策略；拒绝新分配或由调度器选择有余量的 Worker。Actor 释放须满足业务关闭条件或保存/恢复条件；补齐创建失败时资源清理。验收：有连接或未结束的房间不会被新房间挤掉；容量拒绝可重试且可观测；创建失败不泄漏旧 Actor；原房间状态及客户端继续有效。
- [x] **ROOM-02 / P1：生命周期接入生产（R05，依赖 ROOM-01）**（睡眠需已持久 checkpoint；新连接/可靠事件唤醒后经 Recovering 恢复原稳定态；迁移中连接返回可重试冲突；关闭要求连接和输入排空，Actor 由 Worker 控制循环关闭；失去 owner 后同时清除本地 client/origin/broadcast/count 记录，防止旧连接影子状态残留）。

### 第二阶段：固定归属的水平分房间

此阶段目录必须约束目标 Worker 的创建/输入准入，不能只新增一份查询表；自动重新授予失联房间写权仍待第三阶段隔离与恢复完成。

- [x] **ROOM-03 / P0：统一房间目录与稳定分配（R02）**（`ControlStoreTests.ConcurrentCreationIsStableAcrossExpansionAndDatabaseRestart` 验证并发创建唯一归属、数据库重启及 2→3 Worker 稳定扩容；Gateway controlled 模式通过控制面目录分配，不使用本地 affinity 代替权威）。
- [x] **ROOM-04 / P1：独立节点地址与跨机接线（R04）**（广告 host/port 与 listen host 分离、缺失/重复 endpoint fail-fast；双 Worker/双 Gateway 独立 TCP 端点双向收发测试通过。真正远端主机路径在 ROOM-12 验收）。
- [x] **ROOM-05 / P1：Worker 健康、连接归属和优雅排空（R08）**（按节点实例 lease 过滤路由；draining 节点停止接新房但保留存量；连接租约及 generation 防迟到 disconnect；健康 Worker 不掩盖其他房间 owner 失联）。

### 第三阶段：权威隔离、恢复与重要事件可靠性

- [x] **ROOM-06 / P1：快照原子写入与生产恢复（R06）**（SQLite 持久 checkpoint 与房间 tick 在同一事务提交；事务 fence 校验 owner 实例/epoch、租约、expected tick，低版本晚写及旧 owner 写入拒绝；重启读取、checksum 损坏和不支持 schema fail-closed 已有测试。恢复模块接线和 schema/checksum 校验由 `ControlCheckpointRestorer` 覆盖。后端仍是单写者 SQLite，容量/HA 属 ROOM-11，不据此宣称生产 HA。）
- [ ] **BLOCKED (2026-10-19, 2026-10-20 复核维持): missing independent hosts and controllable network partition/recovery tests, including ControlHost restart.** **ROOM-07 / P0：真实写入边界的权威隔离（R03，依赖 ROOM-03、ROOM-06）**：旧 owner 迟到写必须被共享权威拒绝。同机已有：真实 `SessionActor`→`ControlClient`→ControlHost HTTP 路径 handoff 后迟到 `/prepare` 拒绝，SQLite+PG 双后端 `ControlHostFencingTests` 2/2（全套 1076/1076；实现见 `tests/Chorus.Engine.Tests/ControlHostFencingTests.cs`）。验收口径：独立进程/网络分区环境验证暂停/隔离 Worker 恢复后仍被拒绝，并覆盖 ControlHost 重启；本地 TestServer/容器不能替代独立主机分区验收。
  复核（2026-10-22）：已核可用目标仍不构成替代。`dev` 虽是另一台物理机，但仅 2 vCPU/1.6 GiB、根盘 40 GiB 且 0 可用；`shanghai-pve` 与本机 VM `coding` 同属一台物理机，且仅约 105.7 GiB thin-pool 可用。缺可控隔离网络与独立故障域，暂停/恢复、Worker 隔离和 ControlHost 重启验收仍无法执行。
  证据（2026-10-20 复核，环境未变）：`ssh -o BatchMode=yes shanghai-pve 'pvecm nodes; pvesm status --content images'` → 无 Corosync 集群、local-lvm 94.21%；`docker info` → Server=29.1.3 Name=coding，非独立物理故障域；多机验收材料 `docs/architecture/room-multihost-acceptance.md`（仅准备材料，未执行，不构成验收）。
  证据（2026-10-20 晚复核，出现新选项但未解卡）：`ssh shanghai-pve` → **本机 `coding` 就是该宿主上的 VM 101**（48 GiB、PID 1479749）；宿主 pve＝16 vCPU / 62 GiB / i9-10880H，`pvesm status --content images` → local-lvm 94.21%、**仅剩约 100.9 GiB**，`free -g` 可用 12 GiB，`qm list` 仅 VM 101 一个在跑，无 Corosync 集群。→ 技术上可在同宿主再起一台 VM 凑出「两节点 + 可控网络分区」（PVE 桥/防火墙可做 VM 间 L3 隔离），但那是**同一物理故障域**，不等于独立主机验收，且创建 VM 属共享宿主上的资源占用动作（会吃掉仅剩的 100.9 GiB 与 12 GiB 内存、影响正在运行的 VM 101），未获用户批准不动手；`hermes peer list` 只有 `win-worker`（Windows），只有 peer DM 通道、无 shell，`hermes peer run` 发只读能力探测 3 分钟仍 `running` 未回，能否承接本轮未证实。**仍缺：独立故障域的第二台主机，或用户明确批准同宿主双 VM 方案（并声明同宿主分区验收可替代独立主机验收）。**
- [x] **ROOM-08 / P1：权威边界去重与事件确认（R07）**（事件按 room + platform/eventId 建持久 receipt，inbox 有界；重复提交返回 duplicate，成功仅在 SQLite 事务提交后返回；checkpoint 消费与待处理事件删除同事务，receipt 在迁移/handoff 后保留。`DuplicateReliableEventAcrossGatewaysAndMigrationKeepsOneReceipt` 覆盖两个独立 store client 重试及迁移去重；拒绝无容量时返回冲突，调用方须重试。仅适用于共享的当前 SQLite 权威，不代表多控制面高可用。）
- [ ] **BLOCKED (2026-10-19, 2026-10-20 复核维持): missing ROOM-07 independent partition environment to prove end-to-end route convergence.** **ROOM-09 / P1：在线迁移与故障接管编排（R02/R03/R05/R06，依赖 ROOM-02～ROOM-08）**。控制面已串联 Move 冻结旧 owner、保存已提交 tick/inbox 游标、Handoff 递增 epoch、目标 EngineHost 恢复/Ready、控制循环移除旧 Actor；Abort 恢复前 owner 但再次递增 epoch，迁移中输入可重试拒绝。存储状态流与恢复/Abort 单测已覆盖。R1020 新增 PostgreSQL migration parity 测试，验证 checkpoint 与待处理输入跨 handoff 保留、未知 receipt 拒绝且事务不变、旧 owner 不能写、目标 worker 消费后 Abort 回到前 owner 并递增 epoch；`PostgresProvider_MigrationPreservesCheckpointAndPendingInputAndFencesOldOwner` 通过。剩余 blocker：ROOM-07 规定的独立网络分区进程测试尚无可用多机故障注入环境；未证明目标进程部分恢复、旧进程仍存活和网络恢复同时发生时端到端路由收敛。 多机验收准备清单与证据模板：`docs/architecture/room-multihost-acceptance.md`（仅准备材料，未执行，不构成验收）。
  复核（2026-10-22）：`dev` 资源不足且不可安全承载测试；`shanghai-pve` 与 `coding` 同物理故障域。仍缺独立网络/进程失败注入环境，端到端路由收敛不得用同宿主结果替代。

### 第四阶段：效率优化与端到端验收

- [x] **ROOM-10 / P0：输出路由去逐消息控制面 RPC（R09）**（EngineControl 独立 1s 刷新完整 routing snapshot；快照含 owner/epoch/node endpoint/连接订阅，过期 fail-closed；`ControlGatewayBus` 输出按本地快照筛目标并拒绝旧 epoch，EngineHost connect 校验改为快照中的 owner + generation，不再逐连接读房间/连接并强制刷新；缓存同 epoch 房间避免周期性重复拉 checkpoint。旧 epoch、失联 owner、订阅变更和 connect generation 有定向测试。tick checkpoint RPC 仍按 tick 同步写入，容量影响属于 ROOM-11。）
- [ ] **BLOCKED (2026-10-19, 2026-10-20 复核维持): capacity gate remains unmet; missing provisioned synchronous replica on an independent physical machine and production-storage validation.** **ROOM-11 / P0：控制面存储容量门槛与生产后端**。已代决：PostgreSQL 16+ 主库 + 独立物理机同步备库（单机/开发保留 SQLite）；门槛＝100 活跃房、66ms tick 下 1,515 checkpoints/s + 1,000 durable events/s，commit p95≤20 / p99≤40 / p99.9≤60ms，RPO=0、人工 RTO≤15min，预算每节点 2TiB NVMe + 24TiB 七日 WAL/base-backup 对象存储（决策与环境限制见 `docs/architecture/room-control-storage.md`）；未验收前不宣称生产 HA、不降低 durable checkpoint 频率。
  隔离 PostgreSQL 容量跑（2026-10-20，本地 docker `postgres:16-alpine` 容器、同 14 核共享机、pool=100、30s×2 轮、`ControlPostgresCapacityBenchmarkTests` opt-in，零失败操作）：checkpoints **1,096-1,154/s**（门槛 1,515 的 72-76%）、commit 延迟 **p50 46-49 / p95 98-107 / p99 136-149 / p99.9 224-232ms**（门槛 p95≤20/p99≤40/p99.9≤60，**p95 超标 5 倍、p50 已超过 p95 预算 2.5 倍**）；durable events **241-258/s**（门槛 1,000 的 24-26%）；WAL 增量 39.3-42.1 MB/30s（≈1.31-1.40 MB/s → 每 100 房约 121 GB/日、0.8 TiB/七日，远低于 24TiB 预算）。两轮方差极小（checkpoints ±5%、p95 ±9%），延迟结论非噪声。**容量门槛未达，延迟是主瓶颈**（吞吐缺口 24-28% 部分可能是共享机噪声，延迟超标 5 倍不可能）；生产机隔离环境下重测才能定案，同步备库/RPO=0/RTO≤15min 尚无环境可验。
  证据（2026-10-20 复核）：基准命令 `CHORUS_RUN_CONTROL_CAPACITY=1 CHORUS_CONTROL_CAPACITY_SECONDS=10 CHORUS_CONTROL_CAPACITY_POOL_SIZE=200 dotnet test tests/Chorus.Engine.Tests/Chorus.Engine.Tests.csproj -c Release --filter FullyQualifiedName~ControlPostgresCapacityBenchmarkTests`（另需 `CHORUS_TEST_POSTGRES`；PG 须 max_connections≥300，误配 100 曾 122 次 SQLSTATE 53300，已排除不作为基线）；最新同机门控数据（R3，2026-10-19，较低宿主负载窗口）：三轮 966–1090 checkpoint/s、158–192 event/s、失败 0、`capacityGatePassed=False`；R4 同机 A/B：事件 CTE `11e985c` vs `ba69ca8` → 1112.8 vs 1035.9 checkpoint/s、192.7 vs 139.0 event/s（+7.4%/+38.7%），checkpoint p95/p99/p99.9 125.27/177.81/355.18→107.49/153.92/266.25ms，支持保留；R5 checkpoint CTE 因宿主负载升高（load 35.93）未取得干净 A/B，不宣称性能提升；测试 `ControlPostgresCapacityBenchmarkTests`、`ControlPostgresIntegrationTests` 5/5、控制面 PG/SQLite 定向 33/33。解锁钥匙＝独立主机隔离容量复测 + 同步备库验收：2026-10-20 `docker info` → 29.1.3/coding 单机；`pvesm status` → local-lvm 94.21%、可用约 105.7GiB，无法搭建选定生产拓扑；本机两个 PG16 容器只读查询 `pg_is_in_recovery()/synchronous_standby_names/pg_stat_replication` 均 `f||0`，不是同步备库/复制验收证据。
  证据（2026-10-20 晚复核，仍不满足）：`shanghai-pve` 仅剩约 100.9 GiB VM 存储（local-lvm 94.21%）、12 GiB 空闲内存，而门槛要**每节点 2 TiB NVMe + 24 TiB 七日 WAL/对象存储**——差一个数量级，且同步备库还需要第二台主机（同宿主双 VM 属同一物理故障域，RPO=0 的验收价值不成立）。容量门槛未达的结论不变，解锁钥匙仍是独立主机隔离容量复测 + 同步备库。
  复核（2026-10-22）：`coding` 实际 1.5 TiB/620 GiB 可用，仍低于每节点 2 TiB；`dev` 根盘 40 GiB 且 0 可用；PVE thin-pool 约 105.7 GiB 可用。无第二台独立物理机和同步备库，不得重测宣称容量/HA。
- [ ] **BLOCKED (2026-10-19, 2026-10-20 复核维持): missing a second independent host and controllable network/process failure environment for comparative multi-node acceptance.** **ROOM-12 / P1：真实多节点正确性与扩展收益验收（依赖对应前置任务）**。本机多 TCP 端点、双 Worker/Gateway 消息互通及多角色端到端集成测试已覆盖；R1020 全套 1076/1076 通过，新增 PostgreSQL handoff/恢复验证仍在同一主机。剩余 blocker 是当前工作区没有第二台独立主机/隔离网络供真实部署，不能把容器/TestServer 结果当作跨物理故障域验收。需要可控断网、进程暂停/恢复和 ControlHost 重启的多机环境，记录 owner、事件受理/保存/应用/拒绝、重连、资源/tick/IPC/快照丢弃及单/多 Worker 同负载数据；本机容器或 UDS 测试不能替代该门槛。 多机验收准备清单与证据模板：`docs/architecture/room-multihost-acceptance.md`（仅准备材料，未执行，不构成验收）。
  证据（2026-10-20 晚复核，仍不满足）：`shanghai-pve` 是本机 `coding` 的宿主（VM 101），能再起一台 VM 凑两节点，但两台同宿主＝**同一物理故障域**，正对本项验收要求「跨物理故障域」，不构成替代；宿主仅剩约 100.9 GiB 存储、12 GiB 内存。`hermes peer list` 唯一 peer `win-worker`（Windows）只有 peer DM 通道、无 shell，只读能力探测 3 分钟未回，承接能力未证实。**仍缺：跨物理故障域的第二台主机 + 可控断网/进程暂停/ControlHost 重启手段。**
  复核（2026-10-22）：唯一新增候选 `dev` 为另一台物理机，但 2 vCPU/1.6 GiB、根盘 0 可用，不能承载对比负载；PVE 与 `coding` 仍同宿主且资源不足。跨物理故障域、隔离网络和进程失败注入手段仍缺。

交付顺序：ROOM-01 → 稳定分房间（ROOM-02～ROOM-05）→ 安全迁移与恢复（ROOM-06～ROOM-09）→ 控制面热路径和存储容量（ROOM-10/ROOM-11）→ 跨机端到端验收（ROOM-12）。第二阶段仅承诺新节点接新房间、存量房间不因集合变化中断；故障自动接管需第三阶段验收后才启用。

## 20. Admin 控制台、主播分层匹配与排行榜计划（2026-10-18）

用户范围：机器数、在线人数、房间数、房间内人数及热点；在线/匹配中主播；主播等级、星级和影响力；按实力分层匹配；排行榜。ControlHost 已有首版管理 API、认证审计、控制台、主播匹配与榜单能力；本节逐项记录验收状态，未完成项只在缺少明确数据源、已定义运维目标或端到端环境时保持 open。依赖 §16 匹配库、§15 排行榜及 §19 房间控制面，缺少生产后端的数据不得用演示数值冒充。

### 数据口径与服务端接口

- [x] **ADMIN-01 / P1：统一统计口径与管理查询 API**（ControlHost `/api/summary`、`/history`、`/nodes`、`/rooms`、`/connections` 汇总多实例控制面数据；机器按 MachineId 去重，Worker/Gateway 独立计数，在线用户按 UserId 去重，连接数单列；平台观众明确显示未知。控制台显示采样/更新时间和过期状态；数据口径在 API/UI 有说明）。
- [x] **ADMIN-02 / P1：控制台身份认证、权限和操作审计**（Cookie 会话、viewer/operator/admin 服务端授权策略、登录限流；变更接口按权限隔离，RoomControlStore 对操作人/目标/前后参数/结果写审计，API 同源校验与安全响应头已接线。操作重复性由 epoch/version/sourceId 条件守卫）。

### 总览、节点与房间

- [x] **ADMIN-03 / P1：全局总览与趋势**。机器/Worker/Gateway、在线/主播、房间/匹配/对局总览、最近一小时在线与房间趋势、下钻和过期状态已实现。**已代决**历史只按低基数平台维度切分：`douyin`、`kuaishou`、`bilibili`、`test`、`other`，未知值归入 `other`；不按部署可控且高基数的 mode/region 分组。保留 720 个 5 秒采样点（1 小时）；全局 `/api/history` 兼容旧样本，平台过滤跳过缺维度的旧样本；控制台趋势可切平台。决策见 `docs/architecture/admin-history.md`。验证：`dotnet test tests/Chorus.Engine.Tests/Chorus.Engine.Tests.csproj -c Release --no-restore` → 1069/1069；`node --check src/Chorus.ControlHost/wwwroot/admin.js`；`pnpm docs:build` → build complete 48.57s。测试 `HistoryCanFilterByCanonicalPlatformWithUnknownPlatformsBoundedToOther` 覆盖汇总、去重用户、未知值收敛和无效筛选。
- [x] **ADMIN-04 / P1：机器与 Worker 管理**（节点目录展示机器/实例、地址/版本、心跳、健康/排空、CPU/内存、容量/承载房间和 actor 诊断；Worker 控制页可停止新分配/恢复分配，房间页提供迁移/中止进度。Worker 与 Gateway 心跳现用 CPU 时间差采样并按机器总核数归一化，首个样本为 0；`ProcessResourceSamplerTests` 验证公式及封顶。没有控制台配置/自动扩缩容操作，未支持操作不展示）。
- [x] **ADMIN-05 / P1：房间列表与详情**（只读控制面目录展示房间 owner/epoch/生命周期/连接数/去重用户/Worker/tick/活动时间；详情列连接角色、pending event、匹配及迁移字段；支持平台/状态筛选与热度观察，使用需要登录的管理 API，控制面 room id 唯一且迁移不另建同一业务房间）。
- [x] **ADMIN-06 / P1：热点识别与容量告警**。保留 Top 20 在线人气与资源压力视图；新增权威入口事件热度，按平台/事件种类/UTC 分钟原子聚合，去重重试不重复计数，保留 60 分钟，礼物只显示原始件数、不跨平台换算价值。**已代决**单平台单分钟 300 个已接纳事件产生控制台观察告警（不外部 paging）；`operate` 权限可确认，确认写审计且幂等，过期/低阈值告警拒绝。实现证据：`ControlDatabase.cs:155-218`、`RoomControlStore.cs:277-295`、`ControlHost/Program.cs:102-105`。验证：`dotnet test tests/Chorus.Engine.Tests/Chorus.Engine.Tests.csproj -c Release --no-restore` → 1072/1072（临时 PostgreSQL 在线）；定向 SQLite/PG store 集成 27/27；`node --check src/Chorus.ControlHost/wwwroot/admin.js`；`pnpm docs:build` → build complete 44.01s。规则及存储方案见 `docs/architecture/admin-history.md`。

### 主播档案与分层匹配

- [x] **ADMIN-07 / P1：主播档案、等级、星级与影响力模型**（身份唯一、平台账号唯一约束、版本/CAS 更新和审计已实现；成长经验换等级/星级，影响力独立存储，竞技实力单独作为 rating；不存在档案时显示未登记，配置规则提供默认值并允许管理员更新。既有默认值已代决为本项目首版规则，非平台人气评分）。
- [x] **ADMIN-08 / P1：主播匹配状态与队列监控**（在线空闲/排队/对局中由连接、队列、MatchRecord 派生；队列主键为主播 ID，重复 join 幂等；队列及匹配视图展示等待、超时、取消、离线清理和机器人补位结果，来源是控制面而非本地进程指标。首版没有“成功待确认”状态，创建成功即占用对局，按当前匹配协议已代决）。
- [x] **ADMIN-09 / P1：按实力分层的匹配机制与规则视图**（控制面 SQLite 事务内唯一入队/组局；rating 层宽、最大差值、跨层等待、mode/region/platform、rematch cooldown、timeout 和 bot fill 均版本化、可审计且控制台可见；等级/星级与影响力不参与竞技匹配，已代决为只展示/运营指标，rating 是唯一实力维度；无动态配置预览，版本 CAS 和参数校验作为首版保护）。
- [x] **ADMIN-10 / P1：匹配质量与对局追踪**（MatchRecord 关联 MatchId、主播、房间、创建时间、RatingGap、CrossTier、bot fill、状态/结果与规则版本；审计记录可追溯组局原因、超时/取消和结算，控制台展示队列等待、对局及完成结果。指标为当前控制面累积记录，不承诺尚未采样的长周期分布图）。

### 排行榜

- [x] **ADMIN-11 / P1：排行榜列表、详情与检索**（控制台区分主播影响力/rating/经验榜、房间热度榜与存储业务榜，展示单位、名次、周期、更新时间和封榜状态；服务端支持分页/搜索/并列名次，权限由 `/api` 控制。平台官方榜只显示已有投影，不伪造来源）。
- [x] **ADMIN-12 / P1：榜单更新、结算与管理审计**（榜单来源 ID 幂等、scope/unit 一致性和 source ledger 上限在事务内校验；结算变更与 profile/board 同事务写入，freeze 幂等且封榜后拒绝新分数，管理写入带审计。周期自动切换/投影延迟未作为首版能力，按已有手动冻结语义已代决）。

### 交付与验收顺序

- [x] **ADMIN-13 / P1：控制台分期交付与端到端验收**。**已代决**验收基线为 2 节点（1 Worker + 1 Gateway）、100 房、500 连接、720 个 5 秒历史点；summary/rooms/connections/history 查询目标 p95≤250ms、p99≤500ms。Playwright Chromium 浏览器 E2E 覆盖错误/成功登录、viewer 越权拒绝、375px 窄屏无横向溢出、API 断网后恢复、操作审计写入；同负载对 4 个 GET 端点各采 30 次。`pnpm test:admin:e2e` 实测 p95/p99：summary 42.0/45.9ms、rooms 6.3/6.4ms、connections 34.4/34.7ms、history 23.3/24.4ms，均满足门槛；门禁接入 `.github/workflows/ci.yml`。

## 明确不做

- B 站 P3 反向写入/互动指令：当前无官方通道，需平台明确允许后再评估
- 不引入分布式 Match 集群、Kafka/RabbitMQ、增量快照/回滚同步/确定性重放
- 不把平台私有适配放进核心
- 不发展成账号/运营/支付/编辑器综合平台
- 单体保持默认部署形态
- 帧同步不做回放/录播对齐（无持久化）
- [x] 跨机多房间架构图整理与核对（2026-10-17 用户令）：产出 docs/architecture/multi-machine-rooms.md，要求：
  1) 架构图用 mermaid（本仓文档已有 mermaid 用法，沿用），覆盖：房间、节点、跨机通信、路由/寻址、状态同步、故障切换
  2) 图中每个组件必须对应真实代码，给 file:line；代码里没有的组件**不许画上去**，若设计上需要则单独标注「未实现」
  3) 核对现状：逐个组件确认实现状态（已实现/部分/未实现），给证据
  4) 发现图与代码不一致处，**以代码为准**，并在文档里记录差异
  5) 构建通过（pnpm docs:build 或对应脚本），贴输出
  6) 完成后勾掉本项并 commit+push，回报 hash、组件数、已实现/未实现计数
硬要求：禁止自造切换链接；中文写真中文禁机翻；glob 禁 node_modules/dist/build
- [x] 文档页三处修正（2026-10-17 用户令）：
  1) logo 缩小至少一半（现在过大）——按 .vitepress/config.* 中 themeConfig.logo 的尺寸设置改；站点 logo 与 docs 内页同时核对
  2) 中英文切换缺失——必须用 vitepress 官方 locales 方案（root=en + zh，各自独立 nav/sidebar），**禁止自造切换链接**
  3) GitHub 链接改为默认带图标版本——用 .vitepress/config 里 socialLinks 的官方写法（icon: 'github'），不要用纯文字链接
  完成后构建通过（pnpm docs:build 或对应脚本）贴输出，勾掉并 commit+push，回报 hash 与三处的 file:line
硬要求：中文写真中文禁机翻；glob 禁 node_modules/dist/build
- [x] 审核整改（2026-10-18 独立审核，见 `docs/review/luna-review-2026-10-18.md`）：
      - [x] 表一高风险：ControlHost 支持显式 `Admin:PublicOrigin`，HTTPS 时强制 Secure cookie，公网 Origin 测试覆盖反代 scheme；证据：`src/Chorus.ControlHost/Program.cs:16-21,28-33,52-54`、`tests/Chorus.Engine.Tests/ControlHostOriginTests.cs:8-67`。
      - [x] 表一高风险：ACK/resync 使用 Gateway 的新鲜 owner/epoch 快照直达缓存 Worker 通道，不再对每条消息调用 `Assign`/`/nodes`；快照过期或 owner 不可写时 fail-closed。证据：`src/Chorus.Gateway/GatewayControl.cs:58-82`、`src/Chorus.Control/ControlRoutingSnapshot.cs:56-75`、`tests/Chorus.Engine.Tests/ControlRoutingSnapshotTests.cs:42-58`。
      - [x] 表一中高：**已代决**审计记录采用追加保留、不自动删除，直到合规/业务方明确期限；审计页展示行数、首末时间和估算 payload，达到 256 MiB 显示容量告警。估算按字段字符数×4 加 32 字节/行，不含索引/WAL/备份；不自动 paging。无归档目标/任务，生产需在告警线前扩容或另行接入归档，禁止静默清理。实现：`src/Chorus.Control/ControlDatabase.cs:264-271`、`src/Chorus.Control/RoomControlStore.cs:260-266`、`src/Chorus.ControlHost/Program.cs:126`、`src/Chorus.ControlHost/wwwroot/admin.js:36,40`；SQLite 与 PostgreSQL provider 均覆盖。策略见 `docs/architecture/audit-retention.md`。
      - [x] 表一中：明确 `LIVE_START`/`LIVE_END` 只归一化为信息型 `RoomLifecycle` 游戏事件，不自动改变 ControlHost 房间状态；修正文档而不擅自接入生命周期副作用。证据：`src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventMapper.cs:103-105`、`src/Chorus.SampleGame/SampleGameModule.cs:43-70`、`docs/reference/platform-bilibili.md:43`、`docs/zh/reference/platform-bilibili.md:42`。
      - [x] 表二按代码修正文档：MaxSessions 满载拒绝新 Actor（`docs/reference/deployment.md:126,130`）；跨机房间架构现状及历史设计标注（`docs/architecture/multi-machine-rooms.md:3,13-21`）；ControlHost 路由/恢复接线及 RoomManager 历史方案（`docs/design/process-topology.md:36-67`）；区分 Matchmaker 库与 ControlHost 主播匹配 API（`docs/guide/matchmaking.md:104-112`）；同步修正 B 站 Enter/LIVE 事件过时描述（`docs/design/bilibili-danmaku-adapter.md:20,119-120`、`docs/zh/design/bilibili-danmaku-adapter.md:19,118-119`）。
      - [x] 表三只完成评估、未实现：独立多机分区注入与 ControlHost SLO/容量基线列为 ROOM-07/11/12 rollout gates；浏览器 e2e 列为 ADMIN-13；抖音真实协议联调需平台合同/凭据及获准联调；热点历史告警需权威事件聚合源和告警策略。依据：本报告表三及 `todo.md` §19 ROOM-07/11/12、ADMIN-06/13。按审核要求，新增能力均待解锁条件具备后再立项。
      - [x] 复核剩余 open：阶段三负载门槛沿用已有受控压测记录（本轮无新可信多 Worker 生产数据）；ROOM-07/09/12 仍无独立机器/隔离网络，ROOM-11 的 RPO/RTO/容量预算已按授权代决、PG provider 已接入但真实负载/WAL 与同步复制未验收。ADMIN-03/06/13 已分别由本轮前序提交补齐历史维度、事件热度告警和浏览器 e2e/SLO；本项补齐审计保留策略。其余环境依赖见 `todo.md:158,250,252,257-258` 和 `docs/architecture/multi-machine-rooms.md` §3–6（R01–R09 处置、扩展边界及 ROOM-07/09/11/12 验收门槛）。
      - [x] R1019/R1020 逐条复核审核表：两项高风险修复仍由代码与定向测试证实；审计保留已代决为追加保留、不自动删除，并增加 256 MiB 控制台容量告警；归档目标未配置，运行前须容量管理。LIVE_START/LIVE_END 文档边界、双语 MaxSessions/匹配说明和三表遗留能力均与现状对齐。ROOM-11 接入 PostgreSQL 后，ACK/resync 的路由快照结论仍成立，存储热路径更新为 PostgreSQL/SQLite provider（详见 `docs/architecture/room-control-storage.md`、`docs/architecture/multi-machine-rooms.md`）。
      门禁：原审查定向测试 31/31、R1019 全量 1066/1066 通过；R1020 `CHORUS_TEST_POSTGRES=<临时 PostgreSQL 16> dotnet test tests/Chorus.Engine.Tests/Chorus.Engine.Tests.csproj -c Release --no-restore --verbosity minimal` → 1073/1073；`pnpm test:admin:e2e` → 100 房/500 连接/720 趋势点通过，审计策略与 usage API 断言通过，summary/rooms/connections/history 各 30 次查询 p95/p99 为 46.6/51.2、7.6/8.4、35.9/36.2、24.5/24.6 ms；`node --check src/Chorus.ControlHost/wwwroot/admin.js`、`git diff --check` 通过；`pnpm docs:build` → build complete 48.14s（仅 VitePress chunk size 提示）。审核整改已全清；ROOM-07/09/11/12 等基础设施验收仍按 §19 保持 open；不 bump 版本。
- [x] 文档时效性清理（2026-10-19）：完成 README/docs 的 88 个 Markdown 页面清点；按代码修正 32 个文档文件，涵盖中英文部署形态、ControlHost/PostgreSQL checkpoint 与恢复、ControlHost 匹配 API、静态 IPC 兼容模式、MaxSessions、观测指标、旧阶段/快照决策描述和 VitePress/pnpm 命令。旧审计、总体架构及 snapshot-restore 决策快照明确标为历史，当前状态链接到维护文档；未改业务代码。命令验证：`pnpm install --frozen-lockfile` 成功；`pnpm docs:build` build complete；`dotnet publish src/Chorus.Server -c Release -o /tmp/chorus-doc-publish` 成功；`dotnet run --project src/Chorus.LoadTool -- --help` 输出当前参数；短时 Gateway 负载 smoke（`--duration 2 --sessions 1 --clients 1 --rate 1 --disconnect-every 0`）sent=11/accepted=11；文档中的 30k/s 长压示例未在本次文档核对中实际跑满；单体 Server 与 Gateway+EngineHost 启动后，文档中的 `/health`、`/ready`、`/sessions`、`/metrics`、`/faces`、`/matches`、`POST /ingress/test` 示例均实测成功。README/operations 中创建 tag/release 的命令仅核对，按本轮禁止项未执行。最终 `git diff --check` 通过。


- [x] en locale（2026-10-24 用户拍板开启）：VitePress 官方 locales 方案，root=English（`en-US`）+ `/zh/`=简体中文（`zh-CN`），中文页整体迁入 `docs/zh/`（48 页）、英文上根（48 页），zh 侧栏/导航挂 `/zh/` 前缀，语言切换为官方生成器（无自制切换器）。测试：`npm run docs:build` 绿（0 dead link）、`grep -c locales docs/.vitepress/config.mts`=2、`docs/zh/index.md` 存在、`grep -c '[一-龥]' docs/index.md`=0。英文草稿 15/48 页仍残留中文片段（流程图标签/JSON 字段注释），待用户复核。
  历史（2026-10-20 代决单语）：仓内曾零英文页、root 标 English 装中文、`zh/` 为 41 张重复中文树且缺首页；处置为先落诚实单语（root=简体中文），英文版待授权译文来源。
- [x] Dependabot 清零（2026-10-21，#172–#176）：package overrides 迁至 pnpm-workspace.yaml 并锁定 decode-uri-component 0.5.0、esbuild 0.25.0、vite 6.4.3；上一单 .NET 全量 1117/1117 与浏览器 E2E 100 房/500 连接/720 趋势点已绿，本轮补跑 pnpm install --frozen-lockfile 与 docs:build 通过。

## 21. 重设计方案（2026-10-25 目标架构，M0 进行中）

- [ ] **M0 总出口仍 open（2026-10-27）**：Linux 空库 PostgreSQL、真实 gRPC host 集成、Agent 离线冷启动/崩溃回收、开发平台模拟器与双 OS 安装配置（语法级验证）均已通过；双 OS 实机安装/启动/升级验收仍未完成（需 Linux/Windows 目标节点）。不得将部分验收标记为 M0 完成；证据见 `docs/architecture/redesign-m0-evidence.md`。
- [x] 操作身份/指纹协议、持久收据及 SchemaMigrator 收口（`0c2bd82`、`56a95a6`、`a8523f4`、`857af82`、`bc2363a`）：包含 schema 版本 1–4、事务迁移/校验和以及 stored fingerprint 验证。
- [x] 新 DataServer / ControlServer 边界和 mTLS 身份隔离（`bc2363a`）：真实 host 集成覆盖 profile/asset/receipt、tenant/role、ownership epoch 及进程报告。
- [x] NodeAgent 双系统骨架、SchemaTool/BootstrapTool 和 M0 验收记录（`bc2363a`）：Linux Release build、Windows `win-x64` 交叉编译通过；测试结果和未运行证据按平台分列。
- [x] M0 定向集成组 7/7、Release 全量 1150/1150，Release solution build 0 errors（`bc2363a`）。旧 ControlHost 能力不按新目标架构折算。
- [x] Linux Agent 离线冷启动/崩溃回收实跑通过，测试改为有界进程等待并按 process-journal 回收孤儿 seed 进程（`087fc4a`、`9e90704`）。
- [x] 重设计身份存储：tenant 作用域身份映射、Login 接管、Resume 轮换幂等，数据库集成测试通过（`be84f99`）。
- [x] M1 证据登记（`be84f99`）：`docs/architecture/redesign-m1-implementation.md` 记录身份持久化基础已落地，StreamerServer 宿主、平台验证 API 与挂载流程未实现，M1 进行中。
- [x] Release 全量 1152/1152，Release solution build 0 errors，win-x64 交叉编译 0 errors（`087fc4a`）。
- [x] 开发平台模拟器（M0 交付件之一，`4c8e898`）：`RedesignPlatformGateway` VerifyLaunch 按平台路由 + `DevelopmentPlatformSimulator` HMAC 凭据签发/验证 + Douyin/Kuaishou `NotProductionReady` 失败关闭桩；契约测试 5/5，全量 1157/1157。真实平台适配器仍属 M1 且保持 NotProductionReady。
- [x] 双 OS 可复现安装配置（`8a0a116`）：`deploy/linux/` systemd 单元（systemd-analyze verify 通过）+ 安装脚本（shellcheck 零告警；受限账号/0700 状态目录/占位符 dotnet 路径）+ `deploy/windows/` 服务安装脚本（PowerShell 解析通过，随机服务密码不落盘）；实机安装与启动验收保持 NOT RUN。
- [x] M1 StreamerServer 登录/恢复宿主（`a72b447`）：StreamerSession/StreamerIdentity 双 gRPC 边界（tenant 入请求并对照 auth.identity 归属校验、Resume 先栅栏后轮换）、协调器次序（VerifyLaunch→Resolve→实体加载→Login，实体未就绪不发 LoginReady 且不建会话）、HMAC 短时访问凭据、DataServer 侧幂等 profile 初始化；真实三宿主集成 + 协调器单测 5/5，Release 全量 1163/1163。Attach generation、access credential 消费端接线、StreamerGateway 宿主与 AT01–AT09 仍 open。
- [x] M1 Attach generation 与 access credential 消费端校验（`1cfc431`）：迁移 0005（关闭标记 + attach 结果代）、AttachConnection 以当前 secret 幂等递增 ConnectionGeneration 不轮换、关闭通知仅当前 gateway/connection/代三者一致才生效（旧代不误标离线）、GetSessionState 事实快照、StreamerSession.ValidateAccess 消费端校验（签名+当前代+租户一致，离线是事实非拒答）；store/宿主/协调器三级测试，Release 全量 1167/1167。StreamerGateway 宿主、Logout 与 AT01–AT09 仍 open。
- [x] M1 GameplayBinding 唯一挂载与跨 GameId LiveSlot 冲突事务（`3dca091`）：迁移 0006（直播会话挂规范直播键 + 部分唯一索引）、OpenLiveSession 幂等注册且规范键不得改绑他租户/他主播、槽行锁内单事务完成会话代栅栏→关旧挂载→新挂载代+1→槽指向（操作幂等键为直播槽作用域，同 OperationId 换 GameId 是冲突）、StartGameplay/GameplayStatus 网关面走 access credential；store/宿主/协调器三级测试，Release 全量 1170/1170。
- [x] M1 StreamerActor 内存状态机与切换途中重启恢复（`4726f3b`）：Loading→Ready→RetainingOffline→Deactivating/RecoveryFailed（架构 5.3），连接/会话/直播/挂载事实独立观测不合成单一 online 布尔，数据库权威、注册表仅投影；Login 在会话提交前执行实体恢复，恢复失败无 LoginReady 且无会话；`crashAfterClosingOldBinding` 注入证明切换途中崩溃回滚后旧挂载唯一活跃、同 OperationId 重试收敛；真实 StreamerServer 进程 Kill+重启后重新登录找回同一挂载并可继续切换（唯一活跃断言）。
- [x] M1 平台事件持久接收与 UnresolvedInbox（`0a82bf1`）：迁移 0007（`input.event` 规范房间键 + durability + PendingAttribution 索引）、`RedesignInputEventStore` 整批单回执事务（同键同哈希重复返回原归属、同键异载荷可见冲突整批回滚、Durable 查不到挂载入收件箱不伪造归属、BestEffort 落库即弃）、归属按首次持久接收冻结（切换后迟到重复仍属旧代）、`ReconcilePendingAsync` 挂载出现后一条语句对账且 received_at 不改写、`m1.proto` PlatformIngest（platform-gateway 角色）；`crashAfterPersist` 注入批中途死亡全回滚后同操作重试收敛；真实三进程 + DataServer Kill/重启后原回执仍在、streamer-server 角色被拒；Release 全量 1180/1180。
- [x] M1 PlatformTask 租约协调与未知结果处置（`d60d0d8`）：迁移 0008（attempt/last_error/租约四列 + 同挂载 Executing 部分唯一索引串行）、`RedesignPlatformTaskStore`（Enqueue 按 binding+operation 幂等；AcquireLease 重分配 bump epoch 不动在途 attempt；BeginAttempt 租约/退避/状态三重栅栏且同事务先写 Outbox 再许外呼；超时→OutcomeUnknown 禁盲重试；不可幂等不可查询→NeedsAttention 终态；ResolveOutcome 允许新租约持有者在旧 attempt 截止时间过后按官方查询收敛 Completed/Retryable）、`m1.proto` PlatformTask（期望/重分配属控制面、attempt 属 platform-gateway、NeedsAttention 仅控制面；宿主测试抓到 Enqueue/AcquireLease 漏角色检查已修）；store 3 例 + 真实三进程 DataServer Kill/重启重分配收敛（AT09 存储与边界语义就绪）；Release 全量 1184/1184。下一项：第 5 步 AT01–AT09 逐项专项（真实平台部分仍阻塞于官方协议证据）。
- [x] M1 AT01–AT09 逐项台账与 AT03 响应丢失补齐（`ec0b6d9`）：台账登记于 `docs/architecture/redesign-m1-implementation.md`（模拟器侧通过 AT01/03/04/07/08/09，AT04 跨应用直播键互斥未验证不折算，AT05/08 对局归属半边随 AT05，AT02 阻塞于真实协议样本、AT06 阻塞于 M2 对局建模，均单列不伪装通过；适配器保持 NotProductionReady）；宿主测试 `RotationResponseLossAcrossRestartKeepsSingleRotationAndFencesOldCommands`（双终端接管→旧终端晚到命令拒绝→轮换提交 v3→StreamerServer Kill+重启→精确重试 Duplicate 同一会话 v3 不二次轮换、同操作改请求/旧密钥均拒、新终端 attach 后旧代关闭不误标离线、`auth.rotation` 仅一行、接管后重试窗口 FailedPrecondition）；Release 全量 1185/1185。下一项：M2（对局/房间建模与 RoundGeneration，AT06 与对局归属随之）。
  历史基线：2026-10-27 登记时新方案只有设计文件（`223e5c4`），没有新角色代码；用户本轮已明确授权续做。后续逐项以验收计划 `docs/architecture/redesign-acceptance.md` 为准，不以文档或协议类型存在宣称阶段通过。
- [x] M2-1 局准入、RoundGeneration 与事件按局路由（`12d6671`）：迁移 0009（`live.binding.round_generation`、`input.event.attributed_room_id` 持久局归属证据、AmbiguousRound 审查索引、Pending 收件箱索引）；`RedesignRoomStore`（StartRound 一个回执事务：ownership 栅栏→排序绑定校验（跨租户/在局/离线占位兜底）→房间行→participation→指针+代数+1→outbox，crashAfterPointerUpdate 注入缝；AbortRound 释放同集合、所有权 Released+epoch+1、abort 后再 abort 被释放栅栏拒绝）；`RedesignRoundInputStore`（RouteEvents 四趟：round_hint 可信才用否则 AmbiguousRound、无提示走当前局、终态 OutOfRound 终态可见、cursor 行锁稠密 sequence、route_version=round_generation、水位不动留给 M2-2；AcceptClientInput 回执键 commandId、会话/参与/epoch 校验链、`@chorus` Durable 事件+同锁 delivery）；PlatformEventReceipt 增 RoomId（重复接收回放原局归属）；`m2.proto` RoundStore/RoundInput + 双 gRPC 服务（准入←control/game-server、路由←gateway/game/control、客户端命令←streamer/game-server，证书定租户）；store 5 例 + 宿主 2 例（真实三进程 DataServer Kill+重启回执重放、角色/跨租户双拒、Abort 后命令拒绝三方一致）；AT05/08 对局归属半边与 AT06 房间模型半边闭合（M2-1 不转 Running、不做帧/快照/结算）；Release 全量 1192/1192，测试库扩族 `chorus_m2_%`。
- [x] M2-2 FrameStore 权威帧提交与对局收件箱（`2d8647e`）：`RedesignFrameStore.CommitFrameAsync`（回执键 frame.commit/roomId/commitId；§6.5 锁序 ownership→room FOR UPDATE（须 Running 且 prev_tick/prev_state_version 乐观相符否则 FrameConflictException 可见拒）→输入引用须本局 Pending delivery（重复引用/他帧已消费/外部引用三拒）→标 Consumed→INSERT game.frame（commit_id 唯一、input_refs 保序）→FullCheckpoint 分支同事务写 game.snapshot（失败整帧回滚不降级）→水位 GREATEST 只跨连续已消费前缀绝不越洞→room last_tick/state_version+1/state_hash→Effects 逐条 outbox（effect_id={roomId}/{tick}/{index}）→final 帧同事务写 game.result+settlement.grant(Planned)+房间 Finishing；crashAfterFrameInsert 注入缝）；ReadInboxAsync（Pending 按序号带 payload/route_version+连续水位）；`m2.proto` FrameStore 服务（CommitFrame 仅 game-server、ReadInbox 加 control-server 诊断）；store 3 例（幂等/栅栏/水位不越洞/三拒；FullCheckpoint 成功与 23505 整帧回滚后同 CommitId 干净重试；final 帧 result+grant+Finishing 后拒再提交；崩溃注入全量回滚收敛）+ 宿主 1 例（真实三进程 ReadInbox→CommitFrame tick1→Kill+重启同 CommitId Duplicate、控制面可读、角色双拒）；房间 Running 激活属 M2-3 测试以 SQL 直置；AT14/AT27 帧部分闭合；Release 全量 1196/1196，测试库族 chorus_m2_frames_。下一项：M2-3 §7 示例玩法 + Chorus.GameServer 宿主 + RoomActor tick + Prepare/激活（AT13 隔离半边）。
- [x] M2-3 示例玩法 + Chorus.GameServer 宿主 + RoomActor（`1a61bc0`）：新 `Chorus.Redesign.ExampleGame`（ExampleGameModule 双阵营计分：CreateInitialState 校验阵营/去重、纯 ApplyTick（LCG 种子每 tick 恰推进一次随状态携带、gift/boost 计分、未知形状与未知参与者确定性忽略、DurationTicks 到点冻结排名并列 draw）、Serialize 规范 JSON、Restore 仅认 schema v1 fail-closed、ComputeStateHash=SHA256(Serialize)、OnHostUnavailable 空控制输入、manifest Default 50ms/快照每 100 tick/DeterministicReplay+FastTest 25ms、FailAfterTick 确定性故障缝）；`RedesignRoomStore` 生命周期（PrepareRound：回执承诺快照哈希+schema、仅 Preparing 写 tick-0 snapshot verified=false；ActivateRound：须 Preparing 且有 tick-0 快照否则拒、→Running state_version=1、outbox activate；SuspendRound：仅 Running→Suspended+outbox suspend；AbortRound 扩受 Suspended 逃生；三者栅栏+幂等）；`m2.proto` RoundStore 三方法 + 新 GameHost 服务（PrepareRoom←control/game-server、RecoverRoom→Unimplemented 留 M2-4）；新 `Chorus.GameServer`（StreamerServer 骨架、端口 7503、无 DB 凭据；@cluster 服务证书+独立租户客户端证书 CHORUS_CLIENT_CERT_PEM 走数据面；RoomRuntime 单 channel+注册表、PrepareRoom=初始态→Prepare→Activate→启动 actor、同房重入 Duplicate；RoomActor PeriodicTimer 循环 ReadInbox→全引用入帧/已知绑定映射输入→ApplyTick→CommitFrame（commitId={roomId}/tick-{tick} 幂等锚、tick%100 FullCheckpoint、final 帧 result+PlanHash）、未知结果同 commitId 重放、明确拒绝或模块连续 3 败→Suspend 可见挂起停 actor 他房继续）；HostEnvironment 增 GamePort/game-server 就绪探针/clientTenant 双证书；模块 3 例 + store 1 例（lifecycle 各状态门/幂等/冲突/双栅栏/挂起后拒帧/abort 逃生）+ 宿主 1 例（真实四进程：健康房 40 tick 跑到 Finishing（40 帧/投递全消费/水位 2/result winner=red/恰 2 条 score 效果）与故障房 FailAfterTick=2 同进程 Suspended——AT13 隔离半边；重复 PrepareRoom Duplicate、streamer 角色拒、RecoverRoom Unimplemented）；AT13 隔离半边+AT14/27 帧链推进；Release 全量 1201/1201，测试库族 chorus_m2_lifecycle_。下一项：M2-4 快照与恢复（PutSnapshot/LoadRecovery、确定性重放、坏快照回退，AT19）。
- [x] M2-4 快照与恢复（`409f677`）：新 `RedesignRecoveryStore`（PutSnapshotAsync：回执 snapshot.put canonical {RoomId,Tick,Hash,SchemaVersion}、store 自验 SHA256 哈希不符不入库、锁序 ownership→room FOR UPDATE 须 Running 且 tick==last_tick 只钉头部、plain INSERT verified=true 故意不 upsert（同 tick 异快照以约束冲突暴露）、crashAfterSnapshotInsert 半写注入缝（回滚→无行无回执）；LoadRecoveryAsync 只读恢复计划一次成读：房间头+全快照 newest-first 含 verified+投递日志按 sequence（event_id/binding/payload/consumed）+最旧快照后帧（input_refs 保序/state_hash/module_version/config_hash）+参与映射、未知房 null）；`m2.proto` FrameStore 增 PutSnapshot（←game-server）/LoadRecovery（←game-server+control-server）、GameHost.RecoverRoom 转正（Reply 增 status/last_tick/snapshot_tick/replayed_frames）；`RoomRuntime.RecoverRoomAsync`：注册表命中——已终局 actor（Completed）答 Finishing 尾、在跑 actor 答 already running 均不重启 tick；未知房/epoch 不符/非 Running 可见拒；Finishing 直接 Recovered=true；Running 重放——快照 newest→oldest（Restore 拒识或哈希失配跳过）→其后帧逐个 tick 连续+InputRef 经投递映射回参与者+ApplyTick 重算+帧哈希比对到头→PutSnapshot 钉 store-verified 头部（确定性 op id {owner}/snapshot/{room}/{tick}，被拒 Recovered=false）→actor 自 (state,lastTick,stateVersion) 续 tick；全候选失败→SuspendRoom 可见挂起；重放只读不重触发效果；`RoomActor` ctor 增 lastTick/stateVersion 恢复锚+final 提交后 Completed 标志；store 2 例（半写不可见+同 op 收敛单行 verified+哈希不符拒+非头部 tick/挂起房 FrameConflict+错实例 StaleOwner+同 op 异内容 IdempotencyConflict；计划全形状+未知房 null）+ 宿主 2 例（Kill 后同端口重启 RecoverRoom：SnapshotTick=100/ReplayedFrames=head−100/头部 verified 快照/续跑 200 帧到 Finishing 且与未受扰孪生房 result 逐字节相等——崩溃+恢复不移动确定性结局，快照链恰 {0 false,100 false,head true,200 false}；坏哈希失配回退 tick-0 全量重放仍完赛、唯一锚 payload 损毁→Recovered=false+Suspended+round.suspended outbox+帧冻结）；M2-3 例 RecoverRoom 断言转 Finishing 尾（帧数仍 40）；AT19 闭合（四条验收词全落地）+AT14/27 帧链恢复互证推进；Release 全量 1205/1205，测试库族 chorus_m2_recovery_。下一项：M2-5 结算 §6.4+FinalizeRound §6.6+排行榜投影+默认资产库存守卫（AT18、AT20、AT27 结算部分）。
- [x] M2-5 结算、FinalizeRound 与排行榜（`04f3944`）：迁移 0010（settlement.grant 增 asset_id/delta、投影表 game.leaderboard_score + score DESC/player_id ASC 排名索引）；`RedesignFrameStore` PlannedGrant 扩 AssetId/Delta 入指纹与 final 帧事务；`RoomActor` 终局清单自模块状态推导（ResultJson ranking×FactionByParticipant 按玩家序、GrantKey round-score、OperationId {roomId}/grant/{player}、仅正分；阵营随快照→恢复 actor 同清单；final 提交后即 Settle、Finishing 恢复尾先 Settle）；新 `RedesignSettlementStore`（CommitResultAsync 逐 grant 独立回执事务：种默认资产 ON CONFLICT→UPDATE balance+delta WHERE balance+delta>=0 RETURNING 打负可见拒→账本恰一行→Granted→排行榜投影 upsert；第 N 笔崩溃只回滚该笔、重试只补未完成、全清重复 Duplicate；crashAfterGrant 缝。FinalizeRoundAsync 回执 round.finalize：Finishing/result 存在/清单全 Granted/Pending 投递清零四前置全可见拒→单事务 participation Closed→有序清 current_room_id→Settled→所有权 Released epoch+1（不校验 owner，终结者不持所有权）→outbox {roomId}/0/settle；重复 Duplicate、再终局拒。ReadLeaderboardAsync score DESC player_id ASC LIMIT）。`m2.proto` Settlement 服务（Ready/CommitResult/FinalizeRound/ReadLeaderboard 角色面；请求带 tenant_id——@cluster 控制面必须指名租户、租户证书指名他租户 PermissionDenied 无跨租户穿透）；store 4 例（第 2 笔崩溃→p1 Granted p2/p3 Planned 账本 1 行、重试收敛 3 Granted 每笔恰一行余额/榜单一致、异 op Duplicate、未知房拒；FinalizeRound 栅栏族+Settled/Closed/指针 NULL/Released epoch+1/outbox+同绑定下一代 round_generation=2；profile.patch 声称 points:999999 资产仍 100 账本仍 1 行；双房并行结算同批玩家账户行串行化余额=和榜单=和）+ 宿主 1 例（真实四进程对局到 Finishing→自动结算 grant 全 Granted/账本 2 行/余额 100/30/控制面读榜单确定性序→Kill+重启 DataServer 重发结果 Duplicate 账本不增→控制面 @cluster act-as-tenant FinalizeRound Settled/epoch+1/参与 Closed/指针释放/所有权 Released→同绑定 room-m2-11 开局跑到 Finishing round_generation=2→他租户指名与 streamer 角色双 PermissionDenied）；AT18 闭合、AT20 闭合、AT27 单节点全项落地；Release 全量 1210/1210，测试库族 chorus_m2_settlement_。下一项：M2-6 客户端同步协议（Subscribe：Snapshot/Delta/CommandReceipt/RoundResult/ResyncRequired）+ 双主播示例（AT15、AT06 重连半边）。
- [x] M2-6 客户端同步协议（`bec1ab6`）：`m2.proto` 新 RoundSync 服务（GameServer，Subscribe 流；请求带 room/game/epoch+已应用基线 (last_applied_tick, last_applied_state_version)；SyncMessage oneof {Snapshot,Delta,RoundResult,ResyncRequired} 全部携带房间身份 room/epoch/tick/state_version/module_version/config_hash；Delta 含 base_version（客户端拒收基线未应用）+state_hash+input_event_ids+effects+命令回执；命令走 input.event 平台 @chorus、dedup key=command id（InboxItemProto 增 platform/source_dedup_key））；`RoomSubscriber` 有界队列容量 64——溢出清积破坏 NeedsSnapshot 且丢弃触发溢出的 delta（前驱已丢它即不连续）、终态消息永不丢；`RoomActor` _publishGate 单锁内推进头部三元组并扇出（订阅者只见一致已提交三元组）、NeedsSnapshot 者改收头部全量快照、final 帧先补快照再 result+Complete；Subscribe() 终局房→最终快照+冻结 result+Complete、基线落后→头部快照、基线在头→纯 delta；`RoundSyncGrpcService` 仅 streamer/control、未知房 NotFound、epoch 失配→单条 ResyncRequired(current_epoch) 收流；**活性收口（本轮排障）**：RunAsync finally——循环无论因何结束，未提交 result 的房对全部在册订阅者 ResyncRequired+Complete、_loopEnded 拒晚到注册；根因链=60 秒租约上限被工作受限长房越过→过期租约把 CommitFrame 以 stale-owner FailedPrecondition 拒→SuspendRound 撞同栅栏被 RoomRuntime 吞→循环退出订阅者永等（堆转储证据：任务 RanToCompletion、订阅者 2、房 Running、头 486/487、零异常对象）；修复=finally 收口+commit 拒绝路径 stderr 证据行+控制面续租（架构 §4.2 ControlServer 授予并续租；测试 RenewableClaim 每 20s 同 owner 重 claim、epoch 不变仅延长，生产续租属 M3）；HostEnvironment 增 CHORUS_TEST_DUMP_HOST_OUTPUT 逐宿主转储开关；宿主 3 例（慢订阅者重基线：600 tick@5ms 工作受限房、快者恰 1 快照+无缝链到 600/601、慢者溢出重基线≥2 次同 result 房不等它；断线重连：过期 epoch 恰一条 ResyncRequired、读到 tick≥12 断开房继续、携基线重连首条头部快照无缝到 141/140、流式与 DB result 结构一致、晚到者终局快照+result；命令回执：恰一条 delta 带 cmd-boost-1 回执、重发 Duplicate 不二回执、red 130-134 blue 30）；AT15 闭合、AT06 重连半边闭合；Release 全量 1213/1213，测试库族沿用 chorus_m0_host_。下一项：M2-6b 双主播控制台示例 `Chorus.RoundDemo`+RoundDemoScenarioTests。
