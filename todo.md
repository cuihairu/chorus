# TODO

## 22. 架构审查整改：原子任务（2026-10-29）

本清单仅保留当前重设计的未完成任务，任务编号保持稳定；旧 Gateway / EngineHost / ControlHost 阶段台账、已完成实施流水及失效限制已从本文件移除。历史证据查 Git 历史和各阶段实施文档，不再作为下一轮执行计划。旧 `ROOM-*` 编号不能代替这里的 `FIX-ROOM-*`。

执行前先阅读 [当前架构](docs/architecture/architecture.md)、[职责与接口契约](docs/architecture/redesign-contracts.md)及 [AT 验收计划](docs/architecture/redesign-acceptance.md)。已有代码和未提交改动须核对后复用，不能仅凭文档、旧测试计数或工作区代码存在勾选完成。

当前职责：ControlServer 负责注册目录、匹配调度、路由发布、恢复和结束协调；StreamerServer 负责主播实体与个人业务；StreamerGateway 直连 GameServer 传递战斗输入与帧；PlatformGateway 根据 ControlServer 路由直推房间事件。DataServer 只负责明确的数据读写、事务与约束，不解析平台业务、不选择房间、不编排结算或平台调用。

执行纪律：

- 每个复选框对应一个可独立评审、可独立判定通过的交付件；不把整组、整个阶段设为一个复选框。
- 每项完成后在该条末尾追加：`证据：提交 hash；测试名称/验证命令；结果；报告路径（如适用）`。只有文档描述、测试文件存在或全量通过计数，不能代替该项验收。
- 验收测试必须实际执行；环境变量导致测试直接 return、缺少数据库或故障节点时，保持未勾选并注明 NOT RUN 和缺少的条件。
- 实现项包含其定向行为验证；独立故障注入、性能和外部环境任务另行勾选。代码完成但专门故障验收未完成时，不宣称整条恢复链闭合。
- `P0` 为正确性/恢复闭环优先，`P1` 为容量与运行治理，`P2` 为扩展与发布证据。优先级不是对所有故障严重性的替代；依赖编号决定执行先后。
- 无依赖项可独立开始；涉及契约的实现必须等对应契约项完成。环境验收与 AT 验收计划共用证据，不重复执行相同场景。

### 22.1 帧提交结果未知：冻结请求再重试（P0）

代码入口：`src/Chorus.GameServer/RoomActor.cs`、`src/Chorus.DataServer/FrameGrpcService.cs`、`RedesignFrameStore.cs`。当前风险：提交成功但响应丢失后重新读取 Inbox，以原 CommitId 提交不同内容，引发幂等冲突并停房。

- [ ] **FIX-FRAME-01：保存待确认帧。** 依赖：无。交付：Actor 保存原输入、完整 CommitFrame 请求、候选状态和待发布内容。验收：同一提交未确认时不再次取出或读取新输入，也不重新执行 ApplyTick；连续两次发送的规范化业务请求完全相同。
- [ ] **FIX-FRAME-02：确认成功后只推进一次。** 依赖：FIX-FRAME-01。交付：Committed/Duplicate 回执均完成同一待确认帧，再清除 pending。验收：响应重放不重复推进 StateVersion、不重复发布命令回执或结果，发布状态与已提交 StateHash 一致。
- [ ] **FIX-FRAME-03：区分暂时故障与确定拒绝。** 依赖：FIX-FRAME-01。交付：未知结果按原请求有界退避，StaleOwner/请求冲突停止推进并记录原因。验收：Unavailable 后仍重试原请求；旧 epoch 被拒后不再发新帧；取消能结束重试循环。
- [ ] **FIX-FRAME-04：注入“提交成功、响应丢失”。** 依赖：FIX-FRAME-01、FIX-FRAME-02、FIX-FRAME-03。交付：真实 RoomActor→DataServer 故障测试，在 DB commit 后丢弃响应。验收：房间继续到终局，DB 每 tick 一帧、每输入一次消费、每效果一份；客户端链连续且最终状态与 DB 一致（AT14/27）。
- [ ] **FIX-FRAME-05：注入“提交未完成、新输入到达”。** 依赖：FIX-FRAME-01、FIX-FRAME-02、FIX-FRAME-03。交付：提交前断连/回滚期间追加另一输入的测试。验收：旧 pending 帧原样重试，新输入只进入之后的帧，不能被混进原 CommitId。

### 22.2 本地房间生命周期与回收（P0）

代码入口：`src/Chorus.GameServer/RoomRuntime.cs`、`RoomActor.cs`。当前风险：字典中存在 Actor 被当作正在运行；停止 Actor 和终局 Actor 长期驻留。

- [ ] **FIX-ROOM-01：显式记录 Actor 执行状态。** 依赖：无。交付：区分 Running、Stopped/Suspended、Completed、Disposed，暴露实际 epoch、最后提交 Tick 和停止原因。验收：正常终局、模块失败、fencing 拒绝分别得到正确状态；未提交终局不能被标为 Completed。
- [ ] **FIX-ROOM-02：校验恢复入口的已有 Actor。** 依赖：FIX-ROOM-01。交付：RecoverRoom 对已有对象检查 epoch 和循环状态。验收：仅同 epoch 且实际运行中的 Actor 返回 Running；停止对象不返回恢复成功；返回的 LastTick 为真实头部。
- [ ] **FIX-ROOM-03：串行化同房间的创建与恢复。** 依赖：FIX-ROOM-02。交付：每 RoomId 的激活协调，覆盖 Prepare 与 Recover 交错。验收：并发请求只启动一个有效 Tick 循环；冲突请求可见拒绝；失败后释放激活门，后续合法重试可执行。
- [ ] **FIX-ROOM-04：移除退出的 Actor。** 依赖：FIX-ROOM-01、FIX-ROOM-03。交付：对失效/被替代 Actor 按对象身份清理循环与订阅资源；正常终局保留待收尾实体，直到 FIX-END-14 获得释放后的销毁授权。验收：停止 Tick 不等于提前销毁正常终局房间；旧 Actor 延迟退出不删除新 Actor；重复释放不抛异常。
- [ ] **FIX-ROOM-05：有界保留终局查询能力。** 依赖：FIX-ROOM-04。交付：独立有期限/数量上限的结果缓存，或持久结果查询回退。验收：获授权销毁并回收 Actor 后仍能查询最终结果；超过保留上限不会保留完整 Actor；晚到订阅行为有明确契约。
- [ ] **FIX-ROOM-06：验证同进程二次接管。** 依赖：FIX-ROOM-02、FIX-ROOM-03、FIX-ROOM-04。交付：A→B→A 房间迁移测试。验收：A 使用新 epoch 从持久头恢复，旧对象不阻止激活、不恢复旧状态，始终只有一个可提交者。
- [ ] **FIX-ROOM-07：验证长时间房间周转。** 依赖：FIX-ROOM-04、FIX-ROOM-05。交付：连续创建/结束/回收房间的 soak 报告，记录轮数、活跃 Actor、缓存项、订阅数和内存趋势。验收：静置回收后 Actor/订阅计数回落，缓存不超上限；无随历史房数线性增长的存活 Actor。

