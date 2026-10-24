
# Chorus (formerly Cohort) Project Analysis and Improvement Suggestions — External Review Archive

> Source: https://chatgpt.com/s/t_6acc63665e7c81919b2d3f5e416c6d0b
> Scrape date: 2026-10-12 (extracted from the share page HTML flight stream, deduplicated and unescaped)
> Nature: **external suggestions, not facts of this repo**. For the landing status of each item, see "Landing Tracking" at the end of this document; implementation is governed by this repo's tests and gates.
> Disposition: first update the docs (this file + README boundaries section + todo.md), then execute in the order phase one → four.
>
> **Timeliness Note (2026-10-13 Audit)**: the problem assertions in the main body of this document are the **state at review time (2026-10-12)**; many have already been fixed by phases one/two (e.g. deduplication atomicity 78f2597, backpressure bounded mailbox 5592d62, ACK safety 499f01c, Tick de-blocking send df71d26, protocol versioning a5f23b8, idle reclamation 362b435, default value 66, AllowAll fail-closed def21d2, etc.). The main body keeps the original review-time appearance without rewriting; **the "Landing Tracking" table and the code prevail**.

---


**Chorus — Live Interactive Game Framework**

A real-time server framework for live interactive games

It uniformly converts external events such as danmaku, gifts, likes, and follows into game inputs, has the server execute authoritative logic, and then synchronizes the state to the streamer client or the streaming companion client.

### 1.1 The Core Problem Chorus Solves

Live interactive games differ from traditional multiplayer online games.

Traditional multiplayer games mainly handle active player input, such as movement, attacks, and skill casts. Live interactive games, on the other hand, have a large number of events coming from external platforms:

- Viewers send danmaku or specific command phrases.
- Viewers send gifts, which may trigger stats or skills.
- High-frequency events such as likes and follows affect game progress.
- Multiple streamers or multiple live rooms may form the same battle.
- External events may arrive in bursts, duplicated, delayed, or out of order.

Therefore, the core value of Chorus is not WebSocket itself, nor an ordinary game loop, but converting such unstable external events into **ordered, bounded, observable, and recoverable game inputs**.

### 1.2 Product Boundaries

| Should belong to Chorus | Should not become a core responsibility of Chorus |
|--------|--------|
| Live platform event adapter interface | Full live platform business backend |
| Event normalization, deduplication, and backpressure | Gift payment, revenue share, commercial settlement |
| Session and Match runtime mechanics | Specific gameplay and art content |
| Server-authoritative Tick and state snapshots | General-purpose MMO world server |
| Client connections, ACK, resync | Full streaming companion client |
| Game module extension interface | Forced dependency on a specific game engine |

It is recommended to keep the lightweight framework positioning and not expand Chorus into a comprehensive platform covering accounts, operations, payments, a game editor, and live management.

## 2. Current Architecture: What Has Been Built?

Judging from the source code, Chorus already has two paths: monolith operation and split operation.

### Default Monolith Mode

**Platform Ingress**

Receives events, validation, normalization, deduplication, faction rules

**SessionManager → SessionActor**

Event queue, Tick, client ACK, state synchronization

**GameModule**

Game rules, authoritative state, snapshot generation

**WebSocket Client**

Welcome, Snapshot, ACK, state recovery after reconnect

Basis: , .

### Optional Split Mode

**Chorus.Gateway**

Public WebSocket, Ingress, client connections

**Chorus.EngineHost**

SessionActor, authoritative Tick, GameModule

**Chorus.Messaging**

IPC Envelope, message bus, UDS / Named Pipe / TCP / HTTP transports

Basis: , , .

### Architecture Assessment

The current layering direction is reasonable, especially:

- `IGameModule` and `IGameModuleFactory` separate gameplay from the runtime framework.
- `SessionActor` serially processes session commands with a single-consumer mailbox, reducing intra-session concurrency complexity.
- `IAudienceEventReducer` provides an extension point for merging peak events.
- IPC uses an independent `Envelope`, allowing the process communication method to be replaced later.
- Tests already cover part of the behaviors of event merging, Match aggregation, message bus, and IPC transport.

