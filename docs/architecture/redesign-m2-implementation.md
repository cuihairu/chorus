# M2 单节点完整对局实施状态

按验收出口（redesign-acceptance.md 102–106 行）：确定性示例玩法、RoomActor、FrameStore、快照/重连、默认资产库存、结算、排行榜、双主播客户端示例；须演示一局从启动、输入、断线、重连到结算（AT13–21 + AT27 单节点部分）。AT16/17 正式属 M4，不折算。分期 7 轮，每轮独立绿门；测试库扩族 `chorus_m2_<topic>_<guid>`（清理白名单加入 `chorus_m2_%`）。

## M2-1 已落地：局准入、RoundGeneration 与事件按局路由（12d6671）

按架构 2/6.2/6.3「开局是控制面所有权栅栏下的一个回执事务：参与者按 BindingId 排序锁定，挂载指针带着 per-mount 局计数移动」「事件按局归属：可信 round_hint 优先，否则绑定当前局；终态局 OutOfRound 终态可见、映射冲突 AmbiguousRound 待核对，绝不静默隔离」：

- 迁移 0009：`live.binding.round_generation`（per-mount 局计数）；`input.event.attributed_room_id`（持久局归属证据，重复事件沿用第一次归属）；部分索引 `ix_input_event_round_review`（AmbiguousRound 审查扫描）与 `ix_input_delivery_pending`（按局收件箱）。纯增量，0008 前语义不变。
- `RedesignRoomStore`：StartRoundAsync 一个回执事务（键 `(tenant,game,"round.start",roomId,operationId)`，canonical v1 含排序后参与者与模块钉住参数）；§6.5 锁序 ownership(FOR UPDATE 栅栏)→排序绑定→房间行；参与校验覆盖跨租户/跨游戏/非活跃态/在局指针/活跃或离线参与（离线仍占位，`ux_game_participation_active` 兜底）；指针移动即 `round_generation+1`，`crashAfterPointerUpdate` 注入缝；outbox `round.started`。AbortRoundAsync 释放同一集合：participation Closed→仅相符指针清空→房间 Aborted→所有权 Released+epoch+1（§6.6 单一释放骨架）→outbox `round.aborted`；abort 后任何再 abort 被已释放栅栏拒绝（精确重试仍走回执回放）。GetRoundAsync 只读快照。
- `RedesignRoundInputStore`：RouteEventsAsync 四趟（无锁候选窗→排序锁绑定→按 event_id 锁事件并复核未路由→逐件裁决）：hint=payload `round_hint`（文档化模拟器约定，不动 M1 canonical 指纹）且该绑定确实参与过提示局才可信，否则 AmbiguousRound；无 hint 用绑定当前局；目标局 Preparing/Running 才入箱——cursor 行锁下分配稠密 sequence、INSERT delivery(Pending, route_version=round_generation)、事件置 Routed+attributed_room_id；终态局/无指针 OutOfRound 终态记录（可审查、不静默 GC）。`consumed_contiguous_sequence` 不动（属 M2-2 帧提交）。`crashAfterCursorAllocation` 注入缝。AcceptClientInputAsync：回执键 commandId（天然命令去重）；§6.3 校验链 auth.session→streamer_state(当前会话+Allowed)→session 行(未吊销未过期)→参与预读→绑定(指向本局)→participation(Active)→room(epoch 相符否则 StaleOwner)→INSERT `@chorus` Durable 事件(Routed+归属)+同 cursor 锁 delivery；接管/参与闭/epoch 错均可见拒绝。
- `RedesignInputEventStore`：PlatformEventReceipt 增可空 RoomId——重复接收回放已提交的原局归属（跨代重投递不重派生）。
- `m2.proto`：`RoundStore{Ready,StartRound,AbortRound,GetRound}`、`RoundInput{Ready,RouteEvents,AcceptClientInput}`（DataServer 宿主）；RoundGrpcService/RoundInputGrpcService 按证书定租户，角色面：准入/中止 ← control-server、game-server；路由 ← platform-gateway、game-server、control-server；客户端命令 ← streamer-server、game-server；StaleOwner→FailedPrecondition、IdempotencyConflict→InvalidArgument。
- 证据：store 级 `RedesignRoomStoreTests` 2 例（准入原子/幂等/异请求冲突/错实例与旧 epoch 栅栏/在局绑定可见拒绝/未知绑定拒绝；Abort 释放指针+所有权 epoch+1、幂等重试、再 abort 被释放栅栏拒绝、re-claim 后同挂载 round_generation=2）与 `RedesignRoundInputStoreTests` 3 例（归属随提示与首收窗口：无提示→当前局稠密单调序列且消费水位未动、局间无指针→OutOfRound 无局、新一代→新局 seq 1、提示已中止局→OutOfRound 带原局 id、提示他绑定局→AmbiguousRound 可扫描、重复接收回放原局 RoomId；路由崩溃注入三方回滚后同操作收敛再试 Duplicate；客户端命令幂等/同 id 异载荷冲突/接管拒绝/新会话续 seq/旧 epoch 栅栏/abort 后拒绝）；宿主级 `RedesignM2HostTests` 2 例（真实三进程：控制面 Claim→game-server StartRound→gateway ingest+route→SQL 断言→DataServer Kill+重启→同操作 Duplicate/GetRound 完好、streamer-server 角色与跨租户调用双拒；真实登录流 AcceptClientInput 幂等/gateway 角色 PermissionDenied/Abort 后同命令 FailedPrecondition，DB+回执+delivery 三方一致）。Release 全量 1192/1192。

AT 台账增量（不标满过，只记闭合半边）：

- **AT05 对局归属半边闭合**：round_hint 可信线索归原局、无局标识按首收窗口固定（binding 当前局）、映射冲突 AmbiguousRound 待核对可扫描、重复接收沿用第一次归属——M1 阻塞项「对局归属阻塞 M2 RoundGeneration」解除；AT05 全项待 M2-4 重连与 M2-6 客户端语义后收口。
- **AT08 对局归属半边闭合**：同一事件恰一条 delivery（稠密 sequence、cursor 行锁），重复批次不重复入箱；帧级顺序重放属 M2-2。
- **AT06 房间模型半边闭合**：房间/参与/指针/代数事务化，Abort 后可开下一代；中断恢复与重连快照半边属 M2-4/M2-6。

