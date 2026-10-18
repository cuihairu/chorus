---
title: Specification Draft
---


# Specification Draft (v1)

This specification is used to guide abstracting the legacy framework into an open-source "live-stream interactive danmaku game framework".

## 1. Goals and Non-Goals

### Goals

- Support 1 or more streamer clients (streaming companion/PC client) connecting to the Chorus Server.
- Ingest interaction events from platforms such as Douyin/Kuaishou (danmaku, gifts, likes, follows, etc.) and convert them into a unified `AudienceEvent` input stream.
- Center on a "reusable frame sync/state sync architecture" to support:
  - single-room (single streamer) gameplay
  - multi-room confrontation (multi-streamer faction/PK)
- Strong observability: being able to explain "why is it not in sync", and being able to self-heal automatically (snapshot catch-up, downsampling, etc.).

### Non-Goals (not done in the open-source version)

- Does not include any specific gameplay (stats, heroes, skills, art, config tables, etc.).
- Does not include private packet-capture/reverse-engineering capabilities of the platforms; only provides an "adapter interface + example stub (or documentation)".

## 2. Overall Architecture

### 2.1 Components

- **Client Gateway (WebSocket)**
  - Streamer client connection entry
  - Session management (authentication, reconnect, subscription)
  - Sending down snapshot/state
- **Platform Ingress (HTTP/Webhook)**
  - Receives platform pushes (or pulled by the adapter)
  - Signature verification/deduplication
  - Conversion into the unified `AudienceEvent`
- **Session Engine (Actor per Session)**
  - One single-threaded actor (mailbox model) per Session/Match, guaranteeing ordering and reasonableness
  - Input buffer, beat control, backpressure, snapshots
- **Game Module (plugin interface)**
  - Open source provides the interface and a minimal example (used to verify the "tick->state->snapshot" chain)

### 2.2 Data Model (Conceptual)

- `Session`: the game session corresponding to one live stream (single streamer).
- `Match`: a confrontation composed of multiple Sessions (e.g. a 2-streamer faction battle).
- `Faction`: a faction (usually bound to a streamer).
- `Audience`: audience players (platform userId).

The minimal landing approach in the current repository:

- `AudienceEvent` can carry `matchId` / `factionId`
- Ingress can complete `factionId` through explicit fields or through the "danmaku passphrase/gift ID -> faction" rules in `Ingress:FactionRules`
- The multiple session modules created by `SampleGameModuleFactory` share the same match-level state store, so sessions with the same `matchId` see the same in-match aggregated view

### 2.3 Match Authoritative Semantics Qualification (review P0-8 conclusion)

**`MatchStateStore` is a display-level shared aggregation, not an authoritative confrontation state.**

- It is written concurrently by multiple `SessionActor`s as each ticks (the internal `lock` only guarantees that a single read/write is not torn, and does not provide a total order across Sessions). Within the same Match, the relative order of writes from two Sessions depends on the advancement timing of their respective ticks, and is unstable under replay/concurrency.
- Therefore it is suitable for: **leaderboard display, big-screen aggregation, atmosphere-style gameplay** (scenarios like "combined contribution of both teams" that do not require precise adjudication).
- It is not suitable for: **fair confrontation adjudication**——win/loss determination, threshold-reached determination ("who reaches 100 likes first"), reward settlement, anti-cheat auditing. These semantics require events to have a globally determined order.

**Authorization conditions (when upgrading is mandatory)**: when the gameplay requires fair cross-faction adjudication or settlement, that Match's events must be funneled to a **single authoritative ordering point**——introduce a MatchActor (queueing and settling all events of that Match), or funnel the whole confrontation into a single SessionActor to carry it. Until then, do not implement a MatchActor (see "Non-Goals": no distributed Match cluster; a single actor ordering on a single machine is sufficient for v1).

## 3. Frame Sync Design (v1: server-authoritative tick + snapshot)

Key principle: **the server always advances and never pauses for a slow end; the slow end catches up with snapshots.**

### 3.1 Tick Clock

- Fixed tick: `tickDurationMs` (default 66ms, unified with the recommended range 50~66; the final value is determined by load testing).
- The server maintains a monotonically increasing `tickId` per Session (starting from 0), driven by `PeriodicTimer` (monotonic clock).
- All sent-down data carries `serverTimeMs` (used by the client to measure drift); `tickId` is sent down with the `snapshot` (not included in `welcome` / `pong`).
- Timeout policy (phase two): when tick processing time exceeds `tickTimeoutMs` (default 250ms) it is counted into the `TotalTickOverruns` observability metric——detection only, no cancellation; GameModule throwing an exception is counted into `TotalTickErrors`, the tick is lost but the actor survives, and diagnostics continue advancing as normal.

### 3.2 Input Buffer (Handling Platform Jitter/Out-of-Order)

- `inputDelayTicks`: platform events, once ingested, are scheduled to be settled together at `tickId + inputDelayTicks`.
- The event ordering rule must be stable (recommended: `(ingestTimeMs, platformEventId)`).