But note: **having Actors does not mean the whole system has achieved reliable Tick; having MatchStateStore does not mean an authoritative Match engine has been achieved.** These two distinctions are the focus of the current architecture review.

## 3. Source Code Problems That Need Priority Handling

The following problems are derived from inspecting the current repository's public source code, not merely inferred from design documents.

### P0: Should Be Fixed First

(P0) **1\. Slow clients may drag down the entire Session**

In `SessionActor.OnTickAsync()`, the server calls each client's `SendSnapshotAsync()` one by one and uses `await` to wait for the send to complete. The send path of `WsSessionClient` includes WebSocket network I/O.

This means that when a client's network is blocked, its send lock is not acquired for a long time, or its write speed is extremely slow, the Actor may fail to advance the next Tick as expected. The "slow end does not drag down the whole" principle in the design document has not been thoroughly implemented through the send path.

**Suggested fixes:**

- The Actor is only responsible for generating authoritative snapshots and updating state, and does not directly wait for each client's network send.
- Establish a bounded outbound queue per client, or an equivalent latest-snapshot delivery mechanism.
- Merge or overwrite stale snapshots for slow clients, keeping the latest authoritative state.
- A single client's send failure, disconnect, or timeout must not block other clients.
- The Gateway split mode also needs to check the per-client `await` send logic in `DispatchSnapshotAsync()`.

Acceptance criteria: artificially create a continuously slow-writing client; the other clients and the server-side Tick can still maintain the intended operating cadence.

Source code: , , .

(P0) **2\. Both the Actor mailbox and the scheduled event queue are unbounded**

`SessionActor` uses `Channel.CreateUnbounded<ICommand>()`, while events accumulate by Tick in `_scheduledByTick`. `MaxEventsPerTick` only limits the merged single-Tick output; it cannot limit the total number of commands waiting in the mailbox, nor the number of raw events before a single scheduling.

When the burst event volume stays above the processing capacity for a long time, memory may keep growing; and by the time the Tick executes, the merging itself may also consume large amounts of CPU and memory.

**Suggested fixes:**

- Use a bounded mailbox and define the semantics when it is full.
- Distinguish ordinary control commands from high-frequency audience events, so event floods do not crowd out control operations such as disconnects.
- Set a per-Session total capacity, a per-Tick capacity, and a maximum wait duration for the event buffer.
- Only perform merging, downsampling, or dropping on events the business allows; events with business value such as gifts must not be dropped silently.
- Record queue depth, rejected count, dropped count, merged count, and the oldest event's wait time.

Acceptance criteria: when the sustained input rate exceeds the consumption capacity, memory and queue depth still have a hard upper bound, and the overload behavior can be explained from logs and metrics.

Source code: , .

(P0) **3\. Event merging may count events into the wrong faction**

This is a concrete data correctness problem in the current code.

`DefaultAudienceEventReducer` groups by `UserId` when merging likes, and by `(UserId, GiftId)` when merging gifts; but when generating the merged event, it uses the first event's `MatchId` and `FactionId` from the entire input list as the context of the merged result.

If events of different factions exist in the same Tick, or a user's event context changes, the merged result may merge counts that originally belonged to different factions and record them all into the first event's faction.

**Suggested fixes:**

- Group likes at least by `(SessionId, MatchId, FactionId, UserId)`.
- Group gifts at least by `(SessionId, MatchId, FactionId, UserId, GiftId)`.
- If different business rules or platform semantics may still exist within the same group, continue incorporating the relevant fields into the grouping key.
- Explicitly record the number of source events and the counting caliber for merged events.
- Correctly count the dropped amount for merged groups that could not be output when exceeding the budget.

Acceptance criteria: within one Tick mixing likes and gifts of the red/blue two factions, the per-faction counts must be conserved separately before and after merging.

Source code: .

(P0) **4\. The platform event verifier allows all events through by default**