## M2-2 已落地：FrameStore 权威帧提交与对局收件箱（2d8647e）

按架构 8.2「一次 Tick：读取持久输入→确定顺序→候选状态执行→生成 FrameRecord 与 Effects→DataServer 事务校验 owner/epoch 与前帧版本，写帧记录、消费标记、幂等业务效果 Outbox→提交成功→发布权威状态」「结果未知时以 CommitId 查询，已提交则继续」与 FullCheckpoint 条款「每 tick 在 CommitFrame 同一事务内保存完整候选状态……不能偷偷降级为不可恢复」：

- `RedesignFrameStore.CommitFrameAsync`：回执键 `(tenant,game,"frame.commit",roomId,commitId)`（CommitId 天然幂等查询锚），canonical v1 含排序输入引用、状态哈希、Effects、checkpoint 哈希与终局结果；§6.5 锁序 ownership 栅栏→room FOR UPDATE（status 须 Running、`prev_tick==last_tick && prev_state_version==state_version` 否则 `FrameConflictException` 可见拒绝，tick=last_tick+1）→输入 delivery 校验（每个引用须为本局收件箱 Pending、重复引用与他帧已消费均拒）→标记 Consumed→INSERT `game.frame`（commit_id 唯一、input_refs 保序）→FullCheckpoint 分支同事务写 `game.snapshot`（失败整帧回滚，verified 留给 M2-4 恢复语义）→水位 `GREATEST` 只跨连续已消费前缀（min(未消费)-1，绝不越洞、绝不回退）→room last_tick/state_version+1/state_hash→Effects 逐条 outbox（effect_id `{roomId}/{tick}/{index}`，kind 禁 `/`）→final 帧同事务写 `game.result`(v1)+`settlement.grant`(Planned)+房间 Finishing→回执 {tick,stateVersion,watermark}。`crashAfterFrameInsert` 注入缝。
- `ReadInboxAsync`：只读对局收件箱（§6.1.6）——Pending delivery 按序号排列带 payload 与 route_version，附连续水位供 RoomActor 续跑定位。
- `m2.proto` 增 `FrameStore{Ready,CommitFrame,ReadInbox}`；FrameGrpcService：CommitFrame 仅 game-server（房主），ReadInbox 允许 game-server/control-server（诊断）；FrameConflict/StaleOwner→FailedPrecondition、IdempotencyConflict→InvalidArgument。
- 证据：store 级 `RedesignFrameStoreTests` 3 例（栅栏/幂等/同 CommitId 异帧冲突/旧 prev tick 拒/旧 epoch 拒/水位连续推进不越洞、已消费与外部引用与重复引用三拒；FullCheckpoint 成功落 snapshot 与碰撞 23505 整帧回滚（帧/消费/水位/房间四方归零）后同 CommitId 干净重试、final 帧 result+Planned grant+Finishing+Finishing 后拒再提交；崩溃注入全量回滚后同操作收敛再试 Duplicate）；宿主级 `RedesignM2HostTests.FrameCommitsSurviveDataServerRestartAndReplayFromReceipts`（真实三进程：ReadInbox 2 项→CommitFrame tick1 水位 2→DataServer Kill+重启→同 CommitFrame Duplicate、收件箱已清、控制面可读诊断、streamer/control 角色 CommitFrame 双 PermissionDenied、SQL 单帧）。房间 Running 激活属 M2-3，测试以 SQL 直置（注释注明）。Release 全量 1196/1196。

AT 台账增量（不标满过，只记闭合半边）：

- **AT14 帧部分闭合**：一帧=一回执事务（帧/消费/水位/房间/EOutbox 同进同出），崩溃后同 CommitId 收敛；AT14 全项待 M2-3/M2-4 的 tick 链与恢复后收口。
- **AT27 帧部分闭合**：提交边界崩溃重启重放回执、前帧版本乐观校验、迟到低 tick 拒绝；终局与结算部分属 M2-5。

## M2-3 已落地：示例玩法、GameServer 宿主与 RoomActor（1a61bc0）

按架构 §7/§8.2/§8.4「玩法模块是受信服务端逻辑包：纯 ApplyTick→候选状态+Effects（写时复制）、Serialize/Restore 带版本守卫、ComputeStateHash；tick 节奏与快照策略由 manifest 钉住；运行时供给的 RNG 状态入快照、计时=tick、整数运算+有序集合保证确定性」「宿主不可用时模块不得凭新读臆造现实」「GameServer 无 DB 凭据，全部持久状态走 DataServer」：

