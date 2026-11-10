# TODO

## 22. 架构审查整改：原子任务（2026-10-29）

当前职责：ControlServer 负责注册目录、匹配调度、路由发布、恢复和结束协调；StreamerServer 负责主播实体与个人业务；StreamerGateway 直连 GameServer 传递战斗输入与帧；PlatformGateway 根据 ControlServer 路由直推房间事件。DataServer 只负责明确的数据读写、事务与约束，不解析平台业务、不选择房间、不编排结算或平台调用。

### 22.1 帧提交结果未知：冻结请求再重试（P0）

代码入口：`src/Chorus.GameServer/RoomActor.cs`、`src/Chorus.DataServer/FrameGrpcService.cs`、`RedesignFrameStore.cs`。当前风险：提交成功但响应丢失后重新读取 Inbox，以原 CommitId 提交不同内容，引发幂等冲突并停房。

- [x] **FIX-FRAME-01：保存待确认帧。** 依赖：无。
  - 证据：bb9f4a9；4/4 绿
- [x] **FIX-FRAME-02：确认成功后只推进一次。** 依赖：FIX-FRAME-01。
  - 证据：bb9f4a9
- [x] **FIX-FRAME-03：区分暂时故障与确定拒绝。** 依赖：FIX-FRAME-01。
  - 证据：bb9f4a9
- [x] **FIX-FRAME-04：注入“提交成功、响应丢失”。** 依赖：FIX-FRAME-01、FIX-FRAME-02、FIX-FRAME-03。
  - 证据：bb9f4a9；2/2 绿
- [x] **FIX-FRAME-05：注入“提交未完成、新输入到达”。** 依赖：FIX-FRAME-01、FIX-FRAME-02、FIX-FRAME-03。
  - 证据：bb9f4a9

### 22.2 本地房间生命周期与回收（P0）

代码入口：`src/Chorus.GameServer/RoomRuntime.cs`、`RoomActor.cs`。当前风险：字典中存在 Actor 被当作正在运行；停止 Actor 和终局 Actor 长期驻留。

- [x] **FIX-ROOM-01：显式记录 Actor 执行状态。** 依赖：无。
  - 证据：75ebce9；8/8 绿；1320/1320 绿
- [x] **FIX-ROOM-02：校验恢复入口的已有 Actor。** 依赖：FIX-ROOM-01。
  - 证据：8d7f6c4；4/4 绿；3/3 绿
- [x] **FIX-ROOM-03：串行化同房间的创建与恢复。** 依赖：FIX-ROOM-02。
  - 证据：8f27a22；4/4 绿；4/4 绿
- [x] **FIX-ROOM-04：移除退出的 Actor。** 依赖：FIX-ROOM-01、FIX-ROOM-03。
  - 证据：f690649；2/2 绿；20/20 绿
- [x] **FIX-ROOM-05：有界保留终局查询能力。** 依赖：FIX-ROOM-04。
  - 证据：ca803ee；4/4 绿；6/6 绿
- [x] **FIX-ROOM-06：验证同进程二次接管。** 依赖：FIX-ROOM-02、FIX-ROOM-03、FIX-ROOM-04。
  - 证据：3040f0d；1/1 绿；11/11 绿
- [x] **FIX-ROOM-07：验证长时间房间周转。** 依赖：FIX-ROOM-04、FIX-ROOM-05。
  - 证据：d97ade1；2/2 绿；1/1 绿

### 22.3 接管状态、就绪与活性（P0）

代码入口：`src/Chorus.ControlServer/TakeoverGrpcService.cs`、`RedesignControlStore.cs`、`src/Chorus.DataServer/RedesignRoomStore.cs`。当前风险：恢复 RPC 前已 Confirm 为 Active；目标进程存活即可续租，不能说明房间恢复成功。

- [x] **FIX-TAKE-01：定义恢复与激活的权限矩阵。** 依赖：无。
  - 证据：fde0726
- [x] **FIX-TAKE-02：提供受限的恢复快照写入。** 依赖：FIX-TAKE-01。
  - 证据：308437b；4/4 绿；5/5 绿
- [x] **FIX-TAKE-03：恢复成功后原子激活。** 依赖：FIX-TAKE-02、FIX-ROOM-03。
  - 证据：64efb6d；5/5 绿；6/6 绿
- [x] **FIX-TAKE-04：持久记录恢复操作。** 依赖：FIX-TAKE-01。
  - 证据：c558b1b；6/6 绿；2/2 绿
- [x] **FIX-TAKE-05：回收超时恢复任务。** 依赖：FIX-TAKE-03、FIX-TAKE-04。
  - 证据：9c8e34b；4/4 绿；1/1 绿
- [x] **FIX-TAKE-06：建立房间进度信号。** 依赖：FIX-ROOM-01。
  - 证据：ceaa98d；5/5 绿；3/3 绿
- [x] **FIX-TAKE-07：按房间进度触发恢复治理。** 依赖：FIX-TAKE-05、FIX-TAKE-06。
  - 证据：2d76603；1/1 绿；6/6 绿
- [x] **FIX-TAKE-08：验证接管各阶段中断。** 依赖：FIX-TAKE-03、FIX-TAKE-04、FIX-TAKE-05。
  - 证据：e277b96；3/3 绿；4/4 绿

### 22.4 恢复读取的一致性与规模（P1）

代码入口：`src/Chorus.DataServer/RedesignRecoveryStore.cs`。当前风险：读取所有快照、所有 delivery 及最老快照后的帧；多查询缺少一致性视图，恢复成本随整局历史增长。

- [x] **FIX-READ-01：固定恢复读取视图。** 依赖：无。
  - 证据：292fe1a；5/5 绿；13/13 绿
- [x] **FIX-READ-02：先读取最新候选快照。** 依赖：FIX-READ-01。
  - 证据：0d0c9d2；6/6 绿；14/14 绿
- [x] **FIX-READ-03：按需读取重放输入。** 依赖：FIX-READ-02。
  - 证据：eb3ef1c；7/7 绿；5/5 绿
- [x] **FIX-READ-04：逐级回退损坏快照。** 依赖：FIX-READ-02、FIX-READ-03。
  - 证据：55dd54f；2/2 绿；19/19 绿
- [x] **FIX-READ-05：限制单次恢复传输大小。** 依赖：FIX-READ-01、FIX-READ-02、FIX-READ-03。
  - 证据：febb5f8；3/3 绿；19/19 绿
- [x] **FIX-READ-06：验证清理与恢复并发。** 依赖：FIX-READ-04、FIX-READ-05。
  - 证据：9466193；5/5 绿；4/4 绿

### 22.5 结算存储、并发执行与阻塞诊断（P0）

代码入口：`src/Chorus.GameServer/RoomRuntime.cs`、`src/Chorus.DataServer/RedesignSettlementStore.cs`、ControlServer。DataServer 提供存储接口；ControlServer 的结算执行者见 FIX-END-11，结束与释放协调见 FIX-END-12。

- [x] **FIX-SETTLE-01：查询待完成结算。** 依赖：无。
  - 证据：e6f49ed；5/5 绿；12/12 绿
- [x] **FIX-SETTLE-03：多执行器并发与失败退避。** 依赖：FIX-END-11。
  - 证据：2ebf2e0
- [x] **FIX-SETTLE-05：暴露终局阻塞原因。** 依赖：FIX-END-12。
  - 证据：5729017
- [x] **FIX-SETTLE-06：验证自动收尾后再次开局。** 依赖：FIX-SETTLE-03、FIX-SETTLE-05、FIX-END-13。
  - 证据：5c28173；1/1 绿；41/41 绿

### 22.6 实时性、容量与负载治理（P1）

- [x] **FIX-PERF-01：增加 Tick 分段耗时。** 依赖：无。
  - 证据：d1f08dc；3/3 绿
- [x] **FIX-PERF-02：增加存储等待诊断。** 依赖：无。
  - 证据：4208e43；5/5 绿；18/18 绿
