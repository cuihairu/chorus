---
title: 重设计协议与数据契约
---

# 重设计协议与数据契约

版本：1.0，2026-10-25。配套[完整设计](architecture.md)。本文件定义目标协议语义，示意字段不是已生成的 Protobuf SDK。

## 1. 通用消息与错误

所有内部 RPC 使用 mTLS gRPC/Protobuf，实例认证绑定本次进程身份。业务 ID 使用 opaque string，平台 ID 必须保留原始字符串，禁止转 JavaScript Number 丢精度。

```text
RequestContext {
  protocol_major, protocol_minor,
  tenant_id, game_id,
  request_id, operation_id, trace_id,
  caller_process_instance_id,
  deadline_unix_ms,
  expected_entity_version?, owner_epoch?, session_version?
}
Result {
  status, operation_id,
  committed_version?, receipt_id?, retry_after_ms?,
  current_epoch?, error_code?, safe_message?
}
```

OperationId 标识一次逻辑操作，重试不变；RequestId 标识一次传输尝试，可改变。接收方保存请求规范化哈希；同 OperationId 不同 payload 返回 IdempotencyConflict，而不是返回另一请求的成功结果。幂等键精确为 `(TenantId, GameId, OperationKind, TargetId, OperationId)`；无游戏作用域的集群操作使用协议保留值 `@cluster`，不得使用空字符串或调用方自造的代号。接收端从已认证上下文确定 tenant/game，并验证目标与操作类型，不能直接信任请求中的作用域字段。

请求哈希覆盖会改变业务效果的规范化字段及 payload，不覆盖 `RequestId`、`TraceId`、传输 deadline、调用实例 ID；这些字段在安全重试时可以变化。规范化编码由每个 RPC 的有版本消息契约定义；同一操作版本的重复调用必须产生相同字节。不同 payload 或规范化版本不得借相同 `OperationId` 取得旧成功回执。回执与业务效果在同一事务提交，超时后先按完整幂等键查询原结果。

M0 持久收据执行入口接受规范化请求指纹与业务写入回调；入口先在事务中占用完整幂等键，再在同一事务内执行回调并保存 JSON 回执，最后提交。相同键的并发请求由 PostgreSQL 唯一索引串行化：指纹相同返回原回执，版本或指纹不同返回 `IdempotencyConflict`；回调失败使收据与业务写入同时回滚。迁移须单独执行，业务进程启动不并发建表。该入口只是底层事务能力，尚不代表 DataServer、身份授权或任何 AT 场景已完成。

| 状态 | 含义 | 调用方处理 |
|---|---|---|
| Committed | 事务已提交，给出原始 receipt | 可以对用户确认 |
| Duplicate | 原操作已提交，返回同一 receipt | 不重复效果 |
| Pending | 持久接受，业务尚未完成 | 按 OperationId 查询，不能声称业务完成 |
| Rejected | 明确未接受 | 显示原因，按新业务意图处理 |
| Retryable | 本次未确认，可重试 | 保持 OperationId，退避 |
| OutcomeUnknown | 无法判定原操作是否提交 | 先查询；不可改 ID 重发 |
| StaleOwner | epoch/实例/租约不符 | 停止旧执行，更新路由 |
| VersionConflict | 数据版本不符 | 重读、重新计算，不盲覆盖 |
| UpgradeRequired | 协议或模块不兼容 | 升级或使用兼容客户端 |

标准错误包括 Unauthorized、Forbidden、SessionRevoked、BindingNotReady、AlreadyInRoom、ReservationExpired、CapacityExceeded、StorageUnavailable、RecoveryFailed、SchemaMismatch、AmbiguousEvent、PlatformOutcomeUnknown。外部返回不包含 token、SQL、机器路径或内部凭据。

默认内部查询 deadline 2 秒、命令 5 秒；平台请求由适配器定义，任务整体可异步。deadline 到期不意味着事务回滚成功。禁止把超时统一变成“失败，可以换 ID 再做一次”。

## 2. 服务契约清单

