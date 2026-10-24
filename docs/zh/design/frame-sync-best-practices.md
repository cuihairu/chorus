---
title: Frame Sync Best Practices
---


# Frame Sync Best Practices

This document focuses on two goals:

- Provide an implementable frame sync solution suitable for live-stream interaction / light battle scenarios
- Explain why the old-style "chase frames / pause" approach easily falls into "neither side catches up"

Related background, worth reading alongside:

- [Frame Sync Design](./frame-sync.md)
- [Overall Architecture](./architecture.md)
- [Legacy Server Analysis](./legacy-real-server-analysis.md)

## Conclusion First

If a project has already fallen into "neither side catching up" during resync, it is usually not because the frame-chasing algorithm isn't aggressive enough, but because the sync architecture itself was chosen wrong.

The more stable approach is not:

- The server waiting for the slowest endpoint
- The client relying on frame backfill/replay to catch up long-term

but rather:

- The server keeps advancing the authoritative tick
- Input settles on a unified tick
- The slow endpoint, once it exceeds the threshold, directly takes a snapshot to align
- The presentation layer masks the jump with interpolation/transitions

In one sentence:

> Don't let the global logic wait for the slow endpoint; let the slow endpoint resync on its own.

## Why "Neither Side Catches Up" Happens

This is the most common runaway path in old-style event lockstep / input-frame broadcast implementations:

1. The server sends frames best-effort by time, instead of a strictly fixed tick.
2. Some client starts lagging because of stuttering, GC, render blocking, or network jitter.
3. The server, in order to "wait for it", pauses or slows down new frame advancement.
4. The fast endpoints are also forced to stop, and both sides' experience degrades together.
5. Once recovered, the server and client attempt to continuously backfill many frames.
6. Continuous backfill makes network packets larger, events pile up in a single frame, and the client main thread gets more stuck.
7. The system enters a positive feedback loop of "the more you chase, the more you fall behind".

This kind of problem is not a local bug, but a strategy error: it couples "global advancement" with "slow endpoint recovery" together.

## Recommended Architecture

The recommended mode is "server-authoritative Tick + state snapshots":

- The server maintains a single authoritative `tickId`
- The server advances logic on a fixed `tickDurationMs`
- Input first enters a buffer, then is scheduled to `tick + inputDelayTicks` for unified settlement
- The client reports the progress it has applied via `ack.lastAppliedTickId`
- When the server detects a client is too far behind, it sends a forced snapshot only to that client

This is also the direction adopted by this project's current documentation and engine implementation.

## The Most Important Principles

### 1. The Server Must Never Pause Global Logic for a Slow Endpoint

The server must keep advancing the authoritative tick.

The reasons are simple:

- Slow endpoint problems are usually local problems and should not drag the whole thing to death
- Live-stream interaction scenarios emphasize "overall smoothness" and "the room keeps running"
- Once the whole thing pauses, the fast endpoints also get dragged into frame-chasing and frame explosions

The correct approach is:

- When the slow endpoint is lightly behind, allow it to gently catch up locally
- When the slow endpoint is heavily behind, directly send a snapshot so it jumps to the latest authoritative state

### 2. Don't Conflate Input and State

Input sync is only responsible for "on which tick an event takes effect"; state sync is responsible for "how the client recovers to the authoritative state".

Don't try to solve all problems by backfilling input frames only. Because:

- The input replay chain is long and recovery is slow
- During replay the client still has to bear logic computation and render load
- Once the missing-frame span is large, recovery cost degrades exponentially

The recommended combination is:

- Day-to-day: input settles on tick
- Recovery: snapshot first, replay only a small amount of deltas when necessary

### 3. The Logic Layer and the Presentation Layer Must Be Separated

The logic layer only recognizes the authoritative tick and does no fancy compensation.

The presentation layer is responsible for:

- Interpolation
- Transition animations
- Camera and UI smoothing

If logic frame-chasing and visual compensation are mixed together, fairness and stability will ultimately both be harmed.

### 4. Every Tick Must Have Backpressure

The number of events processed per tick must have a hard upper limit, e.g. `maxEventsPerTick`.

When the limit is exceeded, one or more of the following strategies must be executed:

- Merge events of the same kind
- Downsample high-frequency, low-value events
- Drop stale events
- Make non-critical presentation events asynchronous, so they don't enter the authoritative logic

Otherwise during peak periods "single frame explosion" easily occurs:

- Single frame packet too large
- Serialization/deserialization time too long
- The client processes too many events in one frame
- The result is more severe lag

## Recommended Resync Strategy

Resync should not rely on a single mechanism; it should be handled in tiers.

### Lightly Behind

Example conditions:

- `lagTicks <= 3~5`

Strategy:

- The client may advance slightly faster than normal speed for a short time
- Each render frame executes at most a small number of extra logic ticks
- A catch-up upper limit must be set, to avoid blowing up the main thread

Applicable scenarios:

- Brief network jitter
- A one-off render stutter
- Slight clock drift

### Moderately Behind

Example conditions:

- `lagTicks > 5~10`

Strategy:

- The server sends a `forced snapshot` to that client separately
- The client directly switches to `snapshot.tickId`
- The visual layer does its own smooth convergence, without requiring the logic layer to backfill all intermediate frames

Applicable scenarios:

- The client stalls for several hundred milliseconds or more
- Network fluctuation causes ack to lag noticeably
- Local backfill catch-up can no longer converge in a short time