### 22.3 接管状态、就绪与活性（P0）

代码入口：`src/Chorus.ControlServer/TakeoverGrpcService.cs`、`RedesignControlStore.cs`、`src/Chorus.DataServer/RedesignRoomStore.cs`。当前风险：恢复 RPC 前已 Confirm 为 Active；目标进程存活即可续租，不能说明房间恢复成功。

- [ ] **FIX-TAKE-01：定义恢复与激活的权限矩阵。** 依赖：无。交付：在 `redesign-contracts.md` 明确 Allocated/Recovering/Active 的允许操作、超时和合法转换。验收：矩阵分别列出 LoadRecovery、恢复快照、CommitFrame、Subscribe、续租权限；恢复失败不得被解释为业务 Ready。
- [ ] **FIX-TAKE-02：提供受限的恢复快照写入。** 依赖：FIX-TAKE-01。交付：Recovering 状态仅允许指定 owner/epoch 提交校验后的恢复快照。验收：新 owner 可保存恢复头；旧 owner、错误 epoch 和普通帧写仍被拒，不能借该入口绕过帧栅栏。
- [ ] **FIX-TAKE-03：恢复成功后原子激活。** 依赖：FIX-TAKE-02、FIX-ROOM-03。交付：去掉恢复 RPC 前的 Active 确认，按恢复凭据执行 fenced 激活。验收：恢复失败不出现 Ready/Active 假成功；重复确认返回同一激活结果；恢复期间旧 owner 仍不可写。
- [ ] **FIX-TAKE-04：持久记录恢复操作。** 依赖：FIX-TAKE-01。交付：保存 OperationId、目标 owner/epoch、阶段、期限、进度及最后错误。验收：控制面重启可查询并继续原操作，不凭 RPC 超时认定未执行，不使用新 ID 重复转移 epoch。
- [ ] **FIX-TAKE-05：回收超时恢复任务。** 依赖：FIX-TAKE-03、FIX-TAKE-04。交付：后台检查过期 Recovering 操作并执行契约规定的暂停或重新分配。验收：目标进程活着但恢复未完成时任务仍能超时；不会无限续期，也不会把旧 epoch 重新启用。
- [ ] **FIX-TAKE-06：建立房间进度信号。** 依赖：FIX-ROOM-01。交付：报告最后提交时间、Tick、恢复阶段和停止原因，和 ProcessReady 分开展示。验收：宿主继续心跳但某房 Actor 停止时，该房被识别为停滞，其他房仍显示正常。
- [ ] **FIX-TAKE-07：按房间进度触发恢复治理。** 依赖：FIX-TAKE-05、FIX-TAKE-06。交付：定义并实现停滞阈值、暂停/重试策略，保留进程租约与业务活性的区别。验收：单房停止不会被进程心跳永久掩盖；短暂慢帧不会导致反复迁移；旧写仍由数据面 fencing 拒绝。
- [ ] **FIX-TAKE-08：验证接管各阶段中断。** 依赖：FIX-TAKE-03、FIX-TAKE-04、FIX-TAKE-05。交付：分别在转移后、恢复中、恢复完成未确认、确认响应丢失时 kill 的故障测试。验收：每个注入点均可按持久操作收敛；没有假 Ready、重复激活或永久 Recovering（AT25/28 单机部分）。

### 22.4 恢复读取的一致性与规模（P1）

代码入口：`src/Chorus.DataServer/RedesignRecoveryStore.cs`。当前风险：读取所有快照、所有 delivery 及最老快照后的帧；多查询缺少一致性视图，恢复成本随整局历史增长。

- [ ] **FIX-READ-01：固定恢复读取视图。** 依赖：无。交付：在一致性读取视图内确定房间 epoch、LastTick、StateVersion 和恢复材料；分页方案须定义不可变恢复边界。验收：并发提交时返回材料只覆盖选定 head，不混入之后的帧或不匹配状态。
- [ ] **FIX-READ-02：先读取最新候选快照。** 依赖：FIX-READ-01。交付：正常路径只读有限候选及候选到 head 的帧，不从最老快照起全量扫描。验收：构造长局后，返回的帧数量受快照间隔约束；明确记录查询行数/返回字节数。
- [ ] **FIX-READ-03：按需读取重放输入。** 依赖：FIX-READ-02。交付：仅加载选定帧引用的输入及必要参与关系。验收：大量历史 delivery 不增加本次无关输入载荷；重放状态哈希与原执行相同。
- [ ] **FIX-READ-04：逐级回退损坏快照。** 依赖：FIX-READ-02、FIX-READ-03。交付：候选失败后按需请求更早快照及增量材料。验收：最新损坏可回退、全部损坏明确 RecoveryFailed；不能跳过缺帧或把未验证材料标为成功。
- [ ] **FIX-READ-05：限制单次恢复传输大小。** 依赖：FIX-READ-01、FIX-READ-02、FIX-READ-03。交付：分页或流式恢复契约，规定字节/条数上限和可取消行为。验收：超过一页的恢复完整成功且无重复/遗漏；取消后释放资源；不依赖无限增大 gRPC 消息上限。
- [ ] **FIX-READ-06：验证清理与恢复并发。** 依赖：FIX-READ-04、FIX-READ-05。交付：恢复读取与 Retention 并发测试。验收：固定恢复链不会中途被剪断；若材料不可用则明确重启恢复，不发布部分恢复状态（AT19/21）。

### 22.5 结算存储、并发执行与阻塞诊断（P0）

代码入口：`src/Chorus.GameServer/RoomRuntime.cs`、`src/Chorus.DataServer/RedesignSettlementStore.cs`、ControlServer。DataServer 提供存储接口；ControlServer 的结算执行者见 FIX-END-11，结束与释放协调见 FIX-END-12。

