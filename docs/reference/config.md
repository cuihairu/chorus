---
title: Configuration Reference
---

<p align="center">
  English | <a href="config.zh.md">简体中文</a>
</p>

# Configuration Reference

Configuration source: the `Session` section of appsettings.json maps to `Chorus.Engine.Session.SessionConfig` (the defaults below are used when not configured).

Current defaults:

```text
tickDurationMs = 66
inputDelayTicks = 2
snapshotEveryTicks = 1
maxEventsPerTick = 200
maxLagTicks = 50
resyncCooldownMs = 2000
maxPendingEvents = 8000
eventEnqueueMaxWaitMs = 50
tickTimeoutMs = 250
maxSessions = 200
idleEvictAfterMs = 600000
maxSnapshotBytes = 1048576
frameHistoryBytes = 8388608
resyncRequestCooldownMs = 1000
maxResyncReplayTicks = 128
```

Key parameters:

- `tickDurationMs`
- `inputDelayTicks`
- `snapshotEveryTicks`
- `maxEventsPerTick`
- `maxLagTicks`
- `resyncCooldownMs`
- `maxPendingEvents` / `eventEnqueueMaxWaitMs` (phase 2 overload protection)
- `tickTimeoutMs` (phase 2 timeout detection)
- `maxSessions` / `idleEvictAfterMs` (phase 2 session lifecycle)
- `maxSnapshotBytes` (phase 3 snapshot size limit)

## Parameter Descriptions

### `tickDurationMs`

Meaning:

- Server-authoritative logic tick period, in milliseconds

Default:

- `66` (~15fps logic progression; unified with the frame-sync-best-practices recommendation on 2026-10-12, final value governed by load test)

Impact:

- Smaller values give finer sync, but put more pressure on the server and clients
- Larger values make the system more stable, but input application and recovery granularity are coarser

Recommendation:

- Live-streaming interactive scenarios usually recommend landing at `50~66`; the current default `66` is already unified with that, final value governed by load test
- Combine with `tickTimeoutMs` (default 250) and watch whether `lastTickProcessMs` exceeds the limit

### `inputDelayTicks`

Meaning:

- How many ticks in the future input is scheduled before unified settlement

Default:

- `2`

Impact:

- Larger values absorb jitter and out-of-order better
- Smaller values feel faster, but are more susceptible to network fluctuation

### `snapshotEveryTicks`

Meaning:

- Periodic snapshot send frequency

Default:

- `1`

Impact:

- `1` means a snapshot is sent every tick
- Larger values lower bandwidth and serialization costs, but recovery becomes slower

Recommendation:

- The current default suits fast validation during development
- In production, usually adjust to `2~5` based on snapshot size and bandwidth cost

### `maxEventsPerTick`

Meaning:

- Maximum number of events entering authoritative logic per tick

Default:

- `200`

Impact:

- Acts as a safety valve against "single-frame explosions"
- Beyond the limit triggers the reducer's trimming, merging, or downsampling effects

### `maxLagTicks`

Meaning:

- How many ticks a client falls behind before forced snapshot recovery is triggered

Default:

- `50`

Impact:

- Larger values make the system more tolerant of short-term jitter
- Larger values also mean slow clients stay in a wrong state longer

Recommendation:

- The current default is conservative, suitable for avoiding frequent resync in the development environment
- In production it usually needs to be significantly tighter, otherwise recovery is not aggressive enough

### `resyncCooldownMs`

Meaning:

- Minimum interval between two **server-pushed** forced snapshots for the same client (the engine decides the client is behind based on the ACK watermark and pushes proactively)

Default:

- `2000`

Impact:

- Prevents repeated resync within a short time
- Too small causes jittery recovery
- Too large leaves problem clients unable to re-align for a long time
- **Not shared with `resyncRequestCooldownMs` (client-initiated pull)**, see below

### `frameHistoryBytes`

Since 2026-10-13 (frame sync P2).

Meaning:

- Byte limit of the per-session frame history ring buffer, storing recent wire snapshots for the client `resync` to refill by frame number

Default:

- `8388608` (8 MiB/session)

Impact:

- Capped by bytes, evicting the oldest frames; a just-added frame is not evicted (when a single frame exceeds the entire capacity, it is kept as the only frame)
- If the refill window start is earlier than the oldest retained frame → treated as a miss (return the latest snapshot + `forced=true`/`reason="resync-gap"`), no partial replay
- Memory upper bound = this value × `maxSessions` (200) ≈ worst case 1.6 GiB; adjust per measured snapshot bytes after the production load test
- `0` disables frame history: all `resync` requests go through the `resync-gap` alignment path

### `resyncRequestCooldownMs`

Since 2026-10-13 (frame sync P2).

Meaning:

- Minimum interval between two **active pulls** (`resync` message) by the same client, engine-layer per-client cooldown

Default:

- `1000`

Impact:

- Repeated requests within the cooldown count into `TotalResyncThrottled` and return directly
- Additionally there is a transport-layer fixed window `Faces:StreamerWs:ResyncPerMinute`; the two layers take effect independently

### `maxResyncReplayTicks`

Since 2026-10-15 (churn fix ③, replay bound).

Meaning:

- Maximum number of frames replayed for a single client `resync` request; the snapshot carries the full state, and when the window exceeds this value, a single
  `forced=true` (`reason="resync-gap"`) latest snapshot is returned directly for alignment, without frame-by-frame replay

Default:

- `128` (about 8.4 seconds of 66ms frames)

Impact:

- Caps the eng→gw message volume per replay (2026-10-15 gate load test: replay flood × snapshot distribution once killed the receive loop, see `todo.md:156`)
- Exceeding the limit counts into `chorus_resync_replay_capped_total` (counted separately from the window miss `chorus_resync_window_misses_total`)
- `0` completely disables replay: all `resync` requests go through the `resync-gap` single-frame alignment

### `maxSnapshotBytes`

Meaning:

- Size limit of a single serialized snapshot, in bytes

Default:

- `1048576` (1 MiB)

Impact:

- Snapshots that exceed the limit after serialization **skip sending** and count into `chorus_snapshots_oversize_total`
- Skipping does not disconnect the client; the client-side ACK watermark and reconnect resync mechanism are responsible for the fallback
- Repeatedly exceeding the limit means state boundedness is out of control (e.g. `Dictionary<userId,...>` only grows); fix the state size first rather than increasing this value

Recommendation:

- Estimate bandwidth with `snapshotEveryTicks` and the number of online clients: per client about `snapshotBytes × clientCount / snapshotEveryTicks / tickDurationMs` of sustained downstream

## Ingress and Listener Face (since 2026-10-12)

Monolith and Gateway are isomorphic. The two types of listener face configuration run on separate paths (see `docs/design/process-topology.md`),
counters are exposed via `GET /faces`.

| Key | Default | Description |
|---|---|---|
| `Ingress:AllowAll` | `false` | **Development switch**: when `true`, registers an unsigned signature verification fallback (platform callbacks without a real verifier are allowed through). When enabled, the startup log prints an ERROR warning; it is only recommended to explicitly enable it in appsettings.Development.json, and production configuration does not include it |
| `Ingress:DedupTtlSeconds` | `600` | Event dedup TTL (platformEventId) |
| `Ingress:FactionRules:CommentKeywords` | empty | Danmaku keywords → faction mapping |
| `Ingress:FactionRules:GiftIds` | empty | Gift ID → faction mapping |
| `Ingress:Douyin:AppSecret` | none | Douyin callback signature verification secret; when missing, `/ingress/douyin` rejects everything (fail-closed) |
| `Ingress:Douyin:SignatureHeader` | `X-Douyin-Signature` | Signature header name (pending joint debugging confirmation, see platform reference docs) |
| `Ingress:Kuaishou:VerifyToken` | none | Kuaishou "消息验证Token"; when missing, `/ingress/kuaishou` rejects everything (fail-closed) |
| `Ingress:Kuaishou:MsgEncryptKey` | none | Kuaishou "消息加密解密KEY" (base64, 32 bytes); when missing, events cannot be decrypted (400) |
| `Ingress:Kuaishou:SignatureHeader` | `kwaisign` | Official signature header name |
| `Ingress:Bilibili:AccessKeyId` / `AccessKeySecret` | none | Bilibili open-live signature credentials (authorized by the streamer personally, never in the repo or logs); when missing, `/ingress/bilibili` rejects everything (fail-closed) |
| `Ingress:Bilibili:AppId` | none | open-live project app_id; prerequisite for bridge startup |
| `Ingress:Bilibili:RoomOwnerAuthCode` | none | Streamer identity authentication code (used by app_start); prerequisite for bridge startup |
| `Ingress:Bilibili:SessionId` | none | Room → chorus session binding (P1 single binding; multiple rooms belong to control-plane todo §14); when missing, event mapping fails (400) |
| `Ingress:Bilibili:IngressUrl` | none | Bridge forwarding target (e.g. `http://127.0.0.1:5083/ingress/bilibili`); the bridge only starts when the four-piece set plus this key are complete (`BilibiliIngressBridgeHostedService`) |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | platform-ingress face request body limit; 413 when exceeded |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | platform-ingress face per-source per-minute limit; 429 when exceeded |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | platform-ingress face single-request budget; 504 on timeout |
| `Faces:StreamerWs:AuthTokens` | none | Streamer WS onboarding token list, `;`-separated, each entry is `token` or `token@unixMs expiration time`. **When not configured and AllowAnonymous is not enabled → all WS connections rejected (fail-closed)** |
| `Faces:StreamerWs:AllowAnonymous` | `false` | Development switch: allow connections without a token. When enabled, the startup log prints a WARN; it is only recommended to explicitly enable it in appsettings.Development.json |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | Single WS message limit; on exceed the connection is closed with `MessageTooBig` |
| `Faces:StreamerWs:AckPerMinute` | `600` | Per-connection per-minute ACK limit; on exceed silently dropped (counted via `/faces` → `streamerWs.ackDropped`) |
| `Faces:StreamerWs:ResyncPerMinute` | `30` | Per-connection per-minute `resync` refill request limit; on exceed dropped (counted via `/faces` → `streamerWs.resyncDropped`; 2026-10-13 frame sync P2) |