Both the monolith service and the Gateway currently register `AllowAllPlatformEventVerifier`. This is suitable as a local development stub, but not suitable for directly undertaking the public-network event verification responsibility of the production environment.

**Suggested fixes:**

- When the production environment has no real verifier, reject external Ingress by default, rather than allowing it through by default.
- Define signatures, timestamps, replay protection, and necessary origin validation for each platform.
- Register the development `test` adapter separately from the production adapter.
- Set limits on Ingress request body size, request frequency, and processing timeout.
- Do not treat "the interface exists" as "platform integration security is complete".

Acceptance criteria: when the production configuration lacks a verifier or keys, external Ingress cannot receive unverified events.

Source code: , , .

(P0) **5\. ACK does not validate monotonicity and the server-side Tick upper bound**

Currently, after receiving an ACK, `SessionActor` directly updates `LastAckTickId`; it neither rejects ACKs beyond the current `TickId` at this entry point, nor requires the ACK to be no lower than the recorded progress.

If the client accidentally sends wrong data, or a malicious client submits a future Tick, the server may mistakenly believe it has caught up, thereby affecting lag determination and resync.

**Suggested fixes:**

- Reject negative ACKs or ACKs greater than the server's current Tick.
- Do not allow ACK progress to go backwards.
- ACK must be bound to the currently valid Session and Client identity.
- Distinguish illegal ACKs, duplicate ACKs, stale ACKs, and normal ACKs, and count them into metrics.

Acceptance criteria: submitting future Ticks, backwards Ticks, or ACKs with another client's identity must all fail to make the server erroneously update synchronization progress.

Source code: , .

### P1: Runtime Mechanics Problems to Solve in the Next Round

**6\. Tick scheduling cannot rely solely on PeriodicTimer triggering**

Currently `SessionActor` uses `PeriodicTimer` to drive Tick. Since Tick processing includes event merging, game logic, snapshot generation, and network sending, once the processing time exceeds the configured period, the actual logic advancement may fall behind the wall clock.

`TickId` still incrementing does not mean every Tick happens on time.

It is recommended to explicitly use a monotonic clock to compute Tick deadlines and define a timeout policy: record timeouts, limit overload work, and downgrade or pause that Session's intake when necessary, rather than endlessly catching up on Ticks. Do not execute logic continuously without upper bound in order to catch up with time.

In addition, `SessionConfig` defaults `TickDurationMs = 100`, i.e. 10 logic advancements per second; the 50–66ms recommended in the docs is another set of starting suggestions. The docs and the default configuration should be unified, and the final parameters should be determined through load testing.

Source code: , .

**7\. Match state is lock-protected, but there is no unified Match execution semantics yet**

`SampleGameModuleFactory` shares one `MatchStateStore` across multiple Sessions and uses locks to protect the aggregated data. Therefore, one cannot simply say it has unprotected concurrent writes.

The real problem is: each Session has its own `SessionActor` and `TickId`, and the shared Match state is advanced by these Sessions at different times. The current snapshot may reflect events already processed by some Sessions, while events of the corresponding Tick for other Sessions have not yet been processed.

For purely display-type total likes, this may be acceptable; but if Match state determines win/loss, faction skills, settlement, or fairness, a more explicit authoritative ordering is needed.

It is recommended to distinguish two requirements:

- **Weakly consistent display aggregation**: keep the shared state store, define it as only for statistical display, and make clear it is not an atomic snapshot of the same Match Tick.
- **Authoritative battle state**: add a `MatchActor` or an equivalent unique authoritative executor, which orders and processes cross-Session Match events.

It is not recommended to convert all Sessions into MatchActors now; first clarify the actual requirements of the gameplay for cross-room consistency.

Source code: , .

**8\. The deduplication check is not atomic, and it happens before the event is successfully delivered**

`EventDeduplicator.TryMark()` first queries the cache, then writes to the cache. When two concurrent requests carry the same event ID, there is a possibility that both pass the query simultaneously.