| 服务 / 宿主 | 方法 | 输入要点 | 持久结果与不变量 |
|---|---|---|---|
| PlatformIdentity / PlatformGateway | VerifyLaunch | AppId、平台凭据、LoginAttemptId | 可信身份与直播上下文；秘密不返回客户端 |
| StreamerSession / StreamerServer | Login、Resume、Logout | 可信 LaunchProof 或 resume secret | SessionVersion，实体 Ready 后方可成功 |
| StreamerBusiness / StreamerServer | GetProfile、ExecuteCommand | SessionVersion、CommandId | 持久命令或明确非关键脏字段 |
| Ownership / ControlServer | Locate、Activate、Renew、Release | EntityId、ProcessInstanceId、Epoch | 原子 owner/epoch；不得由客户端指定可信 owner |
| Directory / ControlServer | GetSnapshot、Watch、AckRoute | since_version、role/partition、GatewayInstance、RouteVersion | 路由确认后开放投递；缺口重拉快照 |
| RoundEnd / ControlServer | BeginEnd、AckDeliveryStopped、GetEndStatus | EndOperationId、挂载/房间代、关闭版本、Gateway incarnation | 关闭确认屏障完成后才授权平台调用，收尾完成后才释放与销毁 |
| Match / ControlServer | Enqueue、Cancel、GetStatus | BindingId、SessionVersion、QueueKey | ticket 幂等；每挂载最多一个有效 ticket |
| MatchStore / DataServer | Reserve、Commit、Abort | Term、ReservationId、PreparedReceipt | 参与者与 Room 同事务切换 |
| Room / GameServer | Prepare、Recover、Drain、Subscribe | RoomId、Epoch、PackageHash | 重复 Prepare 返回同一准备结果 |
| RoomEventIngress / GameServer | PushEvents、GetAcceptance | 已验证来源、BindingGeneration、RoomId、RoundGeneration、OwnerEpoch、EventId、PayloadHash | 定向投递对应 RoomActor；持久接受后才确认 |
| InputStore / DataServer | PersistAcceptedInputs、ReadRecoveryInputs | 由业务服务确定的目标与归属证据、EventId、PayloadHash | 只保存和查询；唯一性、版本与事务校验，不解析事件目标 |
| RoomStore / DataServer | ReadInbox、CommitFrame、PutSnapshot、LoadRecovery | epoch、前帧版本、CommitId | 消费标记与帧同事务 |
| PlayerData / DataServer | Load、Patch、ApplyAssetOperation | PlayerId、ExpectedVersion、OperationId | 不能通过 Patch 覆盖账本资产 |
| SettlementCoordinator / ControlServer | ExecuteSettlement、GetProgress | RoomId、FinalFrameReceipt、PlanHash | 驱动不可变清单与重试；通过 DataServer 幂等保存逐笔效果 |
| SettlementStore / DataServer | ApplyGrant、GetSettlement | 固定 GrantId、OperationId、账本变更 | 每笔至多一个账本效果，不主动执行调度 |
| RoundFinalization / DataServer | FinalizeRound | RoomId、ResultVersion、OperationId | 输入与结算收尾后原子释放参与、挂载指针、容量与 owner |
| PlatformTask / PlatformGateway | Reconcile、GetStatus | TaskId、TaskEpoch | 记录 Desired/Observed，未知结果不盲重试 |
| RoomEpochAdmin / ControlServer（待实现） | GetRoomEpoch、BumpRoomEpoch | tenant/game/room、ExpectedEpoch、TargetEpoch?、OperationId、Reason | 按房间 CAS、仅递增、owner 不变；room 与 ownership 同事务更新；提交与路由同步分别可查询 |
| Node / ControlServer | Register、Report、WatchPlans | AgentInstanceId、sequence | 只接纳当前 incarnation |
| Agent / NodeAgent | EnsureRunning、Drain、Stop | SlotId、PlanGeneration、CommandId | 同 slot 不并发启动两个受管实例 |

以上为修订后的目标协议边界，不代表新增可执行进程或已经实现。DataServer 提供存储操作及约束校验，业务解析、任务调度、结束协调分别属于上述业务宿主；现有接口迁移按 TODO §22.9/22.10 验收。

## 3. 主播客户端协议

### 3.1 上行

