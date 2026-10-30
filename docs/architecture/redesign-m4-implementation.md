# 重设计 M4 实施记录：StreamerGateway WSS 生产边缘

M4 目标（redesign-contracts §3 + architecture §3.3 + operations §1.3）：面向主播客户端的公网 WSS 边缘。消息面 = contracts §3（上行 12 / 下行 13，含 pong/error 传输层）；传输 = 双 Kestrel 监听（内部 mTLS gRPC + 公网 WSS 443→7443，TLS 终结于网关，玩家无客户端证书，身份只来自协议内凭据）；网关零持久状态（架构：不加载长期业务实体）。对照 acceptance.md M4 出口（AT16/17/26/29/30/32 + 基线负载 + 真实平台回归）分轮收账，硬件阻塞项维持 open。

分轮结构（7 轮，每轮独立绿门）：M4-1 传输地基 → M4-2 登录/恢复/登出/会话替换 → M4-3 挂载与排队 → M4-4 入室与快照/增量扇出 → M4-5 命令与回执/退出 → M4-6 网关崩溃/排空/换网关 → M4-7 基线负载与台账收口。

## M4-1 已落地：边缘传输地基与角色打通（56633f8）

**新宿主 `src/Chorus.StreamerGateway`**（旧引擎 `Chorus.StreamerGateway` 名字占用，重设计宿主照 M3-8 `Chorus.PlatformGateway` 骨架）：

- **双 Kestrel 监听（operations §1.3 端口模型的配置层表达）**：内部 `CHORUS_PORT`(默认 7530) = Http2 + mTLS（RequireCertificate + TrustedProcessCertificates.ValidateAndRemember），挂 `StreamerGatewayFace.Ready`/`NotifySessionReplaced`；公网 `CHORUS_WSS_PORT`(默认 7443) = **Http1** + `UseHttps`（仅服务器证书，玩家无客户端证书）+ `UseWebSockets()` + `Map("/ws")`。同一张服务器证书服务两侧；公网固定 Http1（ClientWebSocket 走 HTTP/1.1，禁 RFC8441 extended-connect 防协商漂移）。
- **消息面（contracts §3.1/§3.2 名录逐字对齐）**：`Messages/EdgeTypes.cs` 双向 25 个类型常量 + `EdgeProtocolVersion.Current=1` + 错误码（protocol_mismatch/message_too_large/rate_limited/no_session/invalid_message/gateway_full）；`EdgeJson`（Web 默认 camelCase + bytes→base64）；`EdgeHello/EdgePing/EdgePong/EdgeError` DTO（下行带 `type` 判别字段）。封闭类型集：未定义类型 = 协议违规可见拒后关（1008 PolicyViolation），不是跳过的"未来消息"。
- **传输契约**：hello 先行 fail-closed（第一条必须是 hello；协议版本不符 → `error{protocol_mismatch}` 后关）；64KiB/帧累计 cap（`CHORUS_MAX_MESSAGE_BYTES`，超限 → `error{message_too_large}` + **RFC6455 1009 原生关闭码**——.NET 枚举无 1009，显式 `(WebSocketCloseStatus)1009` 下发）；preauth 超时（`CHORUS_PREAUTH_TIMEOUT_SECONDS` 默认 30s）：hello 迟迟不来的连接在窗内关停——取消挂起的 ReceiveAsync 后托管 WebSocket 已过优雅关窗，直接 Abort（传输级断开；契约保证的是"边缘不留未认证连接"，不是它已无法发送的关闭码）；ping→pong{serverTimeMs} 零会话推断。
- **分类限流（每连接、固定 60s 窗）**：login+resume 10/分、控制类（startGameplay/joinQueue/cancelQueue/leaveRound/logout/requestSnapshot）60/分、ack 600/分（超限**静默丢弃计数**——ack 洪水不得产生回包洪水；login/控制超限回 `error{rate_limited}` 不关连接）；ping/hello 不限（hello 有自己的超时门，ping 只证可达）。`EdgeRateCounter` 时钟可注入，窗翻转精确可测。
- **`TenantApplicationMap`**：`CHORUS_TENANT_APPLICATIONS` JSON（`{tenant:{app:game}}`）启动时倒排 (appId)→(tenant,game)；**同一 app id 出现在两个租户 = 启动即拒**（cluster 作用域网关唯一能命名租户的途径不能有歧义，fail-closed）；空 map/非对象也拒。
- **`EdgeOutbox`（下行扇出地基，M4-4 接线）**：快照 latest-wins 槽 + 有序 delta 队列（容量 256，`CHORUS_*` 后续轮暴露）+ 单读泵；快照 re-baseline = 换掉未发旧快照（计数）并清空队列内被顶替的 delta（计数）；**delta 链不可断中**：队列满 → 整条拒绝 + `NeedsSnapshot` 置位，由快照恢复；粘性条目（round result 回执）永不丢；计数（SupersededSnapshots/SupersededDeltas/RefusedDeltas）全公开。
- **`EdgeSessionRegistry`**：connectionId 内存索引 + `MaxConnections` 预算（`CHORUS_MAX_CONNECTIONS` 默认 10000，满则可见拒 gateway_full）；网关零持久状态——崩溃只丢连接，权威全在 store。
- **自报（架构 4.4）**：`CHORUS_CONTROL_ENDPOINT` 可选 → `ProcessSelfReporter` 报告**内部 gRPC 端点**（路由消费者是集群进程，公网 WSS 口不进路由）；不 claim 任何 ownership。