- [ ] **FIX-SETTLE-01：查询待完成结算。** 依赖：无。交付：可分页定位已有不可变结果但奖励未完成的房间。验收：未提交结果的房间不入队；失败/部分完成的清单可重新发现；查询不随全历史无界返回。
- [ ] **FIX-SETTLE-03：多执行器并发与失败退避。** 依赖：FIX-END-11。交付：ControlServer 结算执行者的任务领取/重领及有界重试机制；单个失败清单不阻塞其他房。验收：两个业务执行者并发、执行中 kill 后，每笔账本仍只有一个效果；失败原因和下次尝试可查询。
- [ ] **FIX-SETTLE-05：暴露终局阻塞原因。** 依赖：FIX-END-12。交付：未处置重要输入、未完成奖励或平台前置条件等阻塞原因可查询。验收：前置条件未满足时不能强行 Settled；条件解除后后台自动继续；记录停留时长。
- [ ] **FIX-SETTLE-06：验证自动收尾后再次开局。** 依赖：FIX-SETTLE-03、FIX-SETTLE-05、FIX-END-13。交付：结算中断恢复后使用同一挂载开启下一局的宿主测试。验收：首局奖励不重复、占位无泄漏、第二局正常接收输入并结束（AT20）。

### 22.6 实时性、容量与负载治理（P1）

依据：`redesign-m2-implementation.md` 的开发机基准仅证明完成和持久一致性，不能证明 100 房 × 20 Hz 达标。必须保留失败样本，不以减少承诺或跳过测试方式标绿。

- [ ] **FIX-PERF-01：增加 Tick 分段耗时。** 依赖：无。交付：记录 GameServer 输入队列等待、持久接收 RPC、ApplyTick、CommitFrame RPC、序列化及整轮延迟；迁移基线的 Inbox 拉取和恢复补读单独标识，不能将每 Tick 拉取定义为目标路径。验收：人为延迟某一阶段时对应分位数上涨，计数不混合不同指标；指标标签避免无界 RoomId。
- [ ] **FIX-PERF-02：增加存储等待诊断。** 依赖：无。交付：记录连接获取、SQL/锁等待和事务提交诊断。验收：连接池饱和与行锁阻塞两个实验可区分，报告不把全部耗时统称为数据库慢。
- [ ] **FIX-PERF-03：重建同步起跑基准。** 依赖：FIX-PERF-01、FIX-PERF-02。交付：全部房间准备好后按共同起点运行，固定预热与采样窗口；输入发生器独立记录计划量、实发量及积压。验收：100 房存在明确共同活跃窗口，无法达到计划输入时报告失败/不足，不用实际低流量替代目标。
- [ ] **FIX-PERF-04：建立可失败的性能门槛。** 依赖：FIX-PERF-03。交付：将目标架构 100 房/20 Hz、Tick 与提交延迟预算写入显式容量验收配置。验收：注入超预算延迟后验收返回失败；未启用容量测试明确显示 NOT RUN，不算通过；环境和持续时间写入报告。
- [ ] **FIX-PERF-05：定位并优化一个已测瓶颈。** 依赖：FIX-PERF-01、FIX-PERF-02、FIX-PERF-03。交付：基于测量只处理首要瓶颈，记录候选原因、改动和同环境 A/B。验收：报告提交延迟、吞吐和资源变化；帧/输入/奖励一致性不回退。后续不同瓶颈必须新建独立任务，不在本条无限追加。
- [ ] **FIX-PERF-06：实现持续超预算策略。** 依赖：FIX-PERF-01。交付：明确补跑上限、连续超预算窗口和降级/暂停行为。验收：持续慢提交可观测地进入策略，不能长期以 Running 静默无限减速；策略不发布未提交帧、不丢重要输入。
- [ ] **FIX-PERF-07：实现单 GameServer 准入预算。** 依赖：FIX-ROOM-01、FIX-PERF-01。交付：按活跃房间和测得的负载预算拒绝新分配，配置非法值显式失败。验收：饱和实例拒绝新房但继续已有房；并发 Prepare 不突破上限；失败创建释放容量。
- [ ] **FIX-PERF-08：执行 100 房同时恢复基准。** 依赖：FIX-READ-05、FIX-TAKE-08、FIX-PERF-03。交付：测量检测、排队、读取、重放、激活的分布和峰值内存。验收：与事先写明的恢复 SLO 比较；容量不足时明确排队/拒绝，无假成功或 OOM；独立节点失电证据仍由 FIX-ENV-01 验收。

### 22.7 调度与模块边界（P1/P2）

- [ ] **FIX-SCHED-01 / P1：接通动态 GameServer 目录。** 依赖：无。交付：ClusterDirectory 使用有效进程报告维护实例和端点，替代仅静态配置。验收：新增/重启实例不需重启 ControlServer 即可发现；旧实例地址失效后不继续用于新分配。
- [ ] **FIX-SCHED-02 / P1：按就绪与容量选择目标。** 依赖：FIX-SCHED-01、FIX-PERF-07。交付：调度过滤过期、NotReady、Draining 和容量不足实例。验收：这些实例均不接新房；已有房不会仅因停止准入而被强制终止。
- [ ] **FIX-MODULE-01 / P2：定义持久模块恢复身份。** 依赖：无。交付：统一 PackageHash、ModuleVersion、ConfigHash、状态 schema、Tick 间隔和恢复模式的权威来源。验收：恢复请求不得任意改变原局 Tick/config；契约写明缺包或不兼容的错误。
- [ ] **FIX-MODULE-02 / P2：按持久身份加载玩法。** 依赖：FIX-MODULE-01。交付：Prepare/Recover 通过模块注册/加载入口替代硬编码 ExampleGameModule。验收：两种测试模块可分别开局；缺失指定包明确拒绝，不回退到默认示例模块。
- [ ] **FIX-MODULE-03 / P2：遵守 FullCheckpoint 恢复模式。** 依赖：FIX-MODULE-02。交付：每帧是否携带完整 checkpoint 由恢复模式决定，不能对 FullCheckpoint 仅周期保存。验收：非确定性测试模块每个已提交 tick 均有同事务 checkpoint；中断恢复不依赖确定性重放。
- [ ] **FIX-MODULE-04 / P2：验证升级后恢复旧局。** 依赖：FIX-MODULE-02。交付：保留旧包恢复窗口并提供升级场景测试。验收：新局使用新版本，旧局仍按原包/config 恢复；删除旧包时明确拒绝且不产生错误帧（AT17 单节点部分）。

### 22.8 权威语义与外部验收（P1/P2）