- [x] **FIX-PERF-03：重建同步起跑基准。** 依赖：FIX-PERF-01、FIX-PERF-02。
  - 证据：85be646；4/4 绿；13/13 绿
- [x] **FIX-PERF-04：建立可失败的性能门槛。** 依赖：FIX-PERF-03。
  - 证据：439b8ff；9/9 绿；NOT RUN
- [x] **FIX-PERF-05：定位并优化一个已测瓶颈。** 依赖：FIX-PERF-01、FIX-PERF-02、FIX-PERF-03。
  - 证据：24b326a、0f6c747；11/11 绿；2/2 绿
- [x] **FIX-PERF-06：实现持续超预算策略。** 依赖：FIX-PERF-01。
  - 证据：1d32562；23/23 绿；13/13 绿
- [x] **FIX-PERF-07：实现单 GameServer 准入预算。** 依赖：FIX-ROOM-01、FIX-PERF-01。
  - 证据：1e833cc；6/6 绿；2/2 绿
- [x] **FIX-PERF-08：执行 100 房同时恢复基准。** 依赖：FIX-READ-05、FIX-TAKE-08、FIX-PERF-03。
  - 证据：88d6b49；5/5 绿；1417/1417 绿；NOT RUN

### 22.7 调度与模块边界（P1/P2）

- [x] **FIX-SCHED-01 / P1：接通动态 GameServer 目录。** 依赖：无。
  - 证据：cc746dd；7/7 绿；2/2 绿
- [x] **FIX-SCHED-02 / P1：按就绪与容量选择目标。** 依赖：FIX-SCHED-01、FIX-PERF-07。
  - 证据：18935f5；8/8 绿；1378/1378 绿
- [x] **FIX-MODULE-01 / P2：定义持久模块恢复身份。** 依赖：无。
  - 证据：80aad99；1384/1384 绿；12/12 绿
- [x] **FIX-MODULE-02 / P2：按持久身份加载玩法。** 依赖：FIX-MODULE-01。
  - 证据：ab1002e
- [x] **FIX-MODULE-03 / P2：遵守 FullCheckpoint 恢复模式。** 依赖：FIX-MODULE-02。
  - 证据：6209082、15be2d9
- [x] **FIX-MODULE-04 / P2：验证升级后恢复旧局。** 依赖：FIX-MODULE-02。
  - 证据：a57519a

### 22.8 权威语义与外部验收（P1/P2）

- [x] **FIX-AUTH-01 / P1：明确 Streamer 所有权语义。** 依赖：无。
  - 证据：a3f3df7；3/3 绿
- [x] **FIX-AUTH-02 / P1：防止迟到声明污染 Streamer 路由。** 依赖：FIX-AUTH-01。
  - 证据：bb1f040
- [x] **FIX-ENV-01 / P2：独立机器分区接管验收。** 依赖：FIX-TAKE-08、FIX-FRAME-04、FIX-ROOM-06。
  - 证据：NOT RUN（不具备独立机器/可控分区/同步复制环境）；依赖 FIX-TAKE-08、FIX-FRAME-04、FIX-ROOM-06 均已勾选；验收手册 docs/architecture/room-multihost-acceptance.md 已记录现有基础设施不满足进入标准（2026-11-06 复核：主机 coding 为 KVM 虚拟机，唯一可达主机 dev/ali 为 2 核 1GiB KVM 云主机且资源不足，home-runner/home-pve 不可达）。
- [ ] **FIX-ENV-02 / P2：数据库主备故障验收。** 依赖：无；环境：独立同步备库及可隔离旧主的操作能力。
- [ ] **FIX-ENV-03 / P2：Linux/Windows 双向重放验收。** 依赖：FIX-MODULE-02；环境：两个真实运行环境。
- [ ] **FIX-ENV-04 / P2：Windows 进程树实机验收。** 依赖：无；环境：Windows 目标节点。
- [ ] **FIX-ENV-05 / P2：抖音生产适配器准入。** 依赖：无；环境：获授权的应用、凭据、官方协议版本和脱敏样本。
- [ ] **FIX-ENV-06 / P2：快手生产适配器准入。** 依赖：无；环境：获授权的应用、凭据、官方协议版本和脱敏样本。

- [ ] **FIX-ENV-07 / P2：Linux 实机安装与升级验收。** 依赖：无；环境：Linux 目标节点。
- [ ] **FIX-ENV-08 / P2：Windows 实机安装与升级验收。** 依赖：FIX-ENV-04；环境：Windows 目标节点。

### 22.9 平台事件到房间的输入闭环（P0/P1）

- [x] **FIX-INPUT-01 / P0：定义房间直接推送契约。** 依赖：无。
  - 证据：0a54da3
- [x] **FIX-INPUT-02 / P0：接通 PlatformGateway 房间路由。** 依赖：FIX-INPUT-01。
  - 证据：551f66d
- [x] **FIX-INPUT-03 / P0：实现目标房间的持久接收。** 依赖：FIX-INPUT-01。
  - 证据：5dc65d2
- [x] **FIX-INPUT-04 / P0：接通推送输入的 Tick 消费。** 依赖：FIX-INPUT-03、FIX-FRAME-04。
  - 证据：363fcca
- [x] **FIX-INPUT-05 / P0：处理推送超时和路由变化。** 依赖：FIX-INPUT-02、FIX-INPUT-03。
  - 证据：92d0fda
- [x] **FIX-INPUT-06 / P0：验证直接推送宿主全链路。** 依赖：FIX-INPUT-02、FIX-INPUT-03、FIX-INPUT-04、FIX-INPUT-05。
  - 证据：b28141c、3b4a2a7
- [x] **FIX-INPUT-07 / P1：验证推送模式的恢复补齐。** 依赖：FIX-INPUT-06、FIX-TAKE-08。
  - 证据：9d82de4、14ba4ed
- [x] **FIX-INPUT-08 / P1：PlatformGateway 平台模块化按需装配。** 依赖：无。
  - 证据：48623a5、6585adb；9/9 绿；2/2 绿
- [x] **旧架构三宿主删除。** 依赖：平台模块任务（6585adb ✅）。
  - 证据：6585adb、f1fc79e、48623a5；1371/1371 绿

### 22.10 房间结束、PlatformGateway 确认、平台通知与销毁（P0）

- [x] **FIX-END-01 / P0：持久结束操作。** 依赖：无。
  - 证据：770f8d2
- [x] **FIX-END-02 / P0：注册挂载路由来源。** 依赖：FIX-INPUT-01。
  - 证据：3990417
- [x] **FIX-END-03 / P0：确认开局路由生效。** 依赖：FIX-END-02、FIX-INPUT-02。
  - 证据：6a0f45b；6/6 绿
- [x] **FIX-END-04 / P0：提交终局后发起收尾。** 依赖：FIX-END-01。
  - 证据：305296c；2/2 绿
- [x] **FIX-END-05 / P0：下发关闭投递命令。** 依赖：FIX-END-03、FIX-END-04。
  - 证据：2f9c112
- [x] **FIX-END-06 / P0：接收端阻止旧局新输入。** 依赖：FIX-END-05、FIX-INPUT-03。
  - 证据：5a1fb4c；2/2 绿
- [x] **FIX-END-07 / P0：PlatformGateway 排空并确认。** 依赖：FIX-END-05、FIX-END-06。
  - 证据：cfb018e
- [x] **FIX-END-08 / P0：验证全部 PlatformGateway 的关闭屏障。** 依赖：FIX-END-07。
  - 证据：7b75846
- [x] **FIX-END-09 / P0：平台结束调用增加前置闸。** 依赖：FIX-END-08。
  - 证据：a9cd7c6
- [x] **FIX-END-10 / P0：按平台能力完成外部收尾。** 依赖：FIX-END-09。
  - 证据：a6eeb69
- [x] **FIX-END-11 / P0：迁出数据宿主的结算业务调度。** 依赖：FIX-SETTLE-01。
  - 证据：7658905