**m4.proto**（package chorus.m4）：`StreamerGatewayFace{Ready; NotifySessionReplaced(SessionReplacedNotice{session_id,connection_id,connection_generation,streamer_id,reason})→SessionReplacedReply{delivered}}`——通知携旧 generation，旧网关只关点名连接（M4-2 接会话事实；当前 registry 命中即 delivered）。

**角色 `streamer-gateway` 全处打通**：HostEnvironment 节点授权角色表 + Ready 探测分支（`StreamerGatewayFace.Ready`，node-agent 证书探测）；ControlGrpcService.ReportProcess 自报角色枚举与 RequireAsync 表；RoutingGrpcService 三处（Ready/GetRoutingSnapshot/WatchRouting——M4-3 WatchRouting 前置）；RoundDemo DemoCluster 授权表。NodeAgent/DeploymentPlan 不动（网关走自报，同 M3-8 先例；监管形态台账 open）。

**测试（+9：host 4 + 单元 5，Release 1255→1264）**：

- host `RedesignM4HostTests`（真 ClientWebSocket over wss，`RedesignM4WsClient`：RemoteCertificateValidationCallback 以 CustomRootTrust 链到测试 root + ExtraStore 补 node authority 中间证书 + RevocationMode.NoCheck——宿主只发叶子证书，中间证书必须显式供给；不关验证）：hello+ping→pong{serverTimeMs>0}；错协议版本（99）→error{protocol_mismatch}→1008 关；>64KiB 帧→error{message_too_large}→**1009 关**；静默连接 2s preauth 窗内关停（容忍优雅关或传输断——取消挂起接收后优雅关不再可能，不变量是"关停"本身）。
- 单元 `RedesignM4EdgeUnitTests`：EdgeJson camelCase/base64/hello 往返；TenantApplicationMap 解析/未知 app 拒/歧义拒/空拒/非对象拒；EdgeOutbox 链保序（满即拒 + NeedsSnapshot + 计数；粘性永拒不了；快照 re-baseline 清 delta 且最新快照先行；泵取消以 OCE 退出被观测）；EdgeRateCounter 窗内耗尽/窗内不回血/固定窗翻转；EdgeTypes 25 常量逐一钉死契约拼写（漂移=断线）。

门禁：Release 全量 **1264/1264 一次过**（负载 12-15 下；轮 1 即决定性）。残留 0（进程/`chorus_m%` 库）。

## M4-2 已落地：WSS 登录/恢复/登出 + SessionReplaced 双腿（ecdc9a5）

**会话三上行 + 两下行（contracts §3.1/§3.2/§3.3）**，`EdgeWs` 分发循环接入 `EdgeSessionClient`（StreamerSession 边界的网关侧客户端，channel 惰性建、按宿主证书）：