```text
Hello { protocolVersion, clientBuild, supportedModuleVersions }
LoginWithPlatformLaunch { platform, appId, launchCredential, loginAttemptId, nextResumeSecret }
Resume { resumeOperationId, resumeCredential, nextResumeSecret, previousSessionId, lastSeenRoomId, lastAppliedStateVersion }
StartGameplay { operationId, gameId, rulesVersion }
JoinQueue { operationId, queueType, options }
CancelQueue { operationId, ticketId, ticketVersion }
GameCommand { commandId, roomId, roomEpoch, clientSequence, payload }
Ack { roomId, roomEpoch, lastAppliedTickId, stateVersion }
RequestSnapshot { roomId, roomEpoch, reason }
LeaveRound { operationId, roomId, reason }
Logout { operationId }
```

Gateway 从认证上下文注入 StreamerId、SessionVersion 和 BindingId，不相信客户端上行的身份字段。GameCommand 接收时校验当前 session 和 participation，将校验结果作为持久输入证据；GameServer 不根据客户端时间提前结算。

默认一帧上行最大 64 KiB；登录尝试、控制命令、ACK 分别限流。会话建立前不接受游戏命令，Ping/Pong 只证明连接可达。

### 3.2 下行

```text
LoginReady { streamerId, sessionId, sessionVersion, accessCredential, accessExpiresAt, resumeRegistered }
GameplayStatus { bindingId, generation, state, reason }
MatchStatus { ticketId, state, reservationId?, roomId? }
RoomJoined { roomId, roomEpoch, participation, moduleVersion, configHash }
Snapshot { roomId, roomEpoch, tickId, stateVersion, schemaVersion, payload }
Delta { roomId, roomEpoch, tickId, baseVersion, stateVersion, payload }
CommandReceipt { commandId, status, receiptId }
SessionReplaced { reason }
RoomSuspended { reason, recoveryAttempt }
RoundResult { resultId, settlementState, result }
ResyncRequired { roomId, roomEpoch, reason }
```

Receipt 表示输入已持久接收，不等于游戏效果已应用；效果由已提交 Tick 或单独 AppliedReceipt 确认。Gateway 不缓存 resume secret 到日志或指标。

连接恢复时以服务器参与关系为准；客户端发送旧 RoomId 只作诊断。新 epoch 的 Snapshot 替换旧局部状态；旧 epoch 或回退 stateVersion 一律丢弃。

### 3.3 登录与 resume 的幂等轮换

客户端先生成并安全存储至少 256 位随机 nextResumeSecret 及 OperationId，再发送登录或 resume。服务端事务写新 secret 哈希、SessionVersion 和轮换回执；不存在“新 secret 只在可能丢失的响应里出现”的窗口。

轮换表保存 PreviousSecretHash、OperationId、RequestHash、ResultSessionId/Version 和短暂重试期限（默认 2 分钟）。旧 secret 在轮换后只能匹配同一 OperationId/RequestHash 查询该结果，不能生成另一轮换、恢复旧权限或执行游戏命令。同操作重试返回同 SessionVersion；返回 access 凭据前再次确认该版本仍为当前且未封禁，若已被别的登录接管则返回 SessionReplaced。

重试期限过后，客户端仍持有发送前保存的新 secret：若上一操作已提交则可用它发起新的 resume；若未提交则旧 secret 仍有效。客户端先查询保存的操作状态再选择，避免交替乱试。所有凭据只能通过 TLS 传输并存入客户端安全存储。

同一登录轮换的响应重传不随意绑定不同连接。换 Gateway 的请求通过当前 session/secret 发起新的 `AttachConnection` 幂等操作，递增 ConnectionGeneration；旧 Gateway 的关闭通知必须携旧 generation，不影响新连接。Gateway 崩溃不会迫使平台启动 token 再次兑换。

## 4. 平台适配器与事件契约

```text
PlatformCapabilities {
  launch_verification, binding_query, explicit_round_reporting,
  stable_event_id_by_kind, callback_ack_format,
  retry_window?, failed_event_fetch?, live_end_notification?,
  external_operation_idempotency_by_kind,
  scope_and_permission_evidence
}
NormalizedEvent {
  platform, app_id, source_room_key,
  source_event_id?, source_batch_id?, source_item_index?,
  source_user_id, source_recipient_id?,
  kind, event_time?, received_at,
  binding_id?, binding_generation?, round_hint?,
  gift_id?, quantity?, value_minor_units?, currency?,
  payload_schema, payload, raw_hash,
  reliability_class
}
```

