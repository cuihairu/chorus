---
title: 规格草案
---


# 规格草案（v1）

本规格用于指导把旧框架抽象为开源的"直播互动弹幕游戏框架"。

## 1. 目标与非目标

### 目标

- 支持 1 个或多个主播客户端（直播伴侣/PC 客户端）连接 Chorus Server。
- 摄取抖音/快手等平台的互动事件（弹幕、礼物、点赞、关注等），转换为统一的 `AudienceEvent` 输入流。
- 以"可复用的帧同步/状态同步架构"为中心，支持：
  - 单房间（单主播）玩法
  - 多房间对抗（多主播阵营/PK）
- 强可观测性：能解释"为什么不同步"，并能自动自愈（快照追赶、降采样等）。

### 非目标（开源版不做）

- 不包含任何具体玩法（数值、英雄、技能、美术、配置表等）。
- 不包含平台私有的抓包/逆向能力；只提供"适配器接口 + 示例桩（或文档）"。

## 2. 总体架构

### 2.1 组件

- **客户端网关（WebSocket）**
  - 主播客户端连接入口
  - 会话管理（鉴权、重连、订阅）
  - 下发快照/状态
- **平台入口（HTTP/Webhook）**
  - 接收平台推送（或由适配器拉取）
  - 验签/去重
  - 转换为统一的 `AudienceEvent`
- **会话引擎（每 Session 一个 Actor）**
  - 每个 Session/Match 一个单线程 actor（邮箱模型），保证顺序与合理性
  - 输入缓冲、节拍控制、背压、快照
- **游戏模块（插件接口）**
  - 开源提供接口与最小示例（用于验证"tick->state->snapshot"链路）

### 2.2 数据模型（概念）

- `Session`：一场直播对应的游戏会话（单主播）。
- `Match`：多个 Session 组成的对抗（如双主播阵营战）。
- `Faction`：阵营（通常绑定一个主播）。
- `Audience`：观众玩家（平台 userId）。

本仓当前的最小落地方式：

- `AudienceEvent` 可携带 `matchId` / `factionId`
- Ingress 可通过显式字段、或经 `Ingress:FactionRules` 的"弹幕口令/礼物 ID -> 阵营"规则补全 `factionId`
- `SampleGameModuleFactory` 创建的多个会话模块共享同一个 match 级状态存储，因此同一 `matchId` 的会话看到同一对局内聚合视图

### 2.3 Match 权威语义限定（评审 P0-8 结论）

**`MatchStateStore` 是展示级的共享聚合，不是权威对抗状态。**

- 它由多个 `SessionActor` 随各自 tick 并发写入（内部 `lock` 只保证单次读写不撕裂，不提供跨 Session 的全序）。同一 Match 内两个 Session 写入的相对顺序取决于各自 tick 的推进时机，在重放/并发下不稳定。
- 因此它适合：**排行榜展示、大屏聚合、氛围型玩法**（"两队合计贡献"这类不需要精确判定的场景）。
- 它不适合：**公平对抗判定**——胜负裁定、达标判定（"谁先到 100 赞"）、奖励结算、反作弊审计。这些语义要求事件具有全局确定的顺序。

**升级的授权条件（何时必须升级）**：当玩法需要跨阵营的公平判定或结算时，该 Match 的事件必须汇聚到**单一权威定序点**——引入 MatchActor（排队并结算该 Match 的全部事件），或把整场对抗收敛到单个 SessionActor 承载。在此之前不实现 MatchActor（见"非目标"：不做分布式 Match 集群；v1 单机单 actor 定序已够）。

## 3. 帧同步设计（v1：服务端权威 tick + 快照）

关键原则：**服务端永远推进，绝不为慢端暂停；慢端用快照追赶。**

### 3.1 tick 时钟

- 固定 tick：`tickDurationMs`（默认 66ms，与推荐区间 50~66 统一；最终值以压测为准）。
- 服务端为每个 Session 维护单调递增的 `tickId`（从 0 起），由 `PeriodicTimer`（单调时钟）驱动。
- 所有下发的数据带 `serverTimeMs`（供客户端测漂移）；`tickId` 随 `snapshot` 下发（不进 `welcome` / `pong`）。
- 超时策略（阶段二）：tick 处理耗时超过 `tickTimeoutMs`（默认 250ms）计入 `TotalTickOverruns` 可观测指标——只检测不取消；GameModule 抛异常计入 `TotalTickErrors`，该 tick 丢失但 actor 存活，诊断照常推进。

### 3.2 输入缓冲（应对平台抖动/乱序）

- `inputDelayTicks`：平台事件一经摄取，调度到 `tickId + inputDelayTicks` 一并结算。
- 事件排序规则必须稳定（推荐：`(ingestTimeMs, platformEventId)`）。

### 3.3 下行：权威状态快照

