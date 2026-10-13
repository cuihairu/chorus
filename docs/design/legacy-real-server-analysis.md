---
title: 旧服问题分析
---

# 旧服问题分析（real-server）

本文件只做”问题定位 + 根因总结”，不复刻旧项目的业务/玩法实现。

> **来源说明（2026-10-13 审计）**：本文引用的旧项目 `real-server` 源码**未随本仓附带**，仓内 `src/` 与 `tests/` 对 `MultGameInfo` / `FRAME_INTERVAL` / `ServerLogic` / `MaxServerFrame` 等符号均为零命中。以下断言无法在本仓内证实，属**外部项目的转述**；取得 real-server 源码后方可复核。

## 现有“帧同步”的实现方式是什么

在旧项目 `real-server` 的 `server1/server.gamelogic/Rooms/MultGameInfo.cs` 里，`MultGameInfo.processFrame()` 以固定间隔打包“互动输入”：

- 服务器以 `ConstValue.FRAME_INTERVAL = 100ms` 生成逻辑帧（约 10Hz）。
- 将 `m_gameInputDataList`（观众弹幕/礼物等输入）在每帧“尽可能多”地出队，形成 `frameData`：
  - `{"type":"frameData","id":<frameId>,"list":[ ...events ]}`
- 将每帧数据保存在 `m_frameDataMap[frameId]`，用于客户端通过 `reqFrameData` 拉取补帧（重连/缺帧）。
- 客户端会通过 `reportFrame` 上报本地帧号，服务器用“追帧/暂停”逻辑试图让两端帧差不至于过大。

核心结论：这是“事件锁步（event lockstep）/输入帧广播”，而不是“服务器权威模拟 + 状态同步”。

## 关键问题（导致帧不同步/追帧不稳定）

### 1) 服务器用“时间驱动”发帧，但缺乏严格的 tick 时钟与漂移控制

相关逻辑（旧项目 `real-server`）：

- `server1/server.gamelogic/ServerLogic.cs`：主循环 `Thread.Sleep(50)` 轮询 `GameRooms.Pulse()`。
- `server1/server.gamelogic/Rooms/MultGameInfo.cs`：用 `m_lastFrameTime` + `FRAME_INTERVAL` 判定是否生成下一帧。

问题点：

- 主循环不是“固定 tick”，而是“尽力轮询”；当负载升高、GC、日志阻塞、IO 等导致一次 Pulse 变慢时，帧生成会产生抖动与“补帧突发”。
- `m_lastFrameTime += FRAME_INTERVAL` 的追赶方式，在长暂停后会产生连续补帧（短时间内大量 `frameData`），容易把客户端/网络打爆，进一步造成落后与恶性循环。

### 2) 追帧策略选择了“暂停服务器”，会把快端一起拖死

相关逻辑：`MultGameInfo.processFrame()` 里基于 `m_Room1FrameID/m_Room2FrameID` 的 `serverPause` 判定（帧差阈值 `MaxServerFrame = 30`）。

问题点：

- 一个客户端卡顿/掉线，会触发服务器停止发新帧，导致另一个客户端也无法前进（观感是“双方一起卡”）。
- 直播对战场景里更合理的是“服务器继续推进 + 落后端快照追赶”，而不是“全局等待最慢端”。

### 3) 缺少明确的“会话起点（tick 0）协议”和一致的节拍收敛机制

现状里，帧号更像“服务器自增编号”，并未同时给出：

- tick 对应的服务器时间（或 tickStartTime）
- tickDuration 的权威配置与客户端校准方式
- 客户端应以“收到的 tick”为准推进，还是以“本地计时器”为准推进

只靠 `reportFrame` 做外环控制，很容易出现：

- 客户端各自按本地时间跑，越跑越偏
- 遇到掉帧/暂停恢复时，双方偏差放大

### 4) 每帧事件数量无硬上限，容易形成超大包与“帧爆炸”

相关逻辑：`processFrame()` 里 `count = m_gameInputDataList.Count`，随后尝试出队 `count` 次。

问题点：

- 高峰期（礼物连击/刷屏）会把大量事件塞进单帧，单条 `frameData` 变得巨大（分片、序列化开销上升）。
- 客户端一帧要处理大量事件，造成卡顿 -> 更落后 -> 触发追帧/暂停。

### 5) 补帧存储无边界，且缺少“快照”概念

相关逻辑：`m_frameDataMap` 持续增长；`SyncFrameMsg()` 只能按区间回放帧数据。

问题点：

- 内存随战斗时长线性增长。
- 重连恢复只能“回放很多帧”，没有“状态快照 + 少量增量回放”的快速恢复路径。

### 6) 出入站消息跨线程队列，顺序/背压不可控，调试困难

现状采用多线程队列将平台事件、服务器消息、客户端消息混合处理。

问题点：

- 缺少 per-client 背压策略（处理不过来时，应该降采样还是强制快照）。
- 缺少体系化观测指标（tick 延迟、队列长度、ack、丢弃数等），排障成本高。

## 根因总结（1 句话）

旧实现把“直播互动输入”当作锁步帧事件广播，但没有把 tick、背压、快照与重连恢复设计成闭环系统；同时使用“暂停服务器等待慢端”的策略，天然容易在高负载和抖动场景下失控。
