---
title: 规格草案
---

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

## 3. 帧同步设计（v1：服务器权威 tick + 快照）

关键原则：**服务器永远推进，不为慢端暂停；慢端用快照追赶。**

### 3.1 Tick 时钟

- 固定 tick：`tickDurationMs`。
- 服务器为每个 Session 维护单调递增 `tickId`（从 0 开始）。
- 所有下发数据都带 `tickId` 与 `serverTimeMs`（用于客户端测漂移）。

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

### 3.5 背压与降采样（直播弹幕场景必须有）

- 每 tick 事件上限：`maxEventsPerTick`
- 超限策略（按玩法取舍）：
  - 合并：同一用户的同类事件合并（点赞合并、礼物叠加）
  - 分层：核心事件优先，聊天类可转为“展示用通道”

## 4. 客户端协议（v1）

见：`/reference/protocol.html`

## 5. 推荐阅读顺序

如果你正在处理“不同步、追帧不稳定、慢端恢复困难”等问题，建议按下面顺序阅读：

1. [整体架构](./architecture.md)
2. [帧同步设计](./frame-sync.md)
3. [帧同步最佳实践](./frame-sync-best-practices.md)
4. [客户端追同步状态机](./client-resync-state-machine.md)
5. [参数调优](../reference/tuning.md)
6. [旧服问题分析](./legacy-real-server-analysis.md)
