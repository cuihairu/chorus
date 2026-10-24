---
title: Troubleshooting
---


# Troubleshooting

This document targets the most common production issues: desync, runaway frame catch-up, slow-client recovery failure, and the whole room freezing together.

It is recommended to use it together with the following documents:

- [Frame Sync Best Practices](../design/frame-sync-best-practices.md)
- [Client Resync State Machine](../design/client-resync-state-machine.md)
- [Parameter Tuning](./tuning.md)
- [Operations and Observability](./operations.md)

## Troubleshooting Order

Do not start by tuning parameters; first determine which category the problem falls into:

1. Is it a single client misbehaving, or the whole room misbehaving together
2. Is it server tick overload, or client-side apply/render being too slow
3. Is it mild lag not converging, or the recovery path itself being broken

If this classification is not done clearly, the handling afterwards will most likely misjudge.

## Symptom 1: Neither Side Can Catch Up During Resync

Typical presentation:

- After one side falls behind, the other side also starts freezing
- The frame gap between the two sides does not converge, and instead grows together
- During recovery, a large volume of backfill frames or message buildup appears briefly

Check first:

1. Whether the server still has "wait for the slow client" or "pause global advancement" logic
2. Whether the client still tries to backfill frames for a long time when heavily lagging
3. Whether snapshot jumps are missing, leaving only historical frame replay for recovery

Most common root cause:

- Global advancement coupled with slow-client recovery
- Recovery strategy incorrectly relying on continuous backfill

Correct handling:

- The server keeps advancing
- Slow clients beyond the threshold recover directly by snapshot
- The visual layer smooths on its own; do not require the logic layer to complete every intermediate frame

## Symptom 2: Only One Client Frequently Falls Behind

Typical presentation:

- A single client's `lagTicks` stays continuously high
- `resyncCount` is noticeably higher than for other clients
- Other clients are basically normal

Check first:

1. That client's `lastAckAgeMs`
2. That client's snapshot application time
3. Whether there is main-thread stutter, heavy resource loading, or rendering spikes

How to judge:

- If only this client is abnormal, prioritize looking at client-side performance
- Do not suspect the server tick strategy first

Handling suggestions:

- Reduce the client's snapshot application cost
- Lower the local catch-up budget
- Check whether ACK is sent only after application has truly completed

## Symptom 3: Everyone Lags Together During Peak Hours

Typical presentation:

- Multiple clients' `lagTicks` rise at the same time
- `lastTickProcessMs` rises noticeably
- resync counts rise together during peak input

Check first:

1. Events per tick (the difference of `totalAppliedEvents` between two adjacent `/sessions` calls)
2. Whether `maxEventsPerTick` is taking effect
3. Whether event merging/downsampling is sufficient
4. Whether snapshot size is too large

Most common root cause:

- Server tick overload
- Single-frame event explosion

Handling suggestions:

- Do backpressure first, then talk about resync
- Raise `tickDurationMs` if necessary
- Separate high-frequency low-value events out of the authoritative logic path

## Symptom 4: The Client Frequently Receives Forced Snapshots

Typical presentation:

- Snapshots with `forced=true, reason=\"lag\"` are very frequent
- The client has just recovered and lags again

Check first:

1. Whether `maxLagTicks` is too small
2. Whether `resyncCooldownMs` is too short
3. Whether the client actually enters `Following` after applying a snapshot
4. Whether ACK semantics are correct

Common misconception:

- Assuming the threshold is too strict

In reality, many times it is:

- The client's recovery action is too heavy
- ACK is sent too early
- The local client is still doing excessive backfill

## Symptom 5: The Client Clearly Received the Snapshot, but the Server Still Judges It as Behind

Check first:

1. Is ACK sent at "snapshot received" rather than at "application complete"
2. Does the client put the snapshot into a queue and then ACK early
3. Has `lastAppliedTickId` actually been updated to the tick the snapshot corresponds to

This kind of problem is essentially an ACK semantics error.

Correct semantics:

- `ack.lastAppliedTickId` only means the authoritative state application has completed

## Symptom 6: Mild Lag Could Be Caught Up, but Catching Up Makes It Worse

Typical presentation:

- The client drops frames more severely as soon as it enters `CatchingUp`
- Main-thread load rises noticeably during catch-up

Check first:

1. How many extra logic ticks are backfilled per frame
2. Whether the full presentation logic still runs during catch-up
3. Whether a per-second catch-up budget cap is missing

Handling suggestions:

- Backfill at most a small number of extra ticks per rendered frame
- The total per-second catch-up count must be capped
- When necessary, downgrade part of the presentation-layer overhead during catch-up

## Symptom 7: Recovery After Reconnection Is Very Slow

Typical presentation:

- After the client reconnects, it takes a long time to catch up again
- The server keeps sending lots of historical data

Check first:

1. Whether there are periodic snapshots at a sufficient frequency
2. Whether it still mainly relies on historical incremental replay
3. Whether snapshot size is too large

Correct direction:

- Reconnect recovery should rely on "latest snapshot + bounded increment" as a priority
- It should not rely on unbounded historical replay

## Quick Judgment Matrix

### Only One Client Is Slow

Suspect first:

- The client's main thread
- Snapshot application performance
- Local ACK logic

### Everyone Is Slow

Suspect first:

- Server tick overload
- Single-frame event explosion
- Snapshot send/serialization cost being too high

### Data Is Received but Still Not in Sync

Suspect first:

- ACK semantics error
- Client state machine error
- Snapshot application coupled with the render layer

## Metrics That Must Be Obtained When Troubleshooting

At minimum you need:

- `tickId` (the session's current authoritative tick)
- Each client's `lastAckTickId`
- Each client's `lagTicks`
- Each client's `lastAckAgeMs`
- `lastTickProcessMs`
- Events per tick (the difference of `totalAppliedEvents` between two adjacent `/sessions` calls)
- Dropped/merged event count
- Each client's `resyncCount`

Without these metrics, tuning parameters online by feel is not recommended.

## Three High-Frequency Misjudgments

### Misjudgment 1: Thinking the Network Is Bad

If all clients lag at the same time, it is more likely server overload, not the network.

### Misjudgment 2: Thinking Frame Catch-Up Is Too Slow

Many times it is not that catch-up is too slow, but that frames should not be kept catching up at all — a snapshot jump should be used.

### Misjudgment 3: Thinking the Threshold Is Too Strict

Frequent resync often indicates that the recovery action is too heavy or that ACK semantics are wrong, not necessarily that `maxLagTicks` is set too low.

## Recommended Troubleshooting Actions

When encountering a production issue, it is recommended to proceed in this order:

1. First see whether it is a single-client or global problem
2. Then see whether `lastTickProcessMs` is approaching the tick period
3. Then look at the client's `lagTicks` and `lastAckAgeMs`
4. Then confirm whether forced snapshots are being triggered frequently
5. Only then tune `inputDelayTicks`, `maxLagTicks`, `snapshotEveryTicks`

## Summary

The worst thing in troubleshooting is watching symptoms while randomly tuning parameters.

A more reliable method is:

- First distinguish the problem category
- Then locate whether it is the advancement path, the recovery path, or client-side performance
- Only then do fine-grained parameter tuning

As long as you find that the system still relies on "long-duration frame backfill recovery", you should prioritize going back to the sync design itself, rather than continuing to optimize catch-up details.
