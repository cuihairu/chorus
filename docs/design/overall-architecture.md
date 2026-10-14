---
title: 总体架构与 Code Agent 实施规范
---

# Chorus 总体技术架构与 Code Agent 实施规范

**文档性质：** 总体架构设计、代码审计基准、后续实施依据
**项目：** Chorus
**代码仓库：** [https://github.com/cuihairu/chorus](https://github.com/cuihairu/chorus)

> **实现状态标注约定：** `Implemented` / `Partial` / `Planned` / `Missing`。每项标注必须有 `file:line` 或测试证据。

---

# 1. 项目定位与架构目标

Chorus 是一个面向直播互动游戏和小游戏接入的、服务端权威的实时游戏框架。

它需要支持：

* 直播平台的弹幕、礼物及其他互动事件驱动游戏。
* 主播进入房间、多个主播参与同一个逻辑房间。
* 真人主播与机器人参与者共同进行匹配。
* 固定 Tick 的权威游戏逻辑、输入排序和状态同步。
* 房间从单进程逐步扩展到多 Worker、多机器。
* 玩家长期数据、游戏历史和统计信息的异步处理。
* 房间排行、比赛排行、周期排行、全局排行和平台原生排行榜。
* 抖音、快手等小游戏运行平台的接入，以及直播互动小玩法的独立适配。
* 房间状态持久化、进程故障恢复、房间迁移和必要的事件补齐。

总体设计原则：

1. Room 是业务房间和权威状态归属的核心概念。
2. 同一平台账号在同一时刻只能绑定一个有效 Room。
3. 平台事件入口与主播连接入口不同，但最终使用统一的 Room 物理路由。
4. 一个 Room 在同一时刻只能有一个有效权威写入者。
5. Room Actor 是实时状态修改的主要执行边界。
6. 普通玩家资料、统计和排行榜聚合不应阻塞房间 Tick。
7. 关键业务记录必须具有明确的持久化、幂等和恢复保证。
8. 单机部署必须保持简单，不要求所有逻辑都独立进程化。
9. 多 Worker 路由必须真正接入运行时，不能只有路由算法或路由表类。
10. 所有实现状态必须有代码、配置或测试证据。

---

# 2. 总体逻辑架构图

```
flowchart TB
    subgraph Clients["外部客户端与事件来源"]
        LivePlatform["直播平台<br/>弹幕、礼物、互动事件"]
        StreamerClient["主播客户端<br/>WebSocket / 操作输入"]
        MiniGameClient["小游戏客户端<br/>抖音、快手等"]
        AdminClient["管理端 / 游戏运营工具"]
    end

    subgraph Edge["接入层"]
        PlatformGateway["Platform Gateway<br/>平台协议适配、验证、限流"]
        StreamerGateway["Streamer Gateway<br/>主播认证、连接、房间操作"]
        MiniGameGateway["MiniGame API / Client Adapter<br/>登录、房间 API、平台能力"]
        AdminAPI["Admin API<br/>配置、运营与查询"]
    end

    subgraph ControlPlane["业务控制面"]
        Identity["Identity Mapping<br/>平台身份、主播、玩家映射"]
        Binding["Platform Binding<br/>平台账号 → 唯一 Room"]
        RoomDirectory["Room Directory<br/>Room 身份与生命周期"]
        Matchmaking["Matchmaking<br/>匹配、参与者分配、机器人填充"]
        RoomRouting["Room Routing<br/>Room → Worker"]
        WorkerRegistry["Worker Registry<br/>Worker 注册、健康、容量"]
        CapabilityRegistry["Platform Capability Registry<br/>平台能力与版本"]
    end

    subgraph Realtime["实时游戏执行面"]
        WorkerA["Room Worker A"]
        WorkerB["Room Worker B"]
        WorkerN["Room Worker N"]

        ActorA["Room Actor<br/>权威状态 + Tick"]
        ActorB["Room Actor<br/>权威状态 + Tick"]
        ActorN["Room Actor<br/>权威状态 + Tick"]

        GameModule["Game Module<br/>游戏规则与模拟"]
        BotRuntime["Bot Runtime<br/>机器人策略与动作"]
        Sync["Realtime Sync<br/>输入确认、快照、增量"]
    end

    subgraph DataPlane["异步数据处理面"]
        DataIngress["Data Command Ingress<br/>命令校验、幂等"]
        DurableBuffer["Reliable Buffer / Outbox<br/>按可靠性级别启用"]
        DataWorker["Data Worker Pool"]
        PlayerRepository["Player Data Repository"]
        GameRecordRepository["Game Record Repository"]
        Leaderboard["Leaderboard Subsystem"]
        Analytics["Statistics / Analytics"]
    end

    subgraph Storage["存储层"]
        ConfigStore[("配置与平台绑定存储")]
        DurableState[("房间快照 / 恢复记录")]
        PlayerDB[("玩家长期数据")]
        GameDB[("游戏记录 / 结算流水")]
        LeaderboardDB[("排行榜数据")]
        EventLog[("可选持久化事件日志")]
    end

    LivePlatform --> PlatformGateway
    StreamerClient --> StreamerGateway
    MiniGameClient --> MiniGameGateway
    AdminClient --> AdminAPI

    PlatformGateway --> Identity
    Identity --> Binding
    Binding --> RoomDirectory

    StreamerGateway --> Identity
    Identity --> RoomDirectory

    MiniGameGateway --> Identity
    MiniGameGateway --> CapabilityRegistry

    RoomDirectory --> Matchmaking
    RoomDirectory --> RoomRouting
    WorkerRegistry --> RoomRouting

    RoomRouting --> WorkerA
    RoomRouting --> WorkerB
    RoomRouting --> WorkerN

    WorkerA --> ActorA
    WorkerB --> ActorB
    WorkerN --> ActorN

    ActorA --> GameModule
    ActorB --> GameModule
    ActorN --> GameModule

    ActorA --> BotRuntime
    ActorB --> BotRuntime
    ActorN --> BotRuntime

    ActorA --> Sync
    ActorB --> Sync
    ActorN --> Sync

    Sync --> StreamerGateway
    Sync --> MiniGameGateway

    ActorA --> DataIngress
    ActorB --> DataIngress
    ActorN --> DataIngress

    DataIngress --> DurableBuffer
    DurableBuffer --> DataWorker
    DataIngress -.普通可丢弃统计可走轻量通道.-> DataWorker

    DataWorker --> PlayerRepository
    DataWorker --> GameRecordRepository
    DataWorker --> Leaderboard
    DataWorker --> Analytics

    PlayerRepository --> PlayerDB
    GameRecordRepository --> GameDB
    Leaderboard --> LeaderboardDB
    GameRecordRepository --> EventLog

    ActorA --> DurableState
    ActorB --> DurableState
    ActorN --> DurableState

    RoomDirectory --> ConfigStore
    Binding --> ConfigStore
    AdminAPI --> ConfigStore
```

## 2.1 图中模块的实现状态约束

此图表示目标逻辑架构，不代表必须部署为十几个独立服务。

逻辑模块和进程边界应分开理解：

* 单进程模式：多个逻辑模块可以处于同一个进程。
* 拆分模式：Gateway 与 EngineHost 可以通过现有 IPC 通信。
* 多 Worker 模式：需要真正实现集群级 Room 路由与权威状态接管。
* 独立数据模式：Data Worker 可以与 EngineHost 分开部署。
* 存储组件：按实际持久化、查询、吞吐和恢复需求选择，不要求每类数据独立部署一个数据库。

---

# 3. 核心领域对象与关系图

必须先明确业务对象的职责，避免 Room、Session、Match、Participant 和 Worker 互相混用。

```
classDiagram
    class PlatformAccount {
        +Platform
        +PlatformAccountId
    }

    class PlatformBinding {
        +BindingId
        +RoomId
        +Status
        +Version
    }

    class Streamer {
        +StreamerId
    }

    class Player {
        +PlayerId
        +Profile
    }

    class Room {
        +RoomId
        +LifecycleState
        +CurrentExecutionId
    }

    class Session {
        +SessionId
        +RuntimeState
    }

    class Match {
        +MatchId
        +MatchState
        +StartTime
        +EndTime
    }

    class Participant {
        +ParticipantId
        +ParticipantType
        +JoinState
    }

    class Bot {
        +BotId
        +StrategyId
    }

    class RoomWorker {
        +WorkerId
        +Endpoint
        +HealthState
    }

    class RoomRoute {
        +RoomId
        +WorkerId
        +Epoch
        +RouteVersion
    }

    PlatformAccount "1" --> "0..1" PlatformBinding : 当前有效绑定
    PlatformBinding "*" --> "1" Room : 指向
    Streamer "1" --> "0..*" Room : 参与关系由业务规则约束
    Player "1" --> "0..*" Participant : 真人玩家身份
    Room "1" --> "0..*" Session : 按实际运行模型决定
    Room "1" --> "0..*" Match : 可承载比赛
    Match "1" --> "1..*" Participant : 参与者
    Participant "0..1" --> "1" Bot : 机器人类型时
    RoomWorker "1" --> "0..*" RoomRoute : 承载多个房间
    RoomRoute "*" --> "1" Room : 路由归属
```

## 3.1 对象语义

### PlatformAccount

外部平台身份。

唯一性必须基于明确的平台标识和平台账号标识，而不是昵称、头像 URL 或展示名称。

### PlatformBinding

当前有效的业务绑定关系。

必须保证同一个 `(Platform, PlatformAccountId)` 在同一时刻最多对应一个有效 Room。

如果某个平台账号对应一个直播间 ID，还需要区分"账号标识"和"直播间标识"，由平台适配器提供可信的映射信息。

### Streamer

主播身份。主播是否可以参与多个 Room，应由业务策略明确，而不是由底层模型隐式决定。

### Player

游戏玩家长期身份，与主播身份、平台账号和客户端连接分离。

真人玩家可能通过平台身份关联到游戏玩家，但不能通过昵称自动合并。

### Room

业务房间、生命周期和权威游戏状态的归属边界。

一个 Room 可以没有主播、一个主播或多个主播。

### Session

必须根据当前代码审计决定是否保留独立语义。

如果现有 Session 就是一次房间运行，先复用现有实现。

如果 Room 需要在多次游戏运行之间长期存在，才引入独立 Room 实体并定义 Room 与 Session 的生命周期关系。

不能仅为了统一名词就新增重复的 Actor、Registry 和路由系统。

### Match

一次匹配或比赛。Match 与 Room 的具体关系必须结合游戏模式决定：

* 如果一个 Room 本身就是一场游戏，可以由 Room 直接承载比赛。
* 如果一个 Room 内有多轮比赛，Room 可以关联多个 Match。
* 如果 Match 跨多个独立 Room，必须额外设计跨房间裁决与结果协调。

不要默认每个 Room 都必须创建独立 Match 服务。

### Participant

Match 中的参与者。

必须明确区分真人与机器人，并记录参与者身份和加入状态。

### Bot

机器人参与者及其策略配置。机器人不是虚构的主播账号，也不是伪造的 WebSocket 客户端。

### RoomWorker

运行 Room Actor 的进程。一个 Worker 可以承载多个 Room。

---

# 4. 平台账号唯一绑定与双入口路由

这是 Chorus 的核心架构约束。

平台回调与主播操作是两种不同的业务入口，但它们最终必须定位到同一个 Room。

```
flowchart TB
    subgraph PlatformFlow["平台事件入口"]
        P1["平台回调"]
        P2["验证平台来源"]
        P3["提取 Platform + AccountId"]
        P4["查询有效 PlatformBinding"]
        P5{"是否唯一绑定"}
        P6["得到 RoomId"]
        P7["拒绝 / 隔离 / 记录错误"]
    end

    subgraph StreamerFlow["主播连接入口"]
        S1["主播客户端连接"]
        S2["验证身份与凭证"]
        S3["确定 StreamerId"]
        S4["解析请求目标 Room"]
        S5["验证主播参与权限"]
        S6["得到 RoomId"]
        S7["拒绝未授权请求"]
    end

    subgraph CommonRouting["统一物理路由"]
        R1["IRoomRouter"]
        R2["RoomRouteTable"]
        R3["RoomId → WorkerId + Epoch"]
        R4["本地 Actor / IPC / 跨机传输"]
        R5["目标 Room Actor"]
    end

    P1 --> P2 --> P3 --> P4 --> P5
    P5 -->|唯一有效绑定| P6
    P5 -->|无绑定或冲突| P7

    S1 --> S2 --> S3 --> S4 --> S5 --> S6
    S5 -->|未授权| S7

    P6 --> R1
    S6 --> R1
    R1 --> R2 --> R3 --> R4 --> R5
```

## 4.1 平台账号绑定约束

数据库或绑定存储应保证有效绑定唯一性。

逻辑约束：

`UNIQUE(Platform, PlatformAccountId)`，并对有效绑定建立唯一性保证。

如果使用带历史记录的绑定表，可以采用独立当前绑定表或支持有效记录唯一性的数据库约束。具体实现应结合现有数据库和事务能力选择。

换绑流程必须防止并发操作造成同一个平台账号同时有效绑定两个房间。

平台事件找不到唯一 Room 时，不能猜测目标房间，也不能向所有可能的房间广播。

当前实现：`src/Chorus.Engine/Routing/PlatformBindingRegistry.cs`（内存实现：绑定冲突 fail-visible 不隐式换绑，
换绑/解绑按当前 Room 做 CAS，防并发双绑与迟到解绑；Resolve 未命中返回 null 由调用方拒绝）。
持久化属 todo §15 持久化层，生产接线随控制面路由落地——在此之前它是单进程退化形态（同 `RoomRouteTable`）。

## 4.2 两条入口共享物理路由

建议定义统一路由契约，具体类型以现有代码为准：

```csharp
public interface IRoomRouter
{
    ValueTask<RoomRouteResult> ResolveAsync(
        string roomId,
        CancellationToken cancellationToken);
}
```

以上仅为目标契约示例，Code Agent 必须先检查现有 Router 接口，尽量复用已有抽象。

物理路由结果至少应能表达：

* RoomId。
* WorkerId。
* RouteVersion 或 Epoch。
* 当前路由是否有效。

## 4.3 与当前代码的关系

必须检查：

* `PlatformIngressFace.HandleAsync()`。
* `IPlatformEventMapper`。
* `AudienceEvent.SessionId`。
* `IStreamerSessionRouter`。
* `ISessionActorRouter`。
* `RoomAffinity`。
* `RoomRouteTable`。
* Gateway 与 EngineHost 的 IPC 路由。

当前平台入口已经存在根据事件中的 SessionId 进行 Actor 路由的逻辑，但这不代表完整的平台账号绑定管理已经实现。

现有 `RoomAffinity` 和 `RoomRouteTable` 也不能仅凭类存在就认定多 Worker 路由已经接入生产链路。

---

# 5. 房间生命周期与 Worker 调度

```
stateDiagram-v2
    [*] --> Created
    Created --> Waiting : 房间创建完成
    Waiting --> Active : 主播或游戏参与者加入
    Active --> Idle : 暂无活跃游戏输入
    Idle --> Active : 新输入或参与者恢复
    Idle --> Sleeping : 符合休眠条件
    Sleeping --> Recovering : 重新激活
    Active --> Migrating : 需要迁移 Worker
    Idle --> Migrating : 需要迁移 Worker
    Migrating --> Recovering : 新 Worker 接管
    Recovering --> Active : 状态恢复完成
    Recovering --> Sleeping : 恢复后继续休眠
    Created --> Closing : 创建失败或取消
    Waiting --> Closing : 业务关闭
    Active --> Closing : 业务关闭
    Sleeping --> Closing : 业务关闭
    Closing --> Destroyed
    Destroyed --> [*]
```

## 5.1 生命周期约束

* 没有主播不等于 Room 可以销毁。
* `SessionRegistry` 的容量淘汰不能直接等同于业务房间销毁。
* 房间休眠不等于权威状态丢弃。
* 房间迁移必须保留必要的权威状态和待处理事件。
* 销毁房间必须有明确业务条件与持久化策略。

当前实现：`src/Chorus.Engine/Routing/RoomLifecycle.cs`（`RoomLifecycleTransitions` 纯跃迁表 +
`RoomLifecycleRegistry` 每房间状态，非法跃迁 fail-visible）。五条约束逐条可测：Destroyed 只能从
Closing 到达（唯一销毁路径，业务条件与持久化策略由调用方保证）；注册表**无驱逐 API**（`SessionRegistry`
的容量淘汰仍是缓存关注点，不隐含业务销毁）；Sleeping 非终态且只能经 Recovering 回到 Active（休眠≠丢弃
权威状态）；Migrating 只能经 Recovering 回到 Active、可中止走 Closing；终态记录才能 `TryForget`。
持久化属 todo §15，生产接线随控制面路由落地——在此之前它是单进程退化形态。

## 5.2 Worker 分配

目标是一个 Worker 承载多个 Room。

```
flowchart LR
    RM["Room Manager / Route Controller"]
    W1["Worker A"]
    W2["Worker B"]
    W3["Worker C"]

    R1["Room 101"]
    R2["Room 102"]
    R3["Room 103"]
    R4["Room 104"]
    R5["Room 105"]

    RM --> W1
    RM --> W2
    RM --> W3

    W1 --> R1
    W1 --> R2
    W2 --> R3
    W2 --> R4
    W3 --> R5
```

初期不必引入独立 Room Manager 进程。

只有当多个 Worker 需要统一注册、容量评估、分配和故障接管时，才将控制面独立出来。

---

# 6. 匹配、真人参与者与机器人架构

```
flowchart TB
    Request["主播发起匹配请求"]
    Auth["身份校验与匹配权限检查"]
    Matchmaker["Matchmaker"]
    Policy["匹配策略<br/>人数、模式、等待时间"]
    Decision{"是否存在合适的真人参与者"}
    RealPlayer["分配真人 Participant"]
    BotFactory["创建 Bot Participant"]
    ParticipantSet["构建参与者集合"]
    MatchCreate["创建或绑定 Match"]
    RoomResolve["分配或选择 Room"]
    Actor["Room Actor"]
    BotRuntime["Bot Runtime"]
    ActionQueue["统一输入队列"]
    GameRules["权威游戏规则与 Tick"]

    Request --> Auth --> Matchmaker --> Policy --> Decision
    Decision -->|是| RealPlayer
    Decision -->|否 / 等待超时| BotFactory

    RealPlayer --> ParticipantSet
    BotFactory --> ParticipantSet

    ParticipantSet --> MatchCreate --> RoomResolve --> Actor
    Actor --> BotRuntime
    BotRuntime --> ActionQueue
    Actor --> ActionQueue
    ActionQueue --> GameRules
```

## 6.1 机器人规则

1. 真人主播只有一个时，允许通过配置决定立即匹配机器人或等待真人。
2. 机器人必须拥有独立 ParticipantId 和 BotId。
3. 机器人策略不能直接修改权威状态。
4. 机器人动作必须进入统一输入处理流程。
5. 机器人动作需要明确其目标 Tick 和排序规则。
6. 房间迁移时必须恢复必要的机器人状态或重新初始化可重建的策略状态。
7. MatchStateStore 不能在没有额外一致性设计的情况下成为跨房间权威裁决系统。

机器人是游戏参与者层的能力，不是平台接入层的能力。

---

# 7. 帧同步与权威 Tick 架构

Chorus 应采用服务端权威的固定 Tick 模拟模型作为目标基础。

除非现有代码证明确实具备客户端确定性模拟和锁步协议，否则不要把当前 Tick Loop 和快照同步称为完整的传统帧同步。

```
flowchart TB
    subgraph Inputs["输入来源"]
        PlatformEvent["平台事件"]
        StreamerAction["主播操作"]
        MiniGameAction["小游戏客户端操作"]
        BotAction["机器人动作"]
    end

    Normalize["输入标准化"]
    Validate["身份、权限、参数校验"]
    Dedup["去重与幂等检查"]
    Sequence["确定性入队与排序"]
    TickAssign["输入 Tick 归属"]
    Mailbox["Room Actor Mailbox"]
    TickLoop["固定 Tick Loop"]
    Apply["按序应用输入"]
    GameModule["IGameModule / Game Rules"]
    State["权威 Room State"]
    Snapshot["状态快照"]
    Delta["可选增量状态"]
    Broadcast["状态发布"]
    Ack["输入确认与错误反馈"]
    DataEvent["必要的游戏结果 / 数据命令"]

    PlatformEvent --> Normalize
    StreamerAction --> Normalize
    MiniGameAction --> Normalize
    BotAction --> Normalize

    Normalize --> Validate --> Dedup --> Sequence --> TickAssign --> Mailbox
    Mailbox --> TickLoop --> Apply --> GameModule --> State

    State --> Snapshot
    State --> Delta
    Snapshot --> Broadcast
    Delta --> Broadcast

    Apply --> Ack
    State --> DataEvent
```

## 7.1 Tick 语义

必须明确定义：

* Tick 频率。
* Tick 编号的起点与递增规则。
* 输入何时进入队列。
* 输入何时正式应用。
* 同一 Tick 内多种输入的确定性顺序。
* 迟到、重复、乱序输入的处理方式。
* Tick 超时、异常和暂停时的处理方式。

不能将平台提供的接收时间直接当作精确的游戏帧时间。对于直播平台，服务器接收时间、平台事件时间和游戏 Tick 是三个不同的概念。

## 7.2 状态同步

至少区分：

* 输入 ACK：告诉发送者输入是否被接收或接受。
* 状态快照：用于首次加入、重连或恢复。
* 增量更新：降低持续同步的带宽成本。
* 事件通知：用于非连续状态的通知与表现。
* 最终结算结果：权威游戏结果，必须与普通展示消息区分。

当前 `GetStateSnapshot()` 不等于完整同步协议，也不等于持久化恢复。

## 7.3 与当前 SessionActor 的关系

现有 `SessionActor` 已有单消费者 mailbox、控制消息与事件 lane、背压和 Tick 执行基础。

Agent 应优先在现有 Actor 模型上补齐协议和测试，而不是未经评估直接引入全新的帧同步引擎。

---

# 8. 异步玩家数据与持久化架构

```
flowchart TB
    subgraph Authoritative["权威执行"]
        Actor["Room Actor"]
        Rules["游戏规则与结算"]
        Result["权威游戏结果"]
        CommandBuilder["构造 DataCommand"]
    end

    subgraph Reliability["可靠性边界"]
        Classification{"数据可靠性等级"}
        BestEffort["普通统计通道"]
        Durable["可靠持久化缓冲 / Outbox"]
        Idempotency["命令幂等与版本校验"]
        Retry["重试与失败恢复"]
        DLQ["失败隔离 / Dead Letter"]
    end

    subgraph DataWorkers["异步数据执行"]
        Worker["Data Worker Pool"]
        PlayerHandler["玩家数据处理"]
        RecordHandler["游戏记录处理"]
        RankHandler["排行榜处理"]
        AnalyticsHandler["统计处理"]
    end

    subgraph DataStores["数据存储"]
        PlayerDB[("玩家长期数据")]
        RecordDB[("游戏历史与结算")]
        RankDB[("排行榜数据")]
        AnalyticsDB[("派生统计数据")]
    end

    Actor --> Rules --> Result --> CommandBuilder
    CommandBuilder --> Classification

    Classification -->|允许丢弃或可重建| BestEffort
    Classification -->|必须可靠| Durable

    Durable --> Idempotency
    BestEffort --> Idempotency
    Idempotency --> Worker
    Worker --> PlayerHandler
    Worker --> RecordHandler
    Worker --> RankHandler
    Worker --> AnalyticsHandler

    PlayerHandler --> PlayerDB
    RecordHandler --> RecordDB
    RankHandler --> RankDB
    AnalyticsHandler --> AnalyticsDB

    Worker --> Retry
    Retry --> Durable
    Retry --> DLQ
```

## 8.1 数据可靠性分级

### 普通统计

可以异步计算、延迟更新，部分情况下可丢弃并重建。

### 玩家资料与进度

可以异步更新，但必须定义版本、并发写入、幂等和查询一致性。

### 游戏记录

根据审计和恢复要求确定持久化等级。关键比赛结果应有可靠持久化边界。

### 关键经济数据

如果存在积分扣减、货币、背包或奖励发放，必须设计业务流水、幂等键、事务约束和失败补偿。

不能先向玩家承诺操作成功，再把关键命令放进可能丢失的内存队列。

## 8.2 DataCommand 建议契约

```csharp
public sealed record DataCommand
{
    public required string CommandId { get; init; }
    public required string CommandType { get; init; }
    public required string RoomId { get; init; }

    public string? MatchId { get; init; }
    public string? PlayerId { get; init; }
    public string? SourceEventId { get; init; }

    public long? Sequence { get; init; }
    public int SchemaVersion { get; init; }

    public required string Payload { get; init; }
}
```

该代码是设计示例，不是要求原样复制。实际序列化格式应复用 Chorus 当前协议习惯。

## 8.3 重要约束

* Data Worker 不得直接修改 Room Actor 内存。
* 数据命令必须有幂等语义。
* 队列容量必须有上限。
* 数据处理延迟必须可观测。
* 重试必须考虑顺序与重复执行。
* 关键数据不能在失败时静默丢弃。
* 不要求初期引入 Kafka 或 RabbitMQ。

---

# 9. 排行榜子系统

排行榜必须是正式架构模块，而不是仅在文档中提及的统计功能。

```
flowchart TB
    subgraph Sources["权威成绩来源"]
        RoomScore["房间实时分数"]
        MatchResult["单局结算结果"]
        PlayerProgress["玩家长期进度"]
    end

    subgraph RankPipeline["排行榜处理"]
        RankEvent["Leaderboard Event"]
        RankValidate["成绩来源校验"]
        RankDedup["幂等与重复成绩检查"]
        RankPolicy["排行榜规则<br/>作用域、排序、并列"]
        RankProjector["Leaderboard Projector"]
    end

    subgraph RankTypes["排行榜类型"]
        RoomRank["RoomLeaderboard"]
        MatchRank["MatchLeaderboard"]
        PeriodRank["PeriodLeaderboard"]
        GlobalRank["GlobalLeaderboard"]
        PlatformRank["PlatformLeaderboard Adapter"]
    end

    subgraph RankStorage["排行榜存储与查询"]
        RankStore[("排行榜存储")]
        RankCache[("可选查询缓存")]
        RankAPI["Leaderboard Query API"]
    end

    subgraph Consumers["消费端"]
        GameClient["游戏客户端"]
        StreamerUI["主播面板"]
        MiniGame["小游戏客户端"]
        PlatformUI["平台原生榜单组件"]
    end

    RoomScore --> RankEvent
    MatchResult --> RankEvent
    PlayerProgress --> RankEvent

    RankEvent --> RankValidate --> RankDedup --> RankPolicy --> RankProjector

    RankProjector --> RoomRank
    RankProjector --> MatchRank
    RankProjector --> PeriodRank
    RankProjector --> GlobalRank
    RankProjector --> PlatformRank

    RoomRank --> RankStore
    MatchRank --> RankStore
    PeriodRank --> RankStore
    GlobalRank --> RankStore
    PlatformRank --> RankStore

    RankStore --> RankCache --> RankAPI

    RankAPI --> GameClient
    RankAPI --> StreamerUI
    RankAPI --> MiniGame
    PlatformRank --> PlatformUI
```

## 9.1 必须明确的排行榜类型

* RoomLeaderboard：当前房间内的实时排行。
* MatchLeaderboard：单局比赛排行。
* PeriodLeaderboard：日榜、周榜、月榜等周期排行。
* GlobalLeaderboard：跨房间的长期排行。
* PlatformLeaderboard：调用平台原生排行榜能力的适配器。

## 9.2 权威数据规则

* 实时房间榜可以基于 Room 当前权威状态更新。
* 单局榜以权威 Match 或游戏结算结果为准。
* 周期榜与全局榜由 Data Worker 异步聚合。
* 平台原生排行榜需要符合平台自身 API、权限和数据模型。
* 客户端不能直接提交任意成绩作为权威分数。
* 重复事件不能重复增加积分。
* 必须明确并列分数、重置、周期边界和最终成绩冻结规则。

## 9.3 查询与更新分离

排行榜写入和排行榜查询应分离。

实时比赛中的分数不必每次变化都同步写入全局排行榜数据库。

全局排行可以适度延迟更新，但必须明确延迟范围、失败恢复和重建方法。

---

# 10. 直播平台与小游戏平台双适配架构

必须区分直播互动事件和小游戏运行时接入。

```
flowchart TB
    subgraph PlatformSources["平台来源"]
        DouyinLive["抖音直播"]
        KuaishouLive["快手直播"]
        BiliLive["哔哩哔哩直播"]
        OtherLive["其他直播平台"]
        DouyinMini["抖音小游戏"]
        KuaishouMini["快手小游戏"]
        OtherMini["其他小游戏平台"]
    end

    subgraph LiveAdapters["直播互动适配"]
        LiveAdapterContract["ILivePlatformAdapter"]
        EventMapper["Platform Event Mapper"]
        LiveCapabilities["弹幕、礼物、用户标识等能力"]
    end

    subgraph MiniAdapters["小游戏运行时适配"]
        MiniClientSDK["Chorus MiniGame SDK"]
        LoginAdapter["平台登录与玩家身份"]
        NetworkAdapter["实时通信与网络能力"]
        MiniCapability["平台能力适配"]
        MiniLeaderboard["平台原生排行榜适配"]
    end

    subgraph ChorusCore["Chorus 核心业务"]
        Identity["身份映射"]
        Binding["平台账号唯一绑定"]
        RoomRouter["统一 Room Router"]
        Matchmaking["匹配与参与者管理"]
        GameRuntime["Room Actor / Game Module"]
        Leaderboard["统一排行榜"]
    end

    DouyinLive --> LiveAdapterContract
    KuaishouLive --> LiveAdapterContract
    BiliLive --> LiveAdapterContract
    OtherLive --> LiveAdapterContract

    LiveAdapterContract --> EventMapper
    EventMapper --> LiveCapabilities
    LiveCapabilities --> Identity

    DouyinMini --> MiniClientSDK
    KuaishouMini --> MiniClientSDK
    OtherMini --> MiniClientSDK

    MiniClientSDK --> LoginAdapter
    MiniClientSDK --> NetworkAdapter
    MiniClientSDK --> MiniCapability
    MiniCapability --> MiniLeaderboard

    LoginAdapter --> Identity
    Identity --> Binding
    Binding --> RoomRouter
    NetworkAdapter --> RoomRouter

    RoomRouter --> Matchmaking
    Matchmaking --> GameRuntime
    GameRuntime --> Leaderboard
    MiniLeaderboard <--> Leaderboard
```

## 10.1 直播平台 Adapter

负责将平台事件转换为 Chorus 的标准事件模型。

必须记录每个平台的：

* 真实支持的事件类型。
* 平台账号与直播间标识。
* 用户身份标识。
* 鉴权与连接方式。
* 事件 ID 和去重能力。
* 限流、断线重连与错误恢复。
* 平台权限、版本及接入限制。

## 10.2 小游戏平台 Adapter

小游戏运行平台是另一类接入边界。

需要独立研究：

* 客户端运行环境。
* JavaScript API 与服务端 OpenAPI。
* 玩家登录和身份标识。
* 实时通信能力与网络限制。
* 房间与匹配接入。
* 排行榜、好友榜和分享能力。
* 支付、奖励和平台审核要求。
* SDK 版本、权限和发布限制。

小游戏平台不能只复用直播平台事件 Adapter 就宣称支持。

## 10.3 候选平台与优先级

建议调研顺序：

* P0：抖音小游戏、抖音直播互动能力。
* P0：快手小游戏、快手直播互动能力。
* P1：哔哩哔哩直播。
* P2：其他小游戏与直播平台。

以上是建议顺序，不是已实现能力清单。

每个平台必须有独立的官方资料、能力矩阵、接口边界和测试证据。

---

# 11. 房间迁移与故障恢复

多 Worker 架构必须能够在 Worker 故障或容量调整时安全迁移 Room。

```
sequenceDiagram
    participant RM as Room Manager
    participant GW as Gateway
    participant Old as Old Worker
    participant Store as Durable State Store
    participant New as New Worker

    RM->>Old: 请求停止接收新权威写入
    Old->>Old: 停止推进该 Room 的权威状态
    Old->>Store: 持久化最终快照与恢复信息
    Store-->>Old: 持久化确认
    Old-->>RM: 确认已停止权威写入

    RM->>New: 准备接管 Room
    New->>Store: 读取快照与必要事件记录
    Store-->>New: 返回恢复数据
    New->>New: 校验版本、恢复状态、重建执行环境
    New-->>RM: 确认恢复完成

    RM->>RM: 增加 Epoch / RouteVersion
    RM->>GW: 发布新的 Room 路由
    GW->>New: 按新路由发送后续事件

    Note over Old,New: 必须防止旧 Worker 在接管后继续权威写入
```

## 11.1 必须保证

* 同一 Room 同一时刻只有一个有效权威写入者。
* 旧 Worker 停止权威写入必须有明确机制。
* 新 Worker 不能在状态恢复完成前接收权威输入。
* 路由版本必须能识别过期路由。
* 已接受但尚未应用的事件必须有恢复策略。
* 持久化快照必须有版本与完整性校验。
* 恢复失败时不能伪装成接管成功。

## 11.2 与现有代码的关系

现有 `IGameModule` 有 `GetStateSnapshot()`，但没有完整的恢复契约。

快照生成、持久化、状态恢复、待处理事件补齐和权威写入隔离是不同的能力，必须分别验收。

---

# 12. 进程部署拓扑

```
flowchart TB
    subgraph DeploymentA["阶段 A：单进程部署"]
        Server["Chorus.Server"]
        LocalIngress["本地 Gateway"]
        LocalRouter["InProc Router"]
        LocalActors["多个 Room Actor"]
        LocalData["本地 Data Worker"]
        LocalStorage[("现有或选定存储")]
        
        Server --> LocalIngress
        LocalIngress --> LocalRouter
        LocalRouter --> LocalActors
        LocalActors --> LocalData
        LocalData --> LocalStorage
    end

    subgraph DeploymentB["阶段 B：Gateway / EngineHost 拆分"]
        GW1["Platform Gateway"]
        GW2["Streamer Gateway"]
        Engine["EngineHost"]
        EngineActors["多个 Room Actor"]
        DataProc["可选独立 Data Worker"]
        
        GW1 --> Engine
        GW2 --> Engine
        Engine --> EngineActors
        EngineActors --> DataProc
    end

    subgraph DeploymentC["阶段 C：多 Worker 集群"]
        PlatformGWs["多个 Platform Gateway"]
        StreamerGWs["多个 Streamer Gateway"]
        Controller["Room Manager / Routing Control"]
        Worker1["EngineHost A"]
        Worker2["EngineHost B"]
        Worker3["EngineHost C"]
        DataWorkers["Data Worker 集群"]
        PersistentStore[("持久化存储")]
        
        PlatformGWs --> Controller
        StreamerGWs --> Controller
        Controller --> Worker1
        Controller --> Worker2
        Controller --> Worker3
        Worker1 --> DataWorkers
        Worker2 --> DataWorkers
        Worker3 --> DataWorkers
        DataWorkers --> PersistentStore
    end
```

以上三个阶段是逐步演进的部署形态，不是三个必须同时运行的拓扑。

单 Worker 阶段不需要为了架构形式强制部署独立 Room Manager。

---

# 13. 可观测性与运营管理

```
flowchart TB
    GatewayMetrics["Gateway Metrics"]
    RoomMetrics["Room / Tick Metrics"]
    RoutingMetrics["Worker / Routing Metrics"]
    DataMetrics["Data Worker Metrics"]
    RankMetrics["Leaderboard Metrics"]
    PlatformMetrics["Platform Adapter Metrics"]

    Collector["Metrics / Logs / Traces Collector"]
    Dashboard["运营与开发者 Dashboard"]
    Alerts["告警规则"]

    GatewayMetrics --> Collector
    RoomMetrics --> Collector
    RoutingMetrics --> Collector
    DataMetrics --> Collector
    RankMetrics --> Collector
    PlatformMetrics --> Collector

    Collector --> Dashboard
    Collector --> Alerts
```

最低限度需要观察：

* 平台事件接收量、失败量、重复量。
* 主播连接数与重连情况。
* 活跃 Room 数与每 Worker 房间数。
* Actor 队列长度与最老事件等待时间。
* Tick 耗时、超时和异常。
* 房间路由失败、迁移和接管次数。
* 快照生成、恢复耗时。
* Data Worker 队列长度、最老消息年龄和失败数。
* 排行榜更新延迟与重复成绩冲突。
* 各平台 Adapter 的连接状态、限流和事件解析失败。

---

# 14. 必须审核的现有代码

Code Agent 必须从真实代码出发确认以下内容：

| 审核目标 | 关键文件或组件 | 需要确认的事项 |
| --- | --- | --- |
| 平台事件入口 | `PlatformIngressFace.cs` | 事件验证、去重、SessionId 来源与投递 |
| 主播连接 | `StreamerWsFace.cs` | hello、身份验证、SessionId 与连接注册 |
| 房间执行 | `SessionActor.cs` | Tick、mailbox、背压、事件顺序、快照 |
| 房间生命周期 | `SessionRegistry.cs` | 创建、LRU 驱逐、空闲清扫和状态保留 |
| 游戏规则 | `IGameModule` 及其实现 | 权威状态、事件应用、快照与恢复 |
| 标准事件 | `AudienceEvent.cs` | SessionId、MatchId、FactionId 等字段语义 |
| 路由 | `RoomAffinity.cs`、`RoomRouteTable.cs` | 是否真正接入 Gateway/EngineHost |
| 平台接入 | Mapper、Adapter 和注册表 | 真实支持的平台与事件类型 |
| 匹配 | Match 相关实现 | 当前 MatchStateStore 是否只是展示聚合 |
| 数据 | Repository、后台任务和持久化代码 | 是否异步、是否可靠、是否幂等 |
| 排行榜 | 排行榜相关代码（如有） | 当前实现状态与数据来源 |
| 部署 | process-topology 等文档 | 单进程、拆分部署和 IPC 的真实接线 |

不得仅根据文档、类名或接口声明判断某个能力已经完成。

---

# 15. 分阶段实施顺序

## 阶段 0：架构审计

不修改生产代码。

交付：

* 当前代码调用链。
* 已实现、部分实现、未实现的能力清单。
* Room 与 Session 的语义结论。
* 平台账号绑定规则审计。
* 帧同步能力差距报告。
* 数据可靠性报告。
* 平台与排行榜能力矩阵。
* 文件级修改计划。

## 阶段 1：统一房间语义和路由

交付：

* 平台账号唯一绑定。
* 主播加入 Room 的授权规则。
* 两条入口共用物理路由。
* 本地 Router 实现。
* 与现有 Session/Actor 的兼容方案。
* 对应测试。

## 阶段 2：补齐游戏执行协议

交付：

* Tick 和输入归属规则。
* 同 Tick 输入排序。
* 机器人参与者模型。
* 状态快照、增量和 ACK 的协议边界。
* 重连和状态恢复设计。

## 阶段 3：异步数据处理

交付：

* DataCommand 契约。
* Data Worker。
* 玩家数据与游戏记录边界。
* 幂等、可靠性分级和背压。
* 必要的持久化缓冲。

## 阶段 4：排行榜

交付：

* 排行榜数据模型。
* 权威成绩来源。
* 实时榜、单局榜、周期榜和全局榜。
* 查询 API。
* 平台原生排行榜适配契约。

## 阶段 5：状态恢复与多 Worker

交付：

* 快照恢复。
* 必要的事件补齐。
* Worker 注册和健康管理。
* Room → Worker 生产路由。
* Epoch/路由版本与旧写者隔离。
* 迁移和故障接管测试。

## 阶段 6：小游戏平台接入

先完成抖音和快手的官方资料调研与能力矩阵，再选择平台做端到端验证。

交付：

* 官方资料清单。
* 客户端 SDK 设计。
* 服务端 API 边界。
* 玩家身份映射。
* 实时通信适配。
* 平台排行榜能力适配。
* 实际可运行的集成测试。

## 阶段 7：压测与扩容

基于真实测试确定：

* 单 Worker 能承载的 Room 数量。
* Tick 延迟和事件吞吐量。
* Gateway 连接与消息能力。
* Data Worker 的处理能力。
* 房间迁移和恢复开销。
* 是否需要更复杂的分布式协调或单房间计算拆分。

---

# 16. 最终验收原则

最终架构必须满足：

1. 一个平台账号同一时刻最多绑定一个有效 Room。
2. 平台事件与主播操作最终定位到同一个 Room 路由体系。
3. 一个 Room 可以只有一名真人主播。
4. 匹配可以使用机器人补齐参与者。
5. 机器人遵循权威游戏规则。
6. 帧同步、Tick、状态快照和输入确认有清晰边界。
7. 玩家长期数据和异步处理不阻塞普通 Tick。
8. 关键数据具有可靠的持久化和幂等保证。
9. 排行榜有正式的业务模型与数据管道。
10. 抖音、快手小游戏和直播互动接入被分别研究。
11. 多 Worker 路由具备实际运行时接线和安全接管能力。
12. 房间恢复不会造成双写或无声丢失已接受事件。
13. 单进程部署仍然保持简单可运行。
14. 所有实现状态都有代码或测试证据。

**最终要求：先审核、再设计差距、后分阶段实现。禁止一次性大规模重构，也禁止将目标架构图中的模块直接标记为已实现。**

---

# 17. 代码审计结果（2026-10-13）

> 审计基准：`main @ 31434a1`。逐项核对 `src/`、`tests/`、`docs/`，每项标注 `file:line` 证据。

## 17.1 审计总览

| 状态 | 数量 | 说明 |
| --- | --- | --- |
| **Implemented** | 22 | 生产接线 + 测试覆盖 |
| **Partial** | 12 | 代码存在但不完整或未接线 |
| **Planned** | 1 | 仅文档，无代码 |
| **Missing** | 45 | 无任何代码 |

**核心结论：** 实时执行核心（SessionActor tick/mailbox/snapshot/ACK）、双入口接入面（Platform/Streamer Gateway + 验证/去重/限流/鉴权）、Gateway/EngineHost 拆分 + 5 种 IPC transport、Prometheus 指标——全部生产接线且有测试。控制面（identity/binding/Room 实体/生命周期/matchmaking/bot/worker registry）、数据面（DataCommand/Data Worker/排行榜/持久化）、迁移恢复、小游戏平台适配——全部缺失或仅文档。

## 17.2 逐项审计表

### §2 逻辑架构模块

| # | 模块 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | Platform Gateway | **Implemented** | `PlatformIngressFace.cs:52-140`（限流→体帽→超时→验签→mapper 链→去重→Tell）；`IngressRateLimiter.cs`；测试 `PlatformIngressFaceTests.cs` 等 7 文件 |
| 2 | Streamer Gateway | **Implemented** | `StreamerWsFace.cs:69-223`（hello→协议门→token 鉴权 fail-closed→ACK 频控→连接生命周期）；测试 `StreamerWsAuthEndpointTests.cs` 等 4 文件 |
| 3 | MiniGame Gateway | **Missing** | 无小游戏客户端 SDK、无登录/房间 API。`PlatformNames.cs` 仅 Douyin/Kuaishou |
| 4 | Admin API | **Partial** | `Chorus.Server/Program.cs:104-106`：只读诊断 `GET /sessions`、`GET /matches`；无配置管理、无运维端点 |
| 5 | Identity Mapping | **Missing** | 无 identity service，无 platform/streamer/player 映射 |
| 6 | Platform Binding | **Missing** | 无 `(Platform, PlatformAccountId) → Room` 绑定存储，无唯一性约束，无换绑流程 |
| 7 | Room Directory | **Partial** | `SessionRegistry.cs:15-218`（session 表 + LRU 驱逐 + 空闲清扫）；**是 session 目录不是 Room 实体**，无生命周期状态 |
| 8 | Matchmaking | **Missing** | 无匹配、无参与者分配、无机器人填充 |
| 9 | Room Routing | **Partial** | `InProcSessionActorRouter.cs:14-27` + `IpcSessionActorRouter.cs:13-26`（session→actor 路由已接线）；`RoomAffinity.cs:12-46` + `RoomRouteTable`（**未接线**，`RoomAffinity.cs:10` 注明 "No production wiring yet"） |
| 10 | Worker Registry | **Missing** | 无 worker 注册、健康上报、容量跟踪 |
| 11 | Platform Capability Registry | **Missing** | 无能力/版本注册。`PlatformNames.cs` 是常量列表 |
| 12 | Room Actor | **Implemented** | `SessionActor.cs:8-626`（权威状态 + 双通道 mailbox + 固定 tick + 快照 + ACK + 背压 + 诊断）；测试 6 文件 |
| 13 | Game Module | **Implemented** | `IGameModule.cs:5-12` + `SampleGameModule.cs:6-109`（计数聚合 + top-10）；测试 `SampleGameModuleTests.cs` |
| 14 | Bot Runtime | **Missing** | 零命中。无 bot/strategy/bot action |
| 15 | Realtime Sync | **Partial** | ACK `SessionActor.cs:344-374`；快照 `:447-542`；**delta sync 缺失**（每次全量 state JSON） |
| 16 | Data Command Ingress | **Missing** | 无命令校验/幂等层 |
| 17 | Durable Buffer / Outbox | **Missing** | 无 outbox、无持久化缓冲 |
| 18 | Data Worker Pool | **Missing** | 无 Data Worker |
| 19 | Player Data Repository | **Missing** | 无玩家持久化 |
| 20 | Game Record Repository | **Missing** | 无游戏记录持久化 |
| 21 | Leaderboard Subsystem | **Missing** | 零命中 `leaderboard|scoreboard|ranking|rank`。`SampleGameModule` top 列表是展示计数器非排行榜子系统 |
| 22 | Statistics / Analytics | **Missing** | 无分析管道。仅内存诊断计数器 `SessionDiagnostics.cs` |
| 23 | 全部存储（Config/DurableState/PlayerDB/GameDB/LeaderboardDB/EventLog） | **Missing** | 无任何持久化。零 DB 包（仅 Memory/Logging/Configuration） |

### §3 领域对象

| # | 对象 | 状态 | 证据 |
| --- | --- | --- | --- |
| 24 | PlatformAccount | **Missing** | 无平台身份实体 |
| 25 | PlatformBinding | **Missing** | 无绑定实体 |
| 26 | Streamer | **Missing** | 无 Streamer 实体（WS token + clientId 是唯一身份） |
| 27 | Player | **Missing** | 无 Player 实体（仅 `AudienceEvent.UserId` 不透明字符串） |
| 28 | Room（独立于 Session） | **Missing** | Session 是唯一运行时房间实体。架构文档 §3.1 的待定问题结论：**Session 保留为房间运行时实体** |
| 29 | Session | **Implemented** | `SessionActor.cs:8`、`SessionRegistry.cs:15`、`SessionManager.cs:12`；测试 `SessionRegistryTests.cs`、`SessionManagerLifecycleTests.cs` |
| 30 | Match | **Partial** | `MatchStateStore.cs:5-93` + `MatchRegistry.cs:5-86`：**仅展示聚合**（各阵营计数），无权威比赛状态、无生命周期 |
| 31 | Participant | **Missing** | 无参与者实体 |
| 32 | Bot | **Missing** | 无 |
| 33 | RoomWorker | **Missing** | 无 |
| 34 | RoomRoute | **Missing** | `RoomRouteTable` 持有 sessionId→workerId 字典但无 RoomRoute 记录（Epoch/RouteVersion） |

### §4 双入口路由

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 35 | 平台事件入口 + 验证 | **Implemented** | `PlatformIngressFace.cs:52-140`（验签 `:73`、去重 `:98`、Tell `:106-129`） |
| 36 | 主播连接 + 鉴权 | **Implemented** | `StreamerWsFace.cs:128-136`（token 鉴权 fail-closed）；测试 `StreamerWsAuthEndpointTests.cs` |
| 37 | IRoomRouter 接口 | **Missing** | 无 `IRoomRouter` 类型。现有缝是 `ISessionActorRouter`（session 级）+ `IStreamerSessionRouter` |
| 38 | RoomRouteTable | **Partial** | `RoomAffinity.cs:53-73`（类存在 + 原子 Replace/Snapshot/TryGetWorker）；**未接线**（`RoomAffinity.cs:10`） |
| 39 | RoomAffinity | **Partial** | `RoomAffinity.cs:12-46`（FNV-1a 稳定哈希 + 排序 worker 集 → 确定性分配）；**未接线** |

### §5 房间生命周期

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 40 | Room 生命周期状态机 | **Missing** | 无 Created/Waiting/Active/Idle/Sleeping/Migrating/Recovering/Closing/Destroyed 状态。`client-resync-state-machine.md` 定义的是客户端侧状态机非 Room 生命周期 |
| 41 | Room Manager / Route Controller | **Missing** | `process-topology.md` 明确推迟到阶段三："房间注册/健康/路由下推等阶段三触发时再建" |
| 42 | Worker 分配 | **Missing** | `RoomAffinity.Assign` 是分配函数但无调用方；无容量评估、无分配流程 |

### §6 匹配与机器人

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 43 | Matchmaker | **Missing** | 无 |
| 44 | Bot Factory / Bot Participant | **Missing** | 无 |
| 45 | Bot 动作进入统一输入队列 | **Missing** | 无 |
| 46 | MatchStateStore | **Partial** | `MatchStateStore.cs:5-93`：内存聚合 likes/gifts/comments per faction；`MatchRegistry.cs:5-86`：事件计数。**确认仅展示聚合** |

### §7 帧同步

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 47 | 输入标准化 | **Implemented** | `IPlatformEventMapper.cs:7` + `AudienceEvent.cs:11-24`（规范模型）；Douyin/Kuaishou Mapper 已实现 |
| 48 | 去重 | **Implemented** | `EventDeduplicator.cs:34-175`（原子 reserve/commit/rollback + TTL + 容量阀）；接线 `PlatformIngressFace.cs:98-122` |
| 49 | Tick 归属 | **Implemented** | `SessionActor.cs:379`：`targetTick = TickId + InputDelayTicks`（默认 2） |
| 50 | Actor Mailbox | **Implemented** | `SessionActor.cs:20-21`：双通道（无界控制 + 有界事件，`SingleReader: true`） |
| 51 | 固定 Tick Loop | **Implemented** | `SessionActor.cs:112`：`PeriodicTimer(TickDurationMs)`（66ms）；tick 超时守卫 `:402-405` |
| 52 | 快照 | **Implemented** | `WireSnapshot.cs:13-33` + `ServerSnapshot.cs`（V=1）；`SessionActor.cs:447-542`（周期 + 强制 resync + 体帽） |
| 53 | 增量同步 | **Missing** | 每次全量 state JSON，无 delta/merge 协议 |
| 54 | ACK | **Implemented** | `ClientAck.cs` + `SessionActor.cs:156-157/:344-374`（invalid/duplicate/stale/accepted 分类） |

### §8 异步数据

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 55 | DataCommand 契约 | **Missing** | 无 |
| 56 | Data Worker | **Missing** | 无 |
| 57 | 可靠性分级 | **Missing** | 无 |
| 58 | 数据命令幂等 | **Missing** | 无（`EventDeduplicator` 是事件入口去重非数据命令幂等） |
| 59 | 重试 / DLQ | **Missing** | 无 |

### §9 排行榜

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 60 | 任何排行榜代码 | **Missing** | 零命中。`SampleGameModule` top 列表是展示计数器非排行榜子系统 |

### §10 平台适配

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 61 | 抖音直播适配器 | **Partial** | `DouyinPlatformEventMapper.cs:28-121`（已接线）；**占位模式**——`DouyinPlatformEventMapper.cs:9-26` 注明 "NOT the official Douyin schema…TODO(joint-debugging)" |
| 62 | 快手直播适配器 | **Partial** | `KuaishouPlatformEventMapper.cs:25-173`（AES-256-CBC 解密 + 映射，已接线）；同样占位模式——内层布局 "NOT publicly documented…must be replaced at joint debugging" |
| 63 | 哔哩哔哩适配器 | **Planned** | 仅文档 `bilibili-danmaku-adapter.md`，结论=部分支持，无代码 |
| 64 | 抖音小游戏 SDK | **Missing** | 无 |
| 65 | 快手小游戏 SDK | **Missing** | 无 |
| 66 | 平台能力注册 | **Missing** | 无 |

### §11 迁移与恢复

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 67 | 快照持久化 | **Missing** | `snapshot-restore.md`："快照仅存在于内存、无持久化" |
| 68 | 状态恢复 | **Missing** | `IGameModule.cs:5-12` 只有 `GetStateSnapshot()`（读出），无 `RestoreState` 入口。`snapshot-restore.md` 待拍板 |
| 69 | 恢复后事件补齐 | **Missing** | 无 |
| 70 | Epoch / 路由版本 | **Missing** | 无 |
| 71 | 旧写者隔离 | **Missing** | 无 |

### §12 部署拓扑

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 72 | 单进程模式 | **Implemented** | `Chorus.Server/Program.cs:13-125`（全 face + actor 同进程，`InProcSessionActorRouter`） |
| 73 | Gateway/EngineHost 拆分 | **Implemented** | `Chorus.Gateway/Program.cs` + `Chorus.EngineHost/Program.cs`；`GatewayRoles.cs:10-34`；IPC 5 种 transport；`/ready` 探针 |
| 74 | 多 Worker 集群 | **Partial** | 三进程拓扑（platform-gw + streamer-gw + 一个 EngineHost）已实现并测试；`RoomAffinity`/`RoomRouteTable` 存在但**未接线**；多 EngineHost 集群级 Room 路由与权威接管不存在 |

### §13 可观测性

| # | 项 | 状态 | 证据 |
| --- | --- | --- | --- |
| 75 | 指标端点 | **Implemented** | 三主机 `GET /metrics`；`ChorusMetricsText.cs:12-147` |
| 76 | 平台事件指标 | **Implemented** | `chorus_ingress_accepted/duplicates/unverified/unmapped/rate_limited/too_large/timed_out_total` |
| 77 | Room/Tick 指标 | **Implemented** | `chorus_sessions`、`chorus_events_*`、`chorus_tick_*`、`chorus_snapshots_*`、`chorus_acks_*` 等 |
| 78 | 路由指标 | **Missing** | 无房间路由失败/迁移/接管计数 |
| 79 | Data Worker 指标 | **Missing** | 无 Data Worker |
| 80 | 排行榜指标 | **Missing** | 无排行榜 |

## 17.3 关键差距与下一步

**已完成（2026-10-13 本轮）：**
1. 帧同步 P2（帧历史 ring buffer + `resync` 拉取消息 + 快照 `frameEvents` 信封字段）——`758a6a5`，18 守卫
2. 帧同步 P1（帧号语义 + 客户端按帧号回放状态机 `Chorus.LoadTool.FramePlayer`）——本轮回填测试

**可立即推进的（有代码基础）：**
3. 抖音/快手适配器联调（占位模式 → 真实字段映射）
4. IGameModule 恢复契约拍板 → 状态恢复代码

**需要先决策的：**
5. Room vs Session 语义（审计结论：Session 保留为房间运行时实体）
6. 平台账号唯一绑定（需要存储层决策）
7. 持久化策略（DB 选型、可靠性分级）

**需要先调研的：**
8. 抖音/快手小游戏平台（官方资料、能力矩阵）
9. 多 Worker 触发条件（CPU 峰值 + 房间量级运行期数据）
