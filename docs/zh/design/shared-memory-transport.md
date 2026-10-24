---
title: Shared Memory IPC Transport Evaluation (MemoryMappedFile + FlatBuffers)
---


# Background

todo §3.5 requires evaluating and planning a shared memory transport (MemoryMappedFile + FlatBuffers).
This document provides: the real cost of each message today, an outline of the approach, cheaper alternatives, risks, and trigger conditions.
Conclusion first: **do not implement yet**; first land the two zero-risk alternative optimizations, and bring in shared memory only once a concrete performance
bottleneck appears (trigger conditions at the end of this document).

# Current State: The Real Cost of One Envelope

In split mode each eng↔gw message goes through (`Chorus.Messaging` / `JsonEnvelopeCodec` /
`LengthPrefixedStream`):

1. `Envelope` (including `body`) → UTF-8 JSON serialization (sender side)
2. 4-byte length prefix + frame written to the socket (one `SendAsync` per sender)
3. Kernel copies ×2 (write to UDS, read from UDS)
4. Receiver side reads the frame → JSON deserialization → into the `_inbox` Channel

The quantifiable costs are mainly **JSON codec + kernel-mode copies + per-connection pump thread scheduling**.
Heartbeats (one every 5s by default) can be ignored; the bulk is snapshots. **Before the optimization**, each client got one `eng.snapshot` per tick (published individually per (session, client)), with a default 66ms tick, 10 clients ≈ about 150 JSON round trips per second. **After the optimization (50f0045)**, broadcast snapshots are published by only the owner elected via `BroadcastOwnerRegistry`, one per session per tick (`EngineBusSessionClient` publish filtering), 10 clients ≈ about 15 per second.

# Cheaper Alternatives (do these first, zero new dependencies)

Ranked by cost-effectiveness, do not bring in shared memory until these two are done:

1. **Publish eng→gw snapshots deduplicated per tick**. Broadcast snapshots have identical content
   for all clients of the same session (Gateway-side `DispatchSnapshotAsync` already fans out per session), but on the engine
   side each `EngineBusSessionClient` publishes a full JSON of its own. Changing this to publish one per session
   per tick (client differences exist only in targeted resync snapshots) drops the eng→gw message volume
   directly from "client count × tick frequency" to "tick frequency", and JSON encoding also saves N-1 copies.
   ✅ **Delivered (50f0045)**: `EngineBusSessionClient` now publishes only in two cases——
   broadcast snapshots without a `TargetClientId` are published only by the session's **broadcast owner**
   (`BroadcastOwnerRegistry`: the first one to connect becomes the owner, ownership transfers to the remaining
   clients when the owner disconnects, cleared when the last one disconnects and re-elected on reconnect; during the transfer window the broadcast for that tick may be skipped once,
   caught up on the next tick); targeted resync snapshots are published only by the targeted client. Zero Gateway
   change (null-target already fans out to the whole session). Regression guards:
   `BroadcastOwnerRegistryTests` (5 cases: election/transfer/independence),
   `EngineBusSessionClientTests` (3 cases: publish filtering),
   `SplitModeEndToEndTests.TwoClientsSameSession_BothReceiveSnapshots`
   (end-to-end acceptance that both clients in the same session receive the broadcast).
2. **JSON → binary envelope (MessagePack/FlatBuffers)**. The snapshot wire format
   (streamer-ws face) has been evaluated and not introduced for now (see `docs/reference/tuning.md`
   "snapshot serialization cost"); the IPC-face envelope format is decoupled from the wire format and can be swapped separately,
   but it introduces a new dependency + dual-side version compatibility requirements, and the benefit likewise needs baseline data first.

# Approach Outline (if done)

## RingBuffer

- **Channels**: reuse the existing eng→gw / gw→eng dual-channel directions, one
  `MemoryMappedFile` per direction, internally a fixed-length ring buffer (SPSC: engine single-writer, gateway single-reader).