- **loginWithPlatformLaunch**：网关先本地 `TenantApplicationMap.TryResolve(appId)`——未映射 app 的拒绝不出网关进程（`invalid_message`，连接保持可用）；`nextResumeSecret` 缺失/<32B = 协议违规；RPC 成功 → `MarkSession(tenant,session,streamer,accessCredential)` 落连接事实 + 下发 `loginReady{streamerId,sessionId,sessionVersion,accessCredential,accessExpiresAt,resumeRegistered}`。
- **resume**：网关**不携带 tenant**（resume 无 appId，租户只有会话自己知道）——StreamerSessionGrpcService 对 `@cluster` 调用方经 `GetSessionTenant` 服务器侧解析；rotation 幂等（`auth.rotation` 重试窗内同 (previousSecret,nextSecret,operationId) 重放同 grant，`duplicate` 不再 displacement）；成功同样 MarkSession+loginReady（resume 的 loginReady 即"恢复完成"）。
- **logout**：连接无会话 → `no_session`（连接保持）；有会话 → RPC `Logout`：coordinator `ValidateAccess`（access credential 签名+会话状态）→ DataServer `RevokeSession`（幂等：二次调用 Applied=false；撤销同时**清 `streamer_state.current_session_id`**——无后继的撤销不得让快照仍报 Current，版本计数器保留不回退）→ actor 关连接；网关收到成功即 `NormalClosure` 关连接（**撤销先于关闭落库**，丢回复的重试由拒答兜底）；logout 无下行 ack（契约）。
- **sessionReplaced 下行（主动腿）**：Login/Resume 的 grant 事务在撤销旧行前回读 previous_{session,gateway,connection,generation}（复用既有 FOR UPDATE 锁，无新竞态）；coordinator 见 previous 非空且**不是请求者自己的连接**（同一连接原地 resume 不通知自己）→ `SessionReplacedNotifier`：路由快照查 Role=streamer-gateway 且 ProcessInstanceId 匹配的端点 → 直连旧网关 `NotifySessionReplaced`（3s/2s deadline，best-effort，RpcException 吞掉——替换本身已在 grant 里持久）；网关侧 `TryDeliverSessionReplacedAsync`：经连接的串行下行 lane 推 `sessionReplaced{reason}` 再关 1008。**被动腿**：网关 RPC 收 PermissionDenied→`session_revoked`、FailedPrecondition→`session_replaced` 错误码映射（`EdgeErrorMapping`），未注册路由的拓扑退化为被动腿。
- **串行下行 lane（EdgeConnection）**：`SemaphoreSlim` + `AttachTransport(sendFrame,close)` 委托——分发循环的回帧与内部面到达的替换通知不可能在 socket 上交错发送。
- **m1 tenant_id 增量（cluster 调用方具名租户）**：Login/Resume/AttachConnection/ConnectionClosed/ValidateAccess/StartGameplay/GameplayStatus 七请求加 `tenant_id` additive；`StreamerSessionGrpcService.RequireAsync` 放行 `streamer-gateway` 角色，`TenantFor(caller,requested)` 沿 M3 栅栏（每租户证书具名无效即拒；`@cluster` 必须具名；DataServer 仍逐请求对 `auth.identity` 栅栏复核）；新 `Logout` RPC、`StreamerIdentity.RevokeSession/GetSessionTenant`、`SessionGrantReply.previous_*`、`LoginReadyReply.resume_registered`（回复只在 grant 事务提交后存在，rotation 收据由构造保证持久）。
- **上行 DTO 带 `type` 判别**：客户端序列化上行帧必须含 `type`（get-only JsonPropertyName），分发信封检查方才认帧——测试客户端与网关共用同一 DTO 集。

**测试（+16：store 3 + host 9 + 单元 4，Release 1264→1280）**：

- store `RedesignIdentityStoreTests`：Login/Resume 回读被替换连接（首 grant 无 previous；resume 报 previous；duplicate 重试不 displacement）；RevokeSession 幂等+点名 displaced 连接（二次 Applied=false 无 displaced；撤销后 resume 拒）；撤销即刻对 GetSessionState 可见（Revoked=true、Current=false）。
- host `RedesignM4HostTests`（四宿主+网关全链，node 由 node-agent RegisterNode 预置——自报无 node 行会被 StaleAgent 拒，路由为空则主动腿找不到网关端点）：login 全字段+连接保持可用；**二次 login 挤掉首连接**（sessionReplaced+1008）；logout 关连接+secret 失效（resume 拒 session_revoked）；**resume rotation + duplicate 不再 displacement**（version=2、access 换新、旧连接收 sessionReplaced、重放同 operationId 第三连接拿同 grant 且第二连接仍应答 ping）；会话分上行先拒 `no_session`（连接保持）；未映射 app 边缘本地拒；坏平台凭据拒后连接可用；**AT32 泄密扫描**（launch credential/两个 secret 的 base64/access credential 不得出现在任何宿主输出——M0HostTests 新增 AllHostOutput 聚合）；已花掉的 resume secret 不能开第二次 rotation。
- 单元 `RedesignM4EdgeUnitTests`：上行/下行 JSON 逐字段对契约；无会话时 session 分上行被拦（RequiresSession 分级）；RpcException 状态→错误码映射；错误码钉死线拼写。

