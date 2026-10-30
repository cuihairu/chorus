---
title: 旧版服务端问题分析
---


# 旧版服务端问题分析（real-server）

本文仅讨论"问题识别 + 根因总结"；不复制旧项目的业务/玩法实现。

> **来源说明（2026-10-13 审计）**：本文引用的旧项目 `real-server` 源码**不在此仓库中**。仓库内 `src/` 与 `tests/` 对 `MultGameInfo` / `FRAME_INTERVAL` / `ServerLogic` / `MaxServerFrame` 等符号的命中数为零。下文断言无法在本仓库内验证，**均转述自外部项目**；须待取得 real-server 源码后方可复核。

## 现有"帧同步"如何实现

在旧项目 `real-server` 中，`server1/server.gamelogic/Rooms/MultGameInfo.cs` 的 `MultGameInfo.processFrame()` 按固定间隔打包"交互输入"：

- 服务端以 `ConstValue.FRAME_INTERVAL = 100ms`（约 10Hz）生成逻辑帧。
- 每帧从 `m_gameInputDataList`（观众弹幕/礼物等输入）尽可能多地出队，形成 `frameData`：
  - `{"type":"frameData","id":<frameId>,"list":[ ...events ]}`
- 每帧数据存入 `m_frameDataMap[frameId]`，供客户端通过 `reqFrameData` 拉取补充帧（重连/丢帧）。
- 客户端通过 `reportFrame` 上报本地帧号，服务端用"追赶/暂停"逻辑尽量使两端帧差不至于过大。

核心结论：这是"事件锁步 / 输入帧广播"，而非"服务端权威模拟 + 状态同步"。

## 关键问题（导致帧失步/重同步不稳定）

### 1）服务端以"时间驱动"方式发帧，但缺少严格的 tick 时钟与漂移控制

相关逻辑（旧项目 `real-server`）：

- `server1/server.gamelogic/ServerLogic.cs`：主循环用 `Thread.Sleep(50)` 轮询 `GameRooms.Pulse()`。
- `server1/server.gamelogic/Rooms/MultGameInfo.cs`：用 `m_lastFrameTime` + `FRAME_INTERVAL` 决定是否生成下一帧。

问题点：

- 主循环不是"固定 tick"，而是"尽力而为的轮询"；负载升高时 GC、日志阻塞、IO 等会让一次 Pulse 变慢，帧生成出现抖动与"帧追赶爆发"。
- `m_lastFrameTime += FRAME_INTERVAL` 的追赶方式会在长时间停顿后连续生成补充帧（短时间内大量 `frameData`），极易压垮客户端/网络，进一步加剧延迟并形成恶性循环。

### 2）重同步策略选择"暂停服务端"，拖垮了快的一方

相关逻辑：`MultGameInfo.processFrame()` 中基于 `m_Room1FrameID/m_Room2FrameID`（帧差阈值 `MaxServerFrame = 30`）的 `serverPause` 判定。

问题点：

- 一方客户端卡顿/断线触发服务端停止发新帧，导致另一方也无法推进（观感即"双方一起卡死"）。
- 在直播对战场景，更合理的方式是"服务端持续推进 + 慢的一方通过快照追赶"，而非"全局等待最慢的一方"。

### 3）缺少清晰的"会话开始（tick 0）协议"与一致的节拍收敛机制

当前状态下，帧号更像"服务端自增编号"，不同时提供：

- tick 对应的服务端时间（或 tickStartTime）
- tickDuration 的权威配置及客户端如何校准
- 客户端应依据"收到的 tick"还是"本地定时器"推进

仅依赖 `reportFrame` 做外环控制，容易导致：

- 各客户端各自按本地时间运行，漂移越来越大
- 丢帧/暂停恢复时，双方偏差被放大

### 4）每帧事件数无硬上限，容易形成超大包与"帧爆炸"

相关逻辑：`processFrame()` 中 `count = m_gameInputDataList.Count`，然后尝试出队 `count` 次。

问题点：

- 高峰期（礼物连击/刷屏）大量事件被塞进单帧，单个 `frameData` 变得巨大（分片、序列化开销上升）。
- 客户端需在单帧处理大量事件，造成卡顿 → 进一步落后 → 触发重同步/暂停。

### 5）补充帧存储无上限，且缺少"快照"概念

相关逻辑：`m_frameDataMap` 持续增长；`SyncFrameMsg()` 只能按区间重放帧数据。

问题点：

- 内存随对战时长线性增长。
- 重连恢复只能"重放大量帧"；没有"状态快照 + 少量增量重放"的快速恢复路径。

### 6）入站/出站消息跨线程队列，顺序/背压不可控，排障困难

当前状态用多线程队列混入平台事件、服务端消息与客户端消息一起处理。

问题点：

- 缺少按客户端的背压策略（处理跟不上时，是降采样还是强制快照）。
- 缺少系统的可观测性指标（tick 延迟、队列长度、ack、丢弃数等），排障成本高。

## 根因总结（一句话）

旧实现把"直播交互输入"当作锁步帧事件广播，却没有把 tick、背压、快照与重连恢复设计成一个闭环系统；同时采用"暂停服务端等待慢的一方"策略，在高负载与抖动场景下天然容易失控。