In addition, the Gateway marks deduplication first, then publishes the IPC message. If publishing fails, subsequent retries may still be judged as duplicates, causing the event to never actually enter the Engine while it cannot be retried with the same event ID.

It is recommended to handle this in phases:

1. Change local deduplication to an atomic reservation operation to eliminate concurrent duplicate entry.
2. Explicitly distinguish the event processing states `Received`, `Accepted`, `Applied`, and `Failed`.
3. For events requiring reliable delivery, introduce a bounded retry queue or a persistent Inbox; do not rely solely on an in-memory cache.
4. Define a platform-level unique ID, deduplication scope, and TTL for events.
5. Clarify the business semantics: successful receipt does not mean the game logic has been applied.

Reliable delivery and persistence do not have to be built as a complex messaging platform from the start, but the interface semantics should be designed correctly first.

Source code: , .

**9\. Some statistics values currently output by MatchRegistry are fixed zeros**

`MatchRegistry`'s `GetDiagnostics()` returns zero directly for `UserCount`, `Likes`, `Gifts`, `Comments`. What it currently actually tracks are the Sessions and Factions corresponding to the Match, the total event count, and the last event time, rather than complete gameplay aggregated statistics.

This is not a concurrency bug, but the interface easily makes users mistakenly think these fields are real-time statistical results.

It is recommended to choose one of two options:

- If it is only responsible for registration and diagnostics, delete the unimplemented statistical fields, or explicitly mark them as unavailable.
- If real statistics need to be displayed, have it read the true aggregated state and unify the counting caliber after deduplication and merging.

Source code: .

**10\. Session lifecycle and client message boundaries need to be completed**

Currently `SessionManager` provides mechanisms for creating and getting Sessions, but no complete idle Session reclamation strategy is seen. The WebSocket access also does not reflect a complete client authorization flow at this entry point; the helper code for receiving messages does not set an explicit maximum message body size.

It is recommended to add:

- Explicit states for Session creation, startup, termination, and destruction.
- Idle timeout, maximum Session count, and reclamation strategy.
- WebSocket message body size limits, ACK rate limiting, and protocol error handling.
- Production-environment Session access authorization, to prevent unauthorized clients from joining arbitrary sessions.
- Isolation, failure state, and recovery strategy when the game module throws exceptions, to avoid a single exception silently exiting the Actor task.

Source code: , .

**11\. The IPC auto-fallback behavior needs verification**

In `IpcMessageBusFactory`, the server side in auto mode can fall back to Named Pipe after a UDS attempt fails, but the client-side transport auto-selection logic mainly decides based on whether the operating system supports UDS. If the server actually falls back to Named Pipe, the client may still try to connect via UDS.

It is recommended that both sides negotiate or determine the actually selected transport, rather than each inferring independently. Also add tests for UDS path permissions, legacy socket files, connection retry, disconnect recovery, and message body size.

This is a reliability problem of the split mode, with priority lower than core event correctness, but it should be fixed before split deployment is formally supported.

Source code: .

## 4. How Should the Event Processing Model Be Redefined?

This is the most important domain model of Chorus. It is recommended to first clarify the lifecycle of each event, and then decide whether to introduce more components.

**1\. Received**

Retain the platform, raw event ID, receipt time, and source information

**2\. Validated**

Signature, format, origin, field range, and event type validation

**3\. Deduplication and Normalization Accepted**

Determine the Session, Match, Faction, and business semantics

**4\. Scheduled**

Enter the specified Tick according to explicit ordering rules

**5\. Applied**

The game logic actually consumes it, producing authoritative state changes

**6\. Published**

Generate snapshots, send state, and record synchronization results

### 4.1 Event Fields That Must Be Clarified

It is recommended to gradually clarify the following semantics on top of the existing `AudienceEvent`; there is no need to add all fields to the public model at once.

