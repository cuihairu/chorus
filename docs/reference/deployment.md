---
title: Deployment and Security
---


# Deployment and Security Configuration

Chorus has two deployment forms, sharing the same configuration model and security semantics.

Related docs:

- [Split Mode](../guide/split-mode.md)
- [Configuration Reference](./config.md)
- [Observability](./observability.md)
- [Troubleshooting](./troubleshooting.md)

## Forms

| Form | Processes | Applicable |
| --- | --- | --- |
| Single-process | `Chorus.Server` | Default, debugging, small scale; ingress/WS/engine in the same process |
| Split (single-gateway process) | `Chorus.Gateway` + `Chorus.EngineHost` | Gateway and authoritative logic on separate machines/containers; goes over IPC |
| Three-process | `Chorus.Gateway` (`Gateway:Roles=platform`) + `Chorus.Gateway` (`Gateway:Roles=streamer`) + `Chorus.EngineHost` | platform-gw / streamer-gw gateway roles deployed on separate machines, room-worker carries multiple rooms; faces are shared with no duplication, see [Process Topology](../design/process-topology.md) §4 |

The `/ingress`, `/ws`, `/faces`, `/metrics` of both forms are completely identical; `/sessions` and `/matches` are only exposed in the single-process form.

## Build and Run

```bash
# 发布（Release）
dotnet publish src/Chorus.Server -c Release -o ./publish
dotnet run --project src/Chorus.Server -c Release --no-build   # 或直接 dotnet ./publish/Chorus.Server.dll

# 端口
ASPNETCORE_URLS="http://0.0.0.0:5083" dotnet ./publish/Chorus.Server.dll

# 环境（默认 Production 只读 appsettings.json；Development 会叠加 appsettings.Development.json 的开发开关）
DOTNET_ENVIRONMENT=Production dotnet ./publish/Chorus.Server.dll
```

Configuration priority: `appsettings.json` → `appsettings.{Environment}.json` → environment variables (`__` as separator, e.g. `Session__MaxSessions`) → command line.

## Endpoints and Ports

| Endpoint | Purpose | Single-process | Split |
| --- | --- | --- | --- |
| `GET /health` | Liveness probe | ✅ | ✅ (Gateway / EngineHost each) |
| `GET /ready` | **Readiness probe (split Gateway only)**: any engine message (heartbeat/welcome/snapshot) received within `Ipc:ReadinessMaxSilenceMs` → 200, otherwise 503 | ❌ | ✅ (Gateway) |
| `GET /sessions` | Session/client metrics JSON (per-client lag/ack detail) | ✅ | ✅ (EngineHost; completed in `c7846a6` — the engine is the session authority in split mode, previously not exposed) |
| `GET /matches` | Match-level aggregate view JSON | ✅ | ❌ The authoritative `MatchRegistry` is only in single-process; use `/metrics` |
| `GET /faces` | Listener face counter JSON | ✅ | ✅ (Gateway) |
| `GET /metrics` | Prometheus text (`chorus_*`) | ✅ | ✅ (all three processes each) |
| `POST /ingress/{platform}` | Platform event entry | ✅ | ✅ (Gateway) |
| `WS /ws` | Streamer/viewer client | ✅ | ✅ (Gateway) |

Default ports are in each one's `Properties/launchSettings.json`: Server `5083`, Gateway `5168`, EngineHost `5067`.

Split mode IPC ports (`Ipc:TcpGatewayToEnginePort=27500`, `Ipc:TcpEngineToGatewayPort=27501`, HTTP transport uses `27600/27601`).

### Split Mode Readiness Probe (since 2026-10-12)

EngineHost sends one `eng.heartbeat` on the eng→gw channel every `Ipc:HeartbeatIntervalMs` (default 5000, `0` disables);
the Gateway refreshes its silence timer whenever it receives any engine message (heartbeat/welcome/snapshot).
Gateway's `GET /ready`:

- Never received an engine message, or silence exceeds `Ipc:ReadinessMaxSilenceMs` (default 15000) → **503** (fail-closed,
  the orchestrator does not switch traffic over); the response body carries `engineSilenceMs` for diagnosis;
- Heartbeat within the window → 200.

| Configuration | Default | Description |
|---|---|---|
| `Ipc:HeartbeatIntervalMs` (EngineHost) | `5000` | Engine heartbeat interval; `0` disables (after disabling, the Gateway can only decide readiness when client connections produce welcome/snapshot) |
| `Ipc:ReadinessMaxSilenceMs` (Gateway) | `15000` | Maximum silence window for the readiness decision, recommended ≥ 3× heartbeat interval |

The single-process form has no IPC hop and does not expose `/ready` (`/health` is sufficient).

## Security Configuration (fail-closed)

