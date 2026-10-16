---
title: 规格草案
---


<p align="center">
  <a href="./spec.md">English</a> | 简体中文
</p>

# 规格草案（v1）

本规格用于指导把旧框架抽象为可开源的“直播互动弹幕游戏框架”。

## 1. 目标与非目标

### 目标

- 支持 1 个或多个主播客户端（直播伴侣/PC 客户端）连接到 Chorus Server。
- 接入抖音/快手等平台的互动事件（弹幕、礼物、点赞、关注等），转为统一的 `AudienceEvent` 输入流。
- 以“可复用的帧同步/状态同步架构”为核心，支撑：
  - 单房间（单主播）玩法
  - 多房间对抗（多主播阵营/PK）
- 强可观测性：能解释“为什么不同步”，并能自动自愈（快照追赶、降采样等）。

### 非目标（开源版本不做）

- 不包含任何具体玩法（数值、英雄、技能、美术、配置表等）。
- 不包含平台的私有抓包/逆向能力；只提供“适配器接口 + 示例 stub（或文档）”。

## 2. 总体架构

### 2.1 组件

- **Client Gateway（WebSocket）**
  - 主播客户端连接入口
  - 会话管理（鉴权、重连、订阅）
  - 下发 snapshot/状态
- **Platform Ingress（HTTP/Webhook）**
  - 接收平台推送（或由适配器拉取）
  - 校验签名/去重
  - 转换为统一 `AudienceEvent`
- **Session Engine（Actor per Session）**
  - 每个 Session/Match 一个单线程 actor（邮箱模型），保证顺序与可推理性
  - 输入缓冲、节拍控制、背压、快照
- **Game Module（插件接口）**
  - 开源提供接口与最小示例（用于验证“tick->状态->快照”链路）

### 2.2 数据模型（概念）

- `Session`：一场直播对应的游戏会话（单主播）。
- `Match`：多 Session 组成的对抗（例如 2 个主播阵营战）。
- `Faction`：阵营（通常与主播绑定）。
- `Audience`：观众玩家（平台 userId）。

当前仓库中的最小落地方式：

- `AudienceEvent` 可携带 `matchId` / `factionId`
- ingress 可通过显式字段或 `Ingress:FactionRules` 中的“弹幕口令/礼物 ID -> 阵营”规则补全 `factionId`
- `SampleGameModuleFactory` 创建的多个 session 模块共享同一个 match 级状态仓库，因此相同 `matchId` 的 session 会看到同一份局内聚合视图

### 2.3 Match 权威语义定性（review P0-8 结论）

**`MatchStateStore` 是展示级共享聚合，不是权威对抗状态。**

- 它由多个 `SessionActor` 各自 tick 时并发写入（内部 `lock` 只保证单次读写不撕裂，不提供跨 Session 全序）。同一 Match 内两个 Session 的写入相对顺序取决于各自 tick 的推进时序，重放/并发下不稳定。
- 因此它适合：**排行榜展示、大屏聚合、氛围型玩法**（"两队合计贡献"这类不要求精确裁决的场景）。
- 它不适合：**公平对抗裁决**——胜负判定、到达阈值判定（"谁先到 100 赞"）、奖励结算、反作弊审计。这些语义要求事件有全局确定的顺序。

**权威化条件（何时必须升级）**：当玩法需要跨阵营公平裁决或结算时，必须把该 Match 的事件收敛到**单一权威定序点**——引入一个 MatchActor（对该 Match 所有事件排队结算），或将整场对抗收敛到单个 SessionActor 承载。在此之前，不实现 MatchActor（见「非目标」：不引入分布式 Match 集群；单机单 actor 定序已足够 v1）。

## 3. 帧同步设计（v1：服务器权威 tick + 快照）

关键原则：**服务器永远推进，不为慢端暂停；慢端用快照追赶。**

### 3.1 Tick 时钟

- 固定 tick：`tickDurationMs`（默认 66ms，与推荐区间 50~66 统一，最终值以压测为准）。
- 服务器为每个 Session 维护单调递增 `tickId`（从 0 开始），由 `PeriodicTimer`（单调时钟）驱动。
- 所有下发数据都带 `serverTimeMs`（用于客户端测漂移）；`tickId` 随 `snapshot` 下发（`welcome` / `pong` 不含）。
- 超时策略（阶段二）：tick 处理耗时超过 `tickTimeoutMs`（默认 250ms）计入 `TotalTickOverruns` 观测指标——只检测不取消；GameModule 抛异常计入 `TotalTickErrors`，tick 丢失但 actor 存活，诊断照常推进。

### 3.2 输入缓冲（处理平台抖动/乱序）

- `inputDelayTicks`：平台事件进入后被调度到 `tickId + inputDelayTicks` 统一结算。
- 事件排序规则必须稳定（建议：`(ingestTimeMs, platformEventId)`）。