门禁：Release 全量 **1280/1280**（负载谷 5min<10 下定局）。过程如实：外部负载（他工程 vitest/tsc 风暴，负载 15-47）下两次全量 1279/1280、1278/1280，唯一反复失败是 M3-7 `AKilledExecutorIsReplacedByQuery` 的 **AssertOutboxAsync 零延迟读**——任务 Completed 可见先于 outbox 效应行落库，负载下必输；按 37538e9 先例把断言改为 30s 收敛等待（期望状态皆终态，等待不会翻转匹配），负载谷全量一次过。M4-2 代码本身三轮全绿零抖动。残留 0（进程/`chorus_m%` 库）。

## M4-3 已落地：挂载/排队上行 + 状态下行 + 路由监视推送（87f6ae3）

**上行三连 + 下行两连（contracts §3.1/§3.2）**，身份注入不变式贯穿：tenant/binding/session 全部来自认证后的连接事实（M4-2 MarkSession/本轮 MarkGameplay），客户端上行身份字段一律忽略——网关对数据面具名，对客户端不信任。

- **startGameplay（m1 StartGameplay）**：`EdgeSessionClient.StartGameplayAsync(tenant, accessCredential, gameId, operationId)` → 成功即 `MarkGameplay(bindingId, gameId, rulesVersion)` 落连接事实（rulesVersion 是客户端声明的可命名事实，参与匹配分区）→ 下发 `gameplayStatus{bindingId,generation,state,reason?}`（`reason="duplicate"` 当且仅当 m1 报重放）。未映射 game → m1 `TenantApplicationDeniedException`→PermissionDenied→边缘呈现 `session_revoked`（与 M4-2 会话映射一致），连接保持可重试。
- **joinQueue（MatchQueue EnqueueTicket）**：分区 = `EdgeQueueKeys.PartitionFor(tenant, game, rulesVersion|"-", queueType)`——**只有网关能命名的四个事实决定分区**，join options 携带的任何东西都不放宽窗口（两 mount 未在这四事实上一致就绝不共享 round window）；缺 rulesVersion 显式落 `-` 段，不与 rules-1 静默同池。`EdgeQueueClient.EnqueueAsync`（cluster 作用域对数据面具名 tenant，沿 M3 TenantFor 栅栏）→ 回 `matchStatus{ticketId,state,reservationId?}`（store 状态直传，仅 `Committed→Matched` 换客户端词汇，`EdgeMatchStates.FromStore`）。成功后 `routingMonitor.Subscribe(streamerId, connectionId, …)`——**从此推送归路由监视**。
- **cancelQueue（MatchQueue CancelTicket）**：以 mount 的 binding 为地址（客户端手里的 ticketId/ticketVersion 是诊断不是寻址，契约 §3.1）→ 回 `matchStatus{Cancelled}`；连接保持可用。
- **未挂载先排队**：joinQueue/cancelQueue 在无 startGameplay 时拒 `invalid_message`（连接保持）——ticket 属于 mount，无 mount 无票。
- **`EdgeQueueClient`**：MatchQueue 边界的网关侧客户端（Lazy channel，按宿主证书 cluster 角色）；`EdgeQueueKeys`（分区合成）；`EdgeMatchStates`（客户端词汇映射）。
- **`EdgeRoutingMonitor`（WatchRouting 消费者）**：`RunAsync` 常驻 watch 循环（`RoutingMonitorHost : BackgroundService` 托管，与宿主同寿命）；notice 无载荷 → 每次版本变化全量拉快照（Routing 服务消费者契约）→ `ApplySnapshotAsync` 记录 streamer 行 room、对每个订阅者**房间变化才回调**（首个 apply 报当前房间含 `""`——中途订阅也能立刻对齐现状；回调携快照值离锁执行，慢推送不阻塞 watch 循环）；`RoomFor(streamerId)` 供 M4-4 扇出问房间端点；连接关闭 `Unsubscribe`。
- **Watch 版本语义（本轮踩坑修正）**：路由版本是 **FNV-1a 内容散列**（control store 明示"deliberate deviation from a monotonic sequence"——漏看 notice 的消费者靠**版本不同**发现差距，不靠大小排序）。首版监视循环写了 `notice.Version <= version → skip`：散列为负（约一半概率）时首条 notice 永远 ≤0 被跳过，监视静默失明——配对测试当场抓获（ticket 已 Committed、两 streamer 路由行都有房间、推送没到）。修正为 `==` 跳过、`!=` 即拉：**"不同"才是变化信号，更小的散列同样是更新的世界**。
- **Matched 推送的收敛窗（本轮第二个产品修正）**：房间在 round **start 事务**即写 `current_room_id` 路由可见，ticket 要到 **CommitMatch** 才落 `Committed`——中间隔着 prepare 往返。首版回调读一次 ticket 不是 Committed 就放弃且房间 notice 已被 claim → 推送永久丢失（近乎确定性，不是低概率竞态）。修正 `OnRoomRoutedAsync`：claim 房间后对 ticket **收敛读**——`Reserved/Preparing`（管线在途）每 150ms 再读（15s 上界）、`Committed` 即推 `matchStatus{Matched, reservationId, roomId}`、`Queued/Cancelled/Expired/无票` 静默退出（该房间不属于此 mount 本轮，后续新房间会再次触发回调）；瞬时 RpcException 在窗内重试。`TryClaimRoomNotice` 保证每房间至多一次推送。
- **队列错误映射（`EdgeErrorMapping.FromQueueRpcStatus`）**：FailedPrecondition/InvalidArgument→`invalid_message`（**队列态拒绝绝不借用 session_replaced**——票被拒不是会话被换）、PermissionDenied→`session_revoked`；会话轮换映射原样保留。Refuse 文案区分队列语境。
- **`EdgeJson` 补 `DefaultIgnoreCondition=WhenWritingNull`**：可选字段缺省即缺席——null 与 missing 在线上不可区分（契约示例形态；单测钉死 `rulesVersion` 未设时整段不出现）。
- **MatchQueueGrpcService 角色放宽**：EnqueueTicket/CancelTicket +`streamer-gateway`，GetTicket +`streamer-gateway`（Ready 于 M4-1 已放行）；coordinator-only 的 ListQueued/ExpireQueued/ListPartitions 不动。