| Field or Concept | Purpose |
|--------|--------|
| `EventId` | Stable event ID provided by the platform or adapter |
| `Platform` | Event source, used for deduplication and audit |
| `IngestTimeMs` | The time Chorus received the event |
| `PlatformEventTime` | Optional; the event time declared by the platform |
| `SessionId` | The game session the event affects |
| `MatchId` / `FactionId` | The battle and faction the event affects |
| `ScheduledTickId` | The Tick the event is expected to take effect in |
| `AppliedTickId` | The Tick the event was actually applied in |
| `ProcessingStatus` | The event's current processing status or result |

`ScheduledTickId` and `AppliedTickId` can first serve as internal scheduling records, rather than being immediately exposed to all platform adapters.

### 4.2 Do Not Treat All Events as Mergable Events

It is recommended to distinguish by semantics:

- **Accumulable events**: like counts, stackable gift quantities.
- **Business events that must not be dropped casually**: paid gifts, one-shot business effects, events that need auditing.
- **Presentational events**: chat danmaku display, notification animations, short-term notices.
- **Control events**: session start, end, faction change, reconnect, etc.

Backpressure strategies must respect business semantics. For example, likes can be accumulated and settled once, but special skills triggered by two different users cannot simply be merged as the same event.

At the same time, merged events need stable counting rules; otherwise the server's totals, faction scores, and client display may become inconsistent.

## 5. Synchronization Architecture: Keep Authoritative Snapshots, but Close the Recovery Loop

Chorus's current synchronization model is the right direction: the server keeps advancing the authoritative state, clients report applied progress via ACK, and receive forced snapshots when they fall too far behind.

However, snapshots by themselves cannot replace a complete runtime recovery mechanism.

It is recommended to add four capabilities:

1. **Snapshot version**: includes protocol version, state Schema version, and necessary game module version information.
2. **Snapshot integrity**: limit snapshot size, validate necessary fields, and define the handling when it cannot be applied.
3. **Client resync loop**: when a client fails to apply a snapshot it must be able to re-request or reconnect; it cannot rely solely on the server repeatedly pushing.
4. **State publication isolation**: network sending does not block authoritative logic; slow clients may skip old snapshots, but must know they need to re-align to the latest authoritative state.

There is a distinction worth emphasizing here: if a snapshot represents complete authoritative state, then slow clients usually do not need to receive every historical snapshot. Old snapshots can be merged, keeping only the latest one. But if certain messages carry one-shot business semantics, they must be handled through an independent reliable event channel and cannot be stuffed into the overwritable snapshot queue.

Reference: , .

## 6. Project Rename: Cohort → Chorus Is Not Fully Complete

I confirmed in the current files of the new repo that there are still multiple Cohort identifiers internally, including the project directory, assemblies, solution, CI workflows, and documentation. Renaming the repository and renaming the product are not the same thing; it is recommended to first decide whether a full internal rename is needed.

### Suggested Handling Checklist

### Rename Wrap-Up Checklist

/

[Copy Checklist]

### Should all `Chorus.*` also be renamed to `Chorus.*`?

My suggestion is: **if Chorus is an already-determined long-term project brand, unify the rename now while it has not been widely released.**

The suggested targets are as follows:

| Current identifier | Suggested target |
|--------|--------|
| `Chorus.sln` | `Chorus.sln` |
| `Chorus.Protocol` | `Chorus.Protocol` |
| `Chorus.Engine` | `Chorus.Engine` |
| `Chorus.Server` | `Chorus.Server` |
| `Chorus.Gateway` | `Chorus.Gateway` |
| `Chorus.EngineHost` | `Chorus.EngineHost` |
| `Chorus.Messaging` | `Chorus.Messaging` |
| `Chorus.SampleGame` | `Chorus.SampleGame` |
| `Chorus.Engine.Tests` | `Chorus.Engine.Tests` |

But there is one exception: if you have already released public NuGet packages, external assembly dependencies, or externally committed protocol names, a compatibility strategy needs to be considered. If currently it is only a GitHub repository name change, the cost of assembly renaming is relatively controllable.

Note that this must not be done with text replacement alone. Directories, `.csproj`, project references, namespaces, the solution, CI, the documentation site, and startup commands should be modified in sync, followed by a full build and test.