- [ ] **FIX-AUTH-01 / P1：明确 Streamer 所有权语义。** 依赖：无。交付：对齐架构、协议和管理接口说明，区分路由 ownership 与 session 轮换写栅栏。验收：明确 TransferStreamer 与 Resume 各自何时改变路由/撤销旧权限，不声称仅转移 ownership 即撤销会话。
- [ ] **FIX-AUTH-02 / P1：防止迟到声明污染 Streamer 路由。** 依赖：FIX-AUTH-01。交付：声明路由归属时校验其对应的当前 session/version，或采用同等有版本投影规则。验收：新会话在 B 生效后，A 的迟到 Claim 不把路由改回 A；合法的新登录仍可接管。
- [ ] **FIX-ENV-01 / P2：独立机器分区接管验收。** 依赖：FIX-TAKE-08、FIX-FRAME-04、FIX-ROOM-06。环境：至少两个独立故障域及可控网络。验收：旧 owner 仍能访问 Data/客户端、但无法访问 Control 时，新 owner 接管后旧 epoch 新写被拒；记录检测至恢复分布（AT24/25/28）。缺环境保持 NOT RUN，SIGSTOP 不替代此项。
- [ ] **FIX-ENV-02 / P2：数据库主备故障验收。** 依赖：无；环境：独立同步备库及可隔离旧主的操作能力。验收：主库故障、同步备库失联、旧主恢复分别验证已确认事务保留与无双写；记录实际 RPO/RTO（AT26），复用 `docs/architecture/room-multihost-acceptance.md` 的环境准备证据，但必须按当前角色与 AT26 实测。
- [ ] **FIX-ENV-03 / P2：Linux/Windows 双向重放验收。** 依赖：FIX-MODULE-02；环境：两个真实运行环境。验收：同一模块版本快照/日志双向重放哈希一致；不通过的模块明确限制恢复域或使用 FullCheckpoint，不能标为跨 OS 确定性通过（AT16）。
- [ ] **FIX-ENV-04 / P2：Windows 进程树实机验收。** 依赖：无；环境：Windows 目标节点。验收：Agent 崩溃重启后回收子孙进程，不误杀 PID 复用进程；输出安装/启动/回收证据（AT29；双 OS 安装与升级分别见 FIX-ENV-07、FIX-ENV-08）。
- [ ] **FIX-ENV-05 / P2：抖音生产适配器准入。** 依赖：无；环境：获授权的应用、凭据、官方协议版本和脱敏样本。验收：独立报告覆盖登录验证、签名反例、回调回执、稳定去重键及未知外部调用结果策略；全部通过后才解除该适配器 NotProductionReady（AT02/07/09）。
- [ ] **FIX-ENV-06 / P2：快手生产适配器准入。** 依赖：无；环境：获授权的应用、凭据、官方协议版本和脱敏样本。验收：独立报告覆盖登录验证、签名反例、回调回执、稳定去重键及未知外部调用结果策略；全部通过后才解除该适配器 NotProductionReady（AT02/07/09）。

- [ ] **FIX-ENV-07 / P2：Linux 实机安装与升级验收。** 依赖：无；环境：Linux 目标节点。交付：按 `deploy/linux/` 安装、启动、排空升级与失败回滚的独立记录。验收：受限账号和权限正确，原持久数据及未结束局可恢复；脚本语法检查或编译不替代实机结果（AT17/29/30）。
- [ ] **FIX-ENV-08 / P2：Windows 实机安装与升级验收。** 依赖：FIX-ENV-04；环境：Windows 目标节点。交付：按 `deploy/windows/` 完成服务安装、启动、升级与失败回滚的独立记录。验收：服务身份与凭据保护符合部署契约，原持久数据及未结束局可恢复；Linux 上交叉编译不替代 Windows 实跑（AT17/29/30）。

建议起点：先做 FIX-FRAME-01 和 FIX-ROOM-01；FIX-TAKE-01、FIX-SETTLE-01、FIX-READ-01 可独立准备。禁止用本节任务数量或单元测试总数替代 AT 出口判定。后续发现新问题追加新编号，不把新工作藏进已勾选项。

### 22.9 平台事件到房间的输入闭环（P0/P1）

2026-10-29 用户明确修订：正常事件主链路为 **PlatformGateway → 目标 GameServer → RoomRuntime → 对应 RoomActor**。DataServer 提供持久化、去重、帧提交和恢复服务。现有 ReadInbox 拉取实现保留为迁移基线，不能把它作为目标主动推送已完成的证据。本节和架构 §6.1 为当前实现入口；旧拉取实现仅作为迁移对照。

- [ ] **FIX-INPUT-01 / P0：定义房间直接推送契约。** 依赖：无。交付：GameServer 房间事件 RPC，携带租户/游戏、RoomId、owner epoch、EventId、来源和 payload，明确 DurableAccepted、Duplicate、StaleOwner、Overloaded、OutcomeUnknown。验收：契约明确持久成功才确认重要输入，重试保持业务 ID；已有旧拉取接口不算交付。
- [ ] **FIX-INPUT-02 / P0：接通 PlatformGateway 房间路由。** 依赖：FIX-INPUT-01。交付：PlatformGateway 根据挂载/局边界固定 RoomId，通过 ControlServer 发布的有版本路由解析 owner 地址及 epoch 并直接调用目标 GameServer。验收：两个房间分布于不同实例时，各事件只推送到所属房间；迟到事件不随当前局变化误入新局；无有效归属显式待处理/拒绝。
- [ ] **FIX-INPUT-03 / P0：实现目标房间的持久接收。** 依赖：FIX-INPUT-01。交付：RoomRuntime 校验目标并将事件交给对应 RoomActor；接收端经 DataServer 幂等持久接收后确认并唤醒房间。验收：错租户/错房间/旧 epoch 拒绝；相同事件仅入账一次；队列满和存储失败可见；内存接收但未持久化不得返回成功。
- [ ] **FIX-INPUT-04 / P0：接通推送输入的 Tick 消费。** 依赖：FIX-INPUT-03、FIX-FRAME-04。交付：Actor 串行处理直接推送接受的事件，原子提交消费与帧。验收：正常业务无需每 Tick 从 DataServer 拉取新输入；重复推送只产生一次效果，客户端效果、帧和消费记录一致。
- [ ] **FIX-INPUT-05 / P0：处理推送超时和路由变化。** 依赖：FIX-INPUT-02、FIX-INPUT-03。交付：原 EventId 查询/重试，StaleOwner 刷新目录后重推；规定重试退避和待处理记录。验收：接收成功响应丢失不重复生效；迁移期间重试不改变 RoomId；旧实例不能接受新权威写入。
- [ ] **FIX-INPUT-06 / P0：验证直接推送宿主全链路。** 依赖：FIX-INPUT-02、FIX-INPUT-03、FIX-INPUT-04、FIX-INPUT-05。交付：平台模拟器→新版 PlatformGateway→GameServer 房间事件 RPC→RoomActor→帧/客户端的真实宿主测试。验收：双房间定向投递正确，提交前后 kill 均不丢已确认输入；测试不能手工调用 RouteEvents 或轮询旧入口替代网关推送。
- [ ] **FIX-INPUT-07 / P1：验证推送模式的恢复补齐。** 依赖：FIX-INPUT-06、FIX-TAKE-08。交付：接管时从持久未消费记录恢复，再继续接收网关直接推送。验收：旧已消费不重复、已确认待消费不丢失；恢复补齐与新推送重叠仍只应用一次，分别报告受理、去重、消费与积压数量。

