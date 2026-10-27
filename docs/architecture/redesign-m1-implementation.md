---
title: M1 主播生命周期实施状态
---

# M1 主播生命周期实施状态

M1 按 [验收计划](redesign-acceptance.md) 实施；真实平台无法联调时允许用明确标记为模拟器的契约实现验证其余流程，但不能把模拟器标成生产适配器。完整目标见[架构第 5–6 章](architecture.md#5-主播登录实体激活和挂载)与[协议契约](redesign-contracts.md)。

## 本轮已落地：身份与登录凭据持久化基础

`RedesignIdentityStore` 在 PostgreSQL 上实现：

- Platform/AppId/OpenId 到稳定 StreamerId 的作用域映射，禁止同一平台身份被另一个 Tenant 重新认领。
- 新 Login 以 streamer 行锁串行化，SessionVersion 单调递增并撤销旧控制会话；只保存至少 256 bit resume secret 的 SHA-256 摘要。
- Resume 原子轮换 secret 与 SessionVersion；精确 OperationId/请求重试在两分钟窗口返回同一 SessionGrant；不同请求重用 operation、旧 secret 重放或已被新 Login 接管时拒绝。
- 会话数据库记录与映射使用现有 M0 `auth.*` schema，使用数据库事务和 streamer_state 行锁，不在事务中执行外部网络调用。

数据库集成证据：`RedesignIdentityStoreTests.PlatformIdentity_LoginTakeoverAndResumeRotationAreTenantScopedAndIdempotent`。此项是 M1 的持久化基础，尚不等于 StreamerServer API 或完整主播登录流程。

## 本轮已落地：平台启动验证接口与开发模拟器

`RedesignPlatformGateway` 按平台名路由 `VerifyLaunch`；`DevelopmentPlatformSimulator` 用 HMAC-SHA256 签发并验证开发启动凭据（AppId 作用域、过期、固定时间签名比较、稳定拒绝码、无签名秘密返回）；`DouyinLaunchAdapter`/`KuaishouLaunchAdapter` 为 `NotProductionReady` 失败关闭桩。生产宿主默认拒绝非 ProductionReady 适配器，开发宿主显式打开；官方协议版本、脱敏真实样本、签名正反用例与成功回执齐备前，桩不验证任何凭据，模拟器证据不折算为生产适配器。契约证据：`RedesignPlatformGatewayTests` 5/5（含伪造/过期/错 AppId/未知平台/越权适配器输出/凭据不外泄）。此项是 M1 平台接入前置，尚不等同 AT02（需真实平台样本）。

## 本轮已落地：StreamerServer 登录/恢复宿主与短时访问凭据

`Chorus.StreamerServer` 新宿主（mTLS，端口默认 7502，自身无数据库连接）与配套边界：

- `m1.proto` 定义 `StreamerSession`（Ready/Login/Resume，面向网关）与 `StreamerIdentity`（ResolveIdentity/Login/Resume/EnsureEntityProfile，DataServer 持久化边界）。StreamerServer 进程证书为 @cluster 作用域，业务 tenant 放在请求字段中，DataServer 逐项对照 `auth.identity` 归属映射校验；Resume 在轮换 secret 之前先做 tenant 栅栏，避免合法会话的 secret 被跨租户请求烧掉。
- `RedesignStreamerLoginCoordinator` 固定次序：租户-应用映射检查 → VerifyLaunch → ResolveIdentity → 实体资料加载 → Login/Resume。实体加载失败抛 `EntityNotReadyException` 且不会产生会话（单测断言 Login 未被调用）；`EnsureEntityProfile` 在 DataServer 侧以幂等 ledger 初始化 profile（并发冲突重读确认）。
- `ChorusAccessCredential`：HMAC-SHA256 短时访问凭据（base64url payload + 签名、固定时间比较、过期拒绝、密钥 ≥256 bit），Login/Resume 成功后签发。
- 真实宿主集成证据：`RedesignM1HostTests.SimulatorLaunchLoginsRotatesResumeAndFencesTenantsAcrossRealHosts`（Control+Data+Streamer 三真实进程：模拟器登录 v1 并落库 profile、Resume 轮换 v2、重试幂等、接管 Login v3、旧会话/伪造凭据/未映射应用/跨租户证书全部 PermissionDenied、访问凭据正确密钥可验错密钥为 null）。协调器契约证据：`RedesignStreamerLoginCoordinatorTests` 5/5。Release 全量 1163/1163。

尚未完成（不计为验收）：面向客户端的 StreamerGateway 宿主、Logout；尚不等同 AT01–AT09 完整验收。

## 本轮已落地：Attach generation 与 access credential 消费端校验

按契约 3.3「换 Gateway 的请求通过当前 session/secret 发起新的 `AttachConnection` 幂等操作，递增 ConnectionGeneration；旧 Gateway 的关闭通知必须携旧 generation，不影响新连接」：

- 迁移 0005：`auth.session.connection_closed_at`（当前代关闭标记）与 `auth.rotation.result_connection_generation`（attach 结果代，供精确重试返回同代）。
- `RedesignIdentityStore.AttachConnectionAsync`：当前 resume secret 授权、不轮换任何秘密；行锁下递增 generation 并重绑 gateway/connection、清除关闭标记；同 OperationId/同请求哈希在重试窗口内返回同代（Duplicate），同操作不同连接或错 secret 拒绝；会话被接管/吊销时拒绝。
- `CloseConnectionAsync`：仅当 gateway、connection、generation 三者与当前行完全一致才标记关闭；旧代、错代、未知会话一律不改动（返回未应用）。重新 attach 会清掉关闭标记。
- `GetSessionStateAsync`：会话事实快照（版本、到期、吊销、关闭、封禁、是否当前代、归属租户），供消费端判定。
- `StreamerSession` 新增 `AttachConnection`/`ConnectionClosed`/`ValidateAccess`：ValidateAccess 先验 HMAC 签名与过期，再对照会话快照要求"当前代且未吊销/未过期/未封禁/版本一致/租户一致"，否则 PermissionDenied；离线（online=false）是路由事实不是认证失败，不拒答。
- 证据：store 级 `RedesignIdentityStoreTests.ConnectionsAttachGenerationsStaleCloseDoesNotDetachAndStateReflectsFacts`（含重开与新登录吊销后的事实）；宿主级 `RedesignM1HostTests.GatewayAttachRotatesGenerationsAndStaleCloseNeverDetachesTheNewConnection`（真实三进程：消费端校验、attach 幂等、旧代/错代关闭无效、当前代关闭后离线、伪造凭据与跨租户校验/关闭全拒）；协调器级 `AttachConnectionPassesThroughWithoutRotation`、`ValidateAccessBindsCredentialToCurrentSessionState`。Release 全量 1167/1167。

AT03 中「新连接不被误标离线」的存储与边界语义已就绪；但 AT03 完整验收还需双终端与响应丢失注入（属后续验收轮）。

## 本轮已落地：直播槽与 GameplayBinding 唯一挂载

按架构 6.2「同一直播切换玩法必须完成旧挂载关闭与本局终结处理，再提交新挂载 generation；挂载唯一约束防止两个客户端同时创建玩法」：

- 迁移 0006：`live.session.canonical_live_session_key` + 部分唯一索引，把已验证直播键链接到跨 GameId 挂载槽（槽主键自 0002 起即 platform+canonical key，无 GameId 维度）。
- `RedesignBindingStore.OpenLiveSessionAsync`：登录时以 VerifyLaunch 得到的规范直播键幂等注册直播会话与槽；同一键不得改绑其他租户或其他主播。协调器 Login 在实体加载通过后、会话提交前调用。
- `StartGameplayAsync`：槽行锁内一个事务完成——会话代栅栏（重读 `current_session_id`，被接管的控制会话不能换挂载）→ 关闭旧活跃挂载 → 新挂载 generation=槽代+1 → 槽指向新挂载；操作幂等走 M0 回执库，键为直播槽作用域（`@cluster` 游戏位），同 OperationId 换 GameId 是冲突不是第二个挂载。
- `GetLiveSession`/`GetBindingStatus` 查询供登录恢复与状态展示；`StreamerSession.StartGameplay`/`GameplayStatus` 以 access credential 鉴别行动会话。
- 证据：store 级 `RedesignBindingStoreTests.LiveSlotIsCrossGameAndHoldsOneActiveBindingAcrossSwitches`（幂等/改绑拒绝/跨 GameId 切换唯一活跃/同操作异请求冲突/接管栅栏/未知直播会话拒绝）；宿主级 `RedesignM1HostTests.GameplayBindingStartsSwitchesAcrossGamesAndFencesTakenOverSessions`（真实三进程 + 数据库断言 Closed/Ready 各一、槽指向 gen2；未映射游戏与被接管凭据 PermissionDenied）；协调器级 `StartGameplayRequiresMappedGameLiveSessionAndCurrentAccess`。Release 全量 1170/1170。

## 本轮已落地：StreamerActor 内存状态机与切换途中重启恢复

按架构 5.3「StreamerActor 内存状态：Loading → Ready → RetainingOffline → Deactivating；任一步可进入 RecoveryFailed」与 5.1 步 5「加载资料、当前挂载……进入 EntityReady。恢复失败时不发送登录成功」：

- `StreamerActor`/`StreamerActorRegistry`（Chorus.StreamerServer）：内存实体按单一信箱串行转移状态；连接、会话、直播、挂载四类事实独立观测，不合成单一 online 布尔。数据库是权威，注册表只是投影——进程重启丢掉的条目由下一次登录/恢复的 `RecoverAsync` 从持久事实重建。带会话恢复落在 Ready（在线）或 RetainingOffline（关闭/吊销/过期：保留内存等待 resume）；显式 Deactivating 决策在事实刷新中存活；无有效挂载且无当前会话才允许卸载（对局/待办事务 M1 尚未建模）。
- 协调器接线：Login 在实体资料加载与直播会话打开之后、会话提交之前执行实体恢复（含待恢复挂载）；任何恢复失败进入 RecoveryFailed，不返回 LoginReady 也不创建会话（gRPC 映射 FailedPrecondition）。Resume/AttachConnection/ConnectionClosed 驱动 Ready↔RetainingOffline；挂载切换后同步更新内存投影。
- 切换途中重启（AT04 重启半边）两级确定性证据：
  - store 级 `RedesignBindingStoreTests.MidSwitchCrashKeepsOldMountAndRetryWithSameOperationConverges`：`crashAfterClosingOldBinding` 注入在关旧挂载后、事务提交前抛出——整个回执事务（含回执占位与业务写）回滚，旧挂载仍是唯一活跃且槽指向不变；同 OperationId 重试干净收敛到 gen2 新游戏、恰好一个活跃挂载。
  - 宿主级 `RedesignM1HostTests.StreamerServerRestartRecoversTheSingleActiveBindingAcrossSwitches`：真实 StreamerServer 进程 Kill 后同端口重启（内存 actor 全部丢失），客户端凭平台身份重新登录（会话 v2 接管），GameplayStatus 恢复出崩溃前的同一 bindingId/gen1，随后跨 GameId 切换 gen2 且数据库断言唯一活跃；崩溃前的访问凭据被接管拒绝。
  - 协调器级 `RedesignStreamerActorTests` 4 例：登录把恢复事实装进 Ready actor 且切换更新投影；恢复失败拦截 LoginReady（无会话创建）且重试收敛；连接关闭→RetainingOffline、Resume/Attach→Ready（离线是路由事实非会话丢失）；空注册表重启恢复已提交事实（崩溃前记忆不可见，只认已提交绑定）与卸载门槛。

第 3 步（含 StreamerActor 状态机与切换途中重启注入）至此完整。Release 全量 1177/1177。

## M1 后续顺序

1. ~~PlatformGateway 的验证接口和 Development Platform Simulator；Douyin/Kuaishou 实现分别保留 `NotProductionReady`~~（已落地，见上）。
2. ~~StreamerServer Login/Resume API、实体资料加载成功后才返回 LoginReady~~；~~同连接 Attach generation 与 access credential 消费端校验接线~~（均已落地，见上）。
3. ~~GameplayBinding 唯一挂载、跨 GameId LiveSlot 冲突事务~~；~~StreamerActor / LoginCoordinator 状态恢复（内存状态机与切换途中重启注入）~~（均已落地，见上）。
4. 持久 PlatformTask、NormalizedEvent/UnresolvedInbox、回调提交后确认、乱序/迟到事件归属（下一项）。
5. 逐项完成 AT01–AT09；外部真实平台需要测试应用/样本时，模拟器证据与生产验收分开记录。

目前没有面向客户端的 StreamerGateway 宿主、Logout、完整轮换响应丢失集成测试、平台持久收件箱或对局终结处理（ClosingInput 窗口属挂载切换的后续）；真实平台适配器保持 `NotProductionReady`。M1 保持进行中，不将旧 Gateway/Actor 代码折算为新架构验收。
