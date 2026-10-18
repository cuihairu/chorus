---
title: "Battle: Sessions, Frame Sync, and Client Integration"
---


# Battle: Sessions, Frame Sync, and Client Integration

Encapsulation status: **Encapsulated**. The battle capability consists of four stable interfaces:

| Block | Entry | Description |
|---|---|---|
| Viewer input | `POST /ingress/{platform}` | danmaku/gift → `AudienceEvent`, the only gameplay input surface |
| Streamer-side rendering | `WS /ws` | hello → welcome → snapshot loop |
| Client SDK | `Chorus.Client.ChorusClient` | connection/rendering/ack/resync all in one |
| Server-side rules | `IGameModule` / `IGameModuleFactory` | registered by the host, called by the engine every tick |

## Understand the Model in One Minute

- **There is no "create match" API**: the session is implicitly established when the first audience event or the first WS hello arrives
  (`src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs:142`, `src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:106`).
  When hello carries no `sessionId`, the server generates `s_{guid}` (`StreamerWsFace.cs:142-143`).
- **Server-authoritative tick**: one logical frame every 66ms by default (`src/Chorus.Engine/Session/SessionConfig.cs:4`),
  events are buffered first and take effect uniformly on the next tick, with `InputDelayTicks` (default 2, `:5`)
  resisting platform jitter.
- **Client only renders**: one frame snapshot per tick (`SnapshotEveryTicks` default 1, `:6`); after applying a frame it replies with `ack`,
  and when missing frames are detected it sends `resync`, the engine replays from frame history or pushes a forced snapshot to align.

## Client Integration: Minimal Example

Use the SDK (recommended; message semantics are already implemented: out-of-order dropping, idempotent replay, ack throttling, resync cooldown):

```csharp
using Chorus.Client;

var options = ChorusClientOptions.Default(
    wsUrl: "ws://localhost:5083/ws",
    sessionId: "s1",        // 首次可传空语义：见下裸协议说明
    clientId: "c1");

await using var client = await ChorusClient.ConnectAsync(
    options,
    onState: state => Console.WriteLine($"tick state: {state.GetRawText()}"));

// client.Completion      —— 连接关闭（服务器关闭/传输错误/Dispose）时完成
// client.FramesApplied   —— 已应用帧数
// client.LastAppliedFrameId / AcksSent / ResyncsSent —— 诊断
```

The `ChorusClient.ConnectAsync` signature is at `src/Chorus.Client/ChorusClient.cs:72`; connecting completes
the hello/welcome handshake, with the handshake timeout controlled by `HandshakeTimeout`. **Callbacks execute on the receive loop; do not block it.**

`ChorusClientOptions` parameters (`ChorusClient.cs:12-34`):

| Parameter | Type | Meaning |
|---|---|---|
| `WsUrl` | string | full `/ws` address |
| `SessionId` / `ClientId` | string | session and client identity |
| `Token` | string? | hello auth token (`Faces:StreamerWs:AuthTokens`, fail-closed) |
| `AckEveryFrames` | int | reply with one ack every N frames (default 2; production limit 600 acks/minute/connection) |
| `ResyncCooldown` | TimeSpan | client resync cooldown (the engine side additionally has `Session:ResyncRequestCooldownMs`, default 1000ms) |
| `HandshakeTimeout` | TimeSpan | hello→welcome budget |

Raw WS protocol (when not using the SDK): JSON text frames; the message `type` constants are in
`src/Chorus.Protocol/ProtocolTypes.cs:5-12`.

```json
// 客户端 → 服务器（首帧必须是 hello）
{"type":"hello","sessionId":"s1","clientId":"c1","token":"...","protocolVersion":1}

// 应用帧后（引擎按此计算 lag；超 MaxLagTicks 会收到 forced 快照对齐）
{"type":"ack","sessionId":"s1","clientId":"c1","lastAppliedTickId":42}

// 检测到帧缺口时（fromFrameId = 最后连续帧号）
{"type":"resync","sessionId":"s1","clientId":"c1","fromTickId":40}
```

