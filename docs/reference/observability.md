---
title: Observability
---


# Observability

Chorus's observability surface consists of three parts: **Prometheus text metrics** (`chorus_*`), **JSON diagnostic endpoints** (`/sessions`, `/matches`), and **structured logs** (with correlation IDs).

Related docs:

- [Operations and Observability](./operations.md)
- [Configuration Reference](./config.md)
- [Troubleshooting](./troubleshooting.md)

## Metrics Endpoint

All three processes expose `GET /metrics`, returning Prometheus text format (`text/plain; version=0.0.4; charset=utf-8`), which Prometheus can scrape directly:

```yaml
# prometheus.yml 示例
scrape_configs:
  - job_name: chorus-server
    static_configs:
      - targets: ["localhost:5100"]        # Chorus.Server（单进程模式）
  - job_name: chorus-engine
    static_configs:
      - targets: ["localhost:5200"]        # Chorus.EngineHost
  - job_name: chorus-gateway
    static_configs:
      - targets: ["localhost:5300"]        # Chorus.Gateway
```

The single-process mode (Chorus.Server) has no IPC hop, so `chorus_ipc_publish_failures_total` is always 0. In split mode, each side outputs the publish failures of its own direction: the Gateway side counts Gateway→Engine events (`src/Chorus.Gateway/GatewayIpcService.cs`), and the EngineHost side counts Engine→Gateway snapshots and welcomes (`src/Chorus.EngineHost/EngineIpcService.cs`, rendered by `RenderEngine`). The engine-side snapshot publishing is best-effort (a publish failure does not kill the tick, see `EngineBusSessionClient.SafePublishAsync`), but failures are counted and alerted on, no longer silently swallowed (`8e87a47`).

## Metric Inventory

### Sessions and Events (Engine-side aggregate)

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_sessions` | gauge | Current live Session count |
| `chorus_session_start_failures_total` | counter | Sessions refused to start (snapshot restoration fail-closed / module construction threw). These failures have no actor diagnostics to read and are invisible in `/sessions` — this counter is the only exposed surface (output by both Server and EngineHost) |
| `chorus_session_idle_evictions_total` | counter | Cumulative evictions by the idle sweeper (no clients and exceeds `IdleEvictAfterMs`). A drop in the `chorus_sessions` gauge cannot distinguish normal endings from evictions; this counter gives the eviction-side ground truth; an abnormal rise means the idle threshold is too aggressive or clients are churning in bulk |
| `chorus_session_capacity_evictions_total` | counter | Cumulative count of oldest sessions evicted after reaching the `MaxSessions` limit (evicted clients reconnect with a new session id). An abnormal rise means capacity is undersized or a session storm |
| `chorus_events_ingested_total` | counter | Events entering the Session event lane (after dedup, before scheduling) |
| `chorus_events_applied_total` | counter | Events settled into authoritative state within a tick |
| `chorus_events_merged_total` | counter | Events merged by coalescing (merging does not lose counts, see [Frame Sync Design](../design/frame-sync.md)) |
| `chorus_events_dropped_total` | counter | Total events dropped due to backpressure |
| `chorus_events_dropped_overflow_total` | counter | Event lane overflow drops (non-gift) |
| `chorus_events_dropped_gifts_total` | counter | Gift events dropped when the enqueue wait timed out (retryable semantics, see [Spec §3.6](../design/spec.md)) |
| `chorus_tick_overruns_total` | counter | Count of ticks whose processing timed out (exceeds `TickTimeoutMs`); detection only, no cancellation |
| `chorus_tick_errors_total` | counter | Ticks where GameModule threw (the actor stays alive; diagnostics keep advancing as usual) |
| `chorus_tick_duration_ms` | gauge | Most recent tick processing duration (max over all Sessions) |
| `chorus_mailbox_depth` | gauge | Mailbox depth (control lane + event lane, max) |
| `chorus_ack_lag_ticks` | gauge | Client ACK lag in ticks (max over all clients) |
| `chorus_process_cpu_percent` | gauge | Worker process CPU as a percentage of total machine capacity (average since the last scrape; all cores busy = 100; output only on EngineHost /metrics, line omitted on first scrape with no previous value). Peak measurement instrument (todo §4): sampled periodically together with LoadTool `--engine-metrics`, the process-topology phase 3 trigger condition "single worker peak CPU > 60%" becomes countable via this |

### Snapshots

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_snapshots_sent_total` | counter | Snapshots delivered |
| `chorus_snapshots_resync_total` | counter | Forced resync snapshots (client lag exceeds `MaxLagTicks`) |
| `chorus_snapshots_oversize_total` | counter | Snapshots skipped for exceeding `MaxSnapshotBytes` after serialization |
| `chorus_snapshot_bytes` | gauge | Bytes of the most recently delivered wire snapshot (max over all Sessions; 0 with no snapshots). Tuning signal: snapshot size drift is observable without a profiler (`f301aa3`); `lastSnapshotBytes` in `/sessions` is the per-session detail of the same value |
| `chorus_client_queue_dropped_total` | counter | Snapshots replaced out of the client outbound latest-wins slot (accumulated across Sessions). Per-client backlog signal: the capacity-1 slot only drops when sending falls behind by more than one beat; the per-client `clientQueueDropped` in `/sessions` is the detail (`6d738b7`) |