- [x] **FIX-END-12 / P0：FinalizeRound 校验完整前置证据。** 依赖：FIX-END-10、FIX-END-11。
  - 证据：2e9dc99
- [x] **FIX-END-13 / P0：释放后开放下一局。** 依赖：FIX-END-12。
  - 证据：4b4eda0
- [x] **FIX-END-14 / P0：幂等销毁旧房间实体。** 依赖：FIX-END-12、FIX-ROOM-04、FIX-ROOM-05。
  - 证据：95dfb48
- [x] **FIX-END-15 / P0：验证结束全链路顺序。** 依赖：FIX-END-09、FIX-END-10、FIX-END-12、FIX-END-13、FIX-END-14。
  - 证据：f87c9f6；5/5 绿

- [x] **FIX-END-16 / P0：拆分房间准备与战斗激活。** 依赖：FIX-END-03、FIX-ROOM-03。
  - 证据：315d633

- [x] **FIX-END-17 / P0：验收主播平台拉起与重复启动。** 依赖：FIX-END-02、FIX-END-03、FIX-END-16。
  - 证据：1bf8a2b；18/18 绿；NOT RUN
- [x] **FIX-INPUT-08 / P0：验收礼物触发具体玩法行为。** 依赖：FIX-INPUT-06。
  - 证据：5b63b94
- [x] **FIX-INPUT-09 / P1：验收点赞计数与合并。** 依赖：FIX-INPUT-06。
  - 证据：9803b78

### 22.11 独立房间 epoch 的 GM 测试能力（P1）

- [x] **FIX-GM-01 / P1：定义房间 epoch 管理契约。** 依赖：无。
  - 证据：23a2165；6/6 绿；58/58 绿
- [x] **FIX-GM-02 / P1：实现房间 epoch 原子推进。** 依赖：FIX-GM-01。
  - 证据：73ccbae；10/10 绿
- [x] **FIX-GM-03 / P1：接通 GM 服务与路由变更。** 依赖：FIX-GM-02、FIX-END-03、FIX-GM-06。
  - 证据：a5632b8
- [x] **FIX-GM-04 / P1：验证指定房间的旧 epoch 被拒。** 依赖：FIX-GM-03。
  - 证据：3737a41
- [x] **FIX-GM-05 / P1：验证 bump 后显式恢复。** 依赖：FIX-GM-04、FIX-ROOM-02、FIX-TAKE-03。
  - 证据：f65f553

- [x] **FIX-GM-06 / P0：实现 ControlServer 操作员管理入口。** 依赖：无。
  - 证据：4ebd198；13/13 绿；2/2 绿
- [x] **FIX-GM-07 / P0：接通 GM 定向分发与生效查询。** 依赖：FIX-GM-06、FIX-LINK-08、FIX-FRAME-02。
  - 证据：c300c79；host 1 + store 1 绿（定向）；feat 665ba78 / test c300c79

### 22.12 多主播帧同步、离线策略与防作弊（P0/P1）

- [x] **FIX-SYNC-01 / P0：定义通用客户端基线应用契约。** 依赖：无。
  - 证据：31d66ea
- [x] **FIX-SYNC-02 / P0：验证双主播同版本收敛。** 依赖：FIX-SYNC-01、FIX-FRAME-04。
  - 证据：8872b40
- [x] **FIX-SYNC-03 / P0：验证断线与迁移追赶。** 依赖：FIX-SYNC-01、FIX-ROOM-06。
  - 证据：7e17d61
- [x] **FIX-SYNC-04 / P1：持久钉住离线策略配置。** 依赖：FIX-MODULE-01。
  - 证据：e8c9051
- [x] **FIX-SYNC-05 / P1：判断全员控制会话离线。** 依赖：FIX-SYNC-04。
  - 证据：e08cfe7
- [x] **FIX-SYNC-06 / P1：实现持久暂停与时间冻结。** 依赖：FIX-SYNC-05、FIX-FRAME-02。
  - 证据：d4cbe12
- [x] **FIX-SYNC-07 / P1：实现暂停期间的输入处置。** 依赖：FIX-SYNC-06、FIX-INPUT-06。
  - 证据：533643a
- [x] **FIX-SYNC-08 / P1：实现恢复就绪与暂停超时终结。** 依赖：FIX-SYNC-06、FIX-SYNC-07、FIX-END-15。
  - 证据：01acf32
- [x] **FIX-SYNC-09 / P0：校验客户端行为意图。** 依赖：FIX-INPUT-06。
  - 证据：ff08d61

### 22.13 开箱即用排行榜（P1）

目标契约：`docs/zh/design/developer-capabilities.md`。已有奖励事务积分投影与 TopN 仅作为基础；下面均需独立交付，业务规则不下沉至 DataServer。

- [x] **FIX-RANK-01 / P1：定义榜单元数据。** 依赖：无。
  - 证据：7ceda26
- [x] **FIX-RANK-02 / P1：实现默认持久 provider 与迁移。** 依赖：FIX-RANK-01。
  - 证据：1aa034a
- [x] **FIX-RANK-03 / P1：从已提交效果幂等计分。** 依赖：FIX-RANK-02。
  - 证据：b642e35
- [x] **FIX-RANK-04 / P1：提供 TopN 查询。** 依赖：FIX-RANK-03。
  - 证据：b4dcd84
- [x] **FIX-RANK-05 / P1：提供个人及附近名次。** 依赖：FIX-RANK-04。
  - 证据：3c47141
- [x] **FIX-RANK-06 / P1：提供稳定分页。** 依赖：FIX-RANK-04。
  - 证据：7080255
- [x] **FIX-RANK-07 / P1：实现周期与迟到归属。** 依赖：FIX-RANK-01、FIX-RANK-03。
  - 证据：d00ea92
- [x] **FIX-RANK-08 / P1：实现 GM 修正与投影重建。** 依赖：FIX-RANK-03、FIX-RANK-07。
  - 证据：e5d97bc
- [x] **FIX-RANK-09 / P1：交付玩法与客户端接入示例。** 依赖：FIX-RANK-03、FIX-RANK-04、FIX-RANK-05、FIX-RANK-07。
  - 证据：95a5fe3

### 22.14 进程注册与主播业务协作闭环（P0/P1）

- [x] **FIX-LINK-01 / P0：让进程自报告反映真实就绪状态。** 依赖：无。 - 证据：d15afa2。
- [x] **FIX-LINK-02 / P0：验收 GameServer 注册报告的身份和时效。** 依赖：无。 - 证据：4c17d3b。
- [x] **FIX-LINK-03 / P0：接通 StreamerServer 到 ControlServer 的匹配意图入口。** 依赖：FIX-END-02。 - 证据：0989aa6。
- [x] **FIX-LINK-04 / P0：可靠同步参与关系提交事件。** 依赖：FIX-LINK-03、FIX-END-16。 - 证据：83844c4。
- [x] **FIX-LINK-05 / P0：恢复 StreamerServer 丢失的参与状态。** 依赖：FIX-LINK-04。 - 证据：ed3b78d。
- [x] **FIX-LINK-06 / P1：验收 StreamerServer 经 DataServer 读写资料。** 依赖：无。 - 证据：b072111。
- [x] **FIX-LINK-07 / P0：验收 StreamerServer 发起幂等资产事务。** 依赖：无。 - 证据：8f1b161。
- [x] **FIX-LINK-08 / P0：绑定 StreamerGateway 的参与关系与房间路由。** 依赖：FIX-LINK-04、FIX-SCHED-01。 - 证据：c05b7d8。
- [x] **FIX-LINK-09 / P0：客户端战斗输入直接到达 GameServer。** 依赖：FIX-LINK-08、FIX-INPUT-01、FIX-INPUT-03。 - 证据：c5c3c60。
- [x] **FIX-LINK-10 / P0：验收帧流直连与订阅资源回收。** 依赖：FIX-LINK-08、FIX-SYNC-01。 - 证据：94981f5。
- [x] **FIX-LINK-11 / P1：实测帧通道的容量与延迟预算。** 依赖：FIX-LINK-10、FIX-PERF-01。 - 证据：a7fc01c。