- `Chorus.ExampleGame`：ExampleGameModule 双阵营计分——CreateInitialState 校验阵营（red/blue）与参与者去重；ApplyTick 纯函数（LCG 种子每 tick 恰推进一次并随状态携带；gift→阵营加分、boost→10×level+seed%5；未知形状与未知参与者确定性忽略；DurationTicks 到点冻结排名出 Result，并列→draw）；Serialize 规范 JSON（snake_case、有序字典）；Restore 仅认 schema v1 否则 fail-closed；ComputeStateHash=SHA256(Serialize)；OnHostUnavailable 返回空控制输入；manifest Default 50ms/快照每 100 tick/DeterministicReplay、FastTest 25ms；FailAfterTick 确定性故障缝（文档化测试变体，非宿主行为）。
- `RedesignRoomStore` 生命周期操作：PrepareRoundAsync（回执 "round.prepare"；canonical 承诺快照哈希+schema，载荷与哈希同变才构成异请求；仅 Preparing 可写 tick-0 snapshot，verified=false 留给 M2-4 恢复语义）；ActivateRoundAsync（"round.activate"；须 Preparing 且存在 tick-0 快照，否则可见拒绝——房间绝不无初始状态运行；→Running、state_version=1、outbox `{roomId}/0/activate`）；SuspendRoundAsync（"round.suspend"；仅 Running 可挂起；→Suspended、outbox `{roomId}/0/suspend`；挂起房不再收帧不收新投递，状态保留待恢复）；AbortRound 扩为接受 Suspended 作终局逃生口。三者均所有权栅栏+幂等重试回放。
- m2.proto：RoundStore 增 PrepareRound/ActivateRound/SuspendRound（← control-server、game-server）；新 GameHost 服务（Ready/PrepareRoom←control-server+game-server/RecoverRoom→Unimplemented，M2-4 落地）。
- `Chorus.GameServer`（新工程，克隆 StreamerServer 骨架，端口 7503，无 DB 凭据）：宿主服务证书为 @cluster、数据面客户端身份为独立租户证书（CHORUS_CLIENT_CERT_PEM/KEY）——DataServer 从证书取租户，无需请求字段；RoomRuntime 持单 channel 与 actor 注册表，PrepareRoom=CreateInitialState→Serialize→PrepareRound(op+"/prepare")→ActivateRound(op+"/activate")→启动 actor，同房重入直接 Duplicate；RoomActor 单循环 PeriodicTimer(manifest.TickMs)：ReadInbox(≤200)→全部引用入帧、仅已知绑定映射为模块输入（外来投递入帧不臆造参与者）→ApplyTick→CommitFrame（commitId=`{roomId}/tick-{tick}` 天然幂等锚；tick%100==0 走 FullCheckpoint 同事务快照；final 帧 ResultJson+PlanHash=SHA256(result)）；结果未知（网络异常）以同 commitId 下次唤醒重放（§8.2）；明确拒绝（FailedPrecondition/InvalidArgument）或模块连续 3 次失败→SuspendRound 可见挂起并停 actor，他房继续。
- 证据：模块级 `ExampleGameModuleTests` 3 例（同配置同脚本双实例哈希一致、快照恢复后续 tick 哈希一致且种子在快照内、终局排名/draw/未知形状忽略、schema 拒识、tick 契约、故障缝按 tick 触发、manifest 钉住、OnHostUnavailable 空）；store 级 `RedesignRoundLifecycleTests` 1 例（未 prepare 即 activate 拒、prepare 恰一次+幂等+异快照冲突+错实例/旧 epoch 双栅栏、activate→Running v1+outbox+重激活拒、帧窗口开放、suspend→Suspended+outbox+重复挂起拒+挂起后拒帧、abort 逃生）；宿主级 `RedesignM2HostTests.GameServerRunsRoundsToResultAndIsolatesModuleFailures`（真实四进程：控制面 Claim→StartRound→control 证书经 GameServer PrepareRoom×2（健康房 40 tick@25ms+故障房 FailAfterTick=2）→gateway 三礼物 ingest+route→健康房 Finishing（40 帧、投递全消费、水位 2、state_version 41、result winner=red 100:30、恰 2 条 score 效果、双房 tick-0 快照）而故障房 Suspended（≤1 帧、round.suspended outbox）——同进程双房一故障零交叉；重复 PrepareRoom Duplicate；streamer 角色 PrepareRoom PermissionDenied；RecoverRoom Unimplemented）。Release 全量 1201/1201。

AT 台账增量（不标满过，只记闭合半边）：

- **AT13 隔离半边闭合**：模块故障按房隔离——连续 3 败→可见挂起，同进程他房跑到终局；故障经模块缝注入。分布与压力证据待 M2-7。
- **AT14/AT27 帧链半边推进**：tick 链由真实 actor 连续提交驱动、final 帧冻结 Result；快照恢复链属 M2-4。

## M2-4 已落地：快照与恢复（409f677）

按架构 §8.2「恢复=最新可用快照+其后帧逐帧验哈希重放，重放只读、绝不重触发已提交效果」与 §10.3「恢复保状态：最新快照损坏回退前一可用；反复失败→可见 Suspended；结束阶段恢复尾不重启战斗 tick」：

- `RedesignRecoveryStore`（DataServer）：PutSnapshotAsync——回执 "snapshot.put"，canonical 承诺 {RoomId,Tick,Hash,SchemaVersion}；store 自验 SHA256(payload)==hash，哈希不符不入库（可见即完整）；锁序 ownership FOR UPDATE→room FOR UPDATE（须 Running 且 tick==last_tick，快照只钉头部）→plain INSERT verified=true，故意不 upsert——同 tick 异快照是真实分歧，以约束冲突暴露而非静默丢；crashAfterSnapshotInsert 注入缝（插入后提交前死亡→整事务回滚，半写快照不可见、无回执）。LoadRecoveryAsync——只读恢复计划一次成读：房间头（status/epoch/last_tick/state_version）+全部快照 newest-first（含 verified 标志）+投递日志按 sequence（event_id/binding/payload/consumed）+最旧快照之后的帧（input_refs 保序反序列化、state_hash、module_version、config_hash）+参与映射；未知房间 null。
- m2.proto：FrameStore 增 PutSnapshot（←game-server）/LoadRecovery（←game-server+control-server 诊断）；GameHost.RecoverRoom 由占位转正（←control-server、game-server），Reply 增 status/last_tick/snapshot_tick/replayed_frames；FrameGrpcService 映射 StaleOwner/FrameConflict→FailedPrecondition、冲突/参数→InvalidArgument。
- RoomRuntime.RecoverRoomAsync：注册表命中分两答——已终局 actor（Completed 标志）答 Finishing 尾、在跑 actor 答 already running，都不重启 tick；否则 LoadRecovery→未知房/epoch 不符/非 Running 可见拒；Finishing 直接 Recovered=true 收尾（M2-5 补结算尾）；Running 走重放：快照 newest→oldest 逐候选——Restore fail-closed 拒识或 ComputeStateHash 与库中哈希失配即跳过（失配是证据不是猜测）→其后帧逐个 tick 连续性校验、InputRef 经投递日志映射回参与者（deliveryByEvent+participantByBinding）、ApplyTick 重算、帧哈希比对，全链达头→PutSnapshot 钉 store-verified 头部快照（确定性 op id {owner}/snapshot/{room}/{tick}，被拒 Recovered=false 可见）→actor 自 (state, lastTick, stateVersion) 续 tick；全部候选失败→SuspendRoom 可见挂起。重放只读：帧与效果已提交一次。
- RoomActor：ctor 增 lastTick/stateVersion 恢复锚；final 帧提交后置 Completed——RecoverRoom 据此答尾不答跑。
- 证据：store 级 `RedesignRecoveryStoreTests` 2 例（崩溃缝半写不可见且无回执、同 op 重试收敛单行 verified、哈希不符拒、非头部 tick/挂起房 FrameConflict、错实例 StaleOwner、同 op 异内容 IdempotencyConflict；恢复计划全形状——头部字段、快照 desc 含 verified、投递序号/绑定/payload/consumed、帧引用+哈希+模块与配置版本、参与映射、未知房 null）；宿主级 `RedesignM2HostTests` 增 2 例并更新 M2-3 例断言——
  - `KilledGameServerRecoversAtTheHeadAndRunsToTheSameResult`：200 tick@25ms 跑过 tick-100 检查点后 Kill GameServer（头部以 Kill 后 DB 实况为准，杀进程与轮询读数之间仍会落几帧）→同端口重启→RecoverRoom Recovered=true、SnapshotTick=100、ReplayedFrames=head−100→头部 store-verified 快照行落库→续跑到 Finishing（200 帧连续、last_tick 200/state_version 201）；同配置同礼物的未受扰孪生房（Kill 后才 Prepare、从未被杀）与恢复房 result 逐字节相等——崩溃+恢复不移动确定性结局；快照链恰 {0(prep,false),100(checkpoint,false),head(verified,true),200(checkpoint,false)}，投递恰一次消费。
  - `CorruptSnapshotsFallBackToTheAnchorOrSuspendVisibly`：长房 tick-100 快照 payload_hash 损毁（哈希失配路径）→回退 tick-0 锚全量重放（SnapshotTick=0、ReplayedFrames=head）仍完赛 winner=red；短房唯一 tick-0 锚 payload 损毁（Restore 拒识路径）→Recovered=false、房间 Suspended、round.suspended outbox、帧数冻结在崩溃头部。
  - M2-3 例 RecoverRoom 断言由 Unimplemented 转：Finishing 房恢复为尾（Recovered=true、帧数仍 40，不重启战斗 tick）。

