---
title: Glossary — rooms, rounds, ticks and ownership epochs
---

# Glossary — rooms, rounds, ticks and ownership epochs

These definitions describe the current redesign, not a claim that every target capability exists. Read this page before the [architecture](/architecture/architecture), [state synchronization](../design/authoritative-sync.md) and [room recovery design](../design/room-recovery.md). Explanatory names do not rename existing protocol fields.

## Three different concepts

| Term | Question answered | Changes when | Does not mean |
|---|---|---|---|
| Tick / TickId | How many logical steps has this round committed? | The room commits its next simulation step | Render frame count or ownership generation |
| Round | Which match is being played? | A match ends and another is created | One tick or one connection |
| RoomEpoch / OwnerEpoch / room-scoped Epoch | Which generation of execution authority may write this room? | Authority transfers, is replaced, or is explicitly bumped by GM | Game time, an input window, Unix time or a round number |

Each room owns independent tick progress and an independent ownership epoch. Values across different rooms are not comparable progress. A whole match can finish without an epoch change. Ordinary reconnect and business pause/resume do not require an epoch bump. This use of epoch comes from distributed write fencing; it is not a mandatory naming convention for game engines.

```text
Room A: epoch 7, tick 100 → 101 → paused at 101 → resumed → 102
Room B: epoch 3, tick 800 → 801 → 802 (unaffected by A's pause)

A transfers execution authority: epoch 7 → 8.
The replacement restores committed tick 102; its next committed tick is 103.
The old executor cannot append valid writes with epoch 7.
```

In this project a GameRoom represents one authoritative round, and its RoomId is not reused after the round ends. One live gameplay binding can participate in successive new rooms. RoomId and RoundGeneration separate rounds; bumping an ownership epoch does not create a new match.

## Objects and identities

| Term | Meaning and scope | Important distinction |
|---|---|---|
| RoomId / GameRoom | Stable identity of one shared round, preserved across server migration | Neither a platform live room nor a GameServer process |
| RoomActor | In-process state and serial simulation loop for one room | Neither an OS thread nor proof of authority |
| RoomRuntime | In-process creation, lookup, recovery and retirement of room actors | Not a separate service process |
| LiveSession | Verified platform broadcast context | Can span multiple game rounds |
| GameplayBinding | The gameplay attached to a live session | Does not imply match participation or battle activation |
| Participation | A streamer/player's seat, faction and withdrawal state in a round | A disconnect does not automatically erase it |
| LoginSession | Chorus authentication and control context | Separate from a platform launch token and a socket |
| Connection | One transport connection to a gateway | A stale disconnect must not revoke its replacement |
| Owner / Ownership | Persistent authority for a particular process instance and entity generation; for rooms it fences writes, for streamers it is the routing picture — session writes are fenced by SessionVersion instead | Liveness, cached routes and client claims cannot grant authority |
| NodeId / ProcessInstanceId | Managed machine identity / identity generated for each process start | Separate from deployment slots and OS PIDs |

## Versions, generations and identifiers

| Field | Scope and purpose | Does not replace |
|---|---|---|
| RoomEpoch / OwnerEpoch | One room's execution authority; checked with owner, status and lease at storage | Tick, RoundGeneration or ProcessInstanceId |
| StateVersion | Version of a room's committed authoritative state | Elapsed real time; equality with Tick is not a universal invariant |
| BaseVersion | State version required to apply a delta | A missing baseline cannot be skipped merely because a newer version arrived |
| RoundGeneration | Successive rounds within a gameplay binding | Room ownership epoch |
| BindingGeneration | Generation of a live gameplay attachment | Connection generation or Tick |
| SessionVersion | Login control version; the streamer-domain write fence, checked during rotation (Login/Resume) and by every session consumer | A new game round — and it is not what the streamer ownership takeover checks; a takeover reroutes without revoking the session |
| ConnectionGeneration | Current connection attachment, fencing stale connection notices | RoomEpoch is not bumped just because a socket reconnects |
| RouteVersion | Version of ControlServer's routing projection, installation and acknowledgement | A route update need not change the room epoch |
| CoordinatorTerm | Authority term for a coordinator lease/partition | Per-room tick or epoch counters |
| TaskEpoch | Execution generation of a platform task | Room state or entitlement to grant rewards |
| OperationId / CommitId / CommandId / EventId | Idempotent identities for operations, frame commits, client commands and platform events | Retrying or migrating does not turn the same event into a new gift |

