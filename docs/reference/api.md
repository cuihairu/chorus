---
title: API Reference (Backend/Admin Side)
---


# API Reference

All endpoints this service exposes to the backend/admin side: HTTP endpoints, WS events, split mode IPC envelopes, and the configuration surface.
All claims are governed by the source code; see file:line references on each entry.

## Authentication

- **HTTP endpoints (/health, /ready, /sessions, /matches, /leaderboard, /faces, /metrics, /) have no authentication** and rely on the internal network assumption;
  production deployments should place them behind a reverse proxy and restrict sources (see the reverse proxy section in [Deployment and Security](./deployment.md)).
- The two surfaces with authentication are: `POST /ingress/{platform}` (platform callback signature verification, fail-closed) and `WS /ws`
  (hello token authentication, fail-closed). See [Configuration Reference](./config.md) for configuration.

## Ports

| Host | HTTP | Source |
|---|---|---|
| `Chorus.Server` (single-process) | `5083` | `src/Chorus.Server/Properties/launchSettings.json:8` |
| `Chorus.PlatformGateway` | internal `7504` (`CHORUS_PORT`); ingress listener only when `CHORUS_INGRESS_PORT` is set | `src/Chorus.PlatformGateway/Program.cs:17`, `:24` |
| `Chorus.StreamerGateway` | internal `7530` (`CHORUS_PORT`); client WSS `7443` (`CHORUS_WSS_PORT`) | `src/Chorus.StreamerGateway/Program.cs:15`, `:18` |
| `Chorus.EngineHost` | `5067` | `src/Chorus.EngineHost/Properties/launchSettings.json:8` |

## HTTP Endpoints

### Chorus.PlatformGateway (`src/Chorus.PlatformGateway/Program.cs`)

| Method Path | Purpose | Source |
|---|---|---|
| `POST /ingress/{platform}` | Platform event entry (rate limiting / verification / mapping / dedup, fail-closed); the public listener opens only when `CHORUS_INGRESS_PORT` is set | `Program.cs:147`, port `:24` |
| mTLS gRPC `GatewayGrpcService` | Cluster-internal face (internal port `CHORUS_PORT`, default `7504`) | `Program.cs:142` |

### Chorus.StreamerGateway (`src/Chorus.StreamerGateway/Program.cs`)

| Method Path | Purpose | Source |
|---|---|---|
| `WS /ws` | Streamer client protocol (public WSS listener, `CHORUS_WSS_PORT`, default `7443`) | `Program.cs:147` |
| mTLS gRPC `GatewayGrpcService` | Cluster-internal face (internal port `CHORUS_PORT`, default `7530`) | `Program.cs:146` |

The gateway hosts hold no session state, so there is no `/sessions` or `/leaderboard` (the latter is on EngineHost / Server). The former single gateway's diagnostic endpoints (`GET /`, `/health`, `/ready`, `/faces`, `/metrics`) are implemented in `Chorus.Gateway.Runtime` (`GatewayHosting.cs:22-35`) and are currently mapped by `Chorus.Server` / `Chorus.EngineHost`, not by the split gateway hosts. The ingress request/error-code and WS-protocol semantics below are shared faces from `Chorus.Gateway.Core` and apply to both the single-process and split forms.

Ingress curl example (single-process `Chorus.Server` on 5083):

```bash
curl -X POST http://localhost:5083/ingress/test -H 'content-type: application/json' \
  -d '{"sessionId":"s1","eventId":"e1","userId":"u1","kind":"comment","text":"hi"}'
```

### Chorus.EngineHost (`src/Chorus.EngineHost/Program.cs`)

| Method Path | Purpose | Response | Source |
|---|---|---|---|
| `GET /` | Self-description | `{"name":"Chorus.EngineHost","ok":true}` | `Program.cs:46` |
| `GET /health` | Liveness probe | 200 empty | `Program.cs:47` |
| `GET /sessions` | Session/client diagnostic JSON (per-client lag/ack detail; session authority in split mode) | JSON | `Program.cs:52` (`c7846a6` completed) |
| `GET /leaderboard/{scope}/{scopeId}` | Leaderboard read-only query (scope/error codes see [Leaderboard](../guide/leaderboard.md)) | JSON | `Program.cs:53` |
| `GET /metrics` | Prometheus text: session diagnostics + `chorus_ipc_publish_failures_total` + `chorus_snapshot_bytes` + `chorus_process_cpu_percent` (worker CPU, omitted on first capture) | text | `Program.cs:54`; CPU gauge `EngineHost/ProcessCpuGauge.cs` |