AT 台账增量（不标满过，只记闭合半边）：

- **AT19 闭合**：快照写一半→事务原子+store 自验哈希，可见即完整；最新快照损坏→回退前一可用（哈希失配与拒识两路都有据）；旧快照重放→逐帧验哈希到头+未受扰孪生结局逐字节相等；Finishing 恢复→尾恢复不重启战斗 tick。四条验收词全部落地。
- **AT14/AT27 帧链推进**：恢复链与帧哈希互证闭合（帧哈希在恢复中被逐帧校验）；终局与结算部分仍属 M2-5。

## M2-5 已落地：结算、FinalizeRound 与排行榜（04f3944）

按架构 §9.2/§9.3「终局清单不可变、逐笔入账、FinalizeRound 是不依赖原 game host 的持久终结者」与 §6.6「终局原子释放占位与挂载指针」：

- 迁移 0010：`settlement.grant` 增 `asset_id`（默认 'points'）与 `delta`（非零约束在 store 层）；新投影表 `game.leaderboard_score`（PK (tenant,game,board,player)，`score DESC, player_id ASC` 排名索引——确定性并列序）。
- `RedesignFrameStore` 清单扩展：PlannedGrant 增 AssetId/Delta，指纹随之承诺；final 帧事务写入的 `settlement.grant` 行携带资产与数额——清单即终局事实，提交后不可再变。
- `RoomActor` 终局清单推导：final 帧 ResultJson 的 ranking{faction,score} × 参与者阵营（FactionByParticipant 随状态入快照）→ 按 player id 序生成 grant（GrantKey "round-score"、OperationId `{roomId}/grant/{player}`、仅正分）；恢复出的 actor 从快照重导出同一清单——崩溃前后清单逐字节一致。final 提交成功后即调 SettleRoom（结果未知吞 RpcException：清单进度已持久，恢复/重试续传）；Finishing 房恢复尾同样先 Settle 再答。
- `RedesignSettlementStore`（DataServer）：
  - CommitResultAsync：读清单（max result_version、Pending 按按 player/grant 排序）→ 逐 grant 独立回执事务（identity (tenant,game,"settlement.grant",room,grant.OperationId)，canonical {PlayerId,AssetId,Delta,GrantKey}）：种默认资产行（ON CONFLICT DO NOTHING）→ `UPDATE balance=balance+delta ... WHERE balance+delta>=0 RETURNING`（会打负即 SettlementRejected 可见拒绝）→ 账本恰一行（line_id 'grant'，reason `settlement:{room}/{grantKey}`）→ grant→Granted → 排行榜投影 upsert（score+=delta）→ 回执。第 N 笔崩溃只回滚该笔，重试只补未完成——每笔恰一账本效果。`crashAfterGrant` 注入缝。全清后重复提交 → Duplicate（pending=0）。
  - FinalizeRoundAsync：回执 "round.finalize"（canonical {RoomId}）；ApplyFinalize 依次校验并全部可见拒绝：房间须 Finishing（Settled→"already settled"）、result 存在、清单全 Granted、本局 Pending 投递清零（consumer_scope room:x）；然后单一事务：participation→Closed（RETURNING binding_id）→ 有序清各绑定 `current_room_id`（仅仍指向本局者）→ 房间→Settled → 所有权 Released epoch+1（终结者不持所有权，仅 FOR UPDATE 不校验 owner）→ outbox `{roomId}/0/settle` round.settled。重复终局回 Duplicate；异 op 再终局可见拒绝。下一局=新房间 id，同绑定 round_generation+1。
  - ReadLeaderboardAsync：`ORDER BY score DESC, player_id ASC LIMIT`（1..1000），board_id=asset_id。
- m2.proto 新 Settlement 服务（DataServer）：Ready←control/game；CommitResult←game/control；FinalizeRound←control/game；ReadLeaderboard←control/game/streamer。三个请求带可选 `tenant_id`：@cluster 证书（控制面）必须指名租户（act-as-tenant），租户证书继承证书租户、指名他租户即 PermissionDenied——无跨租户穿透。SettlementRejected→FailedPrecondition。
- 证据：store 级 `RedesignSettlementStoreTests` 4 例——① 第 2 笔崩溃→p1 Granted/p2、p3 Planned、账本恰 1 行；重试收敛 3 Granted、账本 DISTINCT operation_id=count(*)=3、余额/榜单一致；异 op 重发 Duplicate；未知房可见拒绝。② FinalizeRound 栅栏族：Running 房拒、清单未清拒、Pending 输入未清拒；happy path Settled+epoch+1+participation Closed+指针 NULL+outbox round.settled，重复 Duplicate、再终局拒；同绑定开下一代 round_generation=2。③ profile.patch 声称 `{"assets":{"points":999999}}` → 资产仍 100、账本仍 1 行、榜单不动（JSON 无覆盖账本资产之路）。④ 双房并行结算同一批玩家：账户行串行化、每 grant 恰一行、余额与榜单均双局之和（并发资产增减）。宿主级 `RedesignM2HostTests.SettledRoundFinalizesFreesMountsAndOpensTheNextRound`：真实四进程对局跑到 Finishing（礼物 100/30）→ actor 自动结算（grant 全 Granted、账本 2 行、余额 100/30、控制面读榜单 [100,30] 确定性序）→ Kill+重启 DataServer 后重发结果 Duplicate 且账本不增 → 控制面（@cluster 证书 act-as-tenant）FinalizeRound：Settled/epoch+1/参与 Closed/指针释放/所有权 Released/outbox round.settled → 同绑定 room-m2-11 开局并跑到 Finishing（round_generation=2）→ game 证书指名他租户 PermissionDenied、streamer 角色终局 PermissionDenied。Release 全量 1210/1210。