| Configuration | Default | Production requirement |
| --- | --- | --- |
| `Ingress:AllowAll` | `false` | **Must stay false**. When true all platform events skip signature verification, development demo only |
| `Ingress:Douyin:AppSecret` / `SignatureHeader` | empty | Empty → all Douyin callbacks 401 |
| `Ingress:Kuaishou:VerifyToken` / `SignatureHeader` | empty | Empty → all Kuaishou callbacks 401 |
| `Ingress:DedupTtlSeconds` | `600` | Set per platform retry window; do not be smaller than the platform's maximum retry interval |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | Increase per platform callback max body; 413 when exceeded |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | Set per platform QPS; 429 when exceeded |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | Client timeout budget; 504 on timeout |
| `Faces:StreamerWs:AuthTokens` | empty | **Must be configured**; empty and non-anonymous → all WS rejected |
| `Faces:StreamerWs:AllowAnonymous` | `false` | **Must stay false**. When true, token is not required, development demo only |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | Client message limit; close connection when exceeded |
| `Faces:StreamerWs:AckPerMinute` | `600` | Per-connection ACK rate limiting; dropped when exceeded (`chorus_ws_acks_dropped_total`) |

### WS Token Format

`Faces:StreamerWs:AuthTokens` supports multiple tokens, semicolon-separated, with optional expiration (unix milliseconds):

```json
{
  "Faces": {
    "StreamerWs": {
      "AuthTokens": "dev-token-1;prod-token@1893456000000"
    }
  }
}
```

`dev-token-1` never expires; `prod-token` expires after `1893456000000` (expiration is judged as `token_expired`). Rejection reasons: `missing_token` / `invalid_token` / `token_expired`.

### fail-closed Matrix

| Scenario | Behavior |
| --- | --- |
| Secret not configured + anonymous off | ingress 401, all WS closed |
| Secret not configured + `AllowAll=true` | Allowed through without signature verification (**warning**, do not enable in production) |
| Wrong secret/verifyToken configured | 401 |
| Token expired | Connection closed (`token_expired`) |
| Platform has no corresponding named verifier | 401 (even with `AllowAll=true`, the named verifier takes precedence) |

### Reverse Proxy

- Enable WS long connections: `proxy_http_version 1.1`, `Upgrade`, `Connection` headers; `proxy_read_timeout` much larger than `Session:TickDurationMs` (recommended ≥ 1 hour).
- Body limit for forwarding to the origin ≥ `MaxBodyBytes` + header headroom (e.g. 64KB + 8KB).
- `/ingress` only allows the platform callback source IP whitelist, or put a signature-verification gateway in front.
- `/metrics`, `/sessions`, `/matches` contain no sensitive state, but internal-network-only access is recommended.
- Real TLS terminates at the proxy layer; Chorus itself does not listen on HTTPS.

## Resources and Lifecycle

- `Session:MaxSessions=200`: beyond the limit, evict the least recently seen; the evicted Session disappears directly (the client must reconnect and resync).
- `Session:IdleEvictAfterMs=600000`: reclaimed after 10 minutes idle once all clients disconnect.
- `Session:MaxPendingEvents=8000` / `EventEnqueueMaxWaitMs=50`: event lane capacity and bounded gift wait, 503 retryable when exceeded.
- `Session:MaxSnapshotBytes=1048576`: snapshots exceeding the limit skip sending and count `chorus_snapshots_oversize_total`; the client resync is the fallback.

## Logs and Correlation

Logs use structured templates; key fields: `sessionId`, `clientId`, `tickId`, `eventId`, `messageId` (IPC delivery), `SourceEventId` (event correlation). IPC failures, ACK anomalies, and client reconnects all log these fields. For cross-process correlation see "Correlation IDs" in [Observability](./observability.md).

## State and Restart

- Session state is purely in-memory, no persistence (explicitly a non-goal). Restart means total loss: all clients disconnect and re-hello.
- No cross-process state sharing: the single-process and split forms are each independent; do not run multiple Gateway/EngineHost groups sharing the same `Ipc:UnixSocketDir` prefix.
- Graceful shutdown: process exit terminates sessions immediately; the upstream should handle reconnection.

## Split Mode Containerization

- Three-process form: two Gateway containers configured with `Gateway:Roles=platform` / `Gateway:Roles=streamer` respectively (listener endpoints of unselected roles are not registered, returning 404). The two gateways can share the same EngineHost and the same socket directory: each Gateway is configured with a different `Gateway:Id` (e.g. `platform-gw` / `streamer-gw`), EngineHost is configured with `Ipc:GatewayIds=platform-gw,streamer-gw`, and the engine fans out publishing to the two eng→gw channels (`FanOutMessageBus`, 5c5d25c; guard `ThreeProcessEndToEndTests`). UDS/NamedPipe channel names are distinguished by id suffix; TCP/HTTP ports/URLs are not distinguished by id, so multiple gateways on the same machine use UDS/NamedPipe.
- Two containers: Gateway and EngineHost; `Ipc:Transport=Tcp`, map `27500/27501` (HTTP then `27600/27601`).
- Health checks probe `/health` separately (liveness); **Gateway readiness probes `/ready`** (200 = any engine message arrived within the window),
  on the EngineHost side use `chorus_snapshots_sent_total` in `/metrics` to determine whether the engine is producing snapshots.
- Order is not mandatory: if EngineHost is not ready when Gateway starts, event publishing fails and counts into `chorus_ipc_publish_failures_total` (idempotent retry on the client side), and it recovers automatically once EngineHost is ready.
