[0m
[0m✱ [0mGlob "**/*" [90m300 matches[0m
[0m$ [0mgit status --short && git ls-files | wc -l
412
[0m✱ [0mGrep "TODO|todo|待办|已完成|未完成" [90min . · 200 matches[0m
[0m→ [0mRead todo.md [90m[offset=145, limit=130][0m
[0m$ [0mfind src -name '*.cs' -not -path '*/obj/*' | sort
src/Chorus.Adapters.Abstractions/IAudienceFactionRule.cs
src/Chorus.Adapters.Abstractions/IPlatformEventMapper.cs
src/Chorus.Adapters.Abstractions/IPlatformEventVerifier.cs
src/Chorus.Adapters.Abstractions/IPlatformNamedEventVerifier.cs
src/Chorus.Adapters.Abstractions/KeywordGiftAudienceFactionRule.cs
src/Chorus.Adapters.Platforms/AllowAllPlatformEventVerifier.cs
src/Chorus.Adapters.Platforms/Bilibili/BilibiliIngressBridgeHostedService.cs
src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveClient.cs
src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveProtocol.cs
src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventMapper.cs
src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventVerifier.cs
src/Chorus.Adapters.Platforms/Bilibili/BilibiliSigner.cs
src/Chorus.Adapters.Platforms/Douyin/DouyinAccessTokenProvider.cs
src/Chorus.Adapters.Platforms/Douyin/DouyinMiniGameAuthClient.cs
src/Chorus.Adapters.Platforms/Douyin/DouyinMiniGameOptions.cs
src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventMapper.cs
src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventVerifier.cs
src/Chorus.Adapters.Platforms/Douyin/DouyinRankUploadAdapter.cs
src/Chorus.Adapters.Platforms/EventDeduplicator.cs
src/Chorus.Adapters.Platforms/Kuaishou/KuaishouPlatformEventMapper.cs
src/Chorus.Adapters.Platforms/Kuaishou/KuaishouPlatformEventVerifier.cs
src/Chorus.Adapters.Platforms/MiniGame/MiniGameIdentityBinder.cs
src/Chorus.Adapters.Platforms/MiniGame/MiniGameLeaderboardProjector.cs
src/Chorus.Adapters.Platforms/MiniGame/MiniGameLoginExchange.cs
src/Chorus.Adapters.Platforms/MiniGame/MiniGamePlatformCapabilities.cs
src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs
src/Chorus.Adapters.Platforms/PlatformNames.cs
src/Chorus.Adapters.Platforms/TestPlatformEventMapper.cs
src/Chorus.Client/ChorusClient.cs
src/Chorus.Client/FramePlayer.cs
src/Chorus.Control/Contracts.cs
src/Chorus.Control/ControlClient.cs
src/Chorus.Control/ControlDatabase.cs
src/Chorus.Control/ControlGatewayBus.cs
src/Chorus.Control/ControlRoutingSnapshot.cs
src/Chorus.Control/ControlTickDurability.cs
src/Chorus.ControlHost/ControlMaintenance.cs
src/Chorus.ControlHost/Program.cs
src/Chorus.Control/MatchControlStore.cs
src/Chorus.Control/ProcessResourceSampler.cs
src/Chorus.Control/RoomControlStore.cs
src/Chorus.Data/AnalyticsPipeline.cs
src/Chorus.Data/DataCommand.cs
src/Chorus.Data/DataCommandIdempotencyTracker.cs
src/Chorus.Data/DataCommandOutbox.cs
src/Chorus.Data/GameRecord.cs
src/Chorus.Data/Leaderboard.cs
src/Chorus.Data/PlatformLeaderboard.cs
src/Chorus.Data/PlayerRecord.cs
src/Chorus.Data/StateSnapshot.cs
src/Chorus.Engine/Abstractions/IGameModule.cs
src/Chorus.Engine/Abstractions/IGameModuleFactory.cs
src/Chorus.Engine/Abstractions/ISessionScoreSink.cs
src/Chorus.Engine/Abstractions/SnapshotRecovery.cs
src/Chorus.EngineHost/BroadcastOwnerRegistry.cs
src/Chorus.EngineHost/ControlCheckpointRestorer.cs
src/Chorus.EngineHost/EngineBusSessionClient.cs
src/Chorus.EngineHost/EngineControl.cs
src/Chorus.EngineHost/EngineIpcService.cs
src/Chorus.EngineHost/ProcessCpuGauge.cs
src/Chorus.EngineHost/Program.cs
src/Chorus.EngineHost/ProgramMarker.cs
src/Chorus.EngineHost/SweepGate.cs
src/Chorus.EngineHost/WelcomePublisher.cs
src/Chorus.Engine/Matchmaking/BotRuntime.cs
src/Chorus.Engine/Matchmaking/BotStrategy.cs
src/Chorus.Engine/Matchmaking/Matchmaker.cs
src/Chorus.Engine/Routing/PlatformBindingRegistry.cs
src/Chorus.Engine/Routing/PlatformCapabilityRegistry.cs
src/Chorus.Engine/Routing/PlatformIdentityRegistry.cs
src/Chorus.Engine/Routing/RoomAffinity.cs
src/Chorus.Engine/Routing/RoomAuthorityRegistry.cs
src/Chorus.Engine/Routing/RoomLifecycle.cs
src/Chorus.Engine/Routing/WorkerRegistry.cs
src/Chorus.Engine/Session/DefaultAudienceEventReducer.cs
src/Chorus.Engine/Session/FrameHistory.cs
src/Chorus.Engine/Session/GameModuleRestorer.cs
src/Chorus.Engine/Session/IAudienceEventReducer.cs
src/Chorus.Engine/Session/ISessionClient.cs
src/Chorus.Engine/Session/ISessionTickDurability.cs
src/Chorus.Engine/Session/OutboundClientQueue.cs
src/Chorus.Engine/Session/SessionActor.cs
src/Chorus.Engine/Session/SessionCapacityException.cs
src/Chorus.Engine/Session/SessionClientState.cs
src/Chorus.Engine/Session/SessionConfig.cs
src/Chorus.Engine/Session/SessionDiagnostics.cs
src/Chorus.Engine/Session/SessionRegistry.cs
src/Chorus.Engine/Session/SessionSnapshot.cs
src/Chorus.Engine/Session/WireSnapshot.cs
src/Chorus.Gateway.Core/Actors/SessionActorRef.cs
src/Chorus.Gateway.Core/Faces/FaceCounters.cs
src/Chorus.Gateway.Core/Faces/IngressFaceGuard.cs
src/Chorus.Gateway.Core/Faces/IngressFaceOptions.cs
src/Chorus.Gateway.Core/Faces/IngressRateLimiter.cs
src/Chorus.Gateway.Core/Faces/MatchRegistry.cs
src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs
src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs
src/Chorus.Gateway.Core/Faces/StreamerWsFaceOptions.cs
src/Chorus.Gateway.Core/Leaderboard/LeaderboardHost.cs
src/Chorus.Gateway.Core/Metrics/ChorusMetricsText.cs
src/Chorus.Gateway.Core/Metrics/SessionRegistryCounters.cs
src/Chorus.Gateway.Core/WebSocketHelpers.cs
src/Chorus.Gateway/GatewayClientConnection.cs
src/Chorus.Gateway/GatewayControl.cs
src/Chorus.Gateway/GatewayIpcService.cs
src/Chorus.Gateway/GatewayRoles.cs
src/Chorus.Gateway/IpcSessionActorRouter.cs
src/Chorus.Gateway/IpcSessionRouter.cs
src/Chorus.Gateway/Program.cs
src/Chorus.Gateway/ProgramMarker.cs
src/Chorus.LoadTool/ClientSimulation.cs
src/Chorus.LoadTool/EventPayloadFactory.cs
src/Chorus.LoadTool/IngressDiagnostics.cs
src/Chorus.LoadTool/IngressDriver.cs
src/Chorus.LoadTool/IngressSigner.cs
src/Chorus.LoadTool/LoadToolOptions.cs
src/Chorus.LoadTool/MetricsPeaks.cs
src/Chorus.LoadTool/MultiProcessDriver.cs
src/Chorus.LoadTool/Program.cs
src/Chorus.LoadTool/RatePacer.cs
src/Chorus.Messaging/Envelope.cs
src/Chorus.Messaging/FanOutMessageBus.cs
src/Chorus.Messaging/IMessageBus.cs
src/Chorus.Messaging/Ipc/EnvelopeFactory.cs
src/Chorus.Messaging/Ipc/GatewayCommands.cs
src/Chorus.Messaging/Ipc/IpcDuplexEndpoints.cs
src/Chorus.Messaging/Ipc/IpcEndpointBuilder.cs
src/Chorus.Messaging/Ipc/IpcEndpoint.cs
src/Chorus.Messaging/Ipc/IpcMessageBusFactory.cs
src/Chorus.Messaging/Ipc/IpcMessageTypes.cs
src/Chorus.Messaging/Ipc/IpcNetworkEndpoint.cs
src/Chorus.Messaging/Ipc/IpcSettings.cs
src/Chorus.Messaging/Ipc/IpcTransport.cs
src/Chorus.Messaging/JsonEnvelopeCodec.cs
src/Chorus.Messaging/Transports/FrameReader.cs
src/Chorus.Messaging/Transports/HttpMessageBus.cs
src/Chorus.Messaging/Transports/InProcMessageBus.cs
src/Chorus.Messaging/Transports/LengthPrefixedStream.cs
src/Chorus.Messaging/Transports/NamedPipeMessageBus.cs
src/Chorus.Messaging/Transports/TcpMessageBus.cs
src/Chorus.Messaging/Transports/UnixDomainSocketMessageBus.cs
src/Chorus.Protocol/Messages/ClientAck.cs
src/Chorus.Protocol/Messages/ClientHello.cs
src/Chorus.Protocol/Messages/ClientResync.cs
src/Chorus.Protocol/Messages/FrameEvent.cs
src/Chorus.Protocol/Messages/ServerError.cs
src/Chorus.Protocol/Messages/ServerPong.cs
src/Chorus.Protocol/Messages/ServerSnapshot.cs
src/Chorus.Protocol/Messages/ServerWelcome.cs
src/Chorus.Protocol/Models/AudienceEvent.cs
src/Chorus.Protocol/Models/MatchStateSnapshot.cs
src/Chorus.Protocol/ProtocolJson.cs
src/Chorus.Protocol/ProtocolTypes.cs
src/Chorus.Protocol/ProtocolVersionPolicy.cs
src/Chorus.SampleGame/MatchStateStore.cs
src/Chorus.SampleGame/SampleGameModule.cs
src/Chorus.SampleGame/SampleGameModuleFactory.cs
src/Chorus.Server/InProcSessionActorRouter.cs
src/Chorus.Server/MonolithSessionRouter.cs
src/Chorus.Server/Program.cs
src/Chorus.Server/ProgramMarker.cs
src/Chorus.Server/SessionManager.cs
src/Chorus.Server/WsSessionClient.cs
[0m→ [0mRead docs/architecture/multi-machine-rooms.md 
[0m→ [0mRead Chorus.sln [90m[limit=200][0m
[0m→ [0mRead src/Chorus.ControlHost/Program.cs [90m[limit=450][0m
[0m→ [0mRead src/Chorus.Control/RoomControlStore.cs [90m[limit=350][0m
[0m→ [0mRead src/Chorus.EngineHost/EngineControl.cs [90m[limit=300][0m
[0m→ [0mRead src/Chorus.Gateway/GatewayControl.cs [90m[limit=250][0m
[0m→ [0mRead todo.md [90m[offset=270, limit=180][0m
[0m✱ [0mGrep "\[ \]" [90min todo.md · 8 matches[0m
[0m→ [0mRead src/Chorus.Control/ControlDatabase.cs [90m[limit=300][0m
[0m✱ [0mGlob "src/Chorus.ControlHost/wwwroot/**/*" [90m3 matches[0m
[0m→ [0mRead src/Chorus.EngineHost/EngineIpcService.cs [90m[offset=1, limit=380][0m
[0m→ [0mRead src/Chorus.Control/ControlTickDurability.cs [90m[limit=250][0m
[0m→ [0mRead src/Chorus.Control/Contracts.cs [90m[limit=300][0m
[0m→ [0mRead src/Chorus.EngineHost/ControlCheckpointRestorer.cs [90m[limit=160][0m
[0m→ [0mRead src/Chorus.Engine/Session/SessionActor.cs [90m[offset=180, limit=180][0m
[0m→ [0mRead src/Chorus.ControlHost/ControlMaintenance.cs [90m[limit=220][0m
[0m→ [0mRead src/Chorus.Control/ControlClient.cs [90m[limit=170][0m
[0m→ [0mRead src/Chorus.Gateway/GatewayIpcService.cs [90m[offset=110, limit=180][0m
[0m→ [0mRead src/Chorus.Engine/Session/SessionActor.cs [90m[offset=450, limit=190][0m
[0m→ [0mRead src/Chorus.ControlHost/wwwroot/admin.js [90m[limit=400][0m
[0m→ [0mRead src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventVerifier.cs [90m[limit=140][0m
[0m→ [0mRead src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventMapper.cs [90m[limit=130][0m
[0m→ [0mRead src/Chorus.Adapters.Platforms/Kuaishou/KuaishouPlatformEventVerifier.cs [90m[limit=130][0m
[0m→ [0mRead src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventMapper.cs [90m[limit=220][0m
[0m→ [0mRead src/Chorus.Engine/Session/DefaultAudienceEventReducer.cs [90m[limit=180][0m
[0m→ [0mRead src/Chorus.SampleGame/SampleGameModule.cs [90m[limit=220][0m
[0m→ [0mRead src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs [90m[limit=240][0m
[0m→ [0mRead src/Chorus.Protocol/Models/AudienceEvent.cs [90m[limit=100][0m
[0m→ [0mRead docs/guide/matchmaking.md [90m[offset=1, limit=140][0m
[0m→ [0mRead docs/design/process-topology.md [90m[offset=25, limit=65][0m
[0m→ [0mRead src/Chorus.Control/ControlRoutingSnapshot.cs [90m[limit=220][0m
[0m→ [0mRead src/Chorus.Control/ControlGatewayBus.cs [90m[limit=220][0m
[0m✱ [0mGrep "TLS|HTTPS|https|reverse proxy|反向代理|Control:Url|Control:Key|SecurePolicy" [90min docs · 87 matches[0m
[0m→ [0mRead src/Chorus.ControlHost/wwwroot/index.html [90m[limit=220][0m
[0m→ [0mRead src/Chorus.Control/ControlDatabase.cs [90m[offset=1, limit=32][0m
[0m→ [0mRead src/Chorus.Control/MatchControlStore.cs [90m[limit=260][0m
[0m→ [0mRead src/Chorus.Engine/Routing/RoomLifecycle.cs [90m[limit=180][0m
[0m→ [0mRead docs/reference/deployment.md [90m[offset=100, limit=60][0m
[0m→ [0mRead src/Chorus.Engine/Session/SessionRegistry.cs [90m[limit=260][0m
[0m✱ [0mGrep "DELETE FROM audit|audit.*prun|Prune.*Audit|Audits\(" [90min src · 3 matches[0m
[0m✱ [0mGrep "_controlChannels|DisposeAsync\(\).*control|_outgoingByWorker" [90min src/Chorus.Gateway · 9 matches[0m
### 表一 缺陷（按风险排序）

| 问题 | file:line | 为什么是问题 | 建议动作 |
|---|---|---|---|
| **[高] 反向代理 TLS 部署可能拒绝所有控制台 POST** | `src/Chorus.ControlHost/Program.cs:46-47,53`；`docs/reference/deployment.md:125` | Origin 校验使用 `Request.Scheme`，但中间件管线没有配置 Forwarded Headers。TLS 在代理终止时，应用可能看到 `http`，浏览器发送的 `https://…` Origin 因而不匹配，登录和管理操作返回 403；`SameAsRequest` 也可能因此不给 Cookie 设置 Secure。 | 在 Origin 校验前配置受信任代理的 Forwarded Headers，或校验明确配置的公网 Origin；生产 Cookie 强制 Secure。 |
| **[高] ACK／resync 热路径每次都访问控制面并争抢 SQLite 写锁** | `src/Chorus.Gateway/GatewayIpcService.cs:160-167`；`src/Chorus.Gateway/GatewayControl.cs:50-67`；`src/Chorus.Control/ControlClient.cs:36-38`；`src/Chorus.Control/RoomControlStore.cs:76` | ACK 和 resync 进入 `PublishControlledAsync` 后，每次都执行 `Assign` 和查询 `/nodes`；即使房间已存在，`Assign` 仍通过 `database.Write` 开启写事务。高频客户端消息会与逐 tick checkpoint 写入争用同一 SQLite 写者，可能放大延迟或导致提交超时。 | 高频消息使用本地新鲜路由快照和缓存的 Worker 通道；只在连接登记、可靠事件提交等确需时访问控制面。 |
| **[中高] 审计表无保留或清理机制** | `src/Chorus.Control/ControlDatabase.cs:28,141-145`；`src/Chorus.ControlHost/ControlMaintenance.cs:16-23` | 审计记录只追加；维护循环没有清理审计表。长期运行会持续增加数据库/WAL 占用；磁盘耗尽会影响共享该数据库的可靠事件和 tick checkpoint 写入。 | 定义审计保留期与归档/清理策略，增加数据库与 WAL 容量告警，并确保清理不删除合规要求保留的记录。 |
| **[中] B 站开播／下播事件只归一化，不驱动房间生命周期** | `src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventMapper.cs:103-105`；`src/Chorus.SampleGame/SampleGameModule.cs:43-70` | Mapper 产出 `RoomLifecycle` 事件，但 SampleGame 只处理 Like、Gift、Comment；该事件不会关闭、休眠或更新 ControlHost 房间状态，仍可能被正常接收并确认。 | 明确这类事件仅作信息记录，或接入显式、幂等且经授权的房间生命周期处理流程。 |

### 表二 文档与实现不一致

| 文档说法 | 文档位置 | 代码实际 | 代码位置 |
|---|---|---|---|
| 达到 `MaxSessions` 后淘汰最近最少使用的 Session。 | `docs/reference/deployment.md:129` | 容量满时拒绝创建新 Session，不淘汰现有 Actor。 | `src/Chorus.Engine/Session/SessionRegistry.cs:93-99` |
| 房间管理仍是部分构件；没有生产保存／恢复闭环。 | `docs/architecture/multi-machine-rooms.md:13-21` | 后续第 7 节已记载控制面接线；当前有持久房间目录、fenced checkpoint 和恢复路径。该文档第 1 节的“当前结论”与自身后文及代码不一致。 | `src/Chorus.Control/RoomControlStore.cs:64-76,98-114`；`src/Chorus.EngineHost/ControlCheckpointRestorer.cs:10-17` |
| 多 Worker 路由尚无生产接线，等待阶段三触发。 | `docs/design/process-topology.md:37` | 当前 Gateway 可经 ControlHost 分配房间并按 owner/epoch 路由；EngineHost 从控制面恢复并提交 checkpoint。 | `src/Chorus.Gateway/GatewayControl.cs:50-67`；`src/Chorus.EngineHost/EngineControl.cs:32-55` |
| 仓库没有 join endpoint、技能分级匹配或组局接线。 | `docs/guide/matchmaking.md:104-113` | `Chorus.ControlHost` 提供主播入队 API，控制面按 rating、层级等规则组局。此差异可能源于指南只描述 `Chorus.Engine.Matchmaker`，但没有说明 ControlHost 的另一套实现。 | `src/Chorus.ControlHost/Program.cs:115-119`；`src/Chorus.Control/MatchControlStore.cs:33-97` |

### 表三 值得补的能力

| 能力 | 为什么现在没有 | 价值高低 |
|---|---|---|
| 独立主机上的网络分区、进程暂停／恢复与 ControlHost 重启故障注入 | 现有验收主要是本机测试；仓库记录缺少独立多机故障环境，尚未证明旧 Worker 恢复后仍被拒绝或迁移后路由能收敛。证据：`todo.md:250-252,258`；现有测试边界：`docs/architecture/multi-machine-rooms.md:153-159`。 | **高**：生产启用自动接管前的正确性门槛。 |
| ControlHost 存储容量基线与明确的生产 SLO | 当前控制面是单写者 SQLite，checkpoint 按 tick 写入；尚无多 Worker 写入基线、目标 RPO/RTO 或磁盘预算。证据：`todo.md:257`；`src/Chorus.Control/ControlDatabase.cs:6,15,19-20`。 | **高**：决定可承载规模与是否需要更换后端。 |
| 控制台浏览器级端到端测试 | 当前尚未覆盖登录、越权、窄屏、断网恢复和审计展示；已有静态页面与客户端脚本不足以验证真实交互。证据：`todo.md:292`；`src/Chorus.ControlHost/wwwroot/index.html:5-12`；`src/Chorus.ControlHost/wwwroot/admin.js:62-71`。 | **中高**：控制台包含节点排空、迁移、封榜等管理操作。 |
| 抖音正式回调协议核实与真实联调 | 当前签名和字段解析明确是占位契约，需获准联调后替换；否则不能证明与平台生产协议互通。证据：`src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventVerifier.cs:11-18`；`src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventMapper.cs:10-26`。 | **高**：平台入口未核实前不应视为生产支持。 |
| 热点历史、告警阈值与告警确认流 | 当前已有即时人气／资源观察，但缺可靠互动事件聚合源、历史趋势和告警确认流程。证据：`todo.md:276`。 | **中**：提升运营响应能力，依赖先补齐事件数据口径。 |