AT 台账增量（不标满过，只记闭合半边）：

- **AT18 闭合**：新库安装→首笔 grant 种默认 'points' 行；余额/库存与流水一致→每 grant 恰一账本行、崩溃重试不增行、并发双局余额=和且榜单=和；无 JSON 覆盖奖励→profile.patch 对账本资产零影响（M0 已有版本冲突+幂等入账底座）。
- **AT20 闭合**：结算到第 N 笔 kill→仅未完成 grant 重试且每笔恰一账本效果；FinalizeRound 任意位置可见拒绝/幂等重试收敛；占位、挂载指针与 owner 原子释放（单事务）且能开启下一局（宿主级同绑定二代局跑到终局）。
- **AT27 闭合（结算部分与帧部分合流）**：帧提交、快照、结算 grant、FinalizeRound 全部回执锚定 OperationId——DB 已提交而 RPC 应答丢失时，同 op 重发回放原结果不产生重复效果（宿主级 DataServer Kill 于结算后、重启重发 Duplicate 且账本不增为结算侧证据；M2-2/M2-4 已证帧与快照侧）。AT27 至此单节点部分全项落地。

## M2-6 已落地：客户端同步协议（bec1ab6）

按架构 §8.3「只发布已提交帧；新订阅/滞后订阅先全量快照后增量；每订阅者有界队列、可丢中间显示帧但绝不把不连续 delta 当全量；逻辑永不等待最慢客户端；epoch 失配→ResyncRequired 而非不可验证帧」：

- m2.proto：新 `RoundSync` 服务（GameServer）`Subscribe(SubscribeRequest) returns (stream SyncMessage)`；请求带 room/game/epoch 与订阅者已应用基线 (last_applied_tick, last_applied_state_version)；`SyncMessage` oneof {Snapshot, Delta, RoundResult, ResyncRequired}——每条消息携带房间身份（room/epoch/tick/state_version/module_version/config_hash）；`DeltaMessage` 含 base_version（客户端拒收基线未应用的 delta）、state_hash、input_event_ids、effects 与命令回执；`SnapshotMessage` 含全量状态载荷+schema 版本；`ResyncRequiredMessage` 含 current_epoch+reason。InboxItemProto 增 platform/source_dedup_key——客户端命令走 input.event 平台 "@chorus"、dedup key=command id（§6.3），宿主据此回执精确到命令。
- `RoomSubscriber`：每订阅者有界队列（容量 64）。溢出即清积压并置 NeedsSnapshot，**丢弃触发溢出的那条 delta**（前驱已丢则它自身即不连续），终态消息（RoundResult/ResyncRequired）永不丢——溢出清队后仍入队。actor 对队列只 Offer 从不等待。
- `RoomActor` 发布：`_publishGate` 单锁内同时推进头部 (state, tick, version) 并扇出——订阅者只见一致的已提交三元组。每提交帧构造 delta（base=前一提交版本；命令回执=平台 "@chorus" 的 inbox 项）；NeedsSnapshot 订阅者改收头部全量快照（快照涵盖该帧 delta）；final 帧先补快照（如需）再发 result 并 Complete——终局消息无隙到达。`Subscribe()`：已终局房→最终快照+冻结 result+Complete；基线落后头部→先头部快照；基线在头部→纯 delta 续流。
- `RoundSyncGrpcService`：仅 streamer-server/control-server；未知房 NotFound；epoch 失配→单条 ResyncRequired(current_epoch) 后收流（不给客户端无法验证的帧）；泵循环 Detach 退出。
- **流不得越过喂养它的循环（§8.3 活性规则，本轮排障成果）**：RunAsync 以 finally 收口——循环无论因何结束（终局、挂起、被栅栏拒、OCE），未提交 result 的房对每个在册订阅者发 ResyncRequired+Complete，已终局房仅补 Complete；`_loopEnded` 标志让晚到 Subscribe 绝不注册进已死的循环。排障证据链：全量门禁复现挂起→堆转储显示 actor 任务 RanToCompletion、订阅者 2 个仍在等、房 Running、头 486/487、堆上零异常对象→根因是 **60 秒租约上限被工作受限的长房越过**（每 tick 两次数据面调用，门禁负载下 ~100-200ms/tick）：过期租约把 tick-487 的 CommitFrame 以 stale-owner FailedPrecondition 拒之，commit 拒绝路径置 _stopped 后 SuspendRound 撞同一栅栏、RpcException 被 RoomRuntime 吞掉——循环退出而订阅者永等。修复=活性 finally + 拒绝路径 stderr 证据行（挂起本身可能被同一栅栏拒，证据不依赖它落地）+ 控制面续租。架构 §4.2 本就规定 ControlServer 负责授予与续租（初始 15s/续租 5s），M2 测试即控制面：`RenewableClaim` 每 20s 同 owner 重claim（ON CONFLICT 同 owner 且租约有效→epoch 不变，仅延长）——生产续租随 M3 控制面集成落地。
- 测试基建：HostEnvironment 增 `CHORUS_TEST_DUMP_HOST_OUTPUT` 环境开关——逐宿主转储捕获的 stdout/stderr 到测试输出（本次定位 ArgumentOutOfRangeException 'leaseSeconds' 即靠它）。
- 证据：宿主级 `RedesignM2HostTests` 增 3 例——
  - `SlowSubscribersAreRebaselinedWhileFastSubscribersStreamContiguousDeltas`：600 tick@5ms 工作受限房（TickMs=5 下每 tick 两次数据面往返主导节拍）+双 streamer 订阅者；快者恰 1 条基线快照+无缝 delta 链（WalkSyncChain 逐条断言 BaseVersion==已应用版本）直达 tick-600 result（终版 601）；沉默慢者队列溢出重基线≥2 次仍收敛到同一 result——房从未等它。
  - `DisconnectedSubscriberReconnectsToAHeadSnapshotAndTheSameResult`：过期 epoch 订阅→恰一条 ResyncRequired(current_epoch) 即收流；读到 tick≥12 断开→房继续（SQL 轮询帧数越过 applied+10）→携基线重连→首条为头部快照（tick>applied）、链无缝到 141/140 终局；流式 result 与 DB `game.result` 经 RankingOf 结构化一致（PG jsonb 键序无关）；晚到订阅者收最终快照+result 即收流。
  - `ClientCommandsSurfaceAsReceiptsInTheDeltaStream`：120 tick 房，600ms 后第二 streamer 证书经 DataPort AcceptClientInput（command id "cmd-boost-1"，payload `{"action":"boost","level":3}`）→恰一条 delta 携该命令回执；同 command id 重发 Duplicate=true 且不产生第二条回执；效果兑现=red 130-134（礼物 100+boost 30-34）、blue 恰 30。Release 全量 1213/1213。