官方没有的字段可缺省，不能用随机 UUID 冒充稳定 source_event_id。平台礼物价值使用整数及明确单位，不能按不同平台货币直接合计。仅凭 JSON 内容哈希不能认定两次合法同额礼物是同一笔。

适配器生产启用必须有：专属官方协议来源、脱敏真实样本、签名正反用例、批次/项幂等规则、成功响应、乱序与重试测试。小游戏、普通 webhook、SaaS 协议不是直播玩法协议的替代证据。

## 5. 数据模型

业务数据按 TenantId/GameId 隔离；身份按平台/AppId 隔离。**直播挂载槽例外：其唯一键必须跨 GameId，使用已验证的 Platform + CanonicalLiveSessionKey**，同时记录授权 TenantId；不能用 GameId 把互斥约束分开。跨应用规范直播键或平台互斥能力证据属于适配器准入要求，不凭空假设 OpenId 跨应用相同。下表为简化逻辑表，不是最终 SQL DDL。

| 表 | 关键字段 / 约束 | 写入者 |
|---|---|---|
| control.node | NodeId PK、AgentInstanceId、ReportedAt、Capabilities | ControlServer |
| control.deployment | SlotId PK、DesiredVersion、PlanGeneration、DesiredState | ControlServer |
| control.process | ProcessInstanceId PK、SlotId、NodeId、Lifecycle | ControlServer |
| control.coordinator | PartitionKey PK、Holder、Term、LeaseUntil | 控制事务 |
| control.ownership | Kind+EntityId PK、OwnerInstance、Epoch、LeaseUntil、Version | 控制事务；数据事务加锁验证 |
| auth.identity | UNIQUE(Platform, AppId, OpenId)、PlayerId | DataServer |
| auth.session | SessionId、StreamerId、SessionVersion、RevokedAt、ResumeHash | DataServer |
| auth.rotation | StreamerId+OperationId UNIQUE、PreviousSecretHash、RequestHash、ResultSessionVersion、RetryUntil | DataServer；仅精确重试可读 |
| auth.streamer_state | StreamerId PK、CurrentSessionVersion、CurrentSessionId、EligibilityVersion、BanState | DataServer；匹配和接管共同校验 |
| live.session | LiveSessionId、PlatformRoomKey、StreamerId、StartedAt、EndedAt | DataServer |
| live.slot | Platform+CanonicalLiveSessionKey UNIQUE、TenantId、ActiveBindingId、Generation | DataServer；跨 GameId 互斥 |
| live.binding | BindingId、LiveSessionId、Generation、State、CurrentRoomId | DataServer；活跃挂载唯一索引 |
| live.task | TaskId、TaskEpoch、Desired、Observed、OperationId、NextAttempt | DataServer |
| player.profile | PlayerId PK、Version、SchemaVersion、Data | DataServer |
| player.asset | PlayerId+AssetId PK、Balance、Version | DataServer |
| player.ledger | OperationId+LineId UNIQUE、delta、reason、resulting_balance | DataServer |
| match.ticket | TicketId、BindingId、State、Version、PartitionKey | DataServer |
| match.reservation | ReservationId、RoomId UNIQUE、ExpiresAt、PreparedReceipt | DataServer |
| game.room | RoomId PK、Status、Epoch、LastTick、StateVersion、PackageHash | DataServer |
| game.participation | RoomId+ParticipantId UNIQUE、BindingId、Role、State | DataServer；活跃占位唯一 |
| input.event | SourceDedupKey UNIQUE、PayloadHash、ReceivedAt、Disposition | DataServer |
| input.delivery | EventId+ConsumerScope UNIQUE、RoomId、RouteVersion、Sequence、State | DataServer |
| input.cursor | ConsumerScope PK、NextDeliverySequence、ConsumedContiguousSequence | DataServer；分配序号与写 delivery 同事务 |
| game.frame | RoomId+TickId UNIQUE、CommitId UNIQUE、InputRefs、Hash、Effects | DataServer |
| game.snapshot | RoomId+TickId+Schema UNIQUE、Payload、Hash、Verified | DataServer |
| game.result | RoomId+ResultVersion UNIQUE、FinalFrame、PlanHash、Result | DataServer |
| settlement.grant | RoomId+ResultVersion+PlayerId+GrantKey UNIQUE、Status | DataServer |
| infra.outbox | EffectId UNIQUE、Kind、Payload、Status、NextAttempt | DataServer |
| infra.operation_receipt | TenantId+GameId+OperationKind+TargetId+OperationId PK、RequestHash、CanonicalVersion、ResultJson | 事务执行端；与业务效果同事务；首个迁移见 `src/Chorus.Data/Redesign/0001_operation_receipt.sql` |
| infra.audit | AuditId、Actor、OperationId、Reason、Before/AfterHash | ControlServer / DataServer |