```bash
curl http://localhost:5067/health
curl http://localhost:5067/sessions
curl http://localhost:5067/metrics
```

### Chorus.Server (single-process, `src/Chorus.Server/Program.cs`)

The single-process form has one extra diagnostic endpoint; `/matches` is only exposed in this form (the authoritative `MatchRegistry` is in single-process). `/sessions` is exposed by EngineHost in split mode (see above); `/leaderboard` is on the session host in both forms:

| Method Path | Purpose | Source |
|---|---|---|
| `GET /` | Self-description | `Program.cs:141` |
| `GET /health` | Liveness probe | `Program.cs:142` |
| `GET /sessions` | Session/client diagnostic JSON (lag, ack, dropped counts) | `Program.cs:143` |
| `GET /matches` | Match-level aggregate view JSON | `Program.cs:144` |
| `GET /leaderboard/{scope}/{scopeId}` | Leaderboard read-only query (see [Leaderboard](../guide/leaderboard.md)) | `Program.cs:145` |
| `GET /faces` | Same as Gateway | `Program.cs:146` |
| `GET /metrics` | Same as Gateway | `Program.cs:149` |
| `POST /ingress/{platform}` | Same as Gateway | `Program.cs:160` |
| `WS /ws` | Same as Gateway | `Program.cs:165` |

```bash
curl http://localhost:5083/sessions
curl http://localhost:5083/matches
```

### /faces Fields

`GET /faces` returns two sections, `IngressFaceSnapshot` + `StreamerWsFaceSnapshot`
(`src/Chorus.Gateway.Core/Faces/FaceCounters.cs:67`, `:76`):

- ingress: `accepted` / `rateLimited` / `tooLarge` / `timedOut` / `unverified` / `duplicates` / `unmapped`
- streamerWs: `connections` / `unauthorized` / `protocolMismatch` / `messageTooLarge` / `ackAccepted` / `ackDropped` / `resyncDropped` / `snapshotDropped`

## Ingress Requests and Error Codes

The request body is parsed by the platform mapper into `AudienceEvent`
(`src/Chorus.Protocol/Models/AudienceEvent.cs`, record fields):

| Field | Type | Required | Description |
|---|---|---|---|
| `eventId` | string | Yes | Platform event id, dedup key (TTL see `Ingress:DedupTtlSeconds`) |
| `platform` | string | Mapper injected | Source platform identifier |
| `sessionId` | string | Yes | Session/room id |
| `userId` | string | No | Viewer id (mapper defaults to `anonymous`) |
| `kind` | enum | Yes | `comment` / `like` / `gift` |
| `ingestTimeMs` | long | Mapper injected | Ingest time (unix ms) |
| `text` / `giftId` / `giftCount` / `giftValue` | optional | No | Taken per kind |
| `matchId` / `factionId` | optional | No | Faction assignment hint |

Responses and error codes (`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs`):

| Status | Condition | Source |
|---|---|---|
| 200 `{ok,sessionId,eventId,tick?}` | Accepted; `tick` only in single-process form (engine tick goes over IPC via welcome/snapshot) | `:141-143` |
| 200 `{ok:true,duplicated:true,...}` | Duplicate event (same `eventId` within TTL) | `:101` |
| 400 `{error:"unmapped_event",platform}` | Unknown event type | `:93` |
| 401 | Signature verification failed / missing verifier / missing secret (fail-closed) | `:76` |
| 413 | Request body exceeds `Faces:PlatformIngress:MaxBodyBytes` | `:68` |
| 429 | Exceeds `Faces:PlatformIngress:RateLimitPerMinute` | `:58` |
| 503 | Engine-side delivery failure (dedup rolled back; a platform retry is a completely new delivery); or event lane full | `:133`, `:152` |
| 504 | Single request exceeds `Faces:PlatformIngress:TimeoutMs` | `:148` |

## WS Protocol (/ws)

JSON text frames. Message definitions are in `src/Chorus.Protocol/Messages/`:

### Client → Server