- **Frame format**: `[len:u32][seq:u64][payload]`; the ring header stores
  `writeIndex/readIndex` (u64, monotonically increasing, taken modulo the buffer capacity), read/write indexes updated with
  `Interlocked`/`Volatile`——**no locks introduced**.
- **Wakeup**: after the writer sets seq on completion it signals the reader via `EventWaitHandle` (cross-platform) / eventfd (Linux); the reader may also degrade to 0.5–1ms polling, simple but CPU-expensive.
- **Backpressure**: ring full = slow consumer; the writer **overwriting the oldest unread frame is not viable** (it would tear a half-read frame),
  the correct approach is to drop this message and count it (snapshots may be dropped, events must not be lost and need separate lanes——aligned with the existing
  SessionActor dual-lane semantics).

## FlatBuffers' Role

- Benefit point: **zero-copy reads** (the reader accesses fields by offset, without fully materializing) + schema
  evolution (fields can be added or removed).
- Cost point: schema files + codegen enter the build process; the dynamic
  `object`-typed `body` (the existing `Envelope.Body` is a `JsonElement`) needs to change to a strongly typed union,
  **this is a breaking refactor of the `IMessageBus` contract**, not a pure transport-layer swap.

# Cost and Risk

| Item | Notes |
| --- | --- |
| Cross-platform | Windows/Linux named shared memory and naming semantics differ (`MemoryMappedFile.CreateNew` vs `/dev/shm`); naming rules and lifecycle (who creates, who releases) must be consistent on both sides |
| Crash recovery | A kill -9 on one side leaves half-written frames/hanging indexes; the reader must be able to detect seq discontinuity and rebuild the ring in full (equivalent to a strengthened version of the existing UDS disconnect/reconnect logic) |
| Debuggability | sockets can be observed with `ss`/tcpdump; shared memory is a complete black box; troubleshooting relies on self-built seq/heartbeat monitoring |
| Version rolling | Upgrading eng first and gw later (see `docs/reference/versioning.md`) requires the ring header to carry format version and capacity negotiation, falling back to UDS when incompatible |
| Implementation size | Dual-channel ring + wakeup + backpressure + crash recovery + fallback + baseline; estimated in the "week" range, far exceeding any existing transport |

# Trigger Conditions and Phase Plan

Only start once any of these is satisfied, to avoid paying complexity for a bottleneck that does not exist:

- Baseline shows that in single-machine split mode IPC codec + copies take up
  **>5%** of the tick budget (currently 66ms) (derived by comparing `LastTickProcessMs` percentiles from `/metrics` with load-test scripts between Monolith
  and split mode);
- Single-machine session count × client count pushes the eng→gw message rate to the UDS throughput bottleneck (manifesting as
  continuously growing `MailboxDepth` or rising `TotalTickOverruns`) and
  the "deduplicated publish per tick" optimization is still insufficient.

Phase breakdown (to execute at that time):

1. **Phase A**: eng→gw snapshots deduplicated per tick (alternative 1, independent benefit)——**✅ Delivered (50f0045)**: broadcast snapshots are published only by the owner, one per session per tick, zero Gateway change, eng→gw message volume reduced to "tick frequency"; guards `BroadcastOwnerRegistryTests` 5 cases + `EngineBusSessionClientTests` 3 cases + `TwoClientsSameSession_BothReceiveSnapshots`. Phases B/C still wait on trigger conditions for a real bottleneck;
2. **Phase B**: `Ipc:Transport=SharedMemory` prototype, snapshot channel only (droppable semantics),
   event/control channels still on UDS; A/B baseline against UDS;
3. **Phase C**: only consider a default switch and FlatBuffers after the baseline wins and crash recovery is verified.

# Conclusion

Shared memory is a "last 20% of performance" tool, and there are no trigger conditions in the current repo. Do alternative 1
(deduplicated publish per tick, see above) first, and wait on trigger conditions for the rest until a real bottleneck appears.
