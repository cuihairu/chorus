---
title: Frame Sync (Authoritative Tick + Snapshot)
---

<p align="center">
  English | <a href="frame-sync.zh.md">简体中文</a>
</p>

# Frame Sync (Authoritative Tick + Snapshot)

This document gives chorus's **frame sync semantic plan**: where the frame number comes from, how it is delivered with messages, the client using the frame number as the basis for rendering/ordering, how out-of-order and dropped frames are backfilled, how reconnection and replay are aligned, and how the client's rhythm catches up.

> Status: **plan documented first, no code written in this round**. Every claim in this document carries a `file:line` or source; parts not measured in this repo are explicitly marked `未实测`.

---

## 1. Industry Research (With Sources)

Live danmaku systems generally **do not natively have a "frame number"** — danmaku messages carry a **timestamp** (milliseconds or ISO 8601), and the client sorts by timestamp. Frame number/tick is a **game-layer** concept, produced by the authoritative server. Sources are given item by item below.

### 1.1 Bilibili

- **Web reverse-engineering channel** (unofficial, legal risk see §1.4): danmaku messages carry a **millisecond timestamp**. blivedm's `DanmakuMessage` has `timestamp: int`, with the docstring "timestamp (milliseconds)", taken from the protocol's `info[0][4]`.
  Source: [blivedm models/web.py](https://raw.githubusercontent.com/xfgryujk/blivedm/dev/blivedm/models/web.py) (`DanmakuMessage` dataclass field table).
- **Open platform channel** (official): [open-live.bilibili.com](https://open-live.bilibili.com/); blivedm's `open_live` interface connects to that platform's WebSocket (`wss://live-open.biliapi.com`), with events including `DANMU_MSG`/`SEND_GIFT`/`INTERACT_WORD_V2` etc.
  Source: [blivedm README](https://github.com/xfgryujk/blivedm) (lists the two interfaces `web端` and `B站直播开放平台`).
- **Ordering guarantee**: the Bilibili danmaku protocol itself **has no frame number/sequence number**, only timestamps; ordering is inferred by the client from timestamps + arrival order. This is exactly why chorus introduces the frame number at the game layer (see §2.1).

### 1.2 YouTube Live Chat (Official API)

- Each message carries `snippet.publishedAt` (`datetime`, ISO 8601), described as "The date and time when the message was originally published".
  Source: [YouTube liveChatMessages resource docs](https://developers.google.com/youtube/v3/live/docs/liveChatMessages).
- The polling rhythm is controlled by `pollingIntervalMillis` (`unsigned integer`) in the `liveChatMessages.list` response, described as "how long your API client should wait before requesting additional results".
  Source: [YouTube liveChatMessages/list method docs](https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list).
- **Ordering guarantee**: again only timestamps, no frame number; the official recommendation is to sort by `publishedAt` + control frequency by `pollingIntervalMillis`.

### 1.3 Twitch (Official EventSub WebSocket)

- WebSocket message types: `session_welcome` / `session_keepalive` / `notification` / `session_reconnect` / `revocation`.
  Source: [Twitch EventSub WebSocket reference](https://dev.twitch.tv/docs/eventsub/websocket-reference/).
- **Reconnection**: `session_reconnect` is delivered 30 seconds before edge node failover, carrying `reconnect_url`; "Use this URL as is; do not modify it. The connection automatically includes the subscriptions from the old connection." The old connection keeps delivering events until the new connection receives `session_welcome`.
  Source: [Twitch Handling WebSocket events](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/).
- **Ordering guarantee**: Twitch messages have `metadata.message_id` and `metadata.message_timestamp` (UTC), but **no monotonic frame number**; after reconnection, resumption relies on `reconnect_url` + `message_id` deduplication.

### 1.4 Open-Source Implementations

- **blivedm** (Python, Bilibili danmaku/gift/enter): [github.com/xfgryujk/blivedm](https://github.com/xfgryujk/blivedm). Connects to both the web reverse-engineering and official open-live channels; event model see `blivedm/models/`.
- **bilibili-API-collect** (reverse-engineering doc collection, 20.2k★): [github.com/SocialSisterYi/bilibili-API-collect](https://github.com/SocialSisterYi/bilibili-API-collect) (`docs/live/message_stream.md` records wss `/sub`, auth packet sent within 5 seconds, heartbeat 30s/60s drop, 16-byte packet header, op 2/3/5/7/8). **Note**: that project received a lawyer's letter from Bilibili on 2026-01-28, and archived/deleted the repo on 2026-01-30 (verifiable in the original repo's README) → the web reverse-engineering channel has a real precedent of legal action.
- **OpenDANMU**: a GitHub search only finds [zhengcai/opendanmu](https://github.com/zhengcai/opendanmu) (a C# "third-party danmaku tool", 0 stars, unmaintained since 2022-01), **not a frame sync solution**. As a frame sync reference implementation: **no public sources found**.
- **Public articles on frame sync in danmaku games**: **no public sources found** (no public architecture article on "live danmaku + frame sync" was found; known competitors mostly do immediate mapping of "danmaku event → game input", without frame-level interpolation).

### 1.5 What the Research Implies for chorus

| Platform/Implementation | Ordering Basis | Packet-Loss Recovery | Reconnection | Implication for chorus |
| --- | --- | --- | --- | --- |
| Bilibili web reverse-engineering | Millisecond timestamp | None (UDP-like; lost is lost) | Re-authenticate on reconnect | Ordering must be rebuilt at the game layer |
| YouTube | `publishedAt` + `pollingIntervalMillis` | Naturally covered within the polling window | Re-pull | The official channel has explicit rate control |
| Twitch | `message_id` + `message_timestamp` | `reconnect_url` resumption | Official reconnect flow | The resumption semantics can be borrowed |
| blivedm | Timestamp | None | Reconnect | Event normalization reference |

**Common ground**: upstream only provides timestamps; **the frame number is produced by chorus's own game layer**. This is consistent with the current state of this repo (`tickId` already increases monotonically within the engine, see §2.1).

---

## 2. Current State (Implemented, file:line)

The frame sync **skeleton on the engine side is already in the code**; this section gives evidence item by item.

| Capability | Evidence |
| --- | --- |
| Frame number (tickId) monotonically increasing | `src/Chorus.Engine/Session/SessionActor.cs:52`(the `TickId` property)、`:391`（`TickId++`） |
| Default tick period 66ms (≈15.15 tick/s) | `src/Chorus.Engine/Session/SessionConfig.cs:4`（`TickDurationMs = 66`） |
| Input delay: events scheduled to `tickId + inputDelayTicks` | `SessionConfig.cs:5`（`InputDelayTicks = 2`）、`SessionActor.cs:379`（`targetTick = TickId + _config.InputDelayTicks`） |
| Feed events to the game module every tick | `SessionActor.cs:444`（`_game.ApplyEvents(TickId, tickEvents)`） |
| Periodic snapshot: once every `snapshotEveryTicks` | `SessionActor.cs:447`（`SnapshotEveryTicks > 0 && (TickId % SnapshotEveryTicks) == 0`） |
| Snapshot carries the frame number `tickId` | `src/Chorus.Protocol/Messages/ServerSnapshot.cs:8`（`long TickId`）、`SessionActor.cs:478`/`:497`/`:526`(the three send paths) |
| Snapshot schema version `V = 1` | `ServerSnapshot.cs:20`(`int V = 1`; the comment refers to versioning.md §2) |
| ACK: the client reports `lastAppliedTickId` | `SessionActor.cs:156`(`AckAsync`)、`:346-371`(monotonicity guard)、`:371`(updates `LastAckTickId`) |
| Forced resync: delivered when behind > `maxLagTicks` | `SessionConfig.cs:8`(`MaxLagTicks = 50`)、`SessionActor.cs:550-569`(lag check + cooldown)、`:500`/`:529`(`Forced=true`) |
| resync rate limiting | `SessionConfig.cs:9`（`ResyncCooldownMs = 2000`）、`SessionActor.cs:564` |
| Client connection baseline = current frame | `SessionActor.cs:314-315`（`ConnectedTickId = TickId`、`LastAckTickId = TickId`） |
| Event deduplication | `src/Chorus.Server/Program.cs:73-77`(`EventDeduplicator`, `DedupTtlSeconds` default 600) |
| Bounded event channel + bounded wait | `SessionActor.cs:75`（`MaxPendingEvents`）、`:175`（`EventEnqueueMaxWaitMs`） |
| Per-tick event cap | `SessionConfig.cs:7`（`MaxEventsPerTick = 200`） |
| Snapshot byte cap | `SessionConfig.cs:21`（`MaxSnapshotBytes = 1_048_576`） |
| **No persistence = no replay** | `docs/reference/versioning.md:72-74`("session state is pure in-memory, explicitly out of scope") |

**Conclusion**: frame number generation, delivery with snapshots, ACK, forced resync, backpressure, deduplication — **all implemented**. The gaps are in §3.

---

## 3. Gaps (Planned / Not Implemented)

| # | Gap | Current-State Evidence | Status |
| --- | --- | --- | --- |
| G1 | The client cannot get "which frame each event belongs to" | The snapshot only carries `tickId` (`ServerSnapshot.cs:8`), without per-event frame attribution; events are drained per tick and then deleted (`SessionActor.cs:413-415`) | **Implemented (P2)**: `frameEvents` envelope field (§4.2) |
| G2 | No frame history buffer | The engine only keeps the current tick's events; no ring buffer | **Implemented (P2)**: `FrameHistory` byte-capped ring buffer (§4.3) |
| G3 | No client-side "fetch a given frame" message | The wire protocol only has server→client `snapshot`; `ClientAck` only carries `lastAppliedTickId` as a progress field (plus `sessionId`/`clientId`/`clientTimeMs`, `src/Chorus.Protocol/Messages/ClientAck.cs:3-9`) | **Implemented (P2)**: `resync` message + full IPC chain (§4.3) |
| G4 | No replay/recording alignment | No persistence (`versioning.md:72-74`); snapshots are not persisted to disk | **Explicitly not doing it** (unless the snapshot-restore contract lands first) |
| G5 | No client SDK | Everything in src in this repo is server-side; no client library | **Implemented (P3)**: `Chorus.Client.ChorusClient` (minimal entry: handshake + callback + ACK sampling + resync cooldown; no auto-reconnect, see [Quickstart](../guide/quickstart-frame-sync.md)) |
| G6 | Client rhythm/interpolation untested | No runnable client implementation | **Untested** |

---

## 4. Plan

### 4.1 Frame Number Semantics

**Frame number ≡ tickId** (server-monotonic `long`, +1 per tick, `SessionActor.cs:391`).

- **No separate frame id introduced**: avoid dual clocks (when frame id and tickId drift, the client would be at a loss). tickId is already authoritative, monotonic, and delivered with snapshots — reuse it directly.
- **Frame = tick**: one frame is one tick beat. `tickId` is the frame number.
- The client uses the frame number as the basis for rendering and ordering: receive `snapshot(tickId=F)` → render frame F; the authoritative state of frame F is that snapshot's `state`.

### 4.2 How the Frame Number Is Delivered with Messages

The frame number **is already** delivered with snapshots (`ServerSnapshot.cs:8`). What needs to be added is **per-event frame attribution** (G1) and **client-side replay by frame number**.

**P1 — Frame Number Semantics + Client Replay by Frame Number (Implemented)**

- Frame number = `tickId`, delivered with snapshots; the client sorts and renders by frame number (§4.1).
- Client replay state machine `src/Chorus.LoadTool/FramePlayer.cs`: frame number ≤ applied frame → drop (idempotent; the replayed window will not render twice); frame number > applied frame + 1 → apply and report the first gap frame number for `resync` to fetch. Unit tests `FramePlayerTests` (7); `ClientSimulation` is wired (`LoadTool` outputs the `resyncs=` count).
- The `state.frameEvents` convention is **deprecated**: the original plan stuffed frame attribution into `state` (zero protocol changes); P2's envelope field is the better solution (a first-class protocol field, so the client need not know the `state` convention), and G1 was closed directly with P2.

**P2 — Protocol Extension (Additive Optional, Implemented)**

- Add an **optional** `frameEvents` envelope field to `ServerSnapshot` (`List<FrameEvent>?`, default null → omitted on the wire).
- Per `versioning.md:60-65`: adding an optional field to the wire protocol = allowed (allowed for both 0.x and 1.0+).
- **Do not bump `v`**: additive optional does not change field semantics; `v` is only bumped when a field's **semantics change** (`versioning.md:42-43`: rejecting unknown v + forced resync to be decided when the first v2 is designed).
- Needs re-check per `versioning.md:43`: this field is additive and does not trigger the "reject unknown v" semantics.
- **Implementation**: `src/Chorus.Protocol/Messages/FrameEvent.cs` (`FrameId` / `EventId` / `Kind` / `UserId`); `SessionSnapshot.FrameEvents`; `WireSnapshot.cs` carries it to the wire (null → omit). `SessionActor.DrainFrameEvents` drains the accumulator once per snapshot beat (accumulated per tick, dropping the oldest beats when capped at `MaxPendingFrameEvents = 4096`). Unit tests: `WireSnapshotFrameEventsTests` (omission / camelCase / old-JSON compatibility).

### 4.3 Out-of-Order / Dropped Frames

**Client-side gap detection**: track `lastAppliedFrameId`; a received snapshot's `tickId` not matching expectations (`tickId > lastAppliedFrameId + 1`) → frames were dropped in between. Implementation see `Chorus.LoadTool.FramePlayer` (`FramePlayerTests` covers: consecutive/duplicate/replay drop/gap reporting).

**Frame backfill strategy**:

| Phase | Strategy | Notes |
| --- | --- | --- |
| P1 (no history) | Wait for the next periodic snapshot / trigger forced resync | **Cannot backfill historical frames** — the engine has no history (G2). Existing mechanisms: periodic snapshot (`SessionActor.cs:447`) + forced resync (`:550-569`). Honest marking: dropped frame = skipped frame; the client aligns directly to the latest frame. |
| P2 (implemented) | Engine frame history ring buffer + client fetch | See below. Unit + e2e coverage (`FrameHistoryTests` / `SessionActorFrameHistoryTests` / `WireSnapshotFrameEventsTests`). |

**P2 backfill implementation (landed)**:

- Engine side: per-session frame history ring buffer (in memory), `src/Chorus.Engine/Session/FrameHistory.cs`.
- **Capped by bytes, not by frame count**: N beats × average snapshot bytes is uncontrolled; in the worst case `MaxSnapshotBytes` (1 MiB, `SessionConfig.cs:21`) × N would blow up memory, so it is byte-capped — `Session:FrameHistoryBytes` (default 8 MiB/session), evicting the oldest beyond the cap, **but not evicting the just-added frame** (the eviction condition in `Add` excludes the newest item); when a single frame exceeds the entire capacity, it is kept as the session's only frame.
  - Memory estimate (untested): 8 MiB/session × `MaxSessions` (200, `SessionConfig.cs:17`) = worst case 1.6 GiB. **The cap needs to be set after measuring the average snapshot byte count**; `FrameHistoryBytes = 0` disables it entirely (window queries always miss).
- Client side: a new `resync` message type (client→server) carrying `fromFrameId`; `ProtocolTypes.Resync = "resync"` and the `ClientResync` model.
  - Per `versioning.md:60-65`: adding a message type to the wire protocol = allowed.
  - The engine replays back the snapshot sequence within the `[fromFrameId, current tickId]` window; the window query rejects **partial replay** (`fromFrameId` older than the oldest frame → miss, because replaying from the retained tail would hide gaps).
  - Gaps outside the window (already evicted / history disabled) → replay the latest snapshot + `forced=true` + `reason="resync-gap"`, and the client aligns to the latest.
- **Replay goes through the direct-send channel**: `OutboundClientQueue.SendReplayAsync` bypasses the capacity-1 latest-wins slot (DropOldest would squeeze out the replayed frames leaving only the last one); it serializes frame-by-frame with periodic sends via `_sendGate`, and the whole batch is mutually exclusive via `_replayGate`; replay executes detached, so slow transport does not drag the tick. Periodic snapshots can still be dropped — **replayed frames cannot**.
- Diagnostic counters: `TotalResyncRequests` / `TotalResyncReplays` / `TotalResyncWindowMisses` / `TotalResyncThrottled` / `TotalResyncSnapshotsSent`、`FrameHistoryBytes`.
- **Full transport chain**: `StreamerWsFace` (`resync` branch + fixed-window rate limiting) → `IStreamerSessionBinding.ResyncAsync` → monolith `MonolithSessionRouter` (direct call to `SessionActor`) / gateway `IpcSessionRouter` (`GatewayIpcService.PublishResyncAsync` → `gw.resync` → `GatewayResyncCmd` → `EngineIpcService` → `SessionActor.ResyncAsync`).

**Idempotency and deduplication**:

- Event-level deduplication already exists: `EventDeduplicator` (`Program.cs:73-77`, `DedupTtlSeconds` default 600).
- Snapshot-level idempotency: the client deduplicates by `lastAppliedFrameId` — snapshots with a frame number ≤ the applied one are dropped directly (`protocol.md:121` already defines `lastAppliedTickId` as "the authoritative tick that has truly been applied").
- Duplicate snapshots within the backfill window: the same `tickId` is applied only once (idempotent by frame number).

**Backpressure strategy (implemented)**:

- Bounded event channel: `MaxPendingEvents` (`SessionConfig.cs:11`, default 8000).
- Bounded wait: must-not-lose events such as gifts additionally wait `EventEnqueueMaxWaitMs` when the channel is full (`SessionConfig.cs:13`, default 50ms); only dropped on timeout.
- Per-tick event cap: `MaxEventsPerTick` (`SessionConfig.cs:7`, default 200).
- Backpressure for backfill requests (P2, implemented, two layers):
  - Transport-layer fixed window: `Faces:StreamerWs:ResyncPerMinute` (default 30/min); over the limit, dropped directly and counted in `WsResyncDropped`.
  - Engine-layer per-client cooldown: `Session:ResyncRequestCooldownMs` (default 1000ms); repeated requests within the cooldown are counted in `TotalResyncThrottled` and return immediately. **It is not shared with the `ResyncCooldownMs` (2000ms) for server-pushed lag resync** — the former is the client pulling proactively, the latter is the engine pushing based on ACK judgment.

### 4.4 Reconnection / Replay

**Reconnection (resumption by frame number)**:

- Client reconnects → sends `hello` → the engine takes the **current tickId** as the baseline (`SessionActor.cs:314-315`: `ConnectedTickId = TickId`, `LastAckTickId = TickId`).
- **Resumption = starting from the latest frame**, no historical backfill (there is no history to backfill, G2). This is consistent with the Twitch `reconnect_url` semantics of "old subscriptions keep delivering, the new connection resumes" ([Twitch docs](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/)), but chorus has no cross-connection subscription state — reconnecting is a new baseline.
- **P2 landed**: after reconnecting, the client can directly send `resync { fromFrameId }` to fetch historical frames within the window; outside the window (already evicted), the latest snapshot + `forced=true`/`reason="resync-gap"` is replayed to align to the latest. Reconnection still takes the **current tickId** as the baseline; resync is optional frame backfill and does not change the baseline semantics.

**Replay / recording alignment**:

- **Explicitly not doing it**. No persistence (`versioning.md:72-74`); snapshots are not persisted to disk, so there is no frame sequence to align with.
- If replay is wanted in the future, the prerequisite is: land the snapshot-restore contract first (`docs/design/snapshot-restore.md`), then talk about frame-based alignment.
- The industry approaches to replay alignment (Twitch `reconnect_url`, YouTube polling windows) are all **live resumption**, not **recording alignment**; for recording alignment in danmaku games, **no public sources found**.

### 4.5 Client Rhythm

**Render frame rate vs tick period**:

- Rendering at 60fps (16.67ms/frame) vs tick 66ms (≈15.15 tick/s) → **1 tick ≈ 3.96 render frames**.
- The render layer interpolates between frames F-1 and F (untested; no client SDK in this repo, G5/G6).

**Catch-up strategy**:

| Situation | Strategy | Basis |
| --- | --- | --- |
| Behind by ≤ `maxLagTicks` (50) | Controlled catch-up: speed up the rendering/interpolation rhythm | `protocol.md:135` "under light lag, controlled catch-up may be done" |
| Behind by > `maxLagTicks` | Wait for the forced resync snapshot and align immediately | `SessionActor.cs:550-569` + `protocol.md:104-105` "after receiving a forced snapshot, one should not continue backfilling historical frames for a long time" |
| Ahead | Do not render beyond the last two frames | `protocol.md:136` "under heavy lag, one should wait for or request the latest snapshot" |

**Untested**: the strategies above **have no client implementation in this repo to verify** (G5/G6). After the SDK lands in P3, the actual deviation between the tick period and the render frame rate needs to be measured.

---

## 5. Phasing and Acceptance Criteria

| Phase | Content | Acceptance Criteria |
| --- | --- | --- |
| **P1** | Frame number semantics documented + client replay-by-frame-number state machine | **Completed**: frame number = tickId documented (§4.1); the client replay state machine `FramePlayer` landed and used by `LoadTool`, `FramePlayerTests` 7 cases (consecutive/duplicate/replay drop/gap reporting); real /ws e2e end-to-end smoke passed. The `state.frameEvents` convention is deprecated (replaced by the P2 envelope field) |
| **P2** | Frame history ring buffer + `resync` fetch message + snapshot `frameEvents` envelope field | **Completed**: unit tests `FrameHistoryTests` (7: backfill window / byte-by-byte eviction / oversized frame retention / zero-capacity disable) + `SessionActorFrameHistoryTests` (6: window-hit replay / window miss → `resync-gap` / cooldown throttling / unknown client / `frameEvents` carries the applied frame number / omitted when no events) + `WireSnapshotFrameEventsTests` (5: wire omission / camelCase / old-JSON compatibility / `ClientResync` deserialization / real /ws resync backfill e2e); full suite 260 green |
| **P3** | Client SDK (entry + one callback) | **Completed**: `Chorus.Client` three-line integration (`ChorusClient.ConnectAsync` + one callback); the callback receives a state strictly increasing by frame number; `ChorusClientTests` 3 cases (real /ws render+ACK, idempotent replay under callback blocking, handshake failure throws) + `FramePlayerTests` 7 cases; full suite 270 green |

---

## 6. Related Pages

- [Protocol Reference](../reference/protocol.md) — `snapshot` / `ack` / `hello` / `welcome` messages
- [Versioning Policy](../reference/versioning.md) — the `state` schema version `v`, compatibility rules
- [Config Reference](../reference/config.md) — `Session:*` parameters (`tickDurationMs` / `maxLagTicks`, etc.)
- [Quickstart Frame Sync](../guide/quickstart-frame-sync.md) — N-line integration
- [API Reference](../reference/api.md) — backend/admin endpoints