### Frame Sync / resync (Engine-side aggregate)

The resync path counters from frame-sync P2 were previously only visible in `/sessions`; they are now also exported to `/metrics`:

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_resync_requests_total` | counter | Resync requests initiated by clients (before cooldown filtering) |
| `chorus_resync_replays_total` | counter | Resync requests answered from the frame history window |
| `chorus_resync_window_misses_total` | counter | Resync requests whose window start was missing (answered with the latest snapshot + forced). Tuning signal: sustained growth means the frame history window is too small |
| `chorus_resync_throttled_total` | counter | Resync requests rejected by the per-client cooldown |
| `chorus_resync_replay_capped_total` | counter | Resync requests whose window exceeded `Session:MaxResyncReplayTicks` and were aligned with a single forced snapshot (2026-10-15 replay bound). Tuning signal: a high proportion means many clients are behind beyond the replay cap (reconnect storm / long disconnect); check `chorus_ws_resync_dropped_total` to see whether clients are spinning |
| `chorus_frame_history_bytes` | gauge | Current frame history encoded byte usage (max over all Sessions) |

### ACK (Engine-side classified counts)

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_acks_accepted_total` | counter | ACKs accepted (advancing the watermark) |
| `chorus_acks_duplicate_total` | counter | Duplicate ACKs |
| `chorus_acks_stale_total` | counter | ACKs behind the current watermark |
| `chorus_acks_invalid_total` | counter | Invalid ACKs (client binding validation failure, etc.) |

