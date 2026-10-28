# 重设计 M4 实施记录：StreamerGateway WSS 生产边缘

M4 目标（redesign-contracts §3 + architecture §3.3 + operations §1.3）：面向主播客户端的公网 WSS 边缘。消息面 = contracts §3（上行 12 / 下行 13，含 pong/error 传输层）；传输 = 双 Kestrel 监听（内部 mTLS gRPC + 公网 WSS 443→7443，TLS 终结于网关，玩家无客户端证书，身份只来自协议内凭据）；网关零持久状态（架构：不加载长期业务实体）。对照 acceptance.md M4 出口（AT16/17/26/29/30/32 + 基线负载 + 真实平台回归）分轮收账，硬件阻塞项维持 open。

分轮结构（7 轮，每轮独立绿门）：M4-1 传输地基 → M4-2 登录/恢复/登出/会话替换 → M4-3 挂载与排队 → M4-4 入室与快照/增量扇出 → M4-5 命令与回执/退出 → M4-6 网关崩溃/排空/换网关 → M4-7 基线负载与台账收口。

## M4-1 已落地：边缘传输地基与角色打通（56633f8）

**新宿主 `src/Chorus.Redesign.StreamerGateway`**（旧引擎 `Chorus.StreamerGateway` 名字占用，重设计宿主照 M3-8 `Chorus.Redesign.PlatformGateway` 骨架）：

- **双 Kestrel 监听（operations §1.3 端口模型的配置层表达）**：内部 `CHORUS_PORT`(默认 7530) = Http2 + mTLS（RequireCertificate + TrustedProcessCertificates.ValidateAndRemember），挂 `StreamerGatewayFace.Ready`/`NotifySessionReplaced`；公网 `CHORUS_WSS_PORT`(默认 7443) = **Http1** + `UseHttps`（仅服务器证书，玩家无客户端证书）+ `UseWebSockets()` + `Map("/ws")`。同一张服务器证书服务两侧；公网固定 Http1（ClientWebSocket 走 HTTP/1.1，禁 RFC8441 extended-connect 防协商漂移）。
- **消息面（contracts §3.1/§3.2 名录逐字对齐）**：`Messages/EdgeTypes.cs` 双向 25 个类型常量 + `EdgeProtocolVersion.Current=1` + 错误码（protocol_mismatch/message_too_large/rate_limited/no_session/invalid_message/gateway_full）；`EdgeJson`（Web 默认 camelCase + bytes→base64）；`EdgeHello/EdgePing/EdgePong/EdgeError` DTO（下行带 `type` 判别字段）。封闭类型集：未定义类型 = 协议违规可见拒后关（1008 PolicyViolation），不是跳过的"未来消息"。
- **传输契约**：hello 先行 fail-closed（第一条必须是 hello；协议版本不符 → `error{protocol_mismatch}` 后关）；64KiB/帧累计 cap（`CHORUS_MAX_MESSAGE_BYTES`，超限 → `error{message_too_large}` + **RFC6455 1009 原生关闭码**——.NET 枚举无 1009，显式 `(WebSocketCloseStatus)1009` 下发）；preauth 超时（`CHORUS_PREAUTH_TIMEOUT_SECONDS` 默认 30s）：hello 迟迟不来的连接在窗内关停——取消挂起的 ReceiveAsync 后托管 WebSocket 已过优雅关窗，直接 Abort（传输级断开；契约保证的是"边缘不留未认证连接"，不是它已无法发送的关闭码）；ping→pong{serverTimeMs} 零会话推断。
- **分类限流（每连接、固定 60s 窗）**：login+resume 10/分、控制类（startGameplay/joinQueue/cancelQueue/leaveRound/logout/requestSnapshot）60/分、ack 600/分（超限**静默丢弃计数**——ack 洪水不得产生回包洪水；login/控制超限回 `error{rate_limited}` 不关连接）；ping/hello 不限（hello 有自己的超时门，ping 只证可达）。`EdgeRateCounter` 时钟可注入，窗翻转精确可测。
- **`TenantApplicationMap`**：`CHORUS_TENANT_APPLICATIONS` JSON（`{tenant:{app:game}}`）启动时倒排 (appId)→(tenant,game)；**同一 app id 出现在两个租户 = 启动即拒**（cluster 作用域网关唯一能命名租户的途径不能有歧义，fail-closed）；空 map/非对象也拒。
- **`EdgeOutbox`（下行扇出地基，M4-4 接线）**：快照 latest-wins 槽 + 有序 delta 队列（容量 256，`CHORUS_*` 后续轮暴露）+ 单读泵；快照 re-baseline = 换掉未发旧快照（计数）并清空队列内被顶替的 delta（计数）；**delta 链不可断中**：队列满 → 整条拒绝 + `NeedsSnapshot` 置位，由快照恢复；粘性条目（round result 回执）永不丢；计数（SupersededSnapshots/SupersededDeltas/RefusedDeltas）全公开。
- **`EdgeSessionRegistry`**：connectionId 内存索引 + `MaxConnections` 预算（`CHORUS_MAX_CONNECTIONS` 默认 10000，满则可见拒 gateway_full）；网关零持久状态——崩溃只丢连接，权威全在 store。
- **自报（架构 4.4）**：`CHORUS_CONTROL_ENDPOINT` 可选 → `ProcessSelfReporter` 报告**内部 gRPC 端点**（路由消费者是集群进程，公网 WSS 口不进路由）；不 claim 任何 ownership。

**m4.proto**（package chorus.redesign.m4）：`StreamerGatewayFace{Ready; NotifySessionReplaced(SessionReplacedNotice{session_id,connection_id,connection_generation,streamer_id,reason})→SessionReplacedReply{delivered}}`——通知携旧 generation，旧网关只关点名连接（M4-2 接会话事实；当前 registry 命中即 delivered）。

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

## M4 后续顺序

M4-2 WSS 登录/恢复/登出 + SessionReplaced 双腿 + m1 tenant_id 增量（§3.3 核心，AT01-05 客户端面）——**已落地** → M4-3 挂载/排队上行 + EdgeRoutingMonitor → M4-4 RoundSync 扇出 + Ack/requestSnapshot + EdgeOutbox 接线（AT06/15）→ M4-5 GameCommand/CommandReceipt + LeaveRound（§3 全闭合）→ M4-6 网关崩溃/排空/换网关（§3.3 末段 + AT30 网关腿）→ M4-7 CHORUS_RUN_M4_EDGE 基准 + 台账收口。
