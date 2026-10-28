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

## M4 后续顺序

M4-2 WSS 登录/恢复/登出 + SessionReplaced 双腿 + m1 tenant_id 增量（§3.3 核心，AT01-05 客户端面）→ M4-3 挂载/排队上行 + EdgeRoutingMonitor → M4-4 RoundSync 扇出 + Ack/requestSnapshot + EdgeOutbox 接线（AT06/15）→ M4-5 GameCommand/CommandReceipt + LeaveRound（§3 全闭合）→ M4-6 网关崩溃/排空/换网关（§3.3 末段 + AT30 网关腿）→ M4-7 CHORUS_RUN_M4_EDGE 基准 + 台账收口。