| Message | Fields | Source |
|---|---|---|
| `hello` | `sessionId?`, `clientId?`, `token?`, `clientVersion?`, `protocolVersion?` (default = old client allowed through; mismatch rejected) | `ClientHello.cs:3` |
| `ack` | `sessionId`, `clientId`, `lastAppliedTickId`, `clientTimeMs?` | `ClientAck.cs:3` |

### Server → Client

| Message | Fields | Source |
|---|---|---|
| `welcome` | `sessionId`, `clientId`, `tickDurationMs`, `inputDelayTicks`, `snapshotEveryTicks`, `serverTimeMs`, `protocolVersion` | `ServerWelcome.cs:5` |
| `snapshot` | `sessionId`, `tickId`, `serverTimeMs`, `state` (gameplay JSON), `forced?`, `reason?`, `targetClientId?`, `clientLagTicks?`, `clientLastAckTickId?`, `v` (schema version, defaults to 1 when absent) | `ServerSnapshot.cs:5` (`v` at `:20`) |
| `error` | `message`, `code?` — `Invalid hello` / `First message must be hello` / protocol version reason / `unauthorized: {reason}` / `Engine unavailable` / `Engine welcome timeout` | `ServerError.cs:3`; six send sites `StreamerWsFace.cs:97`, `:114`, `:124`, `:134`, `IpcSessionRouter.cs:39`, `:52` |

For field semantics, the `v` versioning strategy, and the client state machine, see [Protocol Reference](./protocol.md) and [Versioning Strategy](./versioning.md).

### Connection Close Reasons (close status + reason)

| Close | Trigger | Source (`StreamerWsFace.cs`) |
|---|---|---|
| `MessageTooBig` "hello too large" | hello exceeds `Faces:StreamerWs:MaxMessageBytes` | `:90` |
| `NormalClosure` "invalid hello" | hello parse failure | `:96` |
| `NormalClosure` "first message must be hello" | First frame is not hello | `:113` |
| `PolicyViolation` (protocol version reason) | `protocolVersion` mismatch, before authentication | `:123` |
| `PolicyViolation` `unauthorized: {missing_token\|invalid_token\|token_expired}` | hello authentication failure (fail-closed) | `:133` |
| `MessageTooBig` "message too large" | Subsequent message exceeds limit | `:169` |

## IPC Envelopes (split mode internal interface, not a backend surface)

The engine and gateway communicate via `Envelope` (JSON, ordered by `sessionId`); message type constants
`src/Chorus.Messaging/Ipc/IpcMessageTypes.cs:3`:

| Direction | Type | Payload | Source |
|---|---|---|---|
| gw→eng | `gw.connect` / `gw.disconnect` | `GatewayConnectCmd` / `GatewayDisconnectCmd` | `Gateway/GatewayIpcService.cs:96`, `:112` |
| gw→eng | `gw.ack` | `GatewayAckCmd` (`lastAppliedTickId`, `clientTimeMs`) | `GatewayIpcService.cs:117` |
| gw→eng | `gw.audienceEvent` | `AudienceEvent` (with `SourceEventId` correlation) | `EngineHost/EngineIpcService.cs:236` |
| eng→gw | `eng.welcome` | `ServerWelcome` | `EngineIpcService.cs:205` |
| eng→gw | `eng.snapshot` | `ServerSnapshot` (including `v`) | `EngineHost/EngineBusSessionClient.cs:49-59` (constructs `ServerSnapshot` directly and publishes; `WireSnapshot` is only used for in-engine broadcast WS path) |
| eng→gw | `eng.heartbeat` | `EngineHeartbeatCmd` (sentinel sessionId `__heartbeat`) | `EngineIpcService.cs:130` |

Channel naming and multi-gateway fanout: `Ipc:GatewayIds` + `Gateway:Id` (see the channel naming section in [Messaging Abstraction](../design/ipc.md)).

## Configuration Surface

For all configuration keys, defaults, and fail-closed semantics, see [Configuration Reference](./config.md); for readiness probe configuration, see the split mode section in [Deployment and Security](./deployment.md).

## Related Pages

- [Protocol Reference](./protocol.md) (fields and examples, versioning semantics)
- [Observability](./observability.md) (`chorus_*` metric inventory, log key fields, correlation IDs)
- [Platform Adapter Contract](./adapter-contract.md) (`/ingress/{platform}` 8-step processing order and signature verification template)
