---
title: Legacy Server Problem Analysis
---

<p align="center">
  English | <a href="legacy-real-server-analysis.zh.md">简体中文</a>
</p>

# Legacy Server Problem Analysis (real-server)

This document is only about "problem identification + root cause summary"; it does not replicate the business/gameplay implementation of the old project.

> **Source Note (2026-10-13 Audit)**: The `real-server` source code of the old project cited in this document is **not included in this repository**. Within the repo, `src/` and `tests/` contain zero hits for symbols such as `MultGameInfo` / `FRAME_INTERVAL` / `ServerLogic` / `MaxServerFrame`. The assertions below cannot be verified within this repo and are **relayed from the external project**; they can only be re-checked once the real-server source code is obtained.

## How the existing "frame sync" is implemented

In the old project `real-server`, `MultGameInfo.processFrame()` in `server1/server.gamelogic/Rooms/MultGameInfo.cs` packs "interaction inputs" at fixed intervals:

- The server generates logic frames at `ConstValue.FRAME_INTERVAL = 100ms` (about 10Hz).
- It dequeues `m_gameInputDataList` (viewer danmaku/gift and other inputs) "as much as possible" on every frame, forming `frameData`:
  - `{"type":"frameData","id":<frameId>,"list":[ ...events ]}`
- The per-frame data is stored in `m_frameDataMap[frameId]`, used for clients to pull supplementary frames via `reqFrameData` (reconnect/missing frames).
- The client reports its local frame number via `reportFrame`, and the server uses "catch-up/pause" logic to try to keep the frame difference between the two ends from becoming too large.

Core conclusion: this is "event lockstep / input frame broadcast", not "server-authoritative simulation + state synchronization".

## Key problems (causing frame desync/unstable resync)

### 1) The server sends frames in a "time-driven" manner, but lacks a strict tick clock and drift control

Related logic (old project `real-server`):

- `server1/server.gamelogic/ServerLogic.cs`: the main loop uses `Thread.Sleep(50)` to poll `GameRooms.Pulse()`.
- `server1/server.gamelogic/Rooms/MultGameInfo.cs`: uses `m_lastFrameTime` + `FRAME_INTERVAL` to decide whether to generate the next frame.

Problem points:

- The main loop is not a "fixed tick", but "best-effort polling"; when load increases, GC, log blocking, IO, etc. make one Pulse slower, frame generation produces jitter and "frame catch-up bursts".
- The catch-up approach of `m_lastFrameTime += FRAME_INTERVAL` produces consecutive supplementary frames after a long pause (a large amount of `frameData` in a short time), which can easily overwhelm the client/network, further causing lag and a vicious cycle.

### 2) The resync strategy chose to "pause the server", which drags down the fast end as well

Related logic: in `MultGameInfo.processFrame()`, the `serverPause` determination based on `m_Room1FrameID/m_Room2FrameID` (frame difference threshold `MaxServerFrame = 30`).

Problem points:

- One client stuttering/disconnecting triggers the server to stop sending new frames, causing the other client to also be unable to proceed (the perceived effect is "both sides freeze together").
- In a live-stream battle scenario, the more reasonable approach is "server keeps advancing + laggard end catches up via snapshots", rather than "globally waiting for the slowest end".

### 3) Lack of a clear "session start (tick 0) protocol" and a consistent beat convergence mechanism

In the current state, the frame number is more like a "server self-incrementing number", and does not simultaneously provide:

- The server time corresponding to the tick (or tickStartTime)
- Authoritative configuration of tickDuration and how the client calibrates it
- Whether the client should advance based on the "received tick" or the "local timer"

Relying only on `reportFrame` for outer-loop control easily leads to:

- Each client running on local time, drifting further and further apart
- When frames are dropped/pause recovery happens, the deviation of both sides is amplified

### 4) No hard cap on the number of events per frame, easily forming oversized packets and "frame explosion"

Related logic: in `processFrame()`, `count = m_gameInputDataList.Count`, and then it tries to dequeue `count` times.

Problem points:

- During peak periods (gift combos/spamming), a large number of events are stuffed into a single frame, and a single `frameData` becomes huge (fragmentation, serialization overhead increases).
- The client has to process a large number of events in one frame, causing stuttering -> falling further behind -> triggering resync/pause.

### 5) Supplementary frame storage is unbounded, and lacks the "snapshot" concept

Related logic: `m_frameDataMap` keeps growing; `SyncFrameMsg()` can only replay frame data by range.

Problem points:

- Memory grows linearly with battle duration.
- Reconnect recovery can only "replay many frames"; there is no fast recovery path of "state snapshot + small amount of incremental replay".

### 6) Inbound/outbound messages cross-thread queues, ordering/backpressure is uncontrollable, and debugging is difficult

The current state uses multi-threaded queues to mix platform events, server messages, and client messages for processing.

Problem points:

- Lack of a per-client backpressure strategy (when processing cannot keep up, should it be downsampling or forced snapshot).
- Lack of systematic observability metrics (tick latency, queue length, ack, dropped count, etc.), making troubleshooting costly.

## Root Cause Summary (in 1 sentence)

The old implementation treats "live-stream interaction inputs" as lockstep frame event broadcasting, but does not design tick, backpressure, snapshots, and reconnect recovery as a closed-loop system; meanwhile, it uses the strategy of "pausing the server to wait for the slow end", which is naturally prone to running out of control under high-load and jitter scenarios.