不在 JSON 字段里暗藏影响唯一性或授权的关键索引。大二进制快照首期存 PostgreSQL；将来外置对象存储必须使用“先写不可变 blob、验证可读，再提交引用”协议，并处理孤儿对象，不能先引用未上传文件。

## 6. 关键事务算法

### 6.1 CommitFrame

```text
BEGIN
  查 CommitId 回执；若存在，比较 RequestHash 并返回原结果
  按统一锁序锁 room ownership 行
  校验 OwnerInstance/Epoch、LeaseUntil、可提交状态
  锁 game.room；要求 prev_tick == LastTick 且 prev_version == StateVersion
  校验每个 InputRef 已持久、属于本局、尚未被另一帧消费
  写 game.frame（tick=LastTick+1）及唯一 CommitId
  若 FullCheckpoint：同事务写该 tick 完整候选状态与哈希；写失败则整帧回滚
  标记这些 delivery 已消费；更新 LastTick、StateVersion、StateHash
  写 Effects 到 Outbox（EffectId = RoomId/TickId/EffectIndex）
  若结束帧：写不可变 Result/SettlementIntent，并切换 Finishing
  写 receipt
COMMIT
返回 receipt；之后才可发布权威状态
```

重复 receipt 查询属于只读重放，旧 owner 可以得知自己历史提交结果，但不能因此获得新写权限。新 epoch 可以恢复旧 epoch 已提交的历史帧。

### 6.2 接管与写入竞争

接管与 CommitFrame 锁同一 ownership 行。若旧提交先拿锁且合法，它完成后新 epoch 才生效，新 owner 恢复时包含该帧；若接管先提交，旧请求被拒绝。租约到期的事务不能无限持锁：按事务超时回滚/终止，控制面等待实际锁释放后才能宣布接管完成。

所有权事务中不能调用远程恢复 RPC。恢复在分配提交后进行，恢复状态再经 fenced 事务写回。多个 DataServer 共用这一数据库锁和唯一键，不靠进程内 Semaphore 保证全局唯一。

### 6.3 AcceptInput 与局边界

接收时先去重查原回执；锁对应挂载、校验来源与路由版本并保存归属证据。客户端输入还必须锁/校验 auth.streamer_state 中的当前会话版本。挂载边界变更与新输入归属使用同一锁序。无可信 round hint 的事件按首次持久接收时的输入窗口归局；间歇/收尾窗口采用局外处置，不能承诺还原上游发生顺序。

事件可以先进入 UnresolvedInbox：持久接收是对已收到信息的保存承诺，不是承诺已经找到正确房间。之后 RouteEvent 事务只允许从 Unresolved/Accepted 转为一个明确 consumer scope，不允许同一输入悄悄进入两局。需同时做审计、个人贡献与局内效果时，使用不同消费者并各自幂等。客户端输入事务必须先锁主播会话/资格，再锁挂载；平台事件没有客户端会话时直接从挂载锁开始，均符合 §6.5 的统一锁序。

DeliverySequence 在 consumer cursor 行锁内分配，并与 delivery 插入同事务提交；回滚也回滚计数。不能用数据库 sequence 最大值作为消费高水位，因为分配顺序不保证提交顺序。扫描未处置记录并维护连续已完成水位，存在空洞时不越过；按类别跳过暂不能应用的输入必须单独保留 pending 状态，不能把它误标已消费。

### 6.4 资产与结算效果

