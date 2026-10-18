---
title: Parameter Tuning
---


# Parameter Tuning

This document gives the tuning methods for the core frame sync parameters. The goal is not to pursue "absolutely lowest latency", but to achieve a controllable balance among stability, recovery speed, and perceived smoothness.

It is recommended to read it together with:

- [Frame Sync Best Practices](../design/frame-sync-best-practices.md)
- [Frame Sync Design](../design/frame-sync.md)
- [Operations and Observability](./operations.md)

## Tuning Principles

Do not reverse the tuning order.

The correct order is usually:

1. First confirm the architecture is correct: server-authoritative tick, ACK, snapshot, and backpressure are all in place.
2. Then confirm the system is not overloaded: `lastTickProcessMs` must not stay close to the tick period for a long time.
3. Only then fine-tune latency, catch-up, and snapshot frequency.

If the system is already overloaded, tuning `maxLagTicks` or catch-up thresholds no matter how much only delays the time of explosion.

## Recommended Starting Values

Suggested starting ranges for live-stream interaction and gift/danmaku-driven gameplay:

- `tickDurationMs`: `50` or `66`
- `inputDelayTicks`: `2~4`
- `snapshotEveryTicks`: `2~5`
- `maxLagTicks`: `6~10`
- `resyncCooldownMs`: `1000~3000`
- `maxEventsPerTick`: hard-limited according to load test results

These are not final values, but they are sufficient as first-version launch parameters.

## Parameter Descriptions

### `tickDurationMs`

Meaning:

- The server-authoritative logic tick period

Effect of increasing:

- Server pressure decreases
- Number of syncs per second decreases
- The minimum time from input to taking effect becomes longer

Effect of decreasing:

- Sync is finer
- Recovery is smoother
- Server and client pressure rises noticeably

Suggestions:

- For live-stream interaction, prefer starting from `66ms`
- Faster-paced gameplay can try `50ms`
- Before sufficient load testing, it is not recommended to blindly push it down to `33ms`

Danger signal:

- `lastTickProcessMs` frequently exceeds `60%~80%` of `tickDurationMs`

When this happens, do not keep shrinking the tick; solve the overload first.

### `inputDelayTicks`

Meaning:

- How many ticks into the future input is scheduled before being settled in bulk

Role:

- Absorbs network jitter
- Absorbs out-of-order platform events
- Reduces "arrived just in time but already expired" boundary problems

Effect of increasing:

- Stability increases
- The felt latency from operation to taking effect increases

Effect of decreasing:

- Response is faster
- More easily affected by jitter

Suggestions:

- When network fluctuation is obvious, prefer tuning from `2` up to `3` or `4`
- If the event source is not precise player input but live-stream platform events, it is better to be slightly larger than too small

Danger signal:

- Slight jitter frequently causes late events or ACK fluctuation

### `snapshotEveryTicks`

Meaning:

- The send frequency of periodic snapshots

Effect of increasing:

- Bandwidth and serialization overhead decrease
- Slow clients recover more slowly

Effect of decreasing:

- Slow clients recover faster
- Bandwidth and server-side snapshot cost increase

Suggestions:

- For the first version, be conservative and take `2~5`
- If snapshots are light, they can be sent more frequently
- If snapshots are heavy, optimize snapshot size first; do not simply lower the recovery frequency

### Snapshot Serialization Cost (Since 2026-10-12)

Periodic broadcast snapshots in the single-process form are **serialized only once per tick** (`WireSnapshot.Encode`, where serialization produces the UTF-8 bytes of the wire protocol `snapshot` message); all clients reuse the same bytes — adding more clients no longer linearly amplifies JSON serialization overhead. Forced resync snapshots (with target client metadata) are still encoded per client (rare path).

Binary formats (MessagePack/FlatBuffers) — **evaluation conclusion: not introducing for now**:

- Benefit: further saves CPU/bandwidth (about 30–60%), but requires clients to switch decoding libraries in sync;
- Cost: new dependency + wire protocol breaking change (allowed in 0.x, but the JS/TS clients on both platform ecosystems must follow);
- Decision: first fully exploit the "serialize once, reuse" JSON path, observe `chorus_tick_duration_ms` and
  snapshot size (`/sessions`'s `lastSnapshotBytes` and `/metrics`'s `chorus_snapshot_bytes` already expose the wire byte count of each session's most recent snapshot, `8e87a47` and same batch `f301aa3`; oversize see `chorus_snapshots_oversize_total` separately) before deciding again; the introduction timing is placed on the wire protocol
  version bump for 1.0 (see [versioning policy](./versioning.md)), not as a standalone breaking change.

Danger signal:

- After a forced snapshot triggers, the client still has to wait a long time before reconverging

### `maxLagTicks`

Meaning:

- After how many ticks a client is behind, forced snapshot recovery is triggered

Effect of increasing:

- The system is more tolerant of short-term fluctuation
- Slow clients may stay in the wrong state longer

Effect of decreasing:

- Recovery is more aggressive
- resync is triggered more frequently

Suggestions:

- Generally start from `6~10`
- The shorter the tick, the slightly larger the threshold can be
- The longer the tick, the threshold should not be too large, otherwise the felt timing will be noticeably off

Danger signal:

- Some clients' `lagTicks` are already high, but they are still trying local catch-up instead of entering snapshot recovery

### `resyncCooldownMs`

Meaning:

- The minimum interval between two forced snapshots for the same client

Role:

- Prevents the system from repeatedly forcing snapshots in a short time, forming oscillation

Suggestions:

- Generally start from `1000~3000ms`
- If the client's snapshot application itself is heavy, it can be moderately increased

Danger signal:

- A client receiving multiple forced snapshots within a few seconds indicates the threshold is not too low, but that it has no recovery capability at all

### `maxEventsPerTick`

Meaning:

- The maximum number of events allowed to enter the authoritative logic per tick

This is one of the most important safety valves.

Suggestions:

- Must be configured
- Must be paired with an event merging or downsampling strategy
- Must be verified by load test; do not decide by guesswork

Danger signal:

- Single-frame event count grows without bound during peaks
- Server tick time rises linearly with activity peaks

## Typical Tuning Scenarios

### Scenario 1: Frequently Misjudged as Lagging During Slight Jitter

Presentation:

- A client stutters briefly and is judged as behind
- Forced snapshots are too frequent

Handle first:

1. Moderately increase `inputDelayTicks`
2. Slightly increase `maxLagTicks` as appropriate
3. Check whether ACK is sent only after the client has truly applied the snapshot

Do not do first:

- Only raising the client's frame catch-up speed

### Scenario 2: The Client Recovers Very Slowly

Presentation:

- Once `lagTicks` opens up, it is hard to converge

Handle first:

1. Raise snapshot frequency
2. Shrink snapshot size
3. Ensure that for severe lag it jumps directly by snapshot, rather than replaying a long chain

Do not do first:

- Making the client backfill many logic frames within one rendered frame

### Scenario 3: Everyone Lags Together During Peak Hours

Presentation:

- It is not just one client that is slow; the whole room's metrics degrade

Handle first:

1. Check `lastTickProcessMs`
2. Limit `maxEventsPerTick`
3. Increase merging and downsampling
4. Reduce snapshot size

This indicates the problem is most likely server tick overload, not the resync strategy.

### Scenario 4: A Single Client Resyncs Repeatedly

Presentation:

- A client's `resyncCount` keeps growing

Handle first:

1. Look at that client's `lastAckAgeMs`
2. See whether it has main-thread stutter or overly slow snapshot application
3. Check whether there is heavy local replay or render blocking

This is usually a client-side performance problem; it cannot be solved by making the server stop.

## What Must Be Watched During Load Testing

At minimum, watch the correlation among these metrics:

- `lastTickProcessMs`
- `lagTicks`
- `lastAckAgeMs`
- `resyncCount`
- Events per tick
- Dropped/merged event count

Recommended judgment method:

- If only individual clients have high `lagTicks`, prioritize checking the client
- If all clients degrade together, prioritize checking server tick overload
- If `resyncCount` is high but `lagTicks` still does not converge, the recovery path itself is too heavy

## A Practical Judgment Method

When tuning parameters, prioritize answering these three questions:

1. Is the system in a "locally slow client" situation or "globally overloaded"?
2. Does current recovery mainly rely on "gentle catch-up" or "snapshot jump"?
3. Are you tuning the "recovery strategy", or are you masking "tick overload"?

As long as the third question cannot be answered, do not touch the parameters yet.

## Summary

The core of parameter tuning is not pursuing some fixed value, but establishing this stable chain:

- Tick is not overloaded
- Input has buffering
- Single frames have backpressure
- Slow clients can recover by snapshot
- Metrics can explain the problem

Once these points are done, parameters are only fine-tuning; before these points are done, parameters are only an illusion.
