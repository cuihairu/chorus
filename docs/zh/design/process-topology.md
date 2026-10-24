
# Process Topology: Gateway Split and Room Processes (Design)

> Status: design finalized on 2026-10-12 (user proposal + review cross-check). This is a **target shape and cadence**; implementation advances with the todo.md phases; physical split is not landed until trigger conditions are met.


> Current deployment: use **Chorus.PlatformGateway** (`/ingress/{platform}`) and
> **Chorus.StreamerGateway** (`/ws`) as separate executables. Both use the
> `Chorus.Gateway.Runtime` library; neither depends on the other executable or
> uses `Gateway:Roles`. See [independent gateway deployment](../guide/independent-gateways.md).
> The combined `Chorus.Gateway` and role-switch examples below remain for compatibility.

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
| Two | **Four roles, three processes**: platform-gw, streamer-gw, room-worker (carrying multiple rooms); room ownership is coordinated by ControlHost | Gateway role split and control-plane room-directory work | **Wired; production validation remains open**: one Gateway process can carry the `platform` or `streamer` role, and separate platform-gw / streamer-gw / EngineHost processes can communicate. ControlHost now owns room assignment, worker leases, epochs, durable checkpoints and routing snapshots; see the current multi-worker path in §3 and §7. The earlier RoomManager/MatchRegistry proposal below is retained as historical design context, not the deployed design. |
| Three | Independent Worker processes and, when deployed, machines; a separate RoomManager process is not required for the current ControlHost design | Capacity/load evidence or an operational need to scale out; production activation additionally requires ROOM-07/09/11/12 gates | **Control-plane multi-worker routing is wired; production scale-out remains unvalidated.** ControlHost assigns rooms by live Worker capacity; Gateway consumes a fresh owner/epoch routing snapshot; EngineHost restores and durably commits fenced checkpoints. The original `RoomAffinity`/`RoomRouteTable` prototype remains a library path, while deployed control mode uses `RoomControlStore` and `ControlRoutingSnapshot`. PostgreSQL 16+ primary/synchronous-standby storage and the initial 100-room SLO are selected in `docs/architecture/room-control-storage.md`; provider capacity, replication, and independent-machine fault tests remain rollout gates in todo.md §19 ROOM-07/09/11/12. |

**streamer-ws face unified (the first piece of the §3.5 routing abstraction)**: the `streamer-ws` gates of the two listeners were previously two drifting copies of Server/Gateway, now extracted into one copy of `Chorus.Gateway.Core/Faces/StreamerWsFace`, with only the `IStreamerSessionRouter` / `IStreamerSessionBinding` seams diverging between the two hosts:

- Monolith → `MonolithSessionRouter`: in-process `SessionActor`, welcome generated by `SessionConfig`;
- Gateway → `IpcSessionRouter`: registers the client via Engine IPC, welcome returned by the engine (2s budget, timeout fail-closed).

hello authentication (fail-closed), protocol version gate, message size cap, ACK rate limiting, disconnect cleanup are all single-point implemented, with only one line of face call left at each of the two `/ws` endpoints. Regression guards: `StreamerWsAuthEndpointTests` / `ProtocolVersionEndpointTests` (Server) and `StreamerWsGatewayParityTests` (Gateway-side same-caliber close status, error payloads and `streamerWs` counters, e0501bc).

**platform-ingress face and Tell seam unified (§3.5 ActorRef/Tell semantics and in-process routing table)**: the `/ingress` endpoints of the two listeners (rate limiting/size cap/timeout/signature verification/mapping/deduplication/delivery) are extracted into one copy of `Chorus.Gateway.Core/Faces/PlatformIngressFace`, with delivery semantics landing on the shared `ISessionActorRef.TellAsync` (three-state `SessionTellOutcome`: Accepted / Rejected = bounded mailbox full / Unavailable = transport down) and the `ISessionActorRouter.Resolve(sessionId)` routing table:

- Monolith → `InProcSessionActorRouter`: `SessionRegistry.GetOrCreate` is the routing table (capacity cap + idle sweeping; at capacity it rejects new Actors rather than evicting an existing room), Rejected means mailbox full;
- Gateway → `IpcSessionActorRouter`: publishes events keyed in sessionId order to Engine IPC; the real table is on the engine side, transport failures map to Unavailable.

Both undelivered cases follow the same retry caliber: rollback dedup claim + 503; platform retry is a brand-new delivery. The response body stays faithful to the topology: tick fields are only exposed by the Monolith ref (engine tick only travels with welcome/snapshot); `MatchRegistry` moved with the face into `Chorus.Gateway.Core.Faces`, split mode passes null (room statistics belong to the engine). Regression guards: `PlatformIngressFaceTests` (face 8 cases: Accepted tick field / Rejected and Unavailable rollback 503 / deduplication short-circuit before Tell / unmapped 400 / unsigned 401), `SessionActorRefTests` (in-process ref direct tests: inbox / same session same actor / mailbox full Rejected / TickId exposed), `GatewayIngressTellTests` (Gateway returns consecutive retryable 503s when the engine is down).

**Historical RoomManager proposal (not the deployed design)**: the earlier proposal placed a `RoomManager` on a Worker and upgraded `MatchRegistry` into the room directory. The current implementation instead uses ControlHost as the shared room directory and authority, with `RoomControlStore` and `ControlRoutingSnapshot`; `MatchRegistry` remains a local diagnostic aggregate. The separate RoomManager process was not introduced.

## 5. Two Prerequisites That Must Be Solved Before the Split

### 5.1 Single-point ordering of room events (single-writer invariant)

All inputs of the same room must be ordered within a unique actor (the current single-consumer mailbox is exactly this responsibility). The process split relies on **room affinity** to guarantee this; it is not allowed to have two places writing the same room simultaneously.

### 5.2 Recovery path (recovery half)

A room process restart needs an authoritative snapshot to fall back on. The `IGameModule.RestoreState(version, JsonElement)` and `GameModuleRestorer` provide module restoration; EngineHost reads a ControlHost checkpoint through `ControlCheckpointRestorer`, and tick commits are fenced by the current owner instance and epoch. PostgreSQL 16+ is selected for the production multi-worker topology and its provider is integrated; SQLite remains the development/single-host default. Capacity, synchronous replication, and independent process/network failure acceptance remain open in todo.md §19 ROOM-07/09/11/12. The earlier in-memory `ISnapshotBackend` discussion below describes the pre-ControlHost design.

### 5.3 Cross-process routing form (routing table)

The deployed pattern uses ControlHost to maintain `room → WorkerId + WorkerInstance + Epoch`; Gateway and EngineHost refresh local routing snapshots and fail closed when they expire or disagree with the owner epoch. The streamer and platform gateways consume the same authority. The old RoomManager-pushed table remains a conceptual alternative, not a component in the current path.

## 6. Explicitly Not Done

- No distributed Match cluster;
- No Kafka / RabbitMQ or other external messaging platforms;
- Platform private adapter logic does not go into the core;
- The monolith remains the simplest, easiest-to-debug default deployment form.

> "What is needed most right now is clear boundaries, controllable faults, and correct results, not more components." — external review §10; this design follows the same caliber.
