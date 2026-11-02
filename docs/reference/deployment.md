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


> Current deployment: use **Chorus.PlatformGateway** (`/ingress/{platform}`) and
> **Chorus.StreamerGateway** (`/ws`) as separate executables. Both use the
> `Chorus.Gateway.Runtime` library; neither depends on the other executable or
> uses `Gateway:Roles`. See [independent gateway deployment](../guide/independent-gateways.md).
> The legacy single `Chorus.Gateway` host was removed (2026-10-31, `48623a5`); the forms below describe the independent hosts.

## Forms

| Form | Processes | Applicable |
| --- | --- | --- |
| Single-process | `Chorus.Server` | Default, debugging, small scale; ingress/WS/engine in the same process |
| Split (independent gateway hosts) | `Chorus.PlatformGateway` + `Chorus.StreamerGateway` + `Chorus.EngineHost`; add `Chorus.ControlHost` when `Control:Url` is configured | Gateway edge and authoritative logic on separate machines/containers; ControlHost enables shared room authority and durable checkpoints |
| Edge scaling | Multiple `Chorus.PlatformGateway` / `Chorus.StreamerGateway` instances with distinct `Gateway:Id` + `Chorus.EngineHost`; add `Chorus.ControlHost` for controlled rooms | Platform and streamer edges scale independently; multi-worker routing and durable recovery use ControlHost, see [Process Topology](../design/process-topology.md) §4 |

`/ingress/{platform}` lives on `Chorus.PlatformGateway` and `/ws` on `Chorus.StreamerGateway`; the diagnostic surfaces (`/health`, `/ready`, `/faces`, `/metrics`) are currently mapped by `Chorus.Server` / `Chorus.EngineHost` (the gateway hosts do not expose them yet). `/sessions` is a `Chorus.Server` diagnostic endpoint; ControlHost exposes its own authenticated `/api/matches` management API.

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

## Room Control Storage

`Chorus.ControlHost` uses SQLite by default for development and single-host deployments. For multi-worker production, configure a PostgreSQL 16+ authority database; Workers and Gateways continue to access it only through ControlHost HTTP and must not open a shared database file.

```bash
Control__Provider=PostgreSql
Control__ConnectionString='Host=control-db.internal;Database=chorus;Username=chorus;Password=<from-secret-store>;SSL Mode=VerifyFull'
```

Supply the connection string through the deployment secret store, enable TLS and SCRAM authentication, and provision the synchronous standby described in [Room Control Storage](../architecture/room-control-storage.md). SQLite remains the default when `Control__Provider` is omitted. PostgreSQL integration tests require `CHORUS_TEST_POSTGRES` to point at an isolated test database; CI provisions PostgreSQL 16 for that test.

## Endpoints and Ports

| Endpoint | Purpose | Single-process | Split |
| --- | --- | --- | --- |
| `GET /health` | Liveness probe | ✅ | ✅ (EngineHost; gateway hosts not yet) |
| `GET /ready` | **Readiness probe**: any engine message (heartbeat/welcome/snapshot) received within `Ipc:ReadinessMaxSilenceMs` → 200, otherwise 503 | ❌ | Not yet mapped — mechanism retained in `Chorus.Gateway.Runtime` (`GatewayIpcService`), see the readiness section below |
| `GET /sessions` | Session/client metrics JSON (per-client lag/ack detail) | ✅ | ✅ (EngineHost; completed in `c7846a6` — the engine is the session authority in split mode, previously not exposed) |
| `GET /matches` | Match-level aggregate view JSON | ✅ | ❌ The authoritative `MatchRegistry` is only in single-process; use `/metrics` |
| `GET /faces` | Listener face counter JSON | ✅ | ❌ (gateway hosts not yet; face counters remain on Server) |
| `GET /metrics` | Prometheus text (`chorus_*`) | ✅ | ✅ (Server / EngineHost; gateway hosts not yet) |
| `POST /ingress/{platform}` | Platform event entry | ✅ | ✅ (PlatformGateway) |
| `WS /ws` | Streamer/viewer client | ✅ | ✅ (StreamerGateway) |

Default ports: Server `5083`, EngineHost `5067` (`Properties/launchSettings.json`); `Chorus.PlatformGateway` internal `7504` / ingress listener via `CHORUS_INGRESS_PORT`, `Chorus.StreamerGateway` internal `7530` / WSS `7443` (`CHORUS_PORT` / `CHORUS_WSS_PORT`; no launchSettings files).

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

> Status after `48623a5`: the readiness mechanism (`GatewayIpcService` + the two keys above) is retained in `Chorus.Gateway.Runtime`, but no split gateway host currently maps `/ready`; treat this section as the design contract until the independent hosts wire it up.
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
- For a ControlHost behind TLS termination, set `Admin:PublicOrigin` to the exact public origin (for example `https://admin.example.com`). This value is used for unsafe-request Origin checks and forces Secure admin cookies; do not trust arbitrary forwarded headers for this check.

## Resources and Lifecycle

- `Session:MaxSessions=200`: once at capacity, new Session creation is rejected; existing Actors are not evicted. Reduce load or increase capacity before accepting more rooms.
- `Session:IdleEvictAfterMs=600000`: reclaimed after 10 minutes idle once all clients disconnect.
- `Session:MaxPendingEvents=8000` / `EventEnqueueMaxWaitMs=50`: event lane capacity and bounded gift wait, 503 retryable when exceeded.
- `Session:MaxSnapshotBytes=1048576`: snapshots exceeding the limit skip sending and count `chorus_snapshots_oversize_total`; the client resync is the fallback.

## Logs and Correlation

Logs use structured templates; key fields: `sessionId`, `clientId`, `tickId`, `eventId`, `messageId` (IPC delivery), `SourceEventId` (event correlation). IPC failures, ACK anomalies, and client reconnects all log these fields. For cross-process correlation see "Correlation IDs" in [Observability](./observability.md).

## State and Restart

- GameModule state is in memory while a worker is running. When EngineHost uses ControlHost durability, each committed tick also stores a fenced checkpoint and a replacement owner restores it; standalone mode without control durability has no restart recovery.
- ControlHost is the shared room authority for configured gateway/EngineHost processes. Gateway processes use distinct `Gateway:Id` channel suffixes and can share one EngineHost; do not run separate deployments on the same IPC channel names.
- Graceful shutdown: process exit terminates sessions immediately; the upstream should handle reconnection.

## Split Mode Containerization

- Run the two gateway hosts as separate containers (`Chorus.PlatformGateway`, `Chorus.StreamerGateway`), each with a distinct `Gateway:Id` to share the EngineHost fanout IPC. Controlled rooms also require `Chorus.ControlHost` and `Control:Url` on Gateway/EngineHost; ControlHost routing can use the TCP/HTTP advertised endpoint reported by each Worker. Static IPC with multiple gateways still needs UDS/NamedPipe to avoid port conflicts.
- Containers: the two gateway hosts plus EngineHost; `Ipc:Transport=Tcp`, map `27500/27501` (HTTP then `27600/27601`).
- Health checks probe `/health` separately (liveness); gateway readiness (`/ready`) follows the readiness section above but is not yet mapped by the split hosts.
  On the EngineHost side use `chorus_snapshots_sent_total` in `/metrics` to determine whether the engine is producing snapshots.
- Order is not mandatory: if EngineHost is not ready when Gateway starts, event publishing fails and counts into `chorus_ipc_publish_failures_total` (idempotent retry on the client side), and it recovers automatically once EngineHost is ready.