## 7. Test System: From Unit Tests to Fault Verification

The current repository already has tests covering event merging, MatchRegistry, SampleGameModule, the IPC message bus, and several transport implementations. This is a good foundation, but the focus of testing still needs to shift from "the feature runs" to "the system is still correct under abnormal conditions".

It is recommended to complete the following tests:

| Test Type | Behavior That Must Be Verified |
|--------|--------|
| Event merging | Mixed factions, mixed Matches, no count loss when over limit, no faction mixing |
| Deduplication concurrency | Concurrent submission of the same event allows only one effective processing |
| Reliable delivery | Retry after IPC publish failure is not erroneously deduplicated |
| Tick overload | Game logic exceeding the Tick budget does not accumulate without bound |
| Slow client | One client blocking sends does not affect other clients and the server Tick |
| ACK safety | ACKs with future Ticks, backwards Ticks, invalid Clients are rejected |
| Match concurrency | When multiple Sessions input simultaneously, the authoritative state order is explicit |
| Lifecycle | No resource leaks on Session timeout reclamation, disconnect, reconnect, and close |
| Snapshot recovery | Clear error paths for oversized, corrupted, version-mismatched snapshots |
| Security | Unverified platform events, oversized request bodies, and illegal WebSocket messages are rejected |
| IPC fault | Disconnect, restart, transport fallback, half-packet and oversized-packet handling are correct |

The most important among these is establishing an **end-to-end load test**: simulate a large number of audience events, multiple Sessions, clients of different speeds, and random disconnects simultaneously, then observe Tick duration, memory, queue depth, and event counts.

It is recommended to make the load test an independent tool or test project, and not stuff high-load tests into every ordinary unit test.

## 8. Observability: Needs to Upgrade from "How Many Interfaces" to Runtime Diagnostics

Currently `/sessions` and `/matches` can provide some diagnostic data, but it is not enough to locate production problems.

It is recommended to establish at least the following metrics:

- `chorus_tick_duration_ms`: the actual processing duration of each Tick.
- `chorus_tick_lag_ms`: the deviation between the actual Tick and the planned deadline.
- `chorus_ingress_events_total`: total number of received events.
- `chorus_events_applied_total`: number of events actually applied.
- `chorus_events_merged_total`, `chorus_events_dropped_total`: merged and dropped counts.
- `chorus_ingress_duplicates_total`: number of duplicate events.
- `chorus_mailbox_depth`, `chorus_scheduled_events`: queue and pending-event counts.
- `chorus_client_ack_lag_ticks`: how far behind each client's ACK is.
- `chorus_snapshot_send_duration_ms`: snapshot send duration.
- `chorus_snapshot_resync_total`: number of forced snapshots.
- `chorus_ipc_publish_failures_total`: number of IPC publish failures.

It is also recommended to establish correlation IDs for the event processing chain, so that logs can answer:

> When was this gift event received, which Tick was it scheduled to, when was it applied, what state did it change, and when did the client receive the corresponding snapshot?

There is no need to immediately introduce a complex distributed tracing platform to implement these capabilities. You can start with .NET native logging, Metrics, and OpenTelemetry-compatible interfaces, retaining the ability to connect to external monitoring systems.

## 9. Recommended Implementation Route

I suggest dividing the improvements into four phases. In order: first ensure data correctness, then ensure stability under overload, and only after that productionization and extension.

### Phase One: Correctness and Security Boundaries

(Highest priority)

Goal: avoid wrong counting, wrong synchronization, and unauthorized input.

- Fix the cross-faction event merging problem.
- Fix ACK monotonicity and Tick upper bound validation.
- Production rejects unverified platform events by default.
- Fix concurrent deduplication and IPC post-failure retry semantics.
- Clarify whether Match-level state requires authoritative ordering.

---

### Phase Two: Stability and Overload Protection

Goal: no unlimited growth during peak periods, and no being dragged down by slow clients.