## Gateway Role Packaging (split mode, three-process form)

| Key | Default | Description |
|---|---|---|
| `Gateway:Roles` (Gateway) | `all` | The listener roles this Gateway process carries: `all` (one process, two listeners, default), `platform` (only maps `/ingress`, i.e. platform-gw), `streamer` (only maps `/ws`, i.e. streamer-gw). Endpoints of unselected roles are not registered (404); the two listeners' face implementations share one copy (`PlatformIngressFace` / `StreamerWsFace`), no code divergence; `/health`, `/faces`, `/metrics`, `/ready` are not affected by roles. Illegal values throw at startup (fail-fast). The monolith form is not affected by this key. See [Process Topology](../design/process-topology.md) §4 |
| `Gateway:Id` (Gateway) | empty | This gateway's identifier (ASCII letters/digits/`-` only): suffixes this process's eng→gw channel name, so multiple gateways can share the same EngineHost and the same socket directory. Empty = old single-gateway channel name (backward compatible). Illegal characters throw at startup. See the multi-gateway section in [Split Mode](../guide/split-mode.md) |

## IPC and Readiness (split mode)

The `Ipc` section maps to `Chorus.Messaging.Ipc.IpcSettings` (port/transport); heartbeat/readiness/fanout have three additional keys:

| Key | Default | Description |
|---|---|---|
| `Ipc:HeartbeatIntervalMs` (EngineHost) | `5000` | Interval at which the engine sends `eng.heartbeat` on the eng→gw channel; `0` disables |
| `Ipc:ReadinessMaxSilenceMs` (Gateway) | `15000` | Maximum silence window for Gateway `GET /ready` to decide 200; beyond the window 503 (fail-closed) |
| `Ipc:GatewayIds` (EngineHost) | empty | Comma-separated list of gateway identifiers (e.g. `platform-gw,streamer-gw`); the engine creates one eng→gw client per id and fans out publishing per id. Empty = old single-gateway channel |

See the "Split Mode Readiness Probe" section in [Deployment and Security](./deployment.md) for details.

## Recommended Reading

- [Parameter Tuning](./tuning.md)
- [Troubleshooting](./troubleshooting.md)
- [Frame Sync Best Practices](../design/frame-sync-best-practices.md)
- [Platform Integration Reference: Douyin](./platform-douyin.md)
- [Platform Integration Reference: Kuaishou](./platform-kuaishou.md)
- [Platform Adapter Contract](./adapter-contract.md)
- [Deployment and Security](./deployment.md)
- [Versioning Strategy and Compatibility](./versioning.md)
