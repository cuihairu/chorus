---
title: 术语表：房间、对局、Tick 与所有权代次
---

# 术语表：房间、对局、Tick 与所有权代次

本文解释当前重设计中的术语，不代表所有目标能力已经实现。先读本页，再读[架构方案](/architecture/architecture)、[状态同步与离线策略](../design/authoritative-sync.md)及[房间恢复方案](../design/room-recovery.md)。代码和协议中的字段名保持原样；中文解释用于明确含义，不隐含接口已重命名。

## 先分清三个概念

| 术语 | 回答的问题 | 什么时候变化 | 不表示什么 |
|---|---|---|---|
| Tick / TickId，逻辑步号 | 这局已经计算并提交到第几步？ | 房间成功提交下一逻辑帧时推进 | 不是客户端画面帧数，也不是所有权代次 |
| Round，对局 | 当前是哪一场比赛？ | 本局结束、下一局建立时切换 | 不是一次 Tick，也不是一次连接 |
| RoomEpoch / OwnerEpoch / 房间上下文中的 Epoch，房间所有权代次 | 当前哪一代执行者有资格写这个房间？ | 接管、替换执行权或 GM 显式推进时增加 | 不是游戏时间、输入时间窗口、Unix 时间戳或每局编号 |

每个房间独立持有 Tick 和所有权代次，彼此不比较大小。正常运行一整局，所有权代次可以始终不变。普通重连及暂停／恢复不要求增加它。这里的 epoch 是分布式写入隔离概念；不是所有游戏框架都必须使用这个字段名。

```text
房间 A：epoch 7，Tick 100 → 101 → 暂停在 101 → 恢复 → 102
房间 B：epoch 3，Tick 800 → 801 → 802（不受 A 暂停影响）
A 发生服务器接管：epoch 7 → 8
新执行者从最后已提交的 Tick 102 恢复，下一帧提交 Tick 103。
旧执行者持有 epoch 7，即使恢复运行也不能新增有效写入。
```

本项目的 GameRoom 表示一次权威对局，结束后不复用 RoomId；同一直播玩法挂载可以连续参与多个新 RoomId。RoomId 和 RoundGeneration 一起隔离新旧局，不能用推进 RoomEpoch 代替创建下一局。

## 对象与身份

| 术语／字段 | 含义与作用域 | 容易混淆之处 |
|---|---|---|
| RoomId / GameRoom，游戏房间 | 一次共同对局的稳定标识；迁移 GameServer 时不变 | 不等于平台直播间，也不等于 GameServer 进程 |
| RoomActor，房间执行实体 | GameServer 内承载指定房间状态与串行逻辑循环的对象 | 不是 OS 线程；新对象不能凭自身存在获得执行权 |
| RoomRuntime，房间运行时 | GameServer 内按 RoomId 管理 Actor 创建、定位、恢复及回收 | 不是独立服务进程 |
| LiveSession，直播场次 | 经平台验证的一场直播上下文 | 同一场直播可运行多局游戏 |
| GameplayBinding，玩法挂载 | 一场直播与当前玩法的绑定 | 挂载存在不表示已匹配入房或战斗已激活 |
| Participation，参与关系 | 主播／玩家与游戏房间的席位、阵营和退出状态 | 连接断开不自动删除持久参与关系 |
| LoginSession，登录会话 | Chorus 内认证及控制权上下文 | 不等于平台启动 token，也不等于某一条 Socket |
| Connection，网络连接 | 某 Gateway 上的一次连接 | 同一身份可重连；旧连接的关闭通知不能撤销新连接 |
| Owner / Ownership，所有者／所有权 | 持久记录授权哪个进程实例在什么代次处理某个实体 | 进程在线、路由缓存或客户端自报均不能授予所有权 |
| NodeId / ProcessInstanceId，节点／进程实例标识 | 前者标识受管机器，后者在每次进程启动时更新 | 机器、部署槽位、PID 与进程实例是不同对象 |

## 版本、代次与顺序号

| 字段 | 作用域及用途 | 不可替代的其他字段 |
|---|---|---|
| RoomEpoch / OwnerEpoch | 指定房间的所有权代次；存储接收端校验 owner、代次、状态及租约 | Tick、RoundGeneration、ProcessInstanceId |
| StateVersion | 指定房间状态的提交版本，标识客户端应用的权威状态 | 不用它计算经过了多少现实时间；不假定所有玩法与 Tick 永远数值相等 |
| BaseVersion | 当前增量要求客户端已经安装的状态版本 | 收到更大版本不代表可以跳过缺失基线直接应用 |
| RoundGeneration | 玩法挂载连续对局的代次，用于归局和拒绝迟到的旧局操作 | 不是房间权威代次 |
| BindingGeneration | 玩法挂载的代次，隔离旧挂载与新挂载 | 不是连接代，也不是 Tick |
| SessionVersion | 登录会话控制权版本，登录轮换／接管时验证 | 不表示游戏重新开局 |
| ConnectionGeneration | 当前连接绑定的代次，隔离旧连接通知 | 重连不因此推进 RoomEpoch |
| RouteVersion | ControlServer 路由投影的版本，供安装、缺口恢复与 ACK 使用 | 路由版本变大不一定表示房间 epoch 已变 |
| CoordinatorTerm | 某个协调租约／分区的权威任期，防止旧协调者继续决策 | 不与每个房间的 Tick 或 epoch 共用计数器 |
| TaskEpoch | 平台任务执行资格的代次，隔离旧执行者 | 不决定房间状态或是否可以发奖 |
| OperationId / CommitId / CommandId / EventId | 分别关联业务操作、帧提交、客户端命令及平台事件的幂等身份 | 重试保留原身份；迁移不能仅因 epoch 变化就把原事件当新礼物 |