- Change the Actor mailbox and event buffer to bounded mechanisms.
- Remove blocking network sends from the authoritative Tick.
- Clarify Tick timeout, overload, and recovery strategies.
- Add lifecycle and resource reclamation for Sessions.
- Add tests for mixed factions, slow clients, event floods, and fault injection.

---

### Phase Three: Observability and Recovery

Goal: when problems occur, the cause can be located, rather than blindly tuning parameters.

- Establish Tick, queue, event, and snapshot metrics.
- Define snapshot version, size limits, and application failure paths.
- Clarify event processing states and reliable delivery boundaries.
- Verify IPC disconnect, restart, and transport fallback.
- Establish a repeatable load test.

---

### Phase Four: Release and Ecosystem Completion

Goal: enable other developers to understand, integrate, and deploy Chorus.

- Complete the internal naming wrap-up of Cohort → Chorus.
- Provide a clear minimal game module example.
- Provide platform adapter templates and integration contracts.
- Complete the deployment configuration, security configuration, and troubleshooting documentation.
- Clarify the versioning policy, compatibility, and release process.

## 10. What Is Not Worth Doing for Now?

To keep Chorus small, I do not recommend adding the following complexity at the current stage:

- Do not immediately introduce a full distributed Match cluster just to support multiple Sessions.
- Do not directly introduce external messaging platforms such as Kafka or RabbitMQ just for reliable delivery. Define the reliability requirements first, then choose the implementation.
- Do not support incremental snapshots, complex compression, rollback netcode, and deterministic replay too early. First get the existing snapshot mechanism right.
- Do not put the private adapter logic of all live platforms directly into the core project.
- Do not develop Chorus into a comprehensive platform covering accounts, commercial operations, live management, and a game editor.
- Do not split out a large number of independent services just for the sake of abstraction. The monolith should continue to be the simplest, easiest-to-debug default deployment form.

**What is needed most right now is clear boundaries, controllable faults, and correct results, not more components.**

## 11. Final Conclusion

### Overall Assessment

| Dimension | Assessment |
|------|------|
| Product positioning | Holds |
| Basic architecture layering | Reasonable direction |
| Event and state correctness | Needs priority fix |
| High-load and slow-end isolation | Needs strengthening |
| Production security and reliability | Still needs improvement |

I believe Chorus is worth continuing. Its core positioning is quite clear: **not a general-purpose game engine, but a server framework that lets the group interaction of live platforms reliably drive real-time games.**

What is most worth doing next is not rewriting the architecture, but based on the existing code, concentrating on fixing five things:

1. Event merging must not mix data across factions.
2. Slow clients must not block the authoritative Tick.
3. All event queues must have capacity bounds.
4. ACK, deduplication, and delivery semantics must be correct.
5. Production must have real platform validation and client access boundaries.

After these five items are completed and tested, then decide whether an authoritative MatchActor, a persistent event Inbox, or a more complex deployment form is needed.

Finally, regarding the rename, it is recommended to unify on Chorus as early as possible, to avoid the repo, assemblies, documents, and CI carrying two names for a long time. Since the project brand is already determined, let the code and documentation also form complete consistency around the same name.

---

If you want, I can:

- Analyze the impact of slow clients on the system and improvement options
- Discuss the problem of event merging into wrong factions and its correction

---

## Landing Tracking (this repo's execution record, checked off as done)