### Ingress (platform entry face, on both Server and Gateway)

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_ingress_accepted_total` | counter | Events that passed signature verification + mapping + dedup and entered scheduling |
| `chorus_ingress_duplicates_total` | counter | Duplicate events hit by dedup (idempotent return `duplicated=true`) |
| `chorus_ingress_unverified_total` | counter | Events rejected for failing signature verification (production default is fail-closed) |
| `chorus_ingress_unmapped_total` | counter | Events that could not be mapped to a Session |
| `chorus_ingress_rate_limited_total` | counter | Events that triggered rate limiting |
| `chorus_ingress_too_large_total` | counter | Events rejected for exceeding the request body limit |
| `chorus_ingress_timed_out_total` | counter | Times dedup was rolled back because the event lane was full (503) and single-request timeouts (504) |

### WebSocket (Gateway streamer/viewer connection face)

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_ws_connections_total` | counter | WS connections established |
| `chorus_ws_unauthorized_total` | counter | Connections closed for failing hello authentication |
| `chorus_ws_protocol_mismatch_total` | counter | Connections rejected for hello protocol version mismatch (`protocolVersion` non-empty and ≠ the server's current version) |
| `chorus_ws_message_too_large_total` | counter | Times a message body was rejected for exceeding the limit |
| `chorus_ws_acks_accepted_total` | counter | WS ACKs accepted (without triggering rate limiting) |
| `chorus_ws_acks_dropped_total` | counter | WS ACKs dropped for triggering rate limiting |
| `chorus_ws_resync_dropped_total` | counter | Requests dropped by the gateway `/ws` resync fixed-window rate limiting (`Faces:StreamerWs:ResyncPerMinute`) (`c896855`). Complementary to the engine-side `chorus_resync_throttled_total`: this count happens before the request reaches the engine; an abnormal rise means clients are spinning (frequent reconnects / full resyncs) |
| `chorus_ws_snapshot_dropped_total` | counter | Gateway slow-client drop policy counter (2026-10-16, todo §10 blocker ②): the count of old snapshots replaced by newer ones in the per-connection latest-wins outbound slot (capacity 1), accumulated into the process-level `FaceCounters`, so history is not lost across connection reconnects. Only meaningful in split mode — the single-process form has no such hop and it is always 0 (its drops count as `chorus_client_queue_dropped_total`). Paired reading: read together with `chorus_ws_resync_dropped_total` — a rising drop count means the client cannot keep up with the snapshot rate; dropped frames are refilled by the client resync |

### IPC

| Metric | Type | Meaning |
| --- | --- | --- |
| `chorus_ipc_publish_failures_total` | counter | IPC publish failure count. The Gateway side counts Gateway→Engine events, the EngineHost side counts Engine→Gateway snapshots/welcomes (`8e87a47`) |
| `chorus_ipc_engine_silence_ms` | gauge | Milliseconds since the Gateway last received an EngineHost message (heartbeat/welcome/snapshot); `-1` if never received. `/ready` decides based on this (threshold `Ipc:ReadinessMaxSilenceMs`) |

## Correlation IDs

The tracking chain of one platform event inside the system:

```
平台 EventId ──ingress──> Envelope.SourceEventId ──IPC──> Engine 结算 ──tick──> 快照(sessionId+tickId) ──WS──> 客户端
```

- **`AudienceEvent.EventId`**: the platform event id (part of the dedup key); both the ingress response and `duplicated=true` carry it.
- **`Envelope.SourceEventId`**: the optional source event id on the IPC envelope. The Gateway's `/ingress/{platform}` writes `ev.EventId` when publishing audience events; the Engine validates consistency upon receipt and logs a warning on mismatch (`Correlation mismatch`).
- **`Envelope.MessageId`**: the unique id of one delivery (`m_<guid>`), used to locate the publish failure of "this hop" (the logs of `chorus_ipc_publish_failures_total` all carry `message=`).
- **Snapshot correlation**: the snapshot carries `sessionId` + `tickId` + `serverTimeMs`; the client ACK returns `lastAppliedTickId`, completing the closed loop of "which event → which tick → confirmed by which client".
- **ACK correlation**: WS ACKs bind the connection by `sessionId|clientId`; `GatewayAckCmd` carries `lastAppliedTickId` and `ClientTimeMs` (client-measured drift).

When troubleshooting: first check on the ingress face by `eventId` whether it was accepted / deduplicated / rejected; then use `SourceEventId` to trace whether the IPC publish failed; finally look at `applied/merged/dropped` and the `tickId` watermark on the Engine side.

## JSON Diagnostic Endpoints

- `GET /sessions`: list of `SessionDiagnostics` (tick, event counts, lag, ack classified counts, mailbox depth, snapshot counts, etc.).
- `GET /matches`: match-level aggregate view (session list, factions, cumulative event counts, last event time).
- `GET /health`: liveness probe (all three processes have it).

## Log Key Fields

| Field | Where it appears | Meaning |
| --- | --- | --- |
| `eventId` / `SourceEventId` | ingress, engine event lane | Platform event correlation |
| `messageId` | IPC publish failure, engine packet reception | Delivery correlation |
| `sessionId` / `clientId` | Whole chain (WS rejection, loop errors, snapshot send failures all carry them) | Session and client |
| `tickId` | tick overrun, snapshot send failure, dispatch failure paths | Authoritative tick watermark |

The failure logs of the WS loop and snapshot sending (`WebSocket loop error`, `Failed to send snapshot`) all carry
`session=` / `clientId=` / `tick=` structured fields, which can be aligned with the lag/ack fields of the `/sessions` diagnostics for troubleshooting.
