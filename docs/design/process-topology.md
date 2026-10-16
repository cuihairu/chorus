<p align="center">
  English | <a href="process-topology.zh.md">简体中文</a>
</p>

# Process Topology: Gateway Split and Room Processes (Design)

> Status: design finalized on 2026-10-12 (user proposal + review cross-check). This is a **target shape and cadence**; implementation advances with the todo.md phases; physical split is not landed until trigger conditions are met.

## 1. Proposal and Conclusion

The proposed form has four roles:

1. **Platform gateway**: connects to live-streaming platforms such as Douyin / Kuaishou (HTTPS callbacks, open platform long connections);
2. **Streamer gateway**: connects to the streamer client and the streaming companion client (WebSocket, ACK, resync; English identifier `streamer-gw`);
3. **Room management process**: room registration, allocation, health, routing table;
4. **Room process × N**: authoritative tick and GameModule per room.

**Conclusion: the role division holds, but physical processes advance by "roles first, processes later", not splitting into four at once.**

## 2. Why the role division holds

- **Two fault domains**: platform-side burst floods, signature verification, rate limiting, and streamer-side authentication, ACK, resync are unrelated; after splitting, one side being overwhelmed does not take the other side down; rate limiting/verification/timeout are each configured independently (corresponding to review P0-4 "Ingress allows by default" and P1-10 "client message boundary").
- **Separation of scheduling and execution**: room registration/allocation/health/routing belongs to the manager, authoritative tick and gameplay belong to the worker; cross-machine extension does not require refactoring.
- **Existing foundation**: `Chorus.Messaging`'s `Envelope` + UDS/NamedPipe/TCP/HTTP is the inter-process channel; the existing Gateway ↔ EngineHost split pattern is the prototype of "streamer gateway ↔ worker".

## 3. Key fact: the bottleneck is event throughput, not connection count

In live-stream interaction, **viewers do not connect directly to this system** (they connect to Douyin/Kuaishou); this system's connection count is only the streamer client and the streaming companion, which is small; what is really amplified is the **event volume** (danmaku flood peaks). Therefore:

- **One room per process is not adopted**: one room = one `SessionActor`, a single worker process carries dozens to hundreds of rooms;
- **The room management process is a logical component in the single-machine phase** (hosted), not a necessary physical process;
- The scaling basis is events per second and worker CPU, not the number of rooms.

## 4. Phases and Trigger Conditions

