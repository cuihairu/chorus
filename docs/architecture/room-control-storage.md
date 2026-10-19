# Room Control Storage Decision

**Status:** Approved by delegated decision in patrol-1018-chorus.md  
**Date:** 2026-10-19  
**Scope:** ROOM-11 initial production storage decision

## Decision

Use PostgreSQL 16 or later as the multi-worker ControlHost authority store. Production topology is one writable primary and one synchronous standby on separate physical hosts, with PostgreSQL synchronous commit enabled. Keep the existing SQLite provider for local development and single-host deployments. Worker and Gateway processes continue to access the authority only through ControlHost HTTP; they must never mount or open a shared database file.

The first production capacity gate is 100 active rooms at a 66 ms tick, up to 1,000 durable input events/s, and no more than 1,516 checkpoint commits/s before event writes. Checkpoint payload targets are p95 ≤16 KiB, p99 ≤64 KiB, and hard maximum 256 KiB. Under that workload, PostgreSQL commit latency must remain p95 ≤20 ms, p99 ≤40 ms, and p99.9 ≤60 ms; any tick commit exceeding its 66 ms budget is a gate failure. These are initial rollout targets, not measured results or a promise of capacity. Load tests must measure actual payload distribution, WAL volume, lock waits, pool saturation, and commit latency before production activation.

An operation is acknowledged only after its transaction commits. With synchronous replication and `synchronous_commit=on`, the standby has flushed acknowledged transactions, giving RPO 0 for acknowledged events and checkpoints while the primary/standby pair remains intact. Initial failover is manual, with a 15 minute RTO target; automatic promotion stays disabled until split-brain and routing-convergence fault tests pass.

Budget 2 TiB of fast local storage on each database host for database files and bounded local WAL staging, plus 24 TiB of separate object storage for seven days of WAL archive and base backups. This archive ceiling assumes the p95 checkpoint size and stated write rate persist continuously, with headroom for event writes and index/WAL overhead; revise it from observed WAL generation before launch. Alert at 70% and page at 85% of local storage, WAL staging, and archive budgets. This seven-day WAL/base-backup window is for database recovery, not audit-row deletion. Audit records use append-only retention without automatic deletion; the current policy and 256 MiB admin warning are documented in [audit retention](audit-retention.md). No archive destination is configured yet.

## Alternatives

| Option | Assessment |
|---|---|
| Continue SQLite for multi-worker production | Rejected. All tick commits serialize behind one local-file writer; it has no synchronous remote standby and cannot satisfy the selected multi-host RPO target. Keep it for local/single-host use. |
| PostgreSQL primary with asynchronous replica | Rejected for initial production. It improves concurrent writes but can lose acknowledged checkpoint and event commits during promotion, violating the selected RPO 0. |
| PostgreSQL primary with synchronous standby | Selected. It supports concurrent Worker commits and durable acknowledgement on a second host. The accepted costs are replication latency and write unavailability when the synchronous standby cannot acknowledge. |
| Sharded/distributed SQL or event-log architecture | Deferred. It adds partitioning, replay, and cross-shard correctness work before the single PostgreSQL writer has been measured against the initial gate. |

## Implementation and rollout rules

- Room tick commits and reliable event writes preserve owner/epoch fencing, checkpoint/inbox changes, and receipts in one transaction. PostgreSQL uses `READ COMMITTED` with `SELECT ... FOR UPDATE` on the room record for these hot paths; the fence is checked after acquiring that row lock. SQLite continues to use its immediate write transaction. Other control writes retain `SERIALIZABLE` isolation.
- PostgreSQL serialization failures and deadlocks may be retried only by rerunning the complete transaction; callbacks must not perform external side effects.
- Set `Control:Provider=PostgreSql` and provide `Control:ConnectionString` through secret configuration. Do not place credentials in checked-in settings.
- Provision primary and standby on separate physical hosts with synchronous replication, TLS, SCRAM authentication, and a tested manual promotion runbook. Do not count two containers on one host as HA.
- Keep automatic failover disabled until ROOM-07/09/12 network-partition, stale-owner, ControlHost restart, and routing-convergence tests pass.
- Keep the 66 ms durable checkpoint contract until a separately approved replay/loss-window design replaces it.

## Test environment and current limits

The accessible PVE cluster currently has one physical node. Its only guest is the coding VM; its Docker LXC is a shared service host. The local LVM-thin pool is 94.2% used with about 105 GB available, so it cannot safely host the selected 2 TiB + 2 TiB production disks or serve as an independent-host HA test bed. Do not create persistent large VM disks there.

An isolated, ephemeral PostgreSQL 16 Docker service was started locally for provider integration tests. It uses a private Docker bridge, loopback-only port publishing, a 2 GiB memory limit and tmpfs data. This validates SQL/provider behavior only; it is not independent-host, replication, failover, or capacity evidence. ROOM-07/09/12 still require a second physical host and controllable network isolation. The PVE test plan is two disposable 2-vCPU/4-GiB nodes on a private bridge, each with 2 TiB provisioned storage, plus a separate 24 TiB object-storage target; this lab cannot be provisioned on the current thin pool.

## Consequences and open validation

The provider decision is no longer open. ROOM-11 remains open until PostgreSQL completes full storage-contract parity with SQLite, a multi-worker load run meets the latency/WAL gate, and production storage/replication resources are provisioned. PostgreSQL 16 integration coverage verifies reliable event persistence, checkpoint fencing across Store instances, duplicate receipt handling, concurrent assignment uniqueness, migration and connection lifecycle behavior, and event heat acknowledgement. The provider parity regression `Providers_MatchForSummaryHistoryAuditAndEventHeatRetention` compares SQLite/PostgreSQL summary, history, audit records/usage, and event heat retention/pruning. Initial implementation uses synchronous ADO.NET APIs to match the existing ControlDatabase call graph; move the control persistence path to async APIs before adopting a driver release that removes synchronous I/O support.