### Heavily Behind or Long Without ACK

Example conditions:

- `lastAckAgeMs` stays too large
- `lagTicks` stays above the threshold for a long time
- The same client resyncs frequently and still cannot stabilize

Strategy:

- Drop deltas that are too stale
- Keep the latest snapshot and a small number of deltas within a limited window
- Rebuild the local state when necessary

At this point, don't insist on "backfilling frame by frame from history without missing one"; that will usually only make things worse.

## Recommended Parameter Ranges

The following suggestions are better suited to live-stream interaction and gift/danmaku-driven gameplay, and are more stable as initial parameters than blind tuning:

- `tickDurationMs`: `50` or `66`
- `inputDelayTicks`: `2~4`
- `snapshotEveryTicks`: `2~5` (note: this repo's code defaults to `1`, i.e. sends every tick — `src/Chorus.Engine/Session/SessionConfig.cs:6`)
- `maxLagTicks`: `6~10` (note: this repo's code defaults to `50` — `src/Chorus.Engine/Session/SessionConfig.cs:8`, far higher than this recommended range; base tuning on observation)
- `resyncCooldownMs`: `1000~3000`
- `maxEventsPerTick`: must be configured according to load-test results

Basic principles when tuning:

- If network jitter is noticeable, prioritize moderately increasing `inputDelayTicks`
- If recovery is too slow, prioritize raising snapshot frequency rather than increasing frame-chasing intensity
- If the server's tick time approaches the tick period, do peak shaving and backpressure first; don't keep raising frequency

## When to Use Pure Lockstep

Pure input lockstep or rollback is not unusable, but the prerequisites are much more demanding than in live-stream interaction scenarios.

Conditions better suited to pure lockstep/rollback:

- Few players
- High input frequency with very strong fairness requirements
- Logic can be strictly deterministically reproduced
- Client performance and platform differences are controllable

If your scenario is:

- Audience danmaku/gift driven
- Large event volume fluctuation
- Large client device differences
- Emphasizes overall stability and feel more

then "server-authoritative state sync" is usually more appropriate than "pure input lockstep".

## The Most Common Mistakes

The following practices most easily drag the system into a runaway state:

- Waiting for slow endpoints by pausing the server
- Handling large-span lag with long continuous frame backfill
- The client mainly runs on the local clock, and the server only does outer-loop correction
- No upper limit on the number of events per frame
- Only saving history frames, not saving snapshots
- No ack/lag/resync observation per client

## Required Observability

Without metrics, diagnosing "out of sync" is basically guesswork.

At least the following metrics should be recorded:

- `serverTick`
- `clientLastAckTick`
- `lagTicks`
- `lastAckAgeMs`
- `lastTickProcessMs`
- Events per tick
- Dropped/merged events per tick
- Forced snapshot count
- Per-client outbound queue backlog: `OutboundClientQueue.Dropped` (the number of snapshots replaced in each client's latest-wins slot) is exported to `/sessions`'s per-client `clientQueueDropped` and the session aggregate `totalClientQueueDropped`, metric `chorus_client_queue_dropped_total` (`6d738b7`). A capacity-1 slot has only one kind of backlog signal — "drop"; slot depth is meaningless

Watch three danger signals closely:

- `lagTicks` keeps growing
- `lastTickProcessMs` approaches or exceeds `tickDurationMs`
- Events per tick spike sharply during peaks

If all three signals appear at the same time, it usually means the root cause is not "frame-chasing logic isn't smart enough", but that the system is already overloaded, and backpressure and recovery strategy must be addressed first.

## A Practical Set of Judgment Criteria

To quickly judge whether the current approach is healthy, these criteria can be used:

- When a single client disconnects or stutters, can the other clients still see the room advancing normally?
- When a client falls behind, can it recover via snapshot within a finite time, rather than backfilling infinitely?
- During peak input, does the system actively merge/drop low-value events, rather than letting a single frame grow unboundedly?
- Can the lag source be seen directly from metrics: network, client main thread, or server tick overload?

If two or three of these cannot be achieved, don't keep fine-tuning frame-chasing parameters; you should first go back and refactor the sync architecture itself.

## Recommended Implementation Order

If the project is migrating from old-style frame-chasing logic, this order is recommended:

1. First establish the server-authoritative fixed tick.
2. Add `inputDelayTicks` so input settles uniformly on tick.
3. Establish the `ack.lastAppliedTickId` mechanism for the client.
4. Introduce periodic snapshots and forced snapshots.
5. Remove the "server waits for slow endpoints" logic.
6. Finally fine-tune the client's local light catch-up strategy and presentation-layer smoothing.

The key point of this order is: first build the recovery closed loop, then optimize the catch-up experience.

## Summary

For frame sync to be stable, it doesn't rely on "chasing frames more fiercely", but on correct separation of duties:

- The server is responsible for authoritative advancement
- The sync layer is responsible for buffering, ACK, snapshots and recovery
- The client presentation layer is responsible for smoothing

When the system has already fallen into "neither side catching up", the priority is not to check the frame-chasing speed, but these three questions:

- Is the server still waiting for slow endpoints?
- Does recovery still mainly rely on long-chain frame backfill?
- Has a single tick already lost its backpressure?

As long as these three points are fixed, resync problems will generally go from "frequently out of control" to "observable, recoverable, tunable".