| Phase | Form | Trigger Condition | Status (checked item by item on 2026-10-13) |
|---|---|---|---|
| One | **In-monolith code layering**: `platform-ingress` and `streamer-ws` two listeners share one set of face implementations (`PlatformIngressFace` / `StreamerWsFace`, single point for validation/rate limiting/timeout/deduplication/gate), deployed in the same process (the monolith is still the default deployment form) | Platform integration batch | **Completed**: todo §2 platform integration three items, §3 Match/faction three items all checked; the gate and delivery of the two listeners have been unified (next two sections) |
| Two | **Four roles, three processes**: platform-gw, streamer-gw, room-worker (carrying multiple rooms); `RoomManager` as a logical component hosted on the worker side, with `MatchRegistry` upgraded to room registration + routing table | Five correctness items of todo.md phase one completed | **Triggered (5/5: 9ad322d, 499f01c, def21d2, 78f2597, 0409cf2), in progress**: process split has been delivered (`Gateway:Roles`, 15a6dad) — one Gateway process can carry only the `platform` or `streamer` role, platform-gw / streamer-gw / room-worker (EngineHost) three processes become a configuration option, faces have zero duplication; three-process wiring is through (5c5d25c, 2026-10-13: eng→gw channel distinguished by `Gateway:Id` suffix, engine fans out by `Ipc:GatewayIds`, guard `ThreeProcessEndToEndTests`: platform-gw injected events appear on streamer-gw's WS client); the room side has not yet been upgraded (see below "When RoomManager comes up") |
| Three | room-worker independent multi-process/multi-machine, room-manager only becomes a separate process then (room affinity + snapshot recovery) | Single worker peak CPU > 60%, or single-machine room count reaches hundreds magnitude | **Not satisfied, 2 items missing**: ① single worker peak CPU has no data (`Chorus.LoadTool` smoke is functional verification, hundreds of events magnitude, not peak measurement); ② single-machine room count has no hundreds-magnitude deployment data. Both are runtime metrics that cannot be self-proven in the repo; they are left to production/long-term load test peak data (instruments are ready: `chorus_process_cpu_percent` worker process CPU metric + LoadTool runtime sampling reporting peaks, see todo §4 peak measurement items). **The data-independent code half has been delivered first (be2eff7, 2026-10-13)**: `RoomAffinity` (FNV-1a stable hash + pure function mapping of sorted worker set — the same room always maps to the same worker under the same worker set, §5.1 single-writer invariant) + `RoomRouteTable` (whole-map-swapping shared routing table, room-manager pushes, both gateways read, §5.3), guard `RoomAffinityTests` 7 cases; **no production wiring yet**, consumers (room-manager publishing routes, gateway pre-routing) wait for phase three trigger before connecting. The recovery half (§5.2) is not gated by these two data items; its `IGameModule` recovery contract has been decided and landed (`062cc68`, 2026-10-14), production wiring still waits for phase three trigger |

**streamer-ws face unified (the first piece of the §3.5 routing abstraction)**: the `streamer-ws` gates of the two listeners were previously two drifting copies of Server/Gateway, now extracted into one copy of `Chorus.Gateway.Core/Faces/StreamerWsFace`, with only the `IStreamerSessionRouter` / `IStreamerSessionBinding` seams diverging between the two hosts:

- Monolith → `MonolithSessionRouter`: in-process `SessionActor`, welcome generated by `SessionConfig`;
- Gateway → `IpcSessionRouter`: registers the client via Engine IPC, welcome returned by the engine (2s budget, timeout fail-closed).

hello authentication (fail-closed), protocol version gate, message size cap, ACK rate limiting, disconnect cleanup are all single-point implemented, with only one line of face call left at each of the two `/ws` endpoints. Regression guards: `StreamerWsAuthEndpointTests` / `ProtocolVersionEndpointTests` (Server) and `StreamerWsGatewayParityTests` (Gateway-side same-caliber close status, error payloads and `streamerWs` counters, e0501bc).

**platform-ingress face and Tell seam unified (§3.5 ActorRef/Tell semantics and in-process routing table)**: the `/ingress` endpoints of the two listeners (rate limiting/size cap/timeout/signature verification/mapping/deduplication/delivery) are extracted into one copy of `Chorus.Gateway.Core/Faces/PlatformIngressFace`, with delivery semantics landing on the shared `ISessionActorRef.TellAsync` (three-state `SessionTellOutcome`: Accepted / Rejected = bounded mailbox full / Unavailable = transport down) and the `ISessionActorRouter.Resolve(sessionId)` routing table:

- Monolith → `InProcSessionActorRouter`: `SessionRegistry.GetOrCreate` is the routing table (capacity cap + idlest eviction + idle sweeping all in the shared lifecycle), Rejected means mailbox full;
- Gateway → `IpcSessionActorRouter`: publishes events keyed in sessionId order to Engine IPC; the real table is on the engine side, transport failures map to Unavailable.

Both undelivered cases follow the same retry caliber: rollback dedup claim + 503; platform retry is a brand-new delivery. The response body stays faithful to the topology: tick fields are only exposed by the Monolith ref (engine tick only travels with welcome/snapshot); `MatchRegistry` moved with the face into `Chorus.Gateway.Core.Faces`, split mode passes null (room statistics belong to the engine). Regression guards: `PlatformIngressFaceTests` (face 8 cases: Accepted tick field / Rejected and Unavailable rollback 503 / deduplication short-circuit before Tell / unmapped 400 / unsigned 401), `SessionActorRefTests` (in-process ref direct tests: inbox / same session same actor / mailbox full Rejected / TickId exposed), `GatewayIngressTellTests` (Gateway returns consecutive retryable 503s when the engine is down).

**When RoomManager comes up (phase two room side, deliberately not done now)**: the originally designed "`RoomManager` hosted on the worker side + `MatchRegistry` upgraded to room registration + routing table" today has two empty-implementation risks — under a single worker, the "room → worker" mapping is always 1:1, and the routing table is already undertaken by the worker-side shared `SessionRegistry` (capacity cap / idlest eviction / idle sweeping, `SessionRegistryTests`); `MatchRegistry` is still display-level aggregation (spec.md §2.3 qualitative). Therefore phase two first only does the "process splittable" half (`Gateway:Roles`), and room registration / health / route pushdown waits until phase three (multi-worker) triggers, to avoid speculative components.

## 5. Two Prerequisites That Must Be Solved Before the Split

### 5.1 Single-point ordering of room events (single-writer invariant)

All inputs of the same room must be ordered within a unique actor (the current single-consumer mailbox is exactly this responsibility). The process split relies on **room affinity** to guarantee this; it is not allowed to have two places writing the same room simultaneously.

### 5.2 Recovery path (recovery half)

A room process restart needs an authoritative snapshot to fall back on. The contract gap has been closed: `IGameModule` now has the mandatory write-back entry `RestoreState(version, JsonElement)` (decision A finalized on 2026-10-14 and landed, `062cc68`) — recovery input takes the line-protocol JSON (`version` passes through the line protocol `ServerSnapshot.v`), the single point `GameModuleRestorer` executes inside the actor factory (after `Create`, before `SessionActor`); before recovery completes, no tick/ingress events reach the module; recovery failure fail-closed rejects session startup (`SessionRegistry.StartFailures` is observable, no half-state actor left); both birthpoints share the same executor → parity by construction. There is no production wiring currently (recovery source defaults to null = fresh start, behavior unchanged); **the unclosed half** is "where the authoritative snapshot comes from" — persistence medium selection and write timing (the contract layer already has the `ISnapshotBackend`/`SnapshotStore` and `ISnapshotRecoverySource` seams ready), to be wired when phase three triggers. Contract options, tradeoffs and implementation are detailed in [snapshot-restore.md](./snapshot-restore.md).

### 5.3 Cross-process routing form (routing table)

Generalize the existing pattern: the manager maintains the `room → worker` mapping and pushes it to the gateways; the gateway uses it to select the channel to the worker (or use the worker's reverse channel), with the streamer gateway and the platform gateway sharing the same routing table.

## 6. Explicitly Not Done

- No distributed Match cluster;
- No Kafka / RabbitMQ or other external messaging platforms;
- Platform private adapter logic does not go into the core;
- The monolith remains the simplest, easiest-to-debug default deployment form.

> "What is needed most right now is clear boundaries, controllable faults, and correct results, not more components." — external review §10; this design follows the same caliber.