### 22.10 房间结束、PlatformGateway 确认、平台通知与销毁（P0）

用户明确顺序：**通知 PlatformGateway 关闭旧局投递 → 确认生效 → 才通知直播平台 → 完成释放 → 销毁 RoomActor**。目标契约见架构 §6.4。下列业务流程由 ControlServer / StreamerServer / GameServer / PlatformGateway 各自负责，DataServer 只负责数据读写与事务约束。结算业务由 ControlServer 执行，DataServer 只保留幂等存储接口；存储测试不能替代业务收尾验收。

- [ ] **FIX-END-01 / P0：持久结束操作。** 依赖：无。交付：ControlServer 保存 EndOperationId、房间/挂载代、阶段、进度与错误。验收：同一请求重复创建只得到一个操作；每阶段重启能继续，迟到旧代请求不能影响新局。
- [ ] **FIX-END-02 / P0：注册挂载路由来源。** 依赖：FIX-INPUT-01。交付：StreamerServer 在挂载提交后向 ControlServer 注册已验证的平台场次和 BindingGeneration。验收：重复上报幂等，旧代不覆盖新代，未验证的平台身份不能建立路由。
- [ ] **FIX-END-03 / P0：确认开局路由生效。** 依赖：FIX-END-02、FIX-INPUT-02。交付：入场提交且 RoomActor 准备好后发布路由，PlatformGateway 安装并确认版本才开放平台输入。验收：准备失败不产生可投递路由；PlatformGateway 重连先同步快照，未确认版本不能直接投递。
- [ ] **FIX-END-04 / P0：提交终局后发起收尾。** 依赖：FIX-END-01。交付：GameServer 可靠上报最终帧回执并停止战斗 Tick。验收：最终提交后 kill 仍能发现并推进结束操作；通知重发不重复发奖；ClosingInput 事件不改变胜负。
- [ ] **FIX-END-05 / P0：下发关闭投递命令。** 依赖：FIX-END-03、FIX-END-04。交付：ControlServer 持久生成指定挂载/房间代的 CloseRouteVersion，并发往全部持有效路由授权的 PlatformGateway。验收：离线但授权未失效的 PlatformGateway 仍计入待确认集合；重复命令不误关闭新局。
- [ ] **FIX-END-06 / P0：接收端阻止旧局新输入。** 依赖：FIX-END-05、FIX-INPUT-03。交付：GameServer 使用持久输入代/路由栅栏阻止关闭后的新权威输入，保留收尾写权限。验收：旧 PlatformGateway 延迟推送被拒；已接受事件仍可查询和终态处置，不能靠撤销整个 owner 提前堵死收尾。
- [ ] **FIX-END-07 / P0：PlatformGateway 排空并确认。** 依赖：FIX-END-05、FIX-END-06。交付：PlatformGateway 停止旧局新推送，查询在途未知结果、持久处置待投递项后回报 DeliveryStopped。验收：仅入队不返回完成确认；回执携带实例身份、关闭版本、结束操作与截止水位，崩溃恢复不丢待处理项。
- [ ] **FIX-END-08 / P0：验证全部 PlatformGateway 的关闭屏障。** 依赖：FIX-END-07。交付：ControlServer 校验所有有效授权持有者的确认，失联者仅在授权失效且接收端栅栏生效后退出屏障。验收：漏一实例、错版本、旧 incarnation 均不能放行；网络恢复后旧实例不可继续推送。
- [ ] **FIX-END-09 / P0：平台结束调用增加前置闸。** 依赖：FIX-END-08。交付：平台结束任务绑定持久屏障凭据，PlatformGateway 执行前校验授权阶段。验收：人为阻塞 PlatformGateway 确认时平台模拟器收到的结束调用为零；确认完成后才允许首次调用。
- [ ] **FIX-END-10 / P0：按平台能力完成外部收尾。** 依赖：FIX-END-09。交付：区分局结束、玩法解绑和直播结束；复用 OperationId 核对结果，保存成功/NotRequired/OutcomeUnknown。验收：普通换局不误解绑直播；超时不能按成功推进；不可查询且不可幂等的未知结果进入 NeedsAttention。
- [ ] **FIX-END-11 / P0：迁出数据宿主的结算业务调度。** 依赖：FIX-SETTLE-01。交付：由 ControlServer 的业务执行者发现待结算清单并请求 DataServer 执行逐笔存储事务，DataServer 不主动决定结算/收尾步骤。验收：GameServer 退出后业务执行者仍补齐奖励；双执行者不重复记账。
- [ ] **FIX-END-12 / P0：FinalizeRound 校验完整前置证据。** 依赖：FIX-END-10、FIX-END-11。交付：ControlServer 发起释放，DataServer 事务校验 PlatformGateway 屏障、平台收尾、输入终态与奖励完成凭据。验收：任一缺失即拒绝；事务原子释放旧局指针/参与/容量/owner；不在 Finalize 后才首次创建平台结束任务。
- [ ] **FIX-END-13 / P0：释放后开放下一局。** 依赖：FIX-END-12。交付：StreamerServer 在释放回执成功后更新挂载匹配资格。验收：平台失败/未知时不能排新局；成功后可正常再开；旧局迟到事件保留旧归属和去重，不能进入新局。
- [ ] **FIX-END-14 / P0：幂等销毁旧房间实体。** 依赖：FIX-END-12、FIX-ROOM-04、FIX-ROOM-05。交付：ControlServer 请求 GameServer 按 RoomId 和 epoch 销毁并回报；平台任务不依赖原 Actor 存活。验收：Finalize 前不执行正常销毁；重复销毁不抛错，不删新代 Actor；持久结果、账本和去重记录保留。
- [ ] **FIX-END-15 / P0：验证结束全链路顺序。** 依赖：FIX-END-09、FIX-END-10、FIX-END-12、FIX-END-13、FIX-END-14。交付：真实宿主加平台模拟器记录有序证据，并分别在 PlatformGateway 确认前、确认后、平台成功响应丢失和 Finalize 后 kill。验收：每个注入点恢复后仍满足 PlatformGateway 确认先于平台调用、平台收尾先于释放、释放先于正常销毁；恰一份内部奖励，无新旧局串扰。