**测试（+10：host 6 + 单元 4，Release 1280→1290）**：

- host `RedesignM4HostTests`（四宿主+网关全链；`StartEdgeClusterAsync(withMatchmaker:true)` 时 ControlServer 加 coordinator 环境 CHORUS_DATA_ENDPOINT/CHORUS_GAME_ENDPOINTS/CHORUS_MATCH_INTERVAL_MS=200 并起 GameServer——复用 M3-5 自动配对拓扑）：startGameplay 挂载 binding（state Ready、generation≥1、bindingId 非空）且连接可继续用；**两个边缘客户端（不同 openId——同 openId 会互踢）排队被配成同一 round**——双双回 Queued、双双被**推送** Matched（同 roomId/reservationId/ticketId，30s 窗）；cancelQueue 撤票回 Cancelled 且连接可用；排队上行先于挂载双拒 invalid_message 且连接可用；未映射 game 在 startGameplay 拒 session_revoked 且连接保持；**重复 joinQueue 同 operationId 重放同 ticket**（store OperationFingerprint 收据）。
- 单元 `RedesignM4EdgeUnitTests`：上行/下行 JSON 逐字段（rulesVersion 未设时缺席钉死）；分区合成（缺 rules→`-` 段、rules/queueType 不同即不同分区）；票态词汇 + 队列错误映射钉死；路由监视逐订阅者每变化一次（首个 apply 报当前房间含 `""`、变化才再报、重复静默、RoomFor、Unsubscribe 停报）。

