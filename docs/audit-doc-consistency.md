
# Docs–code consistency audit (2026-10-13)

Scope audited: README.md, README.zh.md, all 30 pages under docs/ + .vuepress/config.js + the
public surface (GitHub About / homepageUrl / topics).
Method: enumerate the doc surface → extract each claim → verify claim-by-claim with `file:line`
evidence (before asserting "not implemented", confirm the search path exists) → bucket into three
classes → fix with code as the source of truth.

## Bucket counts

| Class | Count |
| --- | --- |
| Docs ahead of code (dangerous) | 6 |
| Code ahead of docs (missing) | 7 |
| Description mismatch | 28 |
| **Total needing change** | **41** |

> Note: some findings span multiple lines (legacy 4 lines, review 10 lines); 41 is counted by
> claim.

## P0 list

**No P0.** Every port, endpoint, config key and default-value claim matched the source verbatim.

## P1 list (3 items)

| ID | Page | Issue |
| --- | --- | --- |
| O1 | observability.md | Claims "Gateway and EngineHost each count publish failures", but EngineHost neither counts nor exposes `chorus_ipc_publish_failures_total` |
| P1 | platform-douyin.md | Claims "known limit: TryMark is not atomic, publish failures are counted as duplicates", but the code is already atomic (TryReserve + Rollback) |
| P2 | platform-kuaishou.md | Same as P1 |

## Audit table