- [x] **FIX-LINK-12 / P0：接通退出参与的业务控制入口。** 依赖：FIX-LINK-03、FIX-LINK-04。 - 证据：ed709f7。

### 22.15 NodeAgent 本机进程监管（P1）

- [x] **FIX-AGENT-01 / P1：接通六类服务的受控进程槽位。** 依赖：无。 - 证据：e8b2ef0。
- [x] **FIX-AGENT-02 / P1：让 Drain 等待业务排空确认。** 依赖：FIX-LINK-01。 - 证据：661dde0。
- [x] **FIX-AGENT-03 / P1：实现有界异常重启与隔离。** 依赖：无。 - 证据：3f866bb。

### 22.16 Web 控制台：React + Ant Design + ProComponents（P0/P1）

- [x] **FIX-WEB-01 / P0：定义浏览器 Admin API 契约。** 依赖：无。
  - 证据：11f0583；contract 3/3 绿（路由表/命令三元组/WhoAmI 形状，钉编译描述符）；gate 1877/1881（4 共租文档签名零新）
- [x] **FIX-WEB-02 / P1：建立 React 控制台工程。** 依赖：无。
  - 证据：d1cbf5d；pnpm build 绿（tsc+vite）；ci console-build job 入门
- [x] **FIX-WEB-03 / P0：在 ControlServer 暴露浏览器管理 API。** 依赖：FIX-WEB-01、FIX-GM-06。
  - 证据：fa968a0；浏览器 face host 1/1（whoami/401/viewer 读/viewer→gm 403/gm 命令/伪造身份 403/status 放宽/ready-404 对真端点）；迁移 4 文件 9/9；契约 3/3；gate 1879/1882（3 既有文档签名零新）
- [x] **FIX-WEB-04 / P0：接通操作员登录页面。** 依赖：FIX-WEB-02、FIX-WEB-03。
  - 证据：258a00b；页面测试 6/6（fetch 边界 mock：签入/401/503/无会话回退/重探恢复/登出）；pnpm build 绿；pnpm 10+12 双跑冻结安装+测试+构建绿；ci console-build 加 Test 步
- [x] **FIX-WEB-05 / P1：建立类型化 API 客户端。** 依赖：FIX-WEB-01、FIX-WEB-02。
  - 证据：22900c1；生成器+漂移门（重渲染==已提交，CHORUS_REGEN_ADMIN_TS=1 再生）；int64 族实测为 JSON 数字（WEB-03 真端点见证断言钉死）；pnpm build 绿+页面测试 6/6+.NET 定向 5/5
- [ ] **FIX-WEB-06 / P1：实现节点与进程管理页面。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-OBS-07。
- [ ] **FIX-WEB-07 / P1：实现房间详情页面。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-OBS-08。
- [ ] **FIX-WEB-08 / P1：实现主播与匹配页面。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-OBS-09。
- [x] **FIX-WEB-09 / P0：接通管理操作提交与结果查询 API。** 依赖：FIX-WEB-03、FIX-GM-07。
  - 证据：a890110；store 2/2 + 浏览器 face host 1/1（dispatch→DurableAccepted→Applied 数字 tick→无 scope 同答→Audited 证据→NotFound）；回归 9 绿；gate 1880/1886（6 既有文档签名零新）
- [x] **FIX-WEB-10 / P1：实现管理操作与回执页面。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-WEB-09。
  - 证据：6052714；七命令页签+回执查询（仅 id+空维、stage 词分色不折叠）；页面测试 5/5（fetch 边界：提交/派发/回执/viewer 命令地板/403）；pnpm build 绿+11/11；pnpm 10 冻结安装+测试+构建绿
- [x] **FIX-WEB-11 / P1：接入独立房间 epoch 的 GM 表单。** 依赖：FIX-WEB-10、FIX-GM-03、FIX-GM-05。
  - 证据：8fea6af；查/抬两卡（expected 必填 stale 可见拒、target 0 哨兵、routeSync 与提交分显、duplicate 记录值）；页面测试 4/4；pnpm build 绿+15/15；pnpm 10 CI=true 冻结安装+测试+构建绿
- [x] **FIX-WEB-12 / P1：为后台提供排行榜查询接口。** 依赖：FIX-WEB-03、FIX-RANK-04、FIX-RANK-05、FIX-RANK-06、FIX-RANK-07。
  - 证据：b179c8e；三读 RPC（top/rank/around；操作员只命名读键，写侧描述子中性缺省；read_version 0=从未计分哨兵）；宿主测试 1 fact（分页/续页版本不变/唯一名次/窗口/stale 显式拒+malformed 400/viewer 地板 401/缺字段 400）；顺带修 RANK-06 板版本残余窗假 stale 与 agent 种子 7502 互撞（CI 三连红根因）+WEB-01 漂移表 15→18；回归 RANK 180/180+WEB-01 3/3+WEB-05 1/1+WEB-03 1/1；gate 1882/1887 零新签名（M2 时钟族干净树对照钉死，风暴 4 轮判定细节见 ledger）
- [x] **FIX-WEB-13 / P1：实现开箱即用排行榜后台页面。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-WEB-12。
  - 证据：94fd564；三查询卡（TopN 快照走板=Continue 带 token 版本标签不变/stale 400 重启词清场、名次与窗口随版本标签、found=false 是回答非错误、空 period/scope 上 wire 即缺省桶）；页面测试 5/5（wire 全等+走板/stale/作用域/viewer 地板）；pnpm build 绿+20/20；pnpm 10 CI=true 冻结安装+测试+构建绿
- [x] **FIX-WEB-14 / P1：实现排行榜维护操作。** 依赖：FIX-WEB-10、FIX-WEB-13、FIX-RANK-08。
  - 证据：5e42c16；三命令 RPC（adjust/reset/rebuild，gm 地板；audited triple 领位 request_id=幂等键，读答复加 opaque read_version 串——unix-ms 有损、命令令牌需 "O" 无损往返；桥接按码分义 Aborted=stale/FailedPrecondition=界限拒/AlreadyExists=409）；宿主测试 1 fact（修正/同 id 重放 duplicate=需重读后版本——RequireVersion 先于 claim/stale 400 reload 词/负 delta 界限拒/subject+整板重置/重建 wipe 前后 entries 1→0=账本忠实/viewer 403/操作不匹配 403/缺字段 400）；页面 +2 fact（wire 全等+读→版本预填+reload 词+viewer 地板词），console 22/22；漂移门 18→21 方法 17→20 注解；gate 1882/1888 风暴窗零新签名（M2 家族干净树背靠背同败，新签名 PlatformTaskStore/RecoveryStore solo 即绿）
- [x] **FIX-WEB-15 / P1：实现操作审计查询。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-WEB-09。
  - 证据：e221329；读 RPC /admin/api/v1/get-audit-trail（command/operator 逐维精确词过滤、max_entries bounded 1..200、audit_id keyset 游标十进制串——append-only 日志无需快照失效裁决，取 max+1 行裁 has-more）；viewer 地板（匿名 401）；宿主测试 1 fact（首页 newest-first verbatim、全走板恰一次、双过滤+未知词空页、viewer/401/201→400）；漂移门 22 方法/21 注解；页面 Audit trail 卡（Continue 带 cursor 追加更老行+七列表格）+1 fact，console 23/23；gate 1878/1889 风暴 load 33-49 零新签名（FrameChannel 干净树 933fb83 对照 worktree 背靠背同败且跨负载双败=环境实锤；PlatformGatewayRouting solo 3/3 绿；42501×5/M2/FanoutReclaim 归文档族）
