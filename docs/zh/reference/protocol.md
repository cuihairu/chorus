---
title: Protocol Reference
---


# Protocol Reference (v1)

## hello (client -> server)

```json
{"type":"hello","sessionId":"optional","clientId":"optional","token":"optional","protocolVersion":1}
```

Field descriptions:

- `sessionId`: the session the client wants to join; optional
- `clientId`: client identifier; optional; the server may assign one if not provided
- `token`: access credential; optional; verified by the server (see below)
- `protocolVersion`: wire protocol version; optional; currently `1` (see below)

### hello Protocol Version (Since 2026-10-12)

`hello.protocolVersion` / `welcome.protocolVersion` identifies the **wire protocol** version (determined by `ProtocolVersionPolicy.Current` of the server-side `Chorus.Protocol`), unrelated to `clientVersion` (the client's own build number, a free string):

- Default (`null`/not sent) → treated as a legacy client, **allowed through** (backward compatible, no hard upgrade);
- Sent and equal to the server version → allowed through;
- Sent and not equal to the server version → the server first replies `error` (`protocol_mismatch: client=N server=M`),
  then closes the connection with `PolicyViolation`, and counts it in `/faces`'s
  `streamerWs.protocolMismatch`
  and `chorus_ws_protocol_mismatch_total`;
- Version validation happens **before** authentication: clients with the wrong version do not consume authentication decisions.

### hello Authentication (Since 2026-10-12, fail-closed)

After receiving hello, the server verifies the `token` (config `Faces:StreamerWs:AuthTokens`, supports `token@unixMs过期时间`,
multiple tokens separated by semicolons):

- Token missing / unknown / expired → the server first replies `error` (`unauthorized: missing_token | invalid_token | token_expired`),
  then closes the connection with `PolicyViolation`;
- No tokens configured and `Faces:StreamerWs:AllowAnonymous` not enabled → all connections are rejected (fail-closed);
- `AllowAnonymous` is only recommended to be explicitly enabled in the development configuration (appsettings.Development.json).

hello is a single WS text message; exceeding `Faces:StreamerWs:MaxMessageBytes` (default 64KiB) → close the
connection with `MessageTooBig`. ACKs exceeding `Faces:StreamerWs:AckPerMinute` (default 600/connection/minute) →
silently dropped and counted in `/faces`'s `streamerWs.ackDropped`.

## welcome (server -> client)

```json
{
  "type":"welcome",
  "sessionId":"session-1",
  "clientId":"client-1",
  "tickDurationMs":66,
  "inputDelayTicks":2,
  "snapshotEveryTicks":1,
  "serverTimeMs":1710000000000,
  "protocolVersion":1
}
```

Field descriptions:

- `tickDurationMs`: authoritative tick period
- `inputDelayTicks`: how many ticks in the future input is scheduled before settlement
- `snapshotEveryTicks`: periodic snapshot frequency
- `serverTimeMs`: current server timestamp, usable by the client for drift estimation
- `protocolVersion`: server wire protocol version; the client should compare it with its own implementation version and actively disconnect on mismatch rather than guessing fields

After receiving `welcome`, the client should treat it as the authoritative beat configuration, rather than continuing to use local defaults.

## snapshot (server -> client)

```json
{
  "type":"snapshot",
  "sessionId":"session-1",
  "tickId":123,
  "serverTimeMs":1710000000123,
  "state": {},
  "forced":true,
  "reason":"lag",
  "targetClientId":"client-1",
  "clientLagTicks":9,
  "clientLastAckTickId":114,
  "frameEvents":[{"frameId":123,"eventId":"ev-1","kind":"comment","userId":"u-1"}],
  "v":1
}
```

Field descriptions:

- `tickId`: the authoritative tick this snapshot corresponds to
- `serverTimeMs`: server time when the snapshot was generated
- `state`: authoritative state snapshot; v1 is JSON
- `forced`: whether this is a forced resync snapshot
- `reason`: the forced reason; current common values are `lag` (the server actively pushes based on its ACK-based judgment that the client is behind) and `resync-gap` (the backfill frame window of the client's `resync` request has been evicted; the latest frame is fed back)
- `targetClientId`: target client; used when the server wants only a specific slow client to align immediately
- `clientLagTicks`: how many ticks the server judges this client is currently behind
- `clientLastAckTickId`: the most recent ACK tick the server has seen from this client
- `frameEvents`: optional (since 2026-10-13, frame sync P2). Danmaku events applied since the last snapshot; each item's `frameId` is the frame the event was applied in, `eventId` is the event id (deduplication key), `kind` is the normalized event type (`comment` / `like` / `gift` / `share`), and `userId` is the platform user id. **When there are no events the entire field is omitted** (not an empty array) — an additive optional field that does not trigger a `v` bump (see [versioning policy](./versioning.md)). Legacy clients may ignore it.
- `v`: snapshot schema version (live since 2026-10-13, see [versioning policy](./versioning.md)); passed through from the engine snapshot, currently always `1`; when a legacy sender omits it, the receiver reads it as `1`. The handling semantics for unknown versions (reject + force resync) will be defined when the first v2 schema is designed.

Semantics:

- Normal periodic snapshots are used for steady following and reconnect recovery
- When `forced=true && reason="lag"`, it means the server judges the client is behind and needs to align to that snapshot immediately
- When `forced=true && reason="resync-gap"`, it means the start frame of the client's `resync` request is no longer in the engine window; that snapshot is the current latest frame, and the client should align directly without backfilling history
- After receiving a forced snapshot, the client should not keep backfilling history frames for a long time; it should prioritize switching to that snapshot's state

## ack (client -> server)

```json
{
  "type":"ack",
  "sessionId":"session-1",
  "clientId":"client-1",
  "lastAppliedTickId":123,
  "clientTimeMs":1710000000999
}
```

Field descriptions:

- `lastAppliedTickId`: the authoritative tick the client has actually finished applying
- `clientTimeMs`: optional, used to assist drift estimation and troubleshooting

Semantic constraints:

- ACK may only be sent after the state has actually been applied
- ACK cannot mean "message received"
- ACK cannot mean "queued, will apply later"

If the client sends ACK before the snapshot has finished applying, the server will misjudge that it has caught up.

## resync (client -> server)

Since 2026-10-13 (frame sync P2). When the client discovers missing frames or after reconnecting, it pulls the backfill frame window by frame number:

```json
{
  "type":"resync",
  "sessionId":"session-1",
  "clientId":"client-1",
  "fromFrameId":123
}
```

Field descriptions:

- `fromFrameId`: the last frame number the client has applied; the server feeds back the snapshot sequence within the `[fromFrameId, 当前 tickId]` window
- `sessionId` / `clientId`: binding fields, used to verify and locate the target client (requests with unknown `clientId` are counted and then ignored)

Server response semantics (two kinds, mutually exclusive):

1. **Window hit** → feed back every frame snapshot within the window in frame-number order through the direct-send channel (not through the latest-wins queue, so it will not be pushed out by subsequent snapshots). The client applies idempotently by `tickId`: frames with frame numbers ≤ the already-applied frame are dropped directly. Replay and periodic snapshots may interleave in transit; the client must sort by `tickId`, not by arrival order.
2. **Window miss** (the start frame has been evicted, or the session has closed frame history) → feed back a single latest snapshot with `forced=true` and `reason="resync-gap"`; the client aligns to the latest frame and abandons backfilling history.

Rate limiting (two layers):

- WS face fixed window: per connection per minute `Faces:StreamerWs:ResyncPerMinute` (default 30); over-limit requests are dropped and counted in `/faces`'s `streamerWs.resyncDropped`
- Engine per-client cooldown: `Session:ResyncRequestCooldownMs` (default 1000ms); repeated requests within the cooldown are counted in `TotalResyncThrottled` and then returned directly

The server does not send an additional "resync accepted" receipt: the client uses the received snapshot sequence as the response. Retry rules:

- After sending `resync`, **no snapshot at all** received within the agreed budget → retry
- Received a `forced=true` snapshot (`reason="resync-gap"` or `reason="lag"`) → already aligned, **do not retry**
- Received a non-forced snapshot and frames are still being skipped → `resync` may be sent once more; replay frames will be idempotently dropped because their frame numbers ≤ the already-applied frame, so re-sending is safe

## Recommended Implementation Constraints

- When the client is following normally, it should send ACK periodically
- For mild lag, controlled catch-up is acceptable
- For severe lag, it should wait or request the latest snapshot
- The client logic layer should always defer to `tickId` and the authoritative snapshot

## Mapping to the Current Server Implementation

In the current server implementation:

- `welcome` corresponds to `ServerWelcome`
- `snapshot` corresponds to `ServerSnapshot`
- `ack` corresponds to `ClientAck`
- `resync` corresponds to `ClientResync` (frame sync P2)

The key context fields carried by the server's forced resync include:

- `forced`
- `reason`
- `targetClientId`
- `clientLagTicks`
- `clientLastAckTickId`

If the client wants to produce interpretable recovery logs, it is recommended to log these fields together.
