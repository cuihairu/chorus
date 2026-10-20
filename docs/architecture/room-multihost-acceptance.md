# Multi-Host Room Acceptance Runbook

**Status:** Preparation only; no multi-host acceptance has been run.
**Scope:** ROOM-07, ROOM-09, ROOM-11 and ROOM-12 rollout evidence.

This runbook turns the open rollout gates into a repeatable lab session. It does not change the selected architecture or mark any gate complete. Record one dated copy of the evidence tables per lab run; attach raw logs and metric exports rather than replacing them with summaries.

## Entry criteria

Do not start fault scenarios until all of these are true:

| Requirement | Evidence to record |
|---|---|
| Two independent Worker hosts and a separate ControlHost/database failure domain | Host IDs, provider/region, OS/kernel, CPU/RAM, disk type/capacity; show they are not VMs or containers on the same physical host |
| PostgreSQL 16+ primary and synchronous standby on separate hosts; `synchronous_commit=on` | PostgreSQL versions, replication mode/state, synchronous standby name, TLS/auth mode, measured replication lag |
| Isolated test network with reversible, target-scoped partition controls | Interface/subnet, rule owner, exact source/destination/ports, add/remove commands, out-of-band recovery path |
| Room and input workload is reproducible | Build commit, configuration hash with secrets removed, room count, tick interval, payload sizes, event rate, client count, run duration, load-generator host |
| Observability and time alignment work on every node | UTC clock offset, scrape targets, log/metric retention, trace/run ID |
| Recovery and cleanup are rehearsed | Database backup/snapshot ID, process restart commands, network-rule rollback, expected resource cleanup |

Use disposable infrastructure only. Never inject partitions or promote a database in a production environment. Keep automatic failover disabled throughout these tests; the selected initial recovery mode is manual.

### Known candidate environment assessment (2026-10-20, re-probed 22:29 +0800)

The only reachable infrastructure today does not meet the entry criteria; recorded here so the same path is not re-proposed:

- The workspace host `coding` is VM 101 on hypervisor `shanghai-pve` (i9-10880H, 16 vCPU / 62 GiB; `qm list` shows VM 101 as the only running guest). A second VM on that hypervisor would satisfy "two machines" but **fails entry criterion 1 by definition** — same physical host, same failure domain.
- Hypervisor headroom: local-lvm 94.21% used, ~100.9 GiB free, ~12 GiB RAM available — an order of magnitude below the ROOM-11 storage budget (2 TiB NVMe + 24 TiB seven-day WAL/object storage per node).
- Fleet peer `win-worker` (Windows) exposes a DM channel only, no shell; a read-only capability probe went unanswered, so its ability to host any role is unverified.
- A user-approved same-hypervisor lab may still exercise migration/fencing mechanics, but its results must be reported as same-failure-domain evidence and can never close the independent-host, synchronous-replication, capacity, or partition gates (see [Reporting](#reporting-and-gate-closure)).

## Run protocol

1. Record the commit, configuration, host map, UTC start time, and baseline health. Verify all Workers have distinct node and instance IDs and that each room has exactly one owner/epoch.
2. Run the workload without faults for at least 10 minutes. Save per-node CPU, memory, network, disk/WAL, database pool/lock/commit latency, tick duration/overruns, room ownership, routing snapshot age, event accepted/applied counts, and client reconnect/resync counts.
3. Execute one scenario at a time from the matrix below. Record the injected fault start/end times and exact rule/process action. Do not combine scenarios until each passes independently.
4. After removing the fault, continue the workload until routing, event processing, and database replication return to baseline. Capture convergence time and any manual action required.
5. Repeat each scenario three times from a clean state. Preserve every attempt, including failed runs. A pass requires all assertions to hold in all three attempts.

## Scenario matrix

| Gate / scenario | Injection | Required safety and recovery assertions | Evidence / pass condition |
|---|---|---|---|
| ROOM-07: Worker-to-ControlHost partition, old process remains alive | Isolate one Worker from ControlHost while leaving its process and client-facing path alive; keep the new owner path available | Old owner cannot commit a tick or reliable input after lease/epoch fencing; no acknowledged state is lost; replacement owner restores only a committed checkpoint; no overlapping authoritative owner | Timestamped owner/epoch and checkpoint history, commit responses, database rows, logs, client-visible state; zero stale-owner commits and zero duplicate authoritative ticks |
| ROOM-07: ControlHost restart | Restart ControlHost during steady load, then repeat while a room migration is between freeze and Ready | Writes fail closed while authority is unavailable; after restart the durable owner/epoch and migration state are recovered; stale routing cannot authorize a commit | Restart window, API errors, database recovery logs, owner/epoch timeline; no stale commit and successful return to one authority |
| ROOM-09: migration and route convergence under partition | Start a room move, isolate the old Worker at freeze/handoff and separately interrupt the target before Ready; test both abort and retry | Inputs are rejected as retryable or durably queued according to the contract; old owner stays fenced; target restores committed checkpoint/inbox cursor; Gateway routes only after a fresh owner/epoch snapshot; abort returns to a single fenced owner | Per-room transition timeline, snapshot ages, event IDs accepted/applied, client reconnects, measured convergence duration; no split authority, missing acknowledged events, or permanently stale route |
| ROOM-11: PostgreSQL capacity and synchronous replication | Run the selected 100-room, 66 ms workload with up to 1,000 durable events/s against the primary/standby pair | Checkpoint and event commit latency meets p95 ≤20 ms, p99 ≤40 ms, p99.9 ≤60 ms; no tick commit exceeds 66 ms; acknowledged transactions are flushed on the synchronous standby; no unbounded queue/pool/WAL growth | At least three 10-minute steady-state windows; payload distribution, checkpoints/s, events/s, latency histograms, WAL bytes/s, lock/pool waits, replication state/lag, tick overruns; every stated limit met |
| ROOM-11: primary loss and manual promotion | Stop or isolate the primary, verify writes stop, then promote the synchronous standby using the documented operator procedure | No acknowledged transaction is missing; writes resume only after a single primary is established and ControlHost reconnects; measure manual RTO against 15 minutes | Promotion command transcript, pre/post transaction IDs, recovery logs, outage and recovery timestamps; zero acknowledged data loss and RTO ≤15 minutes |
| ROOM-12: one Worker versus two Workers | Run the same room count, event/client workload, hardware class, and duration first on one Worker, then with two Workers; include new-room assignment and an explicit migration | Existing rooms remain with their recorded owner until migration; new rooms distribute according to capacity; migration preserves state and event receipts; compare throughput, latency, per-node utilization, and routing convergence | Config/build hashes and raw metrics for both runs; report normalized throughput and p95/p99 latency. State plainly if two Workers do not improve the measured workload; no linear-scaling claim from a single comparison |

## Evidence record

Copy this block for each run and attach raw artifacts:

```text
Run ID / UTC date:
Commit and configuration hash:
Host map (Worker, ControlHost, primary, standby, load generator):
Fault scenario / exact injection and rollback action:
Workload (rooms, tick, durable events/s, clients, duration):
Baseline and fault-window metric artifact paths:
Owner/epoch and acknowledged/applied event evidence:
Fault start / removed / convergence timestamps:
Pass or fail, assertion, and supporting artifact:
Operator actions, retries, and deviations:
Cleanup and final health:
```

## Reporting and gate closure

Report each gate separately as **PASS**, **FAIL**, or **NOT RUN**. A local Docker database, multiple processes on one host, TestServer, unit tests, or a successful provider integration test may support implementation confidence but cannot close the independent-host, synchronous-replication, capacity, or partition gates. Update the corresponding `todo.md` item only when its full evidence is attached and all required repetitions pass. Until then, keep ROOM-07/09/11/12 blocked and automatic failover disabled.

Related decisions and current evidence: [multi-machine room architecture](multi-machine-rooms.md), [ControlHost storage decision](room-control-storage.md), and root `todo.md` §19.