- [x] **Phase 1 (Correctness and Security Boundaries)**: ① merge cross-faction data mixing (✅ 2026-10-12, composite key (S,M,F,U)/(S,M,F,U,GiftId), acceptance test `DefaultAudienceEventReducerTests.Reduce_MixedFactions_ConservesPerFactionCounts`, commit 9ad322d) ② ACK monotonicity and Tick upper bound (✅ 2026-10-12, invalid/duplicate/stale/accepted four-category counts + client binding, acceptance test `SessionActorAckTests` 6 cases; also fixed the scheduling bug where the run loop silently died after switching to command-driven, commit 499f01c) ③ production rejects unverified Ingress by default (✅ 2026-10-12, `Ingress:AllowAll` defaults to false + fail-closed routing, acceptance test `IngressAllowAllEndpointTests`, commit def21d2) ④ deduplication atomicity and delivery failure semantics (✅ 2026-10-12, reserve/commit/rollback atomic protocol, publish failure Rollback is retryable, acceptance `EventDeduplicatorTests` concurrent exactly-one-winner, commit 78f2597; event lifecycle and four states landed in spec.md §3.6) ⑤ Match authoritative semantics qualification (✅ 2026-10-12, spec.md §2.3: display aggregation is not authoritative, authorization condition = single Match ordering point, conclusion first without implementing MatchActor)
- [x] **Phase 2 (Stability and Overload Protection)**: bounded mailbox and event buffer (✅ 5592d62, control/event separate lanes + gift bounded wait Failed retryable), authoritative Tick de-blocking send (✅ df71d26, Engine+Gateway per-client latest-overwrite outbound queue), Tick timeout policy (✅ 362b435, `TickTimeoutMs` detection + default 66ms unified), Session lifecycle reclamation (✅ 362b435, cap eviction + idle reclamation + GameModule exception isolation), fault injection tests (✅ 5592d62/df71d26 acceptance: flood drop count, slow client tick doesn't stop, module throws exception actor survives)
- [x] **Phase 3 (Observability and Recovery)**: tick/queue/event/snapshot metrics (`chorus_*` naming, ✅ b86f5a8 three-side `/metrics` + `chorus_ipc_publish_failures_total`), snapshot version and size cap (✅ 768e668 `V=1` + `MaxSnapshotBytes` skip count, acceptance `SessionActorSnapshotTests`), IPC disconnect fallback verification (✅ d5d2d64 lazy reconnect recovery after restart / explicit failure when down / Auto UDS↔NamedPipe fallback, acceptance `IpcResilienceTests` 3 cases), event correlation ID (✅ 68f29a9 `Envelope.SourceEventId` + `docs/reference/observability.md`, acceptance `CorrelationTests` 3 cases), repeatable load test (✅ 578415b `src/Chorus.LoadTool`: multi Session × audience events × variable-speed clients × random disconnects, acceptance `LoadToolTests` 6 cases + real Server smoke; doc `docs/guide/load-testing.md`), snapshot recovery loop (✅ 8226727 `SessionActorResyncTests`), Match concurrency without loss (✅ 8226727 `MatchConcurrencyTests`; the qualification that concurrent order is not guaranteed, see spec §2.3). Also: fixed in the same batch the IPC bus double Dispose throwing `ObjectDisposedException` (reproducible in production path, added idempotent guard + 4 regression tests)
- [x] **Phase 4 (Release and Ecosystem)**: Cohort → Chorus internal naming wrap-up (✅ executed early, see todo.md §9), minimal game module example and platform adapter contract (✅ 8a49c1b `docs/guide/game-module.md` + `docs/reference/adapter-contract.md`, contract and code checked word by word, acceptance points = interface signature/processing order/status codes/template compilable), deployment security and versioning policy (✅ 8a49c1b `docs/reference/deployment.md` + `docs/reference/versioning.md`, ports and endpoints verified via `launchSettings.json` and each host `Program.cs`, and one factual error corrected: `/sessions` and `/matches` are only exposed in single-process form)
- [x] Test completion (eleven categories): merge mixed factions ✅ / deduplication concurrency ✅ / delivery failure retry ✅ / Tick overload ✅ / slow client ✅ / ACK safety ✅ / Match concurrency ✅ (8226727) / lifecycle ✅ / snapshot recovery ✅ (8226727) / security ✅ / IPC fault ✅ (d5d2d64)
- [x] Event lifecycle model (Received→Validated→Accepted→Scheduled→Applied→Published) and the four event categories (accumulable / non-droppable / presentational / control) landed in `docs/design/spec.md` (✅ 78f2597, §3.6 six stages + four delivery states + reserve/commit/rollback + four-category event table; this item is a duplicate registration of phase 1 ④, checked off after verifying the content exists)