AT 台账增量（不标满过，只记闭合半边）：

- **AT15 闭合**：慢消费者不阻塞快者——有界队列溢出清积压+丢触发帧，actor 从不等待；快者链连续直达终局；强制滞后以全量快照重基线追平到同一 result。
- **AT06 重连半边闭合**：断线后局继续提交；重连携 (tick, version) 基线→头部快照+无缝增量到冻结 result；流式与 DB 结果结构一致；过期 epoch 一律 ResyncRequired 不给不可验证帧。（房间模型半边 M2-1 已闭合。）

## M2-6b 已落地：双主播控制台示例（b035444）

M2-6 剩余半项——「新 `src/Chorus.RoundDemo` 控制台示例（双主播、§3.2 语义、开发证书认证）+ 同场景类作 RoundDemoScenarioTests 保绿门」。核心设计是 **IDemoCluster 接缝**：`ControlPort/DataPort/StreamerPort/GamePort/DatabaseConnectionString/StartAllAsync(签名密钥,凭据密钥)/Certificate/Channel`——独立 `DemoCluster` 与测试侧 `HostEnvironmentCluster`（包 RedesignM0HostTests.HostEnvironment，处置权留属主测试）实现同一契约，场景代码零分叉：控制台与测试跑的是同一份旅程。

- `DemoCluster`：自含开发集群——进程内根/节点 CA（ProcessCertificateIssuer，roles×tenants 与宿主测试同构）、专库 `chorus_m2_demo_<guid>`（清理白名单 chorus_m2_% 族内）建库+迁移、四真实宿主按环境契约以进程拉起（streamer 挂开发平台模拟器双密钥、game-server 双证书身份 tenant-a 客户端）、逐宿主就绪探针（streamer 的 Ready 只答 streamer-server 探针证书，其余 node-agent）、宿主输出捕获进失败异常文本；DisposeAsync 杀进程（有界 10s 等待）+DROP DATABASE WITH (FORCE)+删临时目录。
- `RoundDemoScenario.RunAsync` 共享旅程（§3.2/§5.3/§6.3/§8.3/§9.3 一条链）：模拟器凭据→双主播 Login+StartGameplay 挂载→控制面 claim+20s 同 owner 续租（§4.2，epoch 不变仅延长）→StartRound→PrepareRoom（120 tick@25ms，seed 6464）→平台礼物 100/30 经 ingest+route→两条边缘自 tick-0 基线订阅→host 边收到首条 delta 即经 DataPort AcceptClientInput 发 durable 命令 demo-boost-1（`{"action":"boost","level":3}`）→双边缘逐条 base_version 链校验到 tick-120 冻结 result→控制面 FinalizeRound 重试至 Settled（manifest 未清时 FailedPrecondition 可见、可重试）→ReadLeaderboard→返回双 DemoSubscriberEvidence+榜单+房态证据记录。durability 用 proto 线上常量字面量 "Durable"（demo 保持纯客户端、不引用 data-server 工程）。
- `Program`：CHORUS_TEST_POSTGRES 前置说明+集群起停+证据收尾输出；实机验证 `dotnet run --project src/Chorus.RoundDemo` 全程输出：119 deltas+1 snapshot×2 边缘、receipts [demo-boost-1]、榜单 131/30、settled at epoch 1；残留零（demo 库 DROP、无孤儿进程）。
- `RoundDemoScenarioTests.TwoStreamersRunTheDemoJourneyToSettlementAndTheLeaderboard`：断言双边缘 FinalTick 恰 120、版本/result 双双一致、**双方各恰一条 demo-boost-1 回执**（回执属已提交帧，全体订阅者可见，非仅发送方）、red 130-134/blue 30、榜单 host（∈[130,134]）首位 guest 30 次位、房 Settled。Release 全量 **1214/1214**（轨迹 …1210→1213→1214）。

本项不新增 AT 闭合——它是 M2-7 全流程出口证据的场景载体（全流程场景测试直接复用本场景加断线/重连段）。

## M2-7a 已落地：保留/清理守卫与容量压力（86beb94，AT21）

M2-7 的保留/清理半项——「保留/清理守卫（只删 Settled 房最后两个 verified 快照之前的帧前缀、零未处置输入才许，破坏恢复链或丢未处置事件即 fail-visible；盘压→拒绝新局+计数，绝不静默删）」。

