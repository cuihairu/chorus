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

## M2 后续顺序

1. ~~局准入与 RoundGeneration、事件按局路由、AbortRound~~（已落地，见上）。
2. ~~FrameStore：CommitFrame（§8.2 全事务）、ReadInbox、消费连续水位~~（已落地，见上）。
3. §7 示例玩法 + Chorus.GameServer 宿主 + RoomActor tick + Prepare/激活（AT13 隔离半边）。
4. 快照与恢复：PutSnapshot/LoadRecovery、确定性重放、坏快照回退（AT19）。
5. 结算 §6.4 + FinalizeRound §6.6 + 排行榜投影 + 默认资产库存守卫（AT18、AT20、AT27 结算部分）。
6. 客户端同步协议（Subscribe）+ 双主播示例（AT15、AT06 重连半边）。
7. 全局出口证据：全流程场景测试、可选单节点基准、保留/清理守卫、AT13–21+AT27 台账收口（AT16/17 如实标注 M4）。

GameServer 工程尚不存在（M2-3 创建，无 DB 凭据，全部持久状态走 gRPC→DataServer）；match 票据与 BootstrapManifest 的 game-server 槽位扩展属 M3。