- [ ] **FIX-END-16 / P0：拆分房间准备与战斗激活。** 依赖：FIX-END-03、FIX-ROOM-03。交付：PrepareRoom 只创建待开战实体并返回准备凭据；ControlServer 在入场提交且 PlatformGateway 路由安装确认后单独激活战斗。验收：未确认路由时 Tick 保持 0；确认并激活后只启动一个循环；激活响应丢失重试不重复启动；准备失败或确认超时不能提前开战。

- [ ] **FIX-END-17 / P0：验收主播平台拉起与重复启动。** 依赖：FIX-END-02、FIX-END-03、FIX-END-16。交付：使用平台脱敏样本或明确标识的模拟器覆盖拉起凭据→验证／绑定→实体加载→LoginReady→GameplayReady→注册路由→开战；重复启动和断线恢复走已有局。验收：无效凭据不登录、挂载未就绪不匹配、重复拉起不另建房、凭据不入日志；真实平台样本缺失保持对应准入 NOT RUN。
- [ ] **FIX-INPUT-08 / P0：验收礼物触发具体玩法行为。** 依赖：FIX-INPUT-06。交付：一条稳定标识的模拟礼物通过平台入口、房间直接推送和配置映射，在已提交 Tick 触发指定技能／积分变化。验收：重复回调只触发一次；接受回执与效果生效可区分；礼物、帧、效果和客户端证据可串联，迟到事件不进入新局。
- [ ] **FIX-INPUT-09 / P1：验收点赞计数与合并。** 依赖：FIX-INPUT-06。交付：为一个已声明平台能力实现增量／累计语义和可选窗口合并。验收：已接受总量与玩法已应用总量可核对；稳定批次重复不重算；阈值行为触发次数符合配置；报告不把一次物理点击必定映射成一次回调。

### 22.11 独立房间 epoch 的 GM 测试能力（P1）

用户要求：按房间单独调整 epoch，方便测试。GM 统一发送到 ControlServer 的独立 Admin 接口，再按架构 §12.1 分发；管理工具不是新的服务进程。目标契约见架构 §4.5。保持 fencing 启用，只允许权威 epoch 单调推进；任意过期值通过测试请求注入。当前尚无可执行 GM 指令。

- [ ] **FIX-GM-01 / P1：定义房间 epoch 管理契约。** 依赖：无。交付：GetRoomEpoch / BumpRoomEpoch 及 GM 调用入口，含 tenant/game/room、ExpectedEpoch、可选 TargetEpoch、OperationId、操作者与理由。验收：未指定目标默认加一；指定目标必须更大；非法、溢出和越权请求拒绝；文档明确不是进程级 epoch。
- [ ] **FIX-GM-02 / P1：实现房间 epoch 原子推进。** 依赖：FIX-GM-01。交付：受限存储事务同时校验并更新 control.ownership 和 game.room 的 epoch，保持 owner 不变，进入 Recovering；回执与审计同事务保存。验收：并发 expected 相同只有一个逻辑操作成功；同 OperationId 重试返回原值；同 ID 不同内容冲突；回滚不出现两处 epoch 不一致。
- [ ] **FIX-GM-03 / P1：接通 GM 服务与路由变更。** 依赖：FIX-GM-02、FIX-END-03、FIX-GM-06。交付：ControlServer 认证与审计入口，提交后可靠发布旧路由暂停/版本变化，分开暴露存储提交和 PlatformGateway 确认状态。验收：漏通知可重放；PlatformGateway 未确认不能显示路由同步成功；不触发平台结束调用，不隐式结束房间。
- [ ] **FIX-GM-04 / P1：验证指定房间的旧 epoch 被拒。** 依赖：FIX-GM-03。交付：双房同 GameServer，单独 bump A 的宿主测试。验收：A 的旧 Actor 帧写、旧 epoch 输入和快照写被拒；A 的已提交历史仍可读；B 的 epoch、Tick 和路由不受影响；无需改 owner 或重启进程。
- [ ] **FIX-GM-05 / P1：验证 bump 后显式恢复。** 依赖：FIX-GM-04、FIX-ROOM-02、FIX-TAKE-03。交付：旧 Actor 退出后由 GM 请求同 owner 按新 epoch 恢复，并经路由确认重新激活。验收：实际从持久头恢复，无同房双循环；重复 bump/恢复不重复增加 epoch 或启动 Actor；响应丢失可按原操作查询。

- [ ] **FIX-GM-06 / P0：实现 ControlServer 操作员管理入口。** 依赖：无。交付：ControlServer 独立 Admin 管理接口及最小 GM 客户端调用示例，认证操作员并按角色和 tenant/game/目标范围授权，操作审计关联 OperationId/指纹/理由。验收：普通主播身份、伪造 Operator 和越权作用域均拒绝；内部进程证书不能冒充任意操作员；同 ID 不同内容冲突，提交后响应丢失能查询原结果。现有内部 Admin gRPC 与请求体 Operator 不作为操作员认证已完成的证据。
- [ ] **FIX-GM-07 / P0：接通 GM 定向分发与生效查询。** 依赖：FIX-GM-06、FIX-LINK-08、FIX-FRAME-02。交付：按架构 §12.1 的明确命令目录，把允许的房间命令定向当前 GameServer/RoomActor，主播命令交 StreamerServer，进程命令经控制计划交 NodeAgent；保存阶段及回执。验收：双房/双实例定向正确；旧 epoch/旧处理者不能生效；RoomActor 命令在已提交 Tick 生效且重试不重复；未实现命令明确拒绝；接口分别显示接受/提交/生效，不把普通 GM 操作改成直接 SQL 或绕过正常结束屏障。

### 22.12 多主播帧同步、离线策略与防作弊（P0/P1）

目标契约与独立时序图：`docs/zh/design/authoritative-sync.md`；现状：`gameserver-threading-current.md`。同步指同一已提交版本状态一致，不承诺网络不同的画面同毫秒显示。