### 3.3 下行：权威状态快照

- 服务器运行 Game Module（权威逻辑）。
- 周期性下发 `StateSnapshot`（当前实现为 `snapshot`）：
  - 简化起见 v1 为全量状态 JSON
  - 后续可扩展增量/压缩

### 3.4 每客户端 ACK 与追赶

客户端周期性发送：

- `ClientAck { sessionId, clientId, lastAppliedTickId }`

服务器侧：

- 若 `serverTick - lastAckTick > maxLagTicks`：
  - 下发 `snapshot(forced=true, reason="lag")` 强制对齐
  - 通过 `resyncCooldownMs` 避免抖动/刷屏式 resync

**快照版本与大小上限（阶段三）**：

- 每张快照带 `v = 1`（schema 版本）；客户端遇到未知版本必须丢弃该快照并保持现有 ACK 行为（lag 自然增长触发服务器强制 resync，形成闭环）。
- 序列化后的 state 超过 `maxSnapshotBytes`（默认 1MB）时跳过本 tick 的发送并计入 `chorus_snapshots_oversize_total`；客户端因收不到快照而停止 ACK → lag 超限 → 状态缩回上限后由强制 resync 对齐。

### 3.5 背压与降采样（直播弹幕场景必须有）

- 每 tick 事件上限：`maxEventsPerTick`
- 超限策略（按玩法取舍）：
  - 合并：同一用户的同类事件合并（点赞合并、礼物叠加）
  - 分层：核心事件优先，聊天类可转为“展示用通道”

### 3.6 事件生命周期与投递四态

事件从进入系统到生效，经过六个阶段：

```
Received → Validated → Accepted → Scheduled → Applied → Published
 (入口)     (验签)      (映射+去重)  (调度到tick)  (引擎结算)  (快照下发)
```

| 阶段 | 落点 | 失败语义 |
| --- | --- | --- |
| Received | ingress 收到原始请求 | 请求体/限流异常 → 拒绝（HTTP 4xx/413/429） |
| Validated | `IPlatformEventVerifier` 验签 | 未通过 → 401，fail-closed（生产默认拒绝） |
| Accepted | mapper 映射 + faction 规则 + 去重占位 | 映射失败 → 400；重复 → `duplicated=true` 幂等返回 |
| Scheduled | SessionActor 调度到 `tickId + inputDelayTicks` | 内存内排队，超限走 3.5 背压策略 |
| Applied | tick 结算，reducer 归并后进入 GameModule | 归并不丢计数（分组键守恒），超预算可丢表现型事件 |
| Published | 快照下发给客户端 | 发布失败按四态 `Failed` 处理 |

**投递四态**（描述一次投递的结局，是观测/排障用语，不是存储字段）：

- `Received`：已收到，尚未验签。
- `Accepted`：验签+映射+去重通过，已进入调度（ingress 返回 200 的含义）。
- `Applied`：已在某个 `tickId` 结算进权威状态（之后客户端可通过 ACK 确认）。
- `Failed`：验签失败 / 映射失败 / IPC 发布失败。**关键约束：`Failed` 绝不能被记成"已见"**——去重采用 reserve/commit/rollback 协议，发布失败即 `Rollback`，平台重试按新投递受理，而不是被判重复丢弃。

**去重 reserve/commit/rollback**（review P0-7）：`EventDeduplicator.TryReserve` 原子占位（并发同 id 恰有一个赢家）→ 发布成功 `Commit`（Reserved→Delivered，TTL 内抑制重复）→ 发布失败 `Rollback`（释放占位，Failed 可重试）。TTL 默认 600s（`Ingress:DedupTtlSeconds`），容量安全阀保证内存有界。

**事件四分类**（决定超限时的取舍策略，见 3.5）：

| 分类 | 例子 | 超限策略 |
| --- | --- | --- |
| 可累计 | 点赞、礼物 | 归并保计数（分组键 `(S,M,F,U[,GiftId])`），永不因归并丢数 |
| 不可丢 | 关键礼物触发、口令指令 | 优先保留，禁止静默丢弃 |
| 表现型 | 普通弹幕、进场特效 | 可丢弃/转展示通道，丢弃计入指标 |
| 控制 | 管理/开关指令 | 独立通道，不与观众事件竞争预算 |

## 4. 客户端协议（v1）

见：`/reference/protocol.html`

## 5. 推荐阅读顺序

如果你正在处理“不同步、追帧不稳定、慢端恢复困难”等问题，建议按下面顺序阅读：

1. [整体架构](./architecture.zh.md)
2. [帧同步设计](./frame-sync.zh.md)
3. [帧同步最佳实践](./frame-sync-best-practices.zh.md)
4. [客户端追同步状态机](./client-resync-state-machine.zh.md)
5. [参数调优](../reference/tuning.zh.md)
6. [旧服问题分析](./legacy-real-server-analysis.zh.md)