```text
BEGIN
  查 OperationId 回执，拒绝同键不同 payload
  验证命令来源：有效主播授权，或已提交的 Frame Effect / SettlementGrant
  按 PlayerId、AssetId 排序锁账户行
  校验余额/库存/业务上限
  写 ledger lines，更新余额/库存
  标记 grant 完成，写投影 Outbox 和 receipt
COMMIT
```

结算器依赖已提交 effect 的不可变来源，不能因为房间 owner 后来迁移而拒绝一笔合法历史结算；也不能允许旧 GameServer 直接构造新的 grant。

### 6.5 统一锁顺序

跨域事务遵循：Coordinator（需要时）→ Ownership（按 kind/id）→ StreamerSession/资格 → LiveSlot/Binding（按 ID）→ Ticket/Reservation → Room/Participation → InputCursor/Input → PlayerAsset → Receipt/Outbox。只读初次去重可在事务前查；并发唯一键冲突回滚后查询原回执。

需要同时改变主播活动资格与匹配状态的流程必须在同一存储契约中实施，不允许先修改内存资格再异步写库。控制库和业务库首期是同一 PostgreSQL 数据库，以上事务不跨数据库。

### 6.6 FinalizeRound

```text
BEGIN
  读取不可变结果并识别所有待释放对象；按统一锁序加锁后重新验证
  校验结果版本、输入窗口关闭、重要输入终态、结算清单已完成或明确补偿处置
  校验同一结束操作的 DeliveryStopped 屏障及 PlatformClose 成功/NotRequired 凭据
  对 RoomId/Generation 仍相符的挂载清除 CurrentRoomId，关闭活跃 participation
  释放本 reservation/room 的容量与匹配占位（唯一释放回执防重复扣减）
  设置 Room Settled，撤销 room ownership 并递增 epoch
  写内部终结结果通知 Outbox、Finalize receipt（平台结束任务已在前置阶段完成）
COMMIT
```

这是一项由 ControlServer 业务协调器发起的受限终局事务。DataServer 仅按持久凭据、版本及约束执行，不承担收尾调度。完整时序见 [架构 §6.4](architecture.md)：先通知 PlatformGateway 关闭投递并取得 DeliveryStopped，才授权 PlatformGateway 调用直播平台；平台成功或有依据的 NotRequired 后才能 FinalizeRound，随后销毁运行实体。PlatformOutcomeUnknown 不满足前置条件。所有步骤绑定 EndOperationId、BindingGeneration、RoomId、RoundGeneration、CloseRouteVersion，重复和迟到命令不能影响新局。

## 7. 运行时与模块接口

```text
GameManifest {
  game_id, module_version, config_hash, state_schema_version,
  recovery_mode: DeterministicReplay | FullCheckpoint,
  tick_duration_ms, max_round_duration_ms,
  input_budget_per_tick, snapshot_size_limit,
  required_platform_capabilities, supported_runtime_rids
}
IGameModule {
  CreateInitialState(RoomSetup, DeterministicSeed) -> State
  ApplyTick(State, TickContext, OrderedInputs) -> CandidateState + Effects
  SerializeState(State) -> Bytes
  RestoreState(Bytes, SchemaVersion) -> State
  ComputeStateHash(State) -> Hash
  OnHostUnavailable(State, ParticipantId, Reason) -> InputCommand
}
```

示意接口要求 ApplyTick 不修改已提交状态；允许实现 copy-on-write 或可回滚缓冲，不能在失败重试时沿着已污染内存继续执行。随机源由 runtime 提供并纳入快照；计时器以 tick 表达。

跨 OS 确定性不是 C# 自动保证：浮点、集合遍历、本地库、序列化顺序均可能破坏重放。模块准入必须进行 Linux→Windows 和 Windows→Linux 快照恢复/重放测试；不通过则限制相同 runtime RID 恢复或选择 FullCheckpoint，并明确容量代价。

## 8. 持久操作与查询

长操作（发布、排空、恢复、结算）都返回 OperationId 和状态地址。状态为 Requested/Running/Succeeded/Failed/NeedsAttention；重试次数、最后进度和失败原因持久化。

客户端、运维和内部调用方都可以查询既有操作。禁止用“HTTP 200 启动了后台任务”冒充任务已成功。数据库 Outbox 的工作者用短事务 claim 和到期重领；处理期间不持数据库行锁调用平台。