- [ ] **FIX-SYNC-01 / P0：定义通用客户端基线应用契约。** 依赖：无。交付：房间/epoch/tick/version/baseVersion 的快照与增量应用规则，明确模块增量载荷要求。验收：重复、回退、缺基线、跨 epoch 均正确丢弃或重基线；无完整增量契约的模块使用快照。
- [ ] **FIX-SYNC-02 / P0：验证双主播同版本收敛。** 依赖：FIX-SYNC-01、FIX-FRAME-04。交付：不同网络延迟的双客户端场景。验收：相同版本状态哈希一致；慢端队列溢出后快照追平，快端 Tick 不被阻塞；不应用未持久帧。
- [ ] **FIX-SYNC-03 / P0：验证断线与迁移追赶。** 依赖：FIX-SYNC-01、FIX-ROOM-06。交付：断线、重连安装快照期间溢出、跨 epoch 重订阅及终局后重连场景。验收：快照与后续增量无隙、不重演奖励、不误重新匹配，最终结果与服务端一致。
- [ ] **FIX-SYNC-04 / P1：持久钉住离线策略配置。** 依赖：FIX-MODULE-01。交付：AllOfflinePolicy、宽限期、最大暂停时长、恢复就绪人数与暂停输入策略进入房间配置/hash。验收：非法人数或缺失行为拒绝准入；恢复和配置升级不偷偷改变原局策略。
- [ ] **FIX-SYNC-05 / P1：判断全员控制会话离线。** 依赖：FIX-SYNC-04。交付：按当前连接代和服务端超时统计控制参与者，区分断线/退出/关播。验收：观测端断开、旧代迟到关闭和单端掉线不能误判全员离线；宽限期受控生效。
- [ ] **FIX-SYNC-06 / P1：实现持久暂停与时间冻结。** 依赖：FIX-SYNC-05、FIX-FRAME-02。交付：暂停边界、逻辑计时与玩法计时器状态持久保存，租约等墙钟维护继续。验收：全员离线按配置暂停，重启仍保持暂停；ContinueToEnd 继续至结果；不能用停止本地 Timer 冒充持久暂停。
- [ ] **FIX-SYNC-07 / P1：实现暂停期间的输入处置。** 依赖：FIX-SYNC-06、FIX-INPUT-06。交付：按配置有界持久暂存、局外处置或拒绝。验收：礼物不静默丢失，计数可核对；恢复按原归属和预算应用；积压超限显式可见。
- [ ] **FIX-SYNC-08 / P1：实现恢复就绪与暂停超时终结。** 依赖：FIX-SYNC-06、FIX-SYNC-07、FIX-END-15。交付：指定人数完成同基线准备后提交恢复边界；超时按玩法结束/补偿。验收：未达到人数不恢复，旧会话准备不计数；超时不绕过平台关闭确认，重复恢复不启双循环。
- [ ] **FIX-SYNC-09 / P0：校验客户端行为意图。** 依赖：FIX-INPUT-06。交付：服务端校验当前控制会话、参与关系、序号、频率、冷却、资源及动作状态。验收：伪造礼物/分数、跨房、过期会话、重放和超频均不能生成非法权威效果；ACK 不能直接产生奖励。

### 22.13 开箱即用排行榜（P1）

目标契约：`docs/zh/design/developer-capabilities.md`。已有奖励事务积分投影与 TopN 仅作为基础；下面均需独立交付，业务规则不下沉至 DataServer。

- [ ] **FIX-RANK-01 / P1：定义榜单元数据。** 依赖：无。交付：tenant/game/board/period/scope、主体类型、Sum/Max、整数边界与并列规则。验收：跨租户键隔离；负分与溢出受控；同分稳定排序，接口不混淆主播与观众主体。
- [ ] **FIX-RANK-02 / P1：实现默认持久 provider 与迁移。** 依赖：FIX-RANK-01。交付：默认 PostgreSQL 表/索引、schema 兼容和明确的数据读写接口。验收：空库迁移可运行，重复迁移/重启不丢榜，目标索引覆盖榜单作用域。
- [ ] **FIX-RANK-03 / P1：从已提交效果幂等计分。** 依赖：FIX-RANK-02。交付：ControlServer 业务模块校验效果来源，按不可变 EffectId 驱动 Sum/Max 投影，DataServer 只执行存储事务。验收：并发、重放、响应丢失均不重复加分；客户端自报分数和无关资产变更不能进入榜。
- [ ] **FIX-RANK-04 / P1：提供 TopN 查询。** 依赖：FIX-RANK-03。交付：有限页大小、明确排序、读取版本和授权。验收：分数相同仍稳定；非法作用域/越权拒绝；空榜有明确空结果。
- [ ] **FIX-RANK-05 / P1：提供个人及附近名次。** 依赖：FIX-RANK-04。交付：GetRank/GetAroundMe，名次与分数对应同一读取视图。验收：未上榜、同分和榜变动场景正确，不输出不同快照拼接的名次。
- [ ] **FIX-RANK-06 / P1：提供稳定分页。** 依赖：FIX-RANK-04。交付：有期限的读取版本/游标，不使用变化榜的裸 offset 冒充一致分页。验收：翻页期间写入不重复/漏人；版本失效明确要求重新查询。
- [ ] **FIX-RANK-07 / P1：实现周期与迟到归属。** 依赖：FIX-RANK-01、FIX-RANK-03。交付：AllTime 和显式配置的日/周/赛季周期及边界时区，保留旧榜。验收：边界前后与迟到结算进入声明周期；重试不随当前时间改榜；换期不清除旧记录。
- [ ] **FIX-RANK-08 / P1：实现 GM 修正与投影重建。** 依赖：FIX-RANK-03、FIX-RANK-07。交付：指定榜/周期/作用域的有版本审计修正与按来源重建。验收：重复命令不重复修正；重建与原来源一致且不重发资产奖励；保留来源证据。
- [ ] **FIX-RANK-09 / P1：交付玩法与客户端接入示例。** 依赖：FIX-RANK-03、FIX-RANK-04、FIX-RANK-05、FIX-RANK-07。交付：默认 provider 下开局计分、终局更新、查榜和个人名次的可运行示例。验收：重复结算不重复加分、换期旧榜可查；示例直接调用框架能力，不自写另一套榜单存储。

### 22.14 进程注册与主播业务协作闭环（P0/P1）

本节对应首页中英文架构图补齐的关系。图展示目标契约，不代表接口已经全部接通；每项独立交付、独立验收后才勾选。已有任务继续复用：动态 GameServer 调度目录见 FIX-SCHED-01，就绪/容量过滤见 FIX-SCHED-02，直播挂载注册见 FIX-END-02，PlatformGateway 开局路由确认见 FIX-END-03，结束释放与下一局资格见 FIX-END-12、FIX-END-13。这里不重复创建这些实现任务。