### 3.3 Downstream: Authoritative State Snapshots

- The server runs the Game Module (authoritative logic).
- Periodically sends down `StateSnapshot` (currently implemented as `snapshot`):
  - For simplicity, v1 is full-state JSON
  - It can later be extended to incremental/compression

### 3.4 Per-Client ACK and Catch-Up

The client sends periodically:

- `ClientAck { sessionId, clientId, lastAppliedTickId }`

Server side:

- If `serverTick - lastAckTick > maxLagTicks`:
  - Send down `snapshot(forced=true, reason="lag")` for forced alignment
  - Avoid jitter/spammy resync through `resyncCooldownMs`

**Snapshot Version and Size Cap (implemented)**:

- Each snapshot carries `v = 1` (schema version); a client encountering an unknown version must discard that snapshot and keep its existing ACK behavior (lag naturally grows, triggering a forced resync from the server, forming a loop).
- When the serialized state exceeds `maxSnapshotBytes` (default 1MB), this tick's send is skipped and counted into `chorus_snapshots_oversize_total`; the client stops ACKing because it receives no snapshots -> lag exceeds the limit -> once the state shrinks back below the cap, forced resync aligns it.

### 3.5 Backpressure and Downsampling (Mandatory for the Live-Stream Danmaku Scenario)

- Per-tick event cap: `maxEventsPerTick`
- Over-limit policy (tradeoff by gameplay):
  - Merge: merge same-type events of the same user (like merging, gift stacking)
  - Layering: core events take priority, chat-type events can be moved to a "display-only channel"

### 3.6 Event Lifecycle and the Four Delivery States

An event goes through six stages from entering the system to taking effect:

```
Received → Validated → Accepted → Scheduled → Applied → Published
 (入口)     (验签)      (映射+去重)  (调度到tick)  (引擎结算)  (快照下发)
```

| Stage | Landing point | Failure semantics |
| --- | --- | --- |
| Received | ingress receives the raw request | Request body/rate limit anomaly → reject (HTTP 4xx/413/429) |
| Validated | `IPlatformEventVerifier` signature verification | Fails → 401, fail-closed (production default is reject) |
| Accepted | mapper mapping + faction rules + deduplication reservation | Mapping failure → 400; duplicate → `duplicated=true` idempotent return |
| Scheduled | SessionActor schedules to `tickId + inputDelayTicks` | In-memory queueing; over-limit goes through the 3.5 backpressure policy |
| Applied | Tick settlement, after reducer merging enters the GameModule | Merging does not lose counts (grouping-key conservation); over-budget presentational events may be dropped |
| Published | Snapshot sent down to the client | Publish failure is handled per the four-state `Failed` |

**The four delivery states** (describing the outcome of a single delivery, a term for observability/troubleshooting rather than a storage field):

- `Received`: received, signature not yet verified.
- `Accepted`: passed signature verification + mapping + deduplication, and has entered scheduling (the meaning of an ingress returning 200).
- `Applied`: settled into the authoritative state at some `tickId` (after which the client can confirm via ACK).
- `Failed`: signature failure / mapping failure / IPC publish failure. **Key constraint: `Failed` must never be recorded as "seen"**——deduplication uses the reserve/commit/rollback protocol; a publish failure is immediately `Rollback`, and platform retries are accepted as a new delivery, rather than being judged as a duplicate and dropped.

**Deduplication reserve/commit/rollback** (review P0-7): `EventDeduplicator.TryReserve` reserves atomically (exactly one winner among concurrent requests with the same id) → on successful publish `Commit` (Reserved→Delivered, suppressing duplicates within the TTL) → on publish failure `Rollback` (releasing the reservation, Failed is retryable). TTL defaults to 600s (`Ingress:DedupTtlSeconds`), with a capacity safety valve guaranteeing bounded memory.

**The four event categories** (determining the over-limit tradeoff strategy, see 3.5):

| Category | Examples | Over-limit policy |
| --- | --- | --- |
| Accumulable | Likes, gifts | Merging preserves counts (grouping key `(S,M,F,U[,GiftId])`), never losing counts due to merging |
| Non-droppable | Key gift triggers, passphrase commands | Retained with priority, silent dropping is forbidden |
| Presentational | Ordinary danmaku, entry effects | May be dropped/moved to the display channel, drops counted into metrics |
| Control | Admin/on-off commands | Separate channel, does not compete with audience events for the budget |

## 4. Client Protocol (v1)

See: `/reference/protocol.html`

## 5. Recommended Reading Order

If you are working on problems such as "not in sync, unstable resync, difficult slow-end recovery", read in the following order:

1. [Overall Architecture](./architecture.md)
2. [Frame Sync Design](./frame-sync.md)
3. [Frame Sync Best Practices](./frame-sync-best-practices.md)
4. [Client Resync State Machine](./client-resync-state-machine.md)
5. [Parameter Tuning](../reference/tuning.md)
6. [Legacy Server Problem Analysis](./legacy-real-server-analysis.md)