门禁：Release 全量 **1290/1290**（真负载谷定局：1min<7、5min<9.5、15min<11 三线同低才起跑）。过程如实：配对测试先在轮内抓出两个产品缺陷（watch 版本 `<=` 门负散列失明；Matched 推送缺收敛窗）——均为"单测绿、全链才现形"的跨平面时序问题；修后 M4 过滤 32/32、M3 邻居 17/17，全量三轮：1289/1290（M2 kill-tick 20s 窗在负载 30 风暴下差 2 tick，单测隔离即绿）、1288/1290（M3-7 executor 平台调用等待窗 + FreePort 端口竞态致 DataServer 绑定失败，双双隔离绿——纯外部负载环境态，零 M4-3 代码交集），第三轮谷内定局 1290/1290。残留 0（进程/`chorus_m%` 库）。

## M4-4 已落地：RoundSync 扇出 + 对局下行链 + Ack/requestSnapshot + EdgeOutbox 接线（c2c3101）

**下行六连（contracts §3.2）+ 上行两连（§3.1）**，AT06/AT15 客户端面单机闭合。网关仍零房间状态：每帧都是 actor 已提交记录的原样转译；角色放行 `RoundSyncGrpcService`/`RoundGrpcService.GetRound` +`streamer-gateway`（SubscribeRequest 不加租户——room_id 全局唯一、GameServer 无租户存储）。

- **`EdgeRoomFanout`（扇出本体）**：每房间**一条** RoundSync 订阅，共享房内全部座位。`Generation` 计数每次（重）订阅递增——被取代的读者只终结自己的代，不能拆掉替代者（CTS cancel-never-dispose：旧读者仍持旧 token）。`ResultDelivered` 标记后流尽→**静默收房**（actor 提交结果后 complete 是正常终局，不误报 resync）；`StreamFailures<3` 同 epoch 断流重试预算（每帧交付清零）；流尽无果 →`ResolveOrEndAsync` 重解析（routing watch × round store 合成，5s 上界）：**Suspended→`roomSuspended{reason,recoveryAttempt=epoch}`**（恢复落点即恢复尝试代）、epoch 移动→更新事实+全座位重置 JoinedSent+按 (0,0) 重订阅（新快照重宣告新 epoch）、其余（房间消失/解析失败）→`resyncRequired{roomId,roomEpoch,reason}`+移房——冻结死链是唯一不可接受结局。gRPC 把流取消面为 `RpcException(Cancelled)`：泛 catch 先查 `IsCancellationRequested` 再定性断流。
- **roomJoined 的结构性排序（决定性设计）**：`EdgeOutbox` 泵**先排快照槽后排有序队列**，而 roomJoined 绝不能坐有序队列——pending 快照会超车它，客户端就会先见帧后见房。解法=per-room `SemaphoreSlim` dispatch gate 内**先 await 直发 roomJoined（串行下行 lane），完成后才入队本帧**：顺序由构造保证，不靠时序祈祷。晚到座位（基线≠流头）强制全房按其基线重订阅——所有人拿下一次快照，无人猜缝。
- **座位基线**：`Join` 用连接已 ack 的事实（`AppliedRoomId` 匹配才取 `(tick,version)`，否则 `(0,0)`）；已处流头的座位纯骑 delta。`requestSnapshot` 上行→按 `(0,0)` 重订阅，outbox 快照槽 re-baseline 全链；未知房/无座位→`invalid_message`（连接保持）。`ack` 上行→`MarkApplied` 落连接事实，**静默无回帧**（回包洪水由 600/分静默丢弃计数制，M4-1 已备）。
- **resume 重进房 `TryRejoinRoomAsync`**：`RoomFor(streamerId)`→`RoomRouteFor(roomId)` 取 **TenantId**（routing 行携带，m3.proto RoomRouteProto）→`GameplayStatus` 恢复 binding →按 `BindingId` 匹配 `GetRound` participation→`Join`。`JoinRoomFanoutAsync` 收敛窗 15s（200ms 步进）：路由端点未到/GetRound RpcException/round 未落——皆重试；participation 不含本 binding→静默退出（该房间不属于此 mount）。
- **`EdgeRoundClient` + `EdgeGameChannelPool`**：round store 只读客户端（Lazy channel）+ `SubscribeStream` 迭代器（call 随枚举释放）；对 game host 按 endpoint 引用计数共享 channel，`StreamClosed` 钩子与订阅获取**逐条平衡**（含 subscribe 调用即抛路径的补释放）——最后一个房流结束即释放该 host，不等进程退出。
- **GetRound 租户坑（本轮主缺陷，探针测试定位）**：`game.room` 键 `(tenant_id,game_id,room_id)`，而 `GetRoundRequest` 原无租户字段——服务端回退**调用方证书租户**，网关是 `@cluster` 作用域 →`found=False` →`JoinRoomFanoutAsync` 窗内静默空转，客户端 Matched 后零帧。症状与定位：路由行健康（Active、owner=game-1、端点正确）、实例自报齐、唯 round 查询落空——临时探针测试 dump 三层证据后指向唯一缺口。修=m2 additive `tenant_id=3` + `TenantFor(caller,requested)` 沿 M3 栅栏（`@cluster` 必须具名；既有空字段调用行为不变），网关两侧（join 窗/ResolveRoom）都传 routing 行的 `TenantId`。
- **`EdgeMessages` 增量**：下行 `roomJoined{roomId,roomEpoch,participation,moduleVersion,configHash}`/`snapshot{…,schemaVersion,payload(base64)}`/`delta{…,baseVersion,payload=effects JSON 数组}`/`roundResult{resultId=room:epoch:tick,settlementState="Committed",result}`/`resyncRequired`/`roomSuspended`；上行 `ack{roomId,lastAppliedTickId,stateVersion}`/`requestSnapshot{roomId}`。delta payload 是帧 effects 的模块词汇转译（kind+解析后的 payload），非边缘发明。