- [x] **FIX-WEB-16 / P1：完成控制台中英文。** 依赖：FIX-WEB-02、FIX-WEB-04。
  - 证据：d692172；自研零依赖 i18n（t(en,zh) 就地对子无键表可漂移、LanguageProvider+localStorage 'chorus-console-language'、默认英文保既有 fact、en 串逐字节不变）；壳菜单+登录卡/壳头双 toggle（按钮命名去往语言，aria-label 语言中立）；五页全词双语，服务器词汇（stage/状态词/duplicate/wire 键名）不译；页面 +2 fact（默认英文→翻 zh→回 en 存 localStorage；预置 zh 首屏中文+壳词中文），console 25/25+tsc+build 绿；antd 两字中文按钮插空格断言 \s* 容忍；console-only 零 .NET 改动全量门不重跑（CI 后盾）
- [x] **FIX-WEB-17 / P0：验收浏览器管理权限与未知结果。** 依赖：FIX-WEB-10、FIX-WEB-15。
  - 证据：9c7e7b2；闭包 fact（WEB-01 表=描述符注解集=探针集互等）+ 走面 fact（21 路逐条：匿名成形体 401/viewer 读 200+未知形状 found=false 或空页/gm 地板恰 403/gm 命令穿门非 401·403/伪造身份恰 403；queue-gate 对记录值+epoch 未知房 found=false）；**抓到并修正真偏离**：get-room-epoch 三层分歧（GM-06 权威 gm 地板 vs WEB-01 表 viewer vs WEB-11 页 mock viewer lookup 照答）——表向实改 read/gm+ContractRoutes internal 单一事实源、EpochPage lookup 卡 gm 地板化+文案改、epoch 测试 viewer fact 重写；定向 7/7+console 23/23+build 绿；测试-only diff 按 WEB-13 先例全量门不重跑（CI 后盾）
- [x] **FIX-WEB-18 / P1：接通控制台生产托管。** 依赖：FIX-WEB-02、FIX-WEB-03、FIX-WEB-04。
  - 证据：46d735c；CHORUS_CONSOLE_DIST 命名 dist 目录：UseStaticFiles+MapFallbackToFile(index.html) 落客户端路由回壳+显式 MapFallback(/admin/api/{**slug}) 404（未知管理路径不被壳吞）；未设时浏览器面 API-only 与 WEB-03 逐字节同；静态资产无认证（登录页即 SPA），/admin/api 的 bearer 门不动；构建是部署步非 dotnet 步；宿主 +2 fact（fixture dist：根/路由落回壳/资产字节+类型/匿名 401/令牌 200/未知 admin 404 无壳标记；未设：根 404+匿名 401），定向 8/8；全量 pin-8 1893 零新签名（M2 双件套干净树背靠背 4/4 同败=环境时钟窗族实证闭环；agent/FanoutReclaim 文档族 solo 绿；RoundInputStore+KilledGameServer 负载伪影 solo 绿）；毕清 9 孤儿库+0 进程

### 22.17 运维总览、机器负载与平台数据链路统计（P1）

- [x] **FIX-OBS-01 / P1：定义运维指标字典。** 依赖：无。
  - 证据：8052f67；Chorus.Rpc/OpsMetrics.cs（OpsMetricSpec Name/Kind/Unit/Labels/Description + All 40 条 + Spec 未知抛错；三宿主共享项目零运行时调用点）；五族：机器（CPU/内存 used·total·percent/load 1·5·15/磁盘 per mount/uptime）+进程（per instance,role）+房间（total/by_state/by_tenant）+在线分口径（players/streamers/rooms by tenant·game）+入站（既有 7 名+raw by source）+事件链（room 会话既有 4 名+delivered/effect+outbox pending/dispatching/done/failed）+出站（calls/failures/latency by endpoint）；约束内建：Prometheus 名+_total 仅计数器+标签仅有界维度（架构 §12 基数规则钉死）；+6 fact（名字合法唯一/_total⟺Counter 抓 3 处 gauge 误用改名/禁 22 词标签/stage-3 既有名闭包注册/Spec 抛错/每条带描述）；行为中性 diff 全量门不重跑（CI 后盾）
- [x] **FIX-OBS-02 / P1：采集机器 CPU 使用率。** 依赖：FIX-OBS-01。
  - 证据：54b1948；NodeAgent/MachineCpuCollector（/proc/stat 聚合行 Parse→(idle,total)+TrySample 相邻样本求差 (1-idleΔ/totalΔ)*100；首样本只立基线；idle+iowait 计空闲、guest/guest_nice 排除、零间隔/缺文件/畸形→无值非假 0；读源可注入闭可测，非 Linux 优雅无值）；+8 fact（解析/iowait/guest 排除/畸形 null/首样本基线+28.5714% 手推/第三样本基线前移/读源消失/零间隔）；测试项目 +NodeAgent 引用；行为中性零调用点全量门不重跑（CI 后盾）
- [x] **FIX-OBS-03 / P1：采集机器内存。** 依赖：FIX-OBS-01。
  - 证据：c083a6f；NodeAgent/MachineMemoryCollector（/proc/meminfo→(TotalBytes,UsedBytes,UsedPercent)；电平单读无基线；Used=Total−MemAvailable（内核可用口径，非自算）；老内核回落 MemFree+Buffers+Cached；kB/MB/GB/B 缩放；缺文件/畸形/无可用数→无值；used 钳 [0,total]；读源可注入非 Linux 无值）；+5 fact（MemAvailable 50.0%/回落/钳 0/null 族/注入源）；行为中性零调用点全量门不重跑（CI 后盾）
- [x] **FIX-OBS-04 / P1：采集系统负载。** 依赖：FIX-OBS-01。
  - 证据：b86c889；NodeAgent/MachineLoadCollector（/proc/loadavg 前三字段→(1m,5m,15m)；电平单读无基线；负值拒畸形；读源可注入非 Linux 无值）；+4 fact（三均值/零负载/null 族/注入源）；行为中性零调用点全量门不重跑（CI 后盾）
- [x] **FIX-OBS-05 / P1：采集磁盘容量与使用率。** 依赖：FIX-OBS-01。
  - 证据：4eff812；NodeAgent/MachineDiskCollector（df -Pk→每挂载 (mount,total,used,percent)；电平单读无基线；伪/内存文件系统 25 名黑名单跳过、overlay 保留、零容量跳过；\040 空格还原；读源可注入非 Linux 无值）；+5 fact（块设备+overlay per mount/伪内存被跳/空格挂载/缺畸形全伪零容量 null/注入源）；行为中性零调用点全量门不重跑（CI 后盾）
- [x] **FIX-OBS-06 / P1：可靠上报机器遥测。** 依赖：FIX-OBS-02、FIX-OBS-03、FIX-OBS-04、FIX-OBS-05。
  - 证据：e31e794；m0.proto ReportMachineTelemetry（presence 标志族）+0041 迁移（ops.machine_sample+ops.machine_disk_sample）+NodeAgent 三件套（Sampler 聚合四采集器/Pipeline 有界缓冲 drop-oldest+有界重试+退出冲刷/Mapper presence）+BootstrapAgent 5s 循环集成（15s 周期 env 可配）+ControlServer 落库（node 行锁 stale agent 拒）；+16 fact（sampler3+pipeline7+mapper2+store3+M0 host1）+schema 迁移断言更新（表 48→50、迁移 40→41）；全量门 1937 测试 16 失败全为文档化共租 flake 或已修回归（3 宿主断言 solo 全绿）
- [x] **FIX-OBS-07 / P1：提供机器与进程统计 API。** 依赖：FIX-WEB-03、FIX-OBS-06。
  - 证据：8be0d37；完整链（用户确认范围：完整链+ControlHost /api 挂载）——进程采集 `MachineProcessCollector`（槽表源+/proc stat jiffies comm 括号后字段 14/15+VmRSS；CPU% 是 delta 首样本立基线，读失败跳过绝不假零）+`MachineProcessProto`（request.process=16 复用 ReportMachineTelemetry RPC）+0042 迁移（ops.process_sample）+store 同事务落库+`TelemetryStore`（MachineStats/ProcessStats 只读 ops，同库假设 Control:ConnectionString，Sqlite 答空非 500）+`/api/telemetry/machine|process`（RequireAuthorization）；+17 fact（collector7+sampler2+mapper2+store6）+schema 迁移断言更新（表 50→51、迁移 41→42）；全量门 1954 测试 8 失败全为文档化共租签名或 solo 转绿（M2 trio solo+干净树同败=环境族）