- 迁移 `0011_retention_capacity.sql`：新 `ops` schema；`game.room` 增 `pruned_before_tick bigint NOT NULL DEFAULT 0`；`ops.capacity_rejection(kind PK, count, last_rejected_at)`——压力拒绝是计数事实而非日志。迁移步 10→11、表 32→33（含 ops），schema 测试同步。
- `RedesignRetentionStore.PruneRoundAsync`：一个回执事务（`retention.prune`，canonical={RoomId, OverrideRetentionWindow}），守卫依次 fail-visible——房间 FOR UPDATE 须 `Settled`；`Settled` 起算 7 天保留窗（`DefaultSettledRetention`），未过窗须显式 override；`input.delivery(consumer_scope='room:<id>', state='Pending')` 必须为零（未处置输入绝不删）；然后才定锚点。删除=帧与快照 `tick_id<keep` 各一条 DELETE + `pruned_before_tick=GREATEST`；`input.event`/`input.delivery`/`game.result`/`settlement.grant`/账本一概不触——去重、审计与结算证据长于帧存活。回执回放使重试返回原结果（Duplicate），后续 operation 得诚实零删。
- **锚点语义（本轮排障成果）**：`verified` 只标记 PutSnapshot 的存储再验证入账路径（RecoverRoom 重放后落头走它）；admission tick-0（RoomStore.PrepareRound）与帧内 FullCheckpoint 写的都是 `verified=false`——普通局一生可以只有 unverified 快照。恢复读的真实契约是「从最老的**任意**快照起逐帧回放、坏最新快照回退下一枚」，故保留必须保住的不变量是**最后两枚快照的回退对 + 老者之上的全部帧**。锚点初版误作 verified-only：普通 Settled 局将永远无可验证锚、永远不可剪（fail-useless）；已改为任一种类最后两枚取老者，`verified` 不再参与保留决策（两条快照写入路径都随单事务落地、回放逐帧验 hash，不是弱锚）。零快照（防御分支）仍拒绝。
- gRPC：`m2.proto` 新 `Retention` 服务（Ready/PruneRound，注册于 DataServer）仅 control-server 可调；@cluster 证书必须具名 tenant（ResolveTenant 同控制面惯例），租户证书越权代他租户→PermissionDenied；`RedesignRetentionException`→FailedPrecondition（拒绝是数据不是静默 no）、重试异请求→InvalidArgument。
- 容量守卫：`RedesignRoomStore` 增 `maxActiveRooms`（Program.cs 读 `CHORUS_MAX_ACTIVE_ROOMS`）；`ApplyStartAsync` 顶部先取 advisory 事务锁（`hashtext('game.room:admission')`）再统计非终态房（`status NOT IN ('Aborted','Settled','Closed')`），≥cap 抛 `RedesignCapacityException`；StartRound 捕获后在回执事务**外**（准入事务已回滚）UPSERT 计数，gRPC 映射 ResourceExhausted。回执存储的 duplicate 早退保证同一 operation 重试不重复计数；拒绝操作零回执残留，同一 operation id 释放容量后重试即干净准入。
- 证据：store 4 例（`RedesignRetentionStoreTests`，专库族 `chorus_m2_retention_<guid>` 白名单内）——active 房拒/窗内拒/Pending 注入行拒（override 也不放行）三连拒后释放输入即诚实 no-op（KeepTick 0）；锚点对（快照 0 admission+20 ckpt+40/80 PutSnapshot → keep 40、删 39 帧+2 快照、恢复链 41..120 连续且 [80,40] 双锚俱在、事件/结果/账目证据全数未动、同 operation 重试回放 39、新 operation 零删）；单 admission 快照房零删；容量=1 下第二局 ResourceExhausted+计数恰 1、abort 后同 operation id 干净准入。host 1 例（`CapacityPressureRejectsNewRoundsAndRetentionNeverBreaksTheChain`，CHORUS_MAX_ACTIVE_ROOMS=1 四宿主真实进程）：真实 StartRound 被拒可见+计数=1→首局 20 tick 跑到 Settled（finalize 重试循环）→窗内 PruneRound FailedPrecondition 含 "retention window"→override 后诚实 no-op（唯一快照=tick 0 admission）→LoadRecovery（game-server 证书，@cluster 证书读不到租户房）链完整 1..20→streamer 证书 PruneRound PermissionDenied。Release 全量 **1219/1219**（轨迹 …1214→1219）。
- AT 台账增量：**AT21 的 M2 单节点部分闭合**——保留守卫（Settled 才剪、回退对之上不破坏、零未处置输入才许、保留窗、fail-visible 拒绝）与压力半边（拒绝新局+计数、绝不静默删）均可演示；跨节点容量模型与盘压测量属 M4。

## M2-7b 已落地：全流程出口证据与单节点基准（96ca847、b71515d、71ad16d，M2 出口）

M2-7 的出口半项——「全流程场景测试（复用 RoundDemoScenario 加断线/重连段）、可选单节点基准、AT13–21+AT27 台账收口」。

### 断线/重连旅程与三方一致（M2-7b-1，96ca847）

- `RoundDemoScenario` 增断线变体：`disconnectGuestEdge` 参数与 `GuestDisconnectTick=12`——guest 边在 tick 12 断开（host 边照常），房间继续跑；400ms 后携 (LastAppliedTick, LastAppliedStateVersion) 基线重连。ReadEdgeAsync 重写为双腿一条 Apply 规则：delta 只认自己链上的 base_version、result 只在自己头到达、**重连快照必须严格高于离开基线**（否则抛出）——重连=基线落后于头时回答全量 Snapshot，链无缝续到冻结 result。
- `DisconnectedEdgeRejoinsTheJourneyAndTheRecordAgreesThreeWays`（验收 line 86 纪律：数据库状态、客户端输出、副作用记录三方一致）：双边缘 FinalTick 恰 120/版本/result 一致、guest 恰 2 枚快照（基线+重连各一）、双边缘各恰一条 demo-boost-1 回执；DB 侧 game.room Settled@120、game.result 的 RankingOf == 双边缘 result 的 RankingOf、settlement.grant Granted=2、player.ledger 对 `settlement:room-demo-1/%` 恰 2 行且 DISTINCT operation_id==count(*)（每 grant 恰一账本效果）、infra.outbox 含 round.started@`{room}/0/start`、round.activated@`/0/activate`、round.settled@`/0/settle`。控制台与测试同一代码路径。Release 全量 **1220/1220**（轨迹 …1219→1220）。

### 单节点 100 房基准（M2-7b-2，b71515d+71ad16d，环境门控）