- [ ] **FIX-LINK-01 / P0：让进程自报告反映真实就绪状态。** 依赖：无。交付：GameServer 的 ReportProcess 根据依赖检查、实体恢复和排空状态生成 Readiness/Admission/EntityRecovery，不再启动后固定上报 Ready/Open/Steady。验收：依赖不可用、恢复未完成及排空三种状态分别得到对应报告；现存房间与新房准入状态可区分；注册成功不等于已获房间 ownership。
- [ ] **FIX-LINK-02 / P0：验收 GameServer 注册报告的身份和时效。** 依赖：无。交付：真实 GameServer→ControlServer 宿主用例，覆盖实例证书、endpoint、报告序号和有效期。验收：冒用角色/实例被拒；迟到旧序号不能覆盖新状态；停止上报后按配置超时失效；重启新实例不能继承旧实例写权限。已有校验代码可复用，以独立证据验收，不重复开发。
- [ ] **FIX-LINK-03 / P0：接通 StreamerServer 到 ControlServer 的匹配意图入口。** 依赖：FIX-END-02。交付：StreamerActor 以当前 session、BindingId/Generation、匹配分区和 OperationId 请求 ControlServer；ControlServer 校验业务前提后调用明确的 DataServer 事务接口。验收：重复操作只创建一个 ticket；旧挂载代、无资格及已有有效参与关系的请求被拒；StreamerServer 不自行挑选 GameServer，DataServer 不负责选择候选或编排匹配。
- [ ] **FIX-LINK-04 / P0：可靠同步参与关系提交事件。** 依赖：FIX-LINK-03、FIX-END-16。交付：ControlServer 为已提交参与关系生成可恢复通知，按主播投递给当前 StreamerServer，携带房间、参与版本、挂载代与操作标识。验收：提交前不能宣告入场；重复与乱序通知不回退当前参与状态；通知响应丢失不再创建房间；参与提交通知与战斗激活通知可区分。
- [ ] **FIX-LINK-05 / P0：恢复 StreamerServer 丢失的参与状态。** 依赖：FIX-LINK-04。交付：StreamerServer 重启或主播接管后，从权威参与快照恢复，再按版本衔接后续通知。验收：快照读取与事件订阅间发生的变化不丢；旧房间结束通知不能清空新局；恢复已有局不重新排队。终局后下一局资格仍遵循 FIX-END-13 的释放回执。
- [ ] **FIX-LINK-06 / P1：验收 StreamerServer 经 DataServer 读写资料。** 依赖：无。交付：使用正式资料接口完成主播实体加载和带版本保存的宿主用例。验收：无数据库凭据也能运行；跨租户及越权主体访问被拒；并发旧版本保存不能覆盖新资料；资料未加载成功不返回实体就绪。业务校验在 StreamerServer，DataServer 保留存储权限与版本约束。
- [ ] **FIX-LINK-07 / P0：验收 StreamerServer 发起幂等资产事务。** 依赖：无。交付：StreamerServer 按业务规则生成稳定 OperationId，请求 DataServer 执行账本事务，并能查询未知结果。验收：响应丢失重试只产生一次资产变更；同 ID 不同内容冲突；非法扣减原子拒绝；DataServer 不根据资产变化自行决定匹配、平台通知或奖励规则。复用既有账本，不新建第二套资产存储。
- [ ] **FIX-LINK-08 / P0：绑定 StreamerGateway 的参与关系与房间路由。** 依赖：FIX-LINK-04、FIX-SCHED-01。交付：ControlServer 发布的参与信息与房间 owner endpoint/epoch 组成有版本投影，StreamerGateway 安装后才能定位战斗目标。验收：两主播分属不同房间和不同 GameServer 时不会串房；缺失、过期及跨挂载代路由不猜测目标；迁移更新 endpoint/epoch 时保留原 RoomId。登录到某个 StreamerServer 不能当作房间定位依据。
- [ ] **FIX-LINK-09 / P0：客户端战斗输入直接到达 GameServer。** 依赖：FIX-LINK-08、FIX-INPUT-01、FIX-INPUT-03。交付：StreamerGateway 根据已验证参与关系直送目标 GameServer，复用命令幂等与持久接收机制，替代当前 EdgeInputClient 将战斗命令直接送入 DataServer 的入口。验收：命令不经 StreamerServer 中转；接收回执与已提交帧中的生效证据分离；错误 epoch 刷新路由后按原 CommandId 重试；跨房伪造和重复发送不能产生额外效果。退出参与属于业务控制请求，不能随战斗输入一并盲目迁移。
- [ ] **FIX-LINK-10 / P0：验收帧流直连与订阅资源回收。** 依赖：FIX-LINK-08、FIX-SYNC-01。交付：GameServer→StreamerGateway→客户端的真实宿主链路，明确 gRPC Subscribe 为一次建立后的持续服务端流，登录/个人业务另走 StreamerServer 请求接口。验收：同一 StreamerGateway 的同房客户端复用上游订阅；最后一个客户端离开时回收订阅资源；跨 epoch 重新建立基线；慢端不会阻塞房间 Tick；StreamerServer 停止时已有合法会话的帧通道不依赖其逐帧转发，仍执行会话有效期与撤销策略。
- [ ] **FIX-LINK-11 / P1：实测帧通道的容量与延迟预算。** 依赖：FIX-LINK-10、FIX-PERF-01。交付：按声明的 Tick 频率、帧大小、房间数及订阅数，记录提交到客户端应用的 p50/p95/p99、队列深度、带宽、CPU 和内存；明确 HTTP/2 并发流、连接池、超时及流控配置。验收：事先声明并逐项核对容量/延迟预算；注入一个慢接收端及批量重连，报告对其他房间的影响；超过预算保持未通过，不凭“使用 gRPC Streaming”宣告达标。是否更换传输协议由同条件测量结果决定。

- [ ] **FIX-LINK-12 / P0：接通退出参与的业务控制入口。** 依赖：FIX-LINK-03、FIX-LINK-04。交付：客户端 LeaveRound 经 StreamerGateway 交给 StreamerServer 校验当前身份和参与关系，再由 ControlServer 协调 DataServer 执行幂等退出事务。验收：重复 OperationId 返回同一结果，旧挂载代/跨房请求不能退出当前参与；退出后战斗输入被拒，其他主播继续按玩法规则运行；涉及房间结束时进入 FIX-END-01 的统一流程。已有工作区 LeaveRound/WithdrawParticipation 代码先核验并复用，不能继续保留 StreamerGateway 绕过业务进程直接写 DataServer 的控制入口。