- [x] **FIX-OBS-08 / P1：提供房间统计 API。** 依赖：FIX-WEB-03、FIX-OBS-01、FIX-LINK-01。
  - 证据：c335e9d；混合读数（用户确认模式延续：两平面各答各的维度）——`RoomStatsStore`（total/active/by_state 从控制面 records Live 谓词过滤，直方图只含出现过的态；by_tenant 走 game.room 与 admission 同过滤 NOT IN 终态，Sqlite 答空缺席非 500）+`/api/rooms/stats`（RequireAuthorization）；+4 fact；无迁移变更（51/42 不动）；全量门 1958 测试 9 失败全为文档化共租签名或 solo 转绿（M2 trio 已两树同败=环境族、容量 p95 5.80ms 争抢 solo 绿）
- [x] **FIX-OBS-09 / P1：提供分口径在线人数。** 依赖：FIX-WEB-03、FIX-OBS-01。
  - 证据：b604f5f；`OnlineStatsStore`（总数=控制面连接复用路由新鲜度规则 NodeLease 15s/ConnectionLease 30s+gateway 健康，distinct 用户非连接；拆分=数据面——SessionId=room id join game.room 非终态取 tenant·game、无房间行会话只在总数；chorus_online_rooms 读 game.participation Active 参与真相；Sqlite 拆分答空）+`/api/online/stats`；+3 fact；全量门 1961 测试 11 失败全为文档化共租签名或 solo 转绿（M2 环境族/宿主时序 solo 绿）
- [x] **FIX-OBS-10 / P1：统计平台原始入口流量。** 依赖：FIX-OBS-01。
  - 证据：802f1f2；网关进程内计数器 `IngressCounters`（Interlocked 七族，决策点 bump 恰一族；rate_limited 无现成限流器恒 0 保 API 稳定）经 `AppCounterReporter`（证书自报信任，默认 2s）→ m0.proto 新 RPC `ReportAppCounters` → 迁移 0043 `ops.app_counter_sample`（family 有界维度 OBS-11/12 复用）→ `IngressStatsStore` 读最新行/15s 新鲜度求和七族 + `input.event GROUP BY platform`（持久累计自始至今）+`/api/ingress/stats`（RequireAuthorization）；Sqlite 零+空；+14 fact（计数器 bump 8 + 快照字典名序 + store 5）；迁移断言 51→52/42→43；全量门 1975 测试两轮失败 8/17 全判非阻塞（42501 共租签名×12、M2 环境族×3、FanoutReclaim 文档化、GameServerRunsRounds 新面孔 solo 19s 绿=负载诱导）；OBS-10 自身测试全量内全绿。
- [x] **FIX-OBS-11 / P1：统计事件投递与生效链路。** 依赖：FIX-OBS-01、FIX-INPUT-06。
  - 证据：fea3e20；两平面诚实分开——会话面 `RoomEventCounters`（Interlocked 四族，房间面决策点 bump：push 手递→ingested、座位拒绝→dropped、inbox pull 整批→ingested、帧 verdict→applied；merged 无 redesign 合并点恒 0 保 API 稳定）经 `AppCounterReporter`（game-server 证书自报，复用 0043 表零迁移）→ `ReportAppCounters` 扩收 game-server 角色；`DeliveryStatsStore` 读投递对=持久 `input.delivery`（Delivered=总行数、EffectsApplied=Consumed 行）+ outbox 四态=`infra.outbox GROUP BY status`（Failed=Σattempts−Done 推导，行永不修剪）+ 会话族=15s 新鲜度求和 → `/api/delivery/stats`；Sqlite 全零+空；+9 fact（宿主驱动会话面 3：脚本化 HttpMessageHandler 应答 RoundInput accept、store 5、漂移钉 1）；迁移断言不动；全量门 1984 测试 3 失败全判非阻塞（M2 clock-window trio：solo 同败 + 干净树 stash 对照同败=环境族，昨日 OBS-10 门同三件同法裁定）；OBS-11 自身测试全量内全绿。
- [x] **FIX-OBS-12 / P1：统计平台出站 API 调用。** 依赖：FIX-OBS-01。
  - 证据：bb4b175；出站面=执行器边界——`PlatformApiCounters`（gateway 进程内，词汇按构造有界 `{adapter.Platform}.execute|status`，失败=传输交换抛出而平台答未执行仍算成功交互，Stopwatch 延迟）+ 直方图骑标量通道（Prometheus 累积 le 桶复合 family，bounds [10..5000]ms 定义于 OpsMetrics 单源，+Inf=calls；观察落单桶、快照发累积和，读者减邻桶还原区间）经常驻 `AppCounterReporter`（原仅 ingress face 挂载→controlEndpoint 下常挂，ingress 族挂载时并入合成快照；**空快照不发报告**——r1 门发现 gateway 无 face 时空报告被控制面拒收崩 BackgroundService 的真缺陷致 12+ 宿主测试连坐，修复后 solo 25/25 全绿）→ 复用 0043 `ops.app_counter_sample` 零迁移；`PlatformApiStatsStore` 解析复合 family 回端点（外族静默跳过、store 常量漂移钉 OpsMetrics）+ fresh gateways 求和（停报者出答）→ `/platform-api/stats`；Sqlite 空；+10 fact（计数器 4 + 执行器集成 1 + 读侧 5）；迁移断言不动；r2 门（修复后二进制）1994 测试 3 败全判非阻塞（M2 clock-window 环境族×2 + FanoutReclaim 文档化件，既裁签名零 NEW）；OBS-12 自身测试门内全绿。
- [x] **FIX-OBS-13 / P1：汇总窗口指标与保留历史。** 依赖：FIX-OBS-07、FIX-OBS-08、FIX-OBS-09、FIX-OBS-10、FIX-OBS-11、FIX-OBS-12。
  - 证据：d004443；`OpsWindowStore` → `/ops/window`（Clamp 1..1440）：窗口增量=每步 LAG 求和（窗前 DISTINCT ON 基线跨界播种、重启下降从零、窗内首读无基线从零、复合族 split_part 卷回字典基名）单条 SQL GROUPING SETS 一扫答总数+分钟序列；停报 reporter 窗内步照计（与活值 15s 出答规则诚实分开）、缺样本分钟缺席不补零；保留：`PruneSamples` 两 DELETE（app_counter 按 reported_at / machine 按 sampled_at，disk/process 子表 CASCADE 随父）+ 迁移 0044 两时间索引（迁移断言 52 表不变/版本 43→44）+ ControlMaintenance 每小时 `Control:OpsRetentionDays`（默认 7，Clamp 1..365）；Sqlite 空；+6 fact；全量门 2000 测试 4 败全判非阻塞（M2 clock-window ×2 多轮既裁 + bring-up 竞态与 agent 超时两件 solo 全绿=负载签名，零 NEW）；OBS-13 自身测试门内全绿。
- [x] **FIX-OBS-14 / P1：实现控制台运维总览。** 依赖：FIX-WEB-04、FIX-WEB-05、FIX-OBS-13。
  - 证据：39787cd；`OverviewPage` 接 home 路由：七卡各对自己的 face 独立应答（loading|error|data 三态、一 `Promise.allSettled` 七路 GET、一卡之败是该卡的错误词永不是白屏）；`getAdminJson` 无体 GET 传输（同 bearer 同 AdminError 映射，与全 POST 契约客户端并存）；卡面=机器表（15m CPU/内存/样本）、房间（总数/活跃/状态/租户）、在线（玩家/主播/按租户游戏）、原始进站（七拒因）、事件投递（链路计数+效果四态）、平台出站（端点 calls/failures）、计数器窗口增量（60m 每族 delta）；**形状守卫**：每 face 判别字段守卫，200 但形状不对（旧控制面/代理插花）是该卡的 unexpected shape 错误词不崩整页（登录测试 catch-all mock 抓出的真缺陷）；+4 console fact（25→29：七面数字渲染+bearer 核对、一 face 500 只错该卡、刷新重取、错误形状不崩页）；既有 login/i18n 两文件 home 实页化后 `Home`/`主页` 只剩菜单承载、菜单挂载晚于响应式 mobile→desktop 翻转一拍——即时 getAllByText 改 waitFor 重试；pnpm frozen install+test 29/29 三连绿+build 绿；无 .NET 改动（七 face 各自门禁于 OBS-07..13），全量门不重跑（同 WEB-10 先例）。