| ID | Page | Claim | Source evidence (file:line) | Class | Severity | Fix |
| --- | --- | --- | --- | --- | --- | --- |
| R-1 | README.md | `.NET net10.0 (see global.json)` | `global.json:2-3` pins SDK `10.0.401`; `net10.0` lives in `src/*/*.csproj:4` | Description mismatch | P2 | Reword to "TargetFramework in csproj; global.json pins the SDK" |
| R-2 | README.md | "there is no automated release" | `release.yml:4-8,33-35` auto-creates a Release on a `v*` tag push | Description mismatch | P2 | Reword to "no scheduled releases; release.yml runs only on a v* tag push" |
| RZ-1 | README.zh.md | Same as R-1 | Same as R-1 | Description mismatch | P2 | Same as R-1 |
| RZ-2 | README.zh.md | Same as R-2 | Same as R-2 | Description mismatch | P2 | Same as R-2 |
| DC-1 | decisions.md | D1 "Adapters.Platforms references Protocol only" | `Chorus.Adapters.Platforms.csproj:16-17` references Abstractions + Protocol | Description mismatch | P2 | Reword to "references Protocol and Adapters.Abstractions" |
| DC-2 | decisions.md | D5 "log categories Chorus.Ingress / Chorus.StreamerWs" | No `Chorus.Ingress` category (src/ searched) | Docs ahead of code | P2 | Reword to "Chorus.StreamerWs; the ingress face has no separate log category yet" |
| AO-1 | architecture-overview.md | mermaid `SA -.->|authoritative snapshot V=1 wire routing| GM` | Snapshots read state from GM (`SessionActor.cs:451`) → SA sends to clients | Description mismatch | P2 | Reverse to `GM -.-> SA` |
| AO-2 | architecture-overview.md | mermaid `SA --> CPU` | `ProcessCpuGauge` is process-wide and only EngineHost `/metrics` samples it | Description mismatch | P2 | Change to `ENGIPC -.-> CPU` |
| C1 | client-resync-state-machine.md | Recommends maxLagTicks 3~5 / >5~10 | `SessionConfig.cs:8` = 50 | Description mismatch | P1 | Note the code default is 50 |
| C2 | client-resync-state-machine.md | Server-side cooperation requirements omit fields already exposed | `SessionClientDiagnostics` has lagTicks/lastAckAgeMs/resyncCount/lastResyncAgeMs | Code ahead of docs | P2 | Add "the server already exposes per-client diagnostic fields" |
| B1 | frame-sync-best-practices.md | Recommends maxLagTicks 6~10 | `SessionConfig.cs:8` = 50 | Description mismatch | P1 | Note the code default is 50 |
| B2 | frame-sync-best-practices.md | Recommends snapshotEveryTicks 2~5 | `SessionConfig.cs:6` = 1 | Description mismatch | P2 | Note the code default is 1 |
| B3 | frame-sync-best-practices.md | Observability needs "per-client outbound queue backlog" | `OutboundClientQueue.Dropped` not in SessionDiagnostics/metrics | Docs ahead of code | P2 | Note it is not exposed yet, pending export |
| F1 | frame-sync.md | "ClientAck carries only lastAppliedTickId" | `ClientAck.cs:3-9` also has sessionId/clientId/clientTimeMs | Description mismatch | P2 | Reword to "carries only lastAppliedTickId as its progress field" |
| F2 | frame-sync.md | Links `guides/quickstart-frame-sync.md` | Actual path is `docs/guide/quickstart-frame-sync.md` | Description mismatch | P2 | Change to `../guide/quickstart-frame-sync.md` |
| L1-L4 | legacy-real-server-analysis.md | Old project MultGameInfo/ServerLogic/MaxServerFrame | The old project's source is not shipped with this repo (zero hits in src/ tests/) | Docs ahead of code | P2 | Add "external project, not shipped with this repo" at the top |
| P1 | process-topology.md | §4 references §5.1/§5.2/§5.3 headings | §5 has no such third-level headings | Description mismatch | P2 | Add the §5.1/§5.2/§5.3 headings |
| R1-R10 | review-2026-10-12-chatgpt.md | Ten problem assertions in the body were fixed in phase one/two | 78f2597/5592d62/499f01c/df71d26/a5f23b8/362b435/def21d2 etc. | Description mismatch | P2 | Add a "state at review time" currency note at the top |
| S1 | shared-memory-transport.md | Current state 150 messages/second; phase A not marked as delivered | ~15 messages/second after 50f0045; phase A delivered | Description mismatch | P2 | Reword to the post-optimization figure + mark phase A delivered |
| SR1 | snapshot-restore.md | EngineIpcService.cs:125 | Actually :152 (CreateActor:150-156) | Description mismatch | P2 | Change to :152 |
| SP1 | spec.md | "all downstream data carries tickId and serverTimeMs" | welcome/pong have no tickId; only snapshot does | Description mismatch | P2 | Change to "serverTimeMs everywhere; tickId with the snapshot" |
| BI1 | bilibili-danmaku-adapter.md | `kind=enter` | `AudienceEventKind` has only Unknown/Comment/Like/Gift | Docs ahead of code | P2 | Note the Enter member needs to be added |
| BI2 | bilibili-danmaku-adapter.md | "each platform = one mapper + one verifier" | There is also IPlatformNamedEventVerifier + PlatformEventVerifierRouter | Code ahead of docs | P2 | Add the named-verifier / router seam |
| GS-01 | getting-started.md | Endpoint list misses /metrics /matches /faces | `Program.cs:109/105/106` | Code ahead of docs | P2 | Add the three endpoints |
| GS-02 | getting-started.md | Node.js recommended >= 20 | package.json has no engines; vuepress 1.9.10 supports node >= 8 | Docs ahead of code | P2 | Change to "any supported LTS, no lower bound set" |
| SM-01 | split-mode.md | EngineHost does only tick + snapshots | It also has /health + /metrics (:5067) | Code ahead of docs | P2 | Add the HTTP face |
| SM-02 | split-mode.md | IPC config misses HeartbeatIntervalMs/ReadinessMaxSilenceMs | `EngineIpcService.cs:61`, `GatewayIpcService.cs:68` | Code ahead of docs | P2 | Add the two keys + Gateway:Id/GatewayIds |
| LT-01 | load-testing.md | `--engine-metrics` scraped at the end | Sampled every `--report-every` for the peak (`Program.cs:93-105`) | Code ahead of docs | P2 | Change to "sampled during the run for the peak" |
| GM-01 | game-module.md | "this tick's state does not advance" | TickId does advance (:391); the snapshot just was not sent (the exception is before :478) | Description mismatch | P2 | Change to "no snapshot is sent this tick" |
| QF-01 | quickstart-frame-sync.md | One ack per frame | AckPerMinute=600; a 66ms tick ≈ 909/minute | Code ahead of docs | P2 | Add the ACK rate-limit constraint |
| A1 | api.md | userId required=yes | The mapper defaults it to anonymous | Description mismatch | P2 | Change to "no (defaults to anonymous)" |
| A2 | api.md | eng.snapshot originates from EngineIpcService.cs + WireSnapshot | Actually constructed directly in EngineBusSessionClient.cs:49-59 | Description mismatch | P2 | Fix the origin |
| D1 | deployment.md | /ready = heartbeat within the window | Any engine message refreshes it (GatewayIpcService.cs:158-160) | Description mismatch | P2 | Change to "any engine message" |
| O1 | observability.md | Gateway and EngineHost each count publish failures | EngineHost's SafePublishAsync swallows exceptions without counting (:64-70) | Description mismatch | P1 | Change to "only the Gateway face emits it" |
| O2 | observability.md | ingress_timed_out_total counts 503 only | It also counts 504 (PlatformIngressFace.cs:133) | Description mismatch | P2 | Add "and 504" |
| P1 | platform-douyin.md | Known limit: TryMark is not atomic | Changed to TryReserve (atomic TryAdd) + Rollback | Description mismatch | P1 | Drop the limit, mark it fixed |
| P2 | platform-kuaishou.md | Same as P1 | Same as P1 | Description mismatch | P1 | Same as P1 |
| P3 | platform-kuaishou.md | "replace MapInner" | There is no MapInner; inner field extraction is in TryMap | Description mismatch | P2 | Change to TryMap |
| R1 | protocol.md | welcome example inputDelayTicks:3 snapshotEveryTicks:2 | Defaults are 2/1 (SessionConfig.cs:5,7) | Description mismatch | P2 | Change to 2/1 |
| T1 | troubleshooting.md | serverTick | The actual field name is tickId | Docs ahead of code | P2 | Change to tickId |
| T2 | troubleshooting.md | events per tick | No such field (only cumulative counts) | Docs ahead of code | P2 | Change to the adjacent difference |
| N1 | tuning.md | lastSnapshotBytes if exposed | Not exposed (only TotalSnapshotsOversize) | Code ahead of docs | P2 | Note it is not exposed; use the oversize metric for now |

## Public surface

| Item | Value | Verdict |
| --- | --- | --- |
| description | "Server-authoritative interactive battle game framework..." | ✅ English, accurate, current |
| homepageUrl | `https://cuihairu.github.io/chorus/` | ⚠️ Points at the Pages site built from this repo's VuePress; removing VuePress would dead-link it (left alone until the user decides) |
| topics | authoritative-server/danmaku/game-server/.../kuaishou | ✅ All current |
| README lead | English main doc + Chinese cross-link | ✅ Bilingual cross-references |

## New vitepress instance

Not applicable — this repo already has a VuePress 1.x site (`docs/.vuepress/`, 30 pages + the
mermaid plugin + Pages auto-deploy in `docs.yml`).

## Fix principle

Code is the source of truth throughout: delete claims ahead of code, add missing descriptions,
reword mismatches; **no code was changed** (this round was pure doc fixes).