```json
// 服务器 → 客户端
{"type":"welcome","sessionId":"s1","clientId":"c1","tickDurationMs":66,"inputDelayTicks":2,"snapshotEveryTicks":1,"serverTimeMs":1760000000000,"protocolVersion":1}
{"type":"snapshot","sessionId":"s1","tickId":42,"serverTimeMs":1760000000066,"state":{...},"v":1,"frameEvents":null}
```

## Response Structures

The message records are defined in `src/Chorus.Protocol/Messages/`:

| Message | Fields | Source |
|---|---|---|
| `welcome` | `sessionId`、`clientId`、`tickDurationMs`、`inputDelayTicks`、`snapshotEveryTicks`、`serverTimeMs`、`protocolVersion` | `ServerWelcome.cs:5-14` |
| `snapshot` | `sessionId`、`tickId`、`serverTimeMs`、`state` (gameplay JSON)、`forced?`、`reason?`、`targetClientId?`、`clientLagTicks?`、`clientLastAckTickId?`、`v` (schema version)、`frameEvents?` (per-event attribution) | `ServerSnapshot.cs:5-24` |
| `error` | `message`、`code?` | `ServerError.cs` |

For field semantics and versioning policy, see [Protocol Reference](../reference/protocol.md) and [Versioning Policy](../reference/versioning.md).

## Error Codes and Rejection Behavior

Failures at the WS layer mostly manifest as **closing the connection** rather than an error code (`src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs`):

| Trigger | Behavior | Source |
|---|---|---|
| hello over the message limit | close `MessageTooBig` "hello too large" | `:94` |
| hello parse failure | close `NormalClosure` "invalid hello" | `:100` |
| first frame is not hello | close `NormalClosure` "first message must be hello" | `:117` |
| hello auth failure (fail-closed) | `error` `unauthorized: {reason}` then close | `:138` |
| subsequent message over the limit | close `MessageTooBig` "message too large" | `:175` |
| engine unavailable / welcome timeout (split mode) | `error` `Engine unavailable` / `Engine welcome timeout` | `src/Chorus.Gateway/IpcSessionRouter.cs:44`、`:57` |

After a successful handshake, only `ack` / `resync` / `ping` are accepted (`StreamerWsFace.cs:182-237`).

## Server Side: Defining Battle Rules

Battle rules implement `IGameModule` (`src/Chorus.Engine/Abstractions/IGameModule.cs:6-24`),
and the host registers the factory (`src/Chorus.Server/Program.cs:17`, `src/Chorus.EngineHost/Program.cs:11`):

| Member | Responsibility |
|---|---|
| `Name` | gameplay identifier (the `game` field in the snapshot) |
| `ApplyEvents(tickId, events)` | consume this batch of audience events each tick and advance gameplay state |
| `GetStateSnapshot()` | produce the snapshot `state` (sent in full to clients every tick; keep the size down) |
| `RestoreState(version, state)` | restore from the authoritative snapshot when the session is born; unknown versions throw directly (fail-closed) |

See `src/Chorus.SampleGame/SampleGameModule.cs` for a reference implementation (Name `:32`, state structure `users`/`totals`/`top` `:125-138`).
For how to write one and antipatterns, see [Game Module](./game-module.md).

## Boundaries (What It Doesn't Do)

- **No HTTP API for creating/joining a match**: the session is implicitly established; the explicit "start game / join" product semantics are undecided
  (for matchmaking placement see the boundaries note in [Matchmaking](./matchmaking.md)).
- **Clients cannot write gameplay data**: on the WS there are only `ack`/`resync`/`ping`; gameplay input can only come from platform ingress (anti-abuse surface minimized).
- **Game type is fixed at the process level**: one host has one `IGameModuleFactory`; selecting gameplay per request is not supported.
- **The gateway holds no session state** (split mode): reconnecting to a different gateway replica does not affect the session, but reconnect semantics are implemented by the client.
- **No persistence**: a process restart ends the match (see Decision 5 in [Architecture Overview](../architecture.md)).

## Related Pages

- [Getting Started](./getting-started.md) / [Frame Sync Best Practices](../design/frame-sync-best-practices.md)
- [Client Resync State Machine](../design/client-resync-state-machine.md)
- [API Reference](../reference/api.md) (error code table)