`control.ownership.epoch`（kind=room）与 `game.room.epoch` 应表示同一房间权威代次并在受限事务中保持一致；不是两个可以各自推进的游戏时钟。其他实体也可能有名为 epoch 的字段，必须看其实体作用域。文档优先写完整的“房间所有权代次”，避免孤立使用 epoch。

## 同步与恢复

| 术语 | 含义 | 边界 |
|---|---|---|
| 逻辑帧 / Tick | 一步输入选择、计算及状态提交 | 以固定逻辑步长模拟，不承诺每一步现实耗时永远等于步长 |
| 渲染帧 / Render frame | Unity 等客户端绘制一次画面 | 可比逻辑帧更快或更慢，不推进服务端权威状态 |
| Snapshot，快照 | 足以重建某个已提交状态的完整基线 | 客户端快照用于恢复展示；持久检查点另用于服务器恢复 |
| Delta，增量 | 对特定 BaseVersion 的状态变化 | 效果事件清单不天然等于任意玩法的完整状态增量 |
| Catch-up，客户端追赶 | 安装当前基线，再应用连续更新 | 默认不逐帧补演全部离线历史，不重复发奖 |
| Replay，服务器重放 | 从持久检查点按已记录顺序重算到恢复头并验证哈希 | 不再次调用外部平台或写入已生效奖励 |
| Recovering，故障恢复中 | 新执行者重建、校验房间状态，尚未获准正常推进 | 进程存活或 RPC 已接受不代表业务恢复成功 |
| Paused，业务暂停 | 持久冻结房间逻辑进度，等待玩法规定的恢复条件 | 心跳、租约及墙钟期限继续；恢复后不能自动变成 Running |
| Suspended，故障挂起 | 异常、材料缺失或校验失败后停止权威推进 | 不能冒充正常结束、正常暂停或成功恢复 |
| Lease，租约 | 按权威时间界定所有权有效期限 | 网络心跳或本地时间不能单独续租／授予写资格 |
| Fencing，旧权威隔离 | 持久写入端拒绝旧 owner／旧代次／失效租约的新增写入 | 仅通知旧进程停止不足以保证隔离 |
| ACK，确认 | 某个明确阶段的确认，例如持久接受、路由安装或基线就绪 | 必须说清确认什么；已接收不等于已应用或平台已结束 |

## 常见操作对照

| 操作 | RoomId | Tick | 房间所有权代次 |
|---|---|---|---|
| 正常战斗 | 不变 | 持续提交推进 | 不变 |
| 仅主播客户端掉线后重连 | 找回原房间 | 按玩法继续或暂停；客户端重建当前状态 | 通常不变 |
| 同一执行者业务暂停／恢复 | 不变 | 暂停后从原进度继续 | 不因暂停本身变化 |
| GameServer 故障接管 | 不变 | 从最后已提交头继续，不归零 | 推进，旧执行者失去资格 |
| GM BumpRoomEpoch 测试 | 不变 | 不因此清零，显式恢复后继续 | 仅目标房间推进 |
| 本局结束后创建下一局 | 新 RoomId | 新对局独立初始化 | 属于新房间，不能与旧局作进度比较 |

## 概念依据与项目选择

- 游戏 Tick 的定义参见 [Unity：Tick and update rates](https://docs.unity.cn/Packages/com.unity.netcode.gameobjects@2.5/manual/learn/ticks-and-update-rates.html)。每个 RoomActor 保存独立进度是本项目的组织方式，不声称 Unity 规定了我们的房间结构。
- 旧写入隔离参见 [Google Chubby 原论文 §2.1](https://storage.googleapis.com/gweb-research2023-media/pubtools/4444.pdf)：写请求携带锁获取计数，接收端拒绝旧计数。另见 [Kafka ProducerFencedException](https://kafka.apache.org/42/javadoc/org/apache/kafka/common/errors/ProducerFencedException.html) 及 [FenceProducersResult.epochId](https://kafka.apache.org/42/javadoc/org/apache/kafka/clients/admin/FenceProducersResult.html)。这些支持 fencing 机制，不代表 Chorus 的具体恢复实现已正确。
- 按房间使用所有权代次、GM 单房推进及暂停恢复屏障是本项目目标契约。实现范围与验收见[完整恢复方案](../design/room-recovery.md)及仓库 `todo.md`。
