# Multi-streamer synchronization, reconnect and anti-cheat

Target contract, 2026-10-29. Synchronization means a single authoritative room commits logical frames and distributes state. See the separate [current threading model](./gameserver-threading-current.md) for implementation facts.

![Authoritative synchronization and reconnect](/images/authoritative-sync-en.svg)

[Open full SVG](/images/authoritative-sync-en.svg)

## Shared authority and convergence

All participants reference one RoomId and one valid owner/epoch. The server orders accepted inputs, computes a tick, commits state/input consumption/effects, and only then publishes. Clients cannot decide scores or assign inputs to ticks using their clocks.

Messages identify room, epoch, tick, state version, module version and config hash. A delta applies only to its declared BaseVersion. Duplicate/older versions are discarded; a gap requires a full snapshot. Delta data must actually reconstruct the required presentation state; an effect list alone is not a universal state diff. Use snapshots where a module lacks a delta contract.

Clients converge to the same state at the same committed version. Network delays prevent a promise of identical display time. Interpolation or prediction is presentation-only and corrected by authoritative state. ACKs are progress hints, not reward evidence. A slow client cannot hold the room tick; bounded queue overflow triggers a new baseline.

## Reconnect and catch-up

A disconnect preserves participation. Resume authenticates and rotates the current session, resolves the original room from server facts and attaches a new connection generation. An old gateway close notice cannot disconnect the replacement.

Default catch-up uses a coherent committed head snapshot, then contiguous deltas after that head. Subscription and head capture must share a publication boundary or resumable commit cursor. Overflow during snapshot installation restarts baselining. Epoch changes require a new route and full snapshot; version numbers across epochs are not one delta chain. A completed room returns the frozen result and settlement progress. History replay is optional future optimization, not a promise of unlimited incremental recovery. Historical effects and rewards are not executed again on clients.

## All controlling streamers offline

The server derives presence from current control sessions, connection generations, heartbeat deadlines and a grace period. A spectator disconnect or zero viewers does not mean all controlling streamers are offline. Unexpected disconnect, live end, explicit exit and server failure are separate causes.

Pin AllOfflinePolicy, OfflineGraceDuration, MaxPauseDuration, ResumeRequiredReadyCount and PausedInputPolicy in the persisted room config/hash:

- ContinueToEnd: current default; keep ticking to the result and never auto-queue offline streamers.
- PauseAtBoundary: commit a durable Paused boundary after grace and freeze logical ticks/timers. Keep wall-clock leases, credentials and platform heartbeats running.
- FinishByRule: commit the game's end/abort/compensation outcome through the normal close barrier.

During pause, platform events require an explicit bounded durable-buffer, out-of-round disposition or rejection policy. Never silently lose accepted gifts. Resume preserves accepted ordering and per-tick budgets. Resume requires the configured number of authenticated participants to complete baseline preparation, followed by a committed resume boundary. Readiness has a deadline and is bound to session/epoch. Max pause duration prevents indefinite stalling. Restart restores Paused rather than accidentally starting ticks. Business pause is distinct from fault suspension or a GM epoch bump.

## Anti-cheat boundary

The server validates session, tenant/game, participation, connection generation, owner/epoch, action rules, resources, cooldowns, sequence and rate. Clients submit intent, never authoritative rewards or scores. Gift/like facts come from verified platform events. Command/Event IDs and fingerprints prevent replay and reject changed content under one ID. Client clocks, ACKs and hashes do not grant rewards or prove honesty. Only authorized state is disclosed.

Authoritative state does not eliminate automation or collusion. Offline policies must explicitly prevent clients from indefinitely pausing an unfavorable round.

## Existing versus pending

Existing foundations include serial tick execution, commit-before-publish, bounded subscriber queues, snapshot baselines and epoch-aware edge reconnect. Direct push migration, a universal client delta contract, durable pause/resume, all-offline policy execution and full action-validation evidence remain work to implement and verify.