- [ ] **FIX-OBS-15 / P1：实现资源与链路阈值告警。** 依赖：FIX-OBS-13、FIX-OBS-14。
- [ ] **FIX-OBS-16 / P1：验收机器统计与断联。** 依赖：FIX-OBS-14、FIX-OBS-15、FIX-WEB-06。
- [ ] **FIX-OBS-17 / P1：验收在线人数及事件链路计数。** 依赖：FIX-OBS-14、FIX-WEB-07、FIX-WEB-08。

### 22.18 Unity（U3D）快速接入 SDK：抖音／快手条件编译（P0/P1）

- [ ] **FIX-U3D-01 / P0：确认两平台 SDK 产品形态及准入矩阵。** 依赖：无。
- [ ] **FIX-U3D-02 / P1：定义统一 Unity SDK 接口。** 依赖：FIX-U3D-01、FIX-SYNC-01。
- [ ] **FIX-U3D-03 / P1：建立 Unity 包及条件编译隔离。** 依赖：FIX-U3D-02。
- [ ] **FIX-U3D-04 / P1：实现 App 配置及物料检查向导。** 依赖：FIX-U3D-03。
- [ ] **FIX-U3D-05 / P1：实现抖音官方 SDK 适配。** 依赖：FIX-U3D-03、FIX-U3D-04。
- [ ] **FIX-U3D-06 / P1：实现快手官方 SDK 适配。** 依赖：FIX-U3D-03、FIX-U3D-04。
- [ ] **FIX-U3D-07 / P0：接通 Unity 到 Chorus 的登录链路。** 依赖：FIX-U3D-02、FIX-END-02、FIX-END-17。
- [ ] **FIX-U3D-08 / P1：接通匹配与原子入房。** 依赖：FIX-U3D-07、FIX-LINK-03、FIX-LINK-04、FIX-END-16。
- [ ] **FIX-U3D-09 / P0：实现 Unity 快照与增量应用器。** 依赖：FIX-U3D-02、FIX-LINK-10、FIX-SYNC-01。
- [ ] **FIX-U3D-10 / P0：接通 Unity 操作意图发送。** 依赖：FIX-U3D-07、FIX-LINK-09、FIX-SYNC-09。
- [ ] **FIX-U3D-11 / P0：实现 Unity 重连与原局追赶。** 依赖：FIX-U3D-07、FIX-U3D-09、FIX-SYNC-03。
- [ ] **FIX-U3D-12 / P1：接通暂停、恢复就绪与正常退出。** 依赖：FIX-U3D-09、FIX-U3D-11、FIX-SYNC-08、FIX-LINK-12、FIX-END-13。
- [ ] **FIX-U3D-13 / P1：提供框架排行榜 Unity 客户端接口。** 依赖：FIX-U3D-07、FIX-RANK-04、FIX-RANK-05、FIX-RANK-06、FIX-RANK-07。
- [ ] **FIX-U3D-14 / P0：核定并接通平台履约及状态回报。** 依赖：FIX-U3D-01、FIX-U3D-05、FIX-U3D-06、FIX-INPUT-08、FIX-END-12。
- [ ] **FIX-U3D-15 / P1：交付配置即可运行的联机示例。** 依赖：FIX-U3D-08、FIX-U3D-09、FIX-U3D-10、FIX-U3D-11、FIX-U3D-12、FIX-U3D-13。
- [ ] **FIX-U3D-16 / P1：验收平台条件编译与 Unity 运行兼容性。** 依赖：FIX-U3D-05、FIX-U3D-06、FIX-U3D-09。
- [ ] **FIX-U3D-17 / P0：验收抖音从拉起到结束的真实链路。** 依赖：FIX-U3D-14、FIX-U3D-15、FIX-OBS-11。
- [ ] **FIX-U3D-18 / P1：发布可复用 SDK 包及中英文快速接入文档。** 依赖：FIX-U3D-04、FIX-U3D-15、FIX-U3D-16、FIX-U3D-17、FIX-U3D-19。
- [ ] **FIX-U3D-19 / P0：验收快手从拉起到结束的真实链路。** 依赖：FIX-U3D-14、FIX-U3D-15、FIX-OBS-11。

### 22.19 多直播平台接入资料专题与后续研究计划（仅规划）

- [x] **FIX-PLAT-01 / P1：建立独立平台接入资料专题。** 依赖：无。
  - 证据：88d6b49
- [x] **FIX-PLAT-02 / P1：定义平台物料及兼容性调查表字段。** 依赖：FIX-PLAT-01。
  - 证据：88d6b49
- [x] **FIX-PLAT-03 / P1：归档抖音接入物料与官方链接。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-04 / P1：归档快手接入物料与官方链接。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-05 / P1：重点收集斗鱼官方接入资料。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-06 / P1：评估斗鱼与当前架构的兼容性。** 依赖：FIX-PLAT-05。
  - 证据：88d6b49
- [x] **FIX-PLAT-07 / P1：收集虎牙物料并评估兼容性。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-08 / P1：收集 B站物料并复核旧适配资料。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-09 / P2：核实视频号与淘宝直播的官方开放边界。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-10 / P2：登记其他国内候选平台资料线索。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-11 / P2：登记海外候选平台资料线索。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-12 / P2：登记第三方弹幕聚合工具的接入边界。** 依赖：FIX-PLAT-02。
  - 证据：88d6b49
- [x] **FIX-PLAT-13 / P1：汇总多平台架构兼容性与扩展点。** 依赖：FIX-PLAT-03、FIX-PLAT-04、FIX-PLAT-06、FIX-PLAT-07、FIX-PLAT-08。
  - 证据：88d6b49
- [x] **FIX-PLAT-14 / P1：形成下一阶段平台准入建议及原子后续任务。** 依赖：FIX-PLAT-13。
  - 证据：88d6b49

### 22.20 房间恢复方案的跨阶段屏障与容量验收（P0/P1）

- [ ] **FIX-REC-01 / P0：接通恢复准备、双 Gateway 路由与 Actor 激活屏障。** 依赖：FIX-TAKE-03、FIX-TAKE-04、FIX-LINK-08、FIX-END-03。
- [ ] **FIX-REC-02 / P1：验收长局及批量房间恢复容量。** 依赖：FIX-READ-05、FIX-TAKE-08、FIX-REC-01。

### 22.21 测试覆盖率补测（2026-10-20 巡检单）