**测试（+9：单元 4 + host 5，Release 1290→1299）**：

- 单元 `RedesignM4FanoutUnitTests`（脚本化流直接驱动状态机，无控制面）：座位链序 roomJoined→snapshot→delta→roundResult 全字段（base64/.effects 数组/resultId/settlementState；终局后无 resync、RequestSnapshot 无房可基线、整链一条订阅）；晚座位 (0,0) 重基线全房（Requests[1] 基线归零、先到座位拿第四帧新快照）；流尽于新 epoch→重订阅+重宣告（Requests[1].Epoch=5、roomJoined/snapshot 皆 5）；Suspended→`roomSuspended{recoveryAttempt}` vs 房间消失→`resyncRequired` 两腿。
- host `RedesignM4HostRoundTests`（四宿主+网关+coordinator+GameServer **自报 game-1**（CHORUS_GAME_ENDPOINTS 静态目录行=instanceId=endpoint，宿主以同名 instanceId 自报——路由房行才有可 join 的端点））：两客户端配对后**完整链到 result**（roomJoined.participation 各得其所、delta baseVersion 逐一链式、resultId 前缀+Committed+ranking）；断线客户端 resume（LastSeenRoomId+applied 基线）→重进房拿**新快照**（tick> 断线基线）；requestSnapshot 重基线链不破 + 未知房间拒 invalid_message 连接保持；**650 ack 洪水全静默**且链不破（洪水后 ping-pong 仍应答——链断检测靠事件不变量非计数）；FailAfterTick=2 三振挂起房→客户端收 `roomSuspended`，`recoveryAttempt==owner.Epoch`（ClaimOwnership→StartRound→PrepareRoom 手工拓扑）。
- 门禁：Release 全量 **1299/1299**（负载谷定局：1min<7、5min<9.5、15min<11 三线同低起跑，谷值 l=6.42/8.60/10.95）。过程如实：host 五连最初全灭于同一症状（Matched 后零帧）——单元 4/4 绿说明状态机对，全链才现形的是**跨平面租户键**（网关证书作用域 × 存储键维度），临时探针测试 dump 路由/实例/round 三层后十分钟定位；修后 M4 过滤 41/41、全量谷内一次过。残留 0（宿主进程/`chorus_m%` 库）。

## M4 后续顺序

M4-4 RoundSync 扇出 + Ack/requestSnapshot + EdgeOutbox 接线（AT06/15）——**已落地** → M4-5 GameCommand/CommandReceipt + LeaveRound（§3 全闭合）→ M4-6 网关崩溃/排空/换网关（§3.3 末段 + AT30 网关腿）→ M4-7 CHORUS_RUN_M4_EDGE 基准 + 台账收口。