- `RedesignM2CapacityBenchmarkTests.HundredRoomsWithAHotRoomReportSingleNodeDistributions`（`CHORUS_RUN_M2_CAPACITY=1` 才跑，缺省走 skip-path 计入门禁；Trait Category=Capacity）：四真实宿主上 100 并发房（每房 1 streamer 挂载、独立 claim+并发续租、100 tick@50ms）+ 热房以 500 durable 事件/s 为目标喂入（热房=最后准备的房，先准备的房会在 setup 期间完赛，喂终态房只得诚实的 OutOfRound）。断言只查「跑成了」：100/100 完成计数、帧数恰 10000、热房 routed>0、喂入零服务器错误；**从不断言延迟阈值**——报告是诚实数据不是门控。
- **基准逼出的两处修复**：
  1. **路由死锁（生产修复 b71515d，RedesignRoundInputStore）**：ApplyRoutingAsync 原先无锁读房态、先取 cursor 再 INSERT delivery——delivery 外键的 FOR KEY SHARE 是第一次碰房行、晚于 cursor；并发 CommitFrame 持房 FOR UPDATE 等 cursor → 40P01 死锁（堆栈实证：RouteEvents 挂在 FK 检查、锁元组在 game.room）。修复=按 §6.5 统一锁序把 Pass 4 拆三步：先解析目标房，再**按序 FOR UPDATE 锁全部目标房**（与 bindings 同款排序锁），最后才 cursor+delivery。副产修复：原无锁房态读可与结算竞态（房刚 Settled 仍可能落 delivery），锁读后终态房诚实记 OutOfRound。
  2. **百租约不能顺序续（基准测试侧）**：LeaseSeconds 上限 60，100 条租约顺序续一圈在负载下 30s+，20s 节奏整圈可超 60s → 所有权栅栏拒掉 setup 中的房（FailedPrecondition 实证）。修复=Task.WhenAll 并发续租、10s 节奏，epoch 漂移与 claim 失败仍视为真失败。
- 2026-10-28 本机一轮数据（**单开发机基线，非生产容量主张；完整容量模型属 M4**）：100 房全数完成、帧 10000/10000、热房喂 825（Routed=800 + OutOfRound=25，后者为热房结算后的终态审计行）、达成 15 events/s（目标 500——满载下 ingest/route RPC 延迟数十秒）、单房完成墙钟 p50=111s / p95=187s / p99=188s（名义 5s，排队主导）、聚合帧率 41 frames/s、CPU game-server 4.7s / data-server 43.2s、WAL 6.9 MB。结论与 ROOM-11 同向：单开发机瓶颈是提交延迟而非吞吐；帧全数提交、825 事件全数有去向、零静默丢失。Release 全量 **1221/1221**（轨迹 …1220→1221）。

### AT13–21 + AT27 台账收口（M2 单节点）

- **AT13**：隔离半边 M2-3（模块 FailAfterTick 连续失败→Suspended 可见、同进程他房继续）、分布半边本基准（100 并发房，Tick/提交/CPU/WAL 分布如实报告）。单节点闭合；正式容量口径（2000 帧/s 目标等）属 M4、不冒领。
- **AT14**：M2-2（同 CommitId 重试回原结果、低序晚交可见拒、水位不越洞、FullCheckpoint 同事务、崩溃注入收敛、宿主 Kill 边界）。
- **AT15**：M2-6/6b（慢订阅者不阻塞快者、溢出后全量追上、断链不应用不连续 delta；重连快照证据见上方旅程）。
- **AT16/17**：**如实标注 M4**——模块确定性契约在位（DeterministicReplay、逐帧哈希验证、恢复逐帧验后继），但跨 OS 重放与配置升级/版本钉住验证未做，不折算。
- **AT18**：M0 播种 + M2-5（默认资产可用、余额/流水一致、profile.patch 不覆盖账本资产）。
- **AT19**：M2-4（快照原子可见、坏最新回退、全坏 RecoveryFailed/Suspended、Finishing 恢复不重启战斗 tick、恢复后与孪生房 result 逐字节相等）。
- **AT20**：M2-5（第 N 笔 kill 只补未完成 grant、每 grant 恰一账本效果、FinalizeRound 各缝崩溃收敛、绑定可开下一代）。
- **AT21**：M2-7a 单节点部分（Settled 才剪、回退对之上不破坏、零未处置输入才许、保留窗、压力拒绝计数、OutOfRound 终态审计永不 GC）；跨节点盘压与容量模型属 M4。
- **AT27**：M2-2 帧半边 + M2-5 结算半边 + M2-7a 保留回执（commit 已完成而 RPC 响应丢失：原 OperationId 查回原结果、无重复效果）。
- **M2 出口演示（acceptance line 106）**：一局从启动（claim→StartRound→PrepareRoom）、输入（平台礼物路由 + durable 客户端命令回执）、断线（guest 边 tick 12 断）、重连（基线携入、快照无缝续链）到结算（FinalizeRound→Settled→排行榜）由 RoundDemoScenario 断线变体 + 三方一致测试 + 控制台同一代码路径全程演示——非仅 WebSocket 收发。

M2 七轮全部完成。multi-worker、跨节点容量、真实平台接入、match 票据、BootstrapManifest 的 game-server 槽位扩展属 M3/M4。

## M2 后续顺序

1. ~~局准入与 RoundGeneration、事件按局路由、AbortRound~~（已落地，见上）。
2. ~~FrameStore：CommitFrame（§8.2 全事务）、ReadInbox、消费连续水位~~（已落地，见上）。
3. ~~§7 示例玩法 + Chorus.GameServer 宿主 + RoomActor tick + Prepare/激活~~（已落地，见上）。
4. ~~快照与恢复：PutSnapshot/LoadRecovery、确定性重放、坏快照回退（AT19）~~（已落地，见上）。
5. ~~结算 §6.4 + FinalizeRound §6.6 + 排行榜投影 + 默认资产库存守卫（AT18、AT20、AT27 结算部分）~~（已落地，见上）。
6. ~~客户端同步协议（Subscribe：Snapshot/Delta/CommandReceipt/RoundResult/ResyncRequired）+ 双主播控制台示例 `Chorus.RoundDemo`~~（均已落地，见上）。
7. ~~全局出口证据：全流程场景测试（复用 RoundDemoScenario 加断线/重连段）、可选单节点基准、保留/清理守卫、AT13–21+AT27 台账收口（AT16/17 如实标注 M4）~~（均已落地，见 M2-7a 与 M2-7b）。

GameServer 已建（无 DB 凭据，全部持久状态走 gRPC→DataServer）；match 票据与 BootstrapManifest 的 game-server 槽位扩展属 M3。