For kind=room, `control.ownership.epoch` and `game.room.epoch` represent the same room authority generation and must agree through a constrained transaction. They are not independent clocks. Other entities may have their own epoch fields: always specify the entity scope. Prefer “room ownership epoch” over a bare “epoch” in explanatory text.

## Synchronization and recovery

| Term | Meaning | Boundary |
|---|---|---|
| Logical frame / Tick | One input-selection, simulation and commit step | Fixed logical duration does not guarantee identical real execution time |
| Render frame | One client display update | Does not advance authoritative state |
| Snapshot | Complete baseline sufficient to reconstruct a committed state | Client baselines and durable server checkpoints serve different recovery roles |
| Delta | Changes applicable to a particular BaseVersion | An effect list is not automatically a full state delta for every game |
| Catch-up | Install a current baseline, then apply contiguous updates | No default replay of all offline animations or rewards |
| Server replay | Recompute checkpoint-to-head from recorded inputs and verify hashes | Do not repeat external calls or already committed rewards |
| Recovering | Rebuilding and validating state under replacement authority | Process liveness or an accepted RPC is not gameplay readiness |
| Paused | Durable business freeze of logical progress | Leases, heartbeats and wall-clock deadlines continue; recovery must preserve pause |
| Suspended | Fault stop due to invalid/missing recovery material or another error | Not normal completion, business pause or successful recovery |
| Lease | Authority expiry measured by the authoritative clock | Local clocks and process heartbeats cannot grant writing rights |
| Fencing | Rejecting new writes from obsolete owners, epochs or expired leases at storage | Asking the old process to stop is insufficient |
| ACK | Confirmation of a specified stage: acceptance, route installation, baseline readiness, etc. | Accepted, applied and platform-close-confirmed are different outcomes |

## Operation examples

| Operation | RoomId | Tick | Room ownership epoch |
|---|---|---|---|
| Normal play | Unchanged | Advances on commit | Unchanged |
| Client reconnect | Restore original room | Room continues or pauses by policy; client restores current state | Normally unchanged |
| Business pause/resume under same owner | Unchanged | Freeze, then continue | No change caused by pause itself |
| GameServer failover | Unchanged | Resume from committed head, not zero | Advances; obsolete owner is fenced |
| GM BumpRoomEpoch | Unchanged | Not reset; explicit recovery required | Only the target room advances |
| Next match | New RoomId | Independently initialized | Belongs to a different room; not progress relative to the old one |

## References versus project choices

- [Unity: Tick and update rates](https://docs.unity.cn/Packages/com.unity.netcode.gameobjects@2.5/manual/learn/ticks-and-update-rates.html) defines simulation ticks. Independent RoomActors are this project's organization, not a claim about Unity's room architecture.
- [Google's Chubby paper, §2.1](https://storage.googleapis.com/gweb-research2023-media/pubtools/4444.pdf) describes carrying lock-acquisition counts with writes and rejecting obsolete counts. See also [Kafka ProducerFencedException](https://kafka.apache.org/42/javadoc/org/apache/kafka/common/errors/ProducerFencedException.html) and [FenceProducersResult.epochId](https://kafka.apache.org/42/javadoc/org/apache/kafka/clients/admin/FenceProducersResult.html). These support the fencing mechanism, not the correctness of Chorus implementation details.
- Per-room epochs, GM bumps and pause/resume barriers are project target contracts. See the [complete recovery design](../design/room-recovery.md) and repository `todo.md` for implementation and acceptance gaps.