- [x] **COV-01 / P1：补进程内处理器覆盖，并如实记录 98% 不可达。** 依赖：无。 - 证据：e04bbf2。
  - 真实口径核实（coverlet XPlat，排除 /obj /bin 生成与缓存；按 (file,line) 去重的行命中）：25,837 有效行 / 18,626 覆盖 = **72.09%**（2026-11-08 全量基线）；codecov 全口径（含生成 proto/镜像代码）约 47%，非生成代码是诚实分母。
  - 缺口根因：重设计后服务器编排跑在子进程里，coverage collector 只挂测试宿主进程 → DataServer 的 gRPC 处理器（角色栅栏/租户栅栏/异常映射/幂等重放）全部不可见。
  - 补测（e04bbf2，23 个测试，两个测试文件）：在测试进程内起真实 Kestrel，复刻 DataServer Program.cs 接线（14 个裸 store 单例 + mTLS + AddGrpc + 全部 MapGrpcService），用 ProcessCertificateIssuer 签发真证书、ProcessGrpcClientFactory 建真信道，覆盖 Round（开/终局、过期 epoch、外来租户、未授权角色、退赛成功/重放/吊销会话/非参与者/错角色/外来租户请求）、StreamerIdentity（身份解析/登录/恢复轮换/旧密钥拒绝/资料一次性创建/连接挂断幂等/挂载-绑定-状态-吊销全生命周期/错角色）、Leaderboard 与 Frame（真提交帧→从 infra.outbox 读 effect→计分幂等/TopN/名次/邻近/GM 修正与过期版本拒绝/未提交效果拒绝/重置/重建）。不改动任何生产代码。
  - 处理器文件增量（确定性，仅由新测试驱动）：RoundGrpcService 0→64/293、StreamerIdentityGrpcService 0→158/188、LeaderboardGrpcService 0→141/200、FrameGrpcService 0→40/186。
  - 补测后全量（2026-11-09，1858/1868 绿；10 个失败全是共租负载风暴下的既有宿主测试签名——FrameChannelCapacity、M0/M2/M3、ModuleCheckpoint、ModuleUpgradeHost、PlatformGatewayRouting、RoundDemo——逐个隔离复跑全绿，无一为新测试）：**73.88%**（19,089 覆盖 / 6,748 未覆盖）。注意跨运行总数受失败测试数摆动 ±1.5%（失败测试不执行其代码路径），per-file 增量才是可归因口径。
  - **98% 不可达（如实记录，不硬凑）**：未覆盖 6,748 行的构成——子进程专属服务器编排（gRPC/WS 处理器与协调器，collector 挂不到子进程）：EdgeWs 483、RoomRuntime 332、AdminGrpcService 325、RoundGrpcService 余 229、MatchCoordinatorService 220、EndOperationCoordinatorService 201、RedesignControlStore 176、DemoCluster 173、StreamerSessionGrpcService 171、MatchGrpcService 148 等；顶层语句 Program.cs 入口接线约 730 行（五个服务器都是顶层程序，无进程内宿主即不可覆盖）；`RequireCertificate` 下无证书/坏证书在 TLS 握手即断，处理器内两条 Unauthenticated 行经真传输不可达；其余为真网络/固定超时/防御性空 catch。即使全部可覆盖行都盖住，理论上限约 92–94%，98% 在当前架构下不可达。
  - **CI 红修复（2026-11-10，1fc1f3f、a987568，纯测试侧零生产改动）**：`Test (with coverage)` 腿挂的 2 个测试根因已钉死——①`AnUnreachableOwnerKeepsTheEventPendingThenDeliversOnceReachable`：快机器上房间 40 tick×25ms≈1s 内自然终局，参与行 Closed/指针置空后，下一次 dispatch pass 的 moot 退休（对已释放轮次是正确行为）把事件退休 Done，撞进 5×400ms 采样窗（负载门禁 tick 拖长反而掩盖此确定性失败；CI 2 次、本地隔离复现同签名）；同测还埋第二洞——streamer 首次自报先于 RegisterNode 落地被拒（报告间隔 60s 不重试到窗口内），进程行缺失使端点修正无从生效。修法：400 tick 让房间寿命≥10s 盖过观察窗+投递；RegisterNode 提前到自报宿主启动之前。隔离 2 绿（15s/17s）+ 门禁内绿。②`AParkedRoomSurfacesAsRoomSuspendedOnTheClient`：150s 窗口在 CI 满载 runner 超时（本地典型 8-20s），事件不变量等待窗放宽到 300s（本地零开销）。修复后全量门禁 1872/1874（12m31s），2 失败均为文档化负载族且隔离复跑绿（streamer agent fact 10s、FrameChannel 容量 3s）。



## 源码分层分析 2026-11-07

### a) 抽象层清单

- 边界接入层：把平台回调和主播 WebSocket 转成可核验、可持久接受的会话与事件入口。代表模块：src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31。
- 业务协调层：持有主播登录、挂载、匹配、恢复协调和停止投递等跨进程业务状态机。代表模块：src/Chorus.ControlServer/MatchCoordinatorService.cs:34。
- 对局运行层：拥有房间生命周期、权威 tick、帧提交、快照和恢复执行。代表模块：src/Chorus.GameServer/RoomRuntime.cs:18。
- 玩法模块层：声明模块版本、tick、检查点、离线策略，并把输入折叠成状态与结果。代表模块：src/Chorus.ExampleGame/ExampleGameModule.cs:9。
- 持久数据层：以 PostgreSQL 事务和幂等回执承载身份、房间、帧、结算、任务与排行。代表模块：src/Chorus.DataServer/RedesignRoomStore.cs:30。
- 平台适配层：验签、解码、映射、去重和平台出站任务，不裁决房间或业务状态。代表模块：src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventVerifier.cs:17。
- 集群监管层：签发/校验进程身份、上报节点与进程、租约续期和本机进程监管。代表模块：src/Chorus.NodeAgent/BootstrapAgent.cs:30。
- 契约序列化层：集中承载 gRPC 契约和客户端消息模型，避免调用面散落在业务实现里。代表模块：src/Chorus.Rpc/Chorus.Rpc.csproj:13。
- 客户端与工具层：提供协议客户端、压测驱动、演示流程和操作控制台。代表模块：src/Chorus.Client/ChorusClient.cs:19。
- 兼容运行层：保留旧单体、旧控制台和 IPC 形态，供历史部署与迁移对照。代表模块：src/Chorus.Server/Program.cs:115。

### b) 超大文件候选

- [ ] LAY-001 [P1] 拆分 src/Chorus.ControlServer/RedesignControlStore.cs（2477 行）——控制面把节点、进程、所有权、房间进度、恢复、结束、路由和投递事务压入单一仓储。（src/Chorus.ControlServer/RedesignControlStore.cs:180）
- [ ] LAY-002 [P1] 拆分 src/Chorus.GameServer/RoomRuntime.cs（1694 行）——准备、激活、事件投递、恢复重建、暂停恢复和释放策略集中在一个运行时。（src/Chorus.GameServer/RoomRuntime.cs:153）
- [ ] LAY-003 [P1] 拆分 src/Chorus.Rpc/Protos/m2.proto（1290 行）——m2 同时声明轮次、帧、结算、排行、恢复和同步等不同领域契约。（src/Chorus.Rpc/Protos/m2.proto:467）
- [ ] LAY-004 [P1] 拆分 src/Chorus.DataServer/RedesignRoomStore.cs（1269 行）——房间准入、所有权、恢复操作和 outbox 事务同文件堆叠。（src/Chorus.DataServer/RedesignRoomStore.cs:125）
- [ ] LAY-005 [P2] 拆分 src/Chorus.DataServer/RedesignRoundInputStore.cs（999 行）——事件归属、席位消费、客户端命令速率和轮次恢复输入耦合在同一存储类。（src/Chorus.DataServer/RedesignRoundInputStore.cs:31）

### c) 目录/分类问题

- [ ] 旧 `Chorus.SampleGame` 与新 `Chorus.ExampleGame` 并存，模块目录按时代切分而非玩法域；接口差异需要同时读两个项目。（src/Chorus.SampleGame/SampleGameModule.cs:6，src/Chorus.ExampleGame/ExampleGameModule.cs:9）
- [ ] `Chorus.Control`/`Chorus.ControlHost` 与 `Chorus.ControlServer` 都使用控制域命名，但前者是旧控制台/持久化路径，后者是重设计控制面，入口命名不足以区分。（src/Chorus.Control/ControlDatabase.cs:13，src/Chorus.ControlServer/RedesignControlStore.cs:170）
- [ ] `Chorus.RoundDemo` 位于 src 但承担演示/验收场景，与正式服务目录和 tests 目录都存在归类歧义。（src/Chorus.RoundDemo/RoundDemoScenario.cs:40，tests/Chorus.Engine.Tests/RoundDemoScenarioTests.cs:1）