- 服务端运行游戏模块（权威逻辑）。
- 周期性下发 `StateSnapshot`（当前实现为 `snapshot`）：
  - 为简单起见，v1 是全量 JSON 状态
  - 后续可扩展为增量/压缩

### 3.4 按客户端 ACK 与追赶

客户端周期性发送：

- `ClientAck { sessionId, clientId, lastAppliedTickId }`

服务端：

- 若 `serverTick - lastAckTick > maxLagTicks`：
  - 下发 `snapshot(forced=true, reason="lag")` 强制对齐
  - 通过 `resyncCooldownMs` 避免抖动/刷屏式重同步

**快照版本与大小上限（已实现）**：

- 每个快照带 `v = 1`（schema 版本）；客户端遇到未知版本必须丢弃该快照并保持既有 ACK 行为（lag 自然增长，触发服务端强制重同步，形成闭环）。
- 序列化后状态超过 `maxSnapshotBytes`（默认 1MB）时跳过该 tick 的发送并计入 `chorus_snapshots_oversize_total`；客户端因收不到快照而停止 ACK -> lag 超限 -> 状态缩回上限以内后由强制重同步对齐。

### 3.5 背压与降采样（直播弹幕场景的必答题）

- 每 tick 事件上限：`maxEventsPerTick`
- 超限策略（按玩法取舍）：
  - 合并：同类同用户事件合并（点赞合并、礼物叠层）
  - 分层：核心事件优先，聊天类事件可移入"仅展示通道"

### 3.6 事件生命周期与四种投递状态

一个事件从进入系统到生效经过六段：

```
Received → Validated → Accepted → Scheduled → Applied → Published
 (入口)     (验签)      (映射+去重)  (调度到tick)  (引擎结算)  (快照下发)
```

| 阶段 | 落点 | 失败语义 |
| --- | --- | --- |
| Received | ingress 收到原始请求 | 请求体/限流异常 → 拒绝（HTTP 4xx/413/429） |
| Validated | `IPlatformEventVerifier` 验签 | 失败 → 401，fail-closed（生产默认拒绝） |
| Accepted | mapper 映射 + 阵营规则 + 去重占位 | 映射失败 → 400；重复 → `duplicated=true` 幂等返回 |
| Scheduled | SessionActor 调度到 `tickId + inputDelayTicks` | 内存排队；超限走 3.5 背压策略 |
| Applied | tick 结算，经 reducer 合并后进入 GameModule | 合并不丢计数（按分组键守恒）；超预算的展示型事件可被丢 |
| Published | 快照下发到客户端 | 发布失败按四态的 `Failed` 处理 |

**四种投递状态**（描述一次投递的结局，是可观测/排障用语，不是存储字段）：

- `Received`：已收到，尚未验签。
- `Accepted`：通过验签 + 映射 + 去重，已进入调度（ingress 返回 200 的含义）。
- `Applied`：已在某个 `tickId` 结算进权威状态（此后客户端可经 ACK 确认）。
- `Failed`：验签失败 / 映射失败 / IPC 发布失败。**关键约束：`Failed` 绝不能被记录为"已见"**——去重采用 reserve/commit/rollback 协议；发布失败立即 `Rollback`，平台重试被当作全新投递接受，而不是被误判为重复而丢弃。

**去重 reserve/commit/rollback**（评审 P0-7）：`EventDeduplicator.TryReserve` 原子占位（同 id 并发请求恰有一个赢家）→ 发布成功 `Commit`（Reserved→Delivered，TTL 内抑制重复）→ 发布失败 `Rollback`（释放占位，Failed 可重试）。TTL 默认 600s（`Ingress:DedupTtlSeconds`），并带容量安全阀保证内存有界。

**四类事件**（决定超限取舍策略，见 3.5）：

| 类别 | 例子 | 超限策略 |
| --- | --- | --- |
| 可累积 | 点赞、礼物 | 合并保计数（分组键 `(S,M,F,U[,GiftId])`），绝不因合并丢计数 |
| 不可丢 | 关键礼物触发、口令指令 | 优先保留，禁止静默丢弃 |
| 展示型 | 普通弹幕、进场特效 | 可丢弃/移入展示通道，丢弃计入指标 |
| 控制型 | 管理/开关指令 | 独立通道，不与观众事件抢预算 |

## 4. 客户端协议（v1）

见：`/reference/protocol.html`

## 5. 推荐阅读顺序

如果你在处理"不同步、重同步不稳定、慢端恢复困难"之类的问题，按以下顺序阅读：

1. [总体架构](./architecture.md)
2. [帧同步设计](./frame-sync.md)
3. [帧同步最佳实践](./frame-sync-best-practices.md)
4. [客户端重同步状态机](./client-resync-state-machine.md)
5. [参数调优](../reference/tuning.md)
6. [旧服务端问题分析](./legacy-real-server-analysis.md)
