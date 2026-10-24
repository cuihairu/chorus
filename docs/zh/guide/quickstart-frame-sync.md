---
title: "Quickstart: Frame Sync Integration"
---


# Quickstart: Frame Sync Integration

This page gives new projects the **minimal runnable** integration steps: one WebSocket client + one callback, rendering the authoritative state by frame number.

> **SDK shipped (2026-10-14, frame sync P3)**: `Chorus.Client.ChorusClient` integrates in one call — hello/welcome handshake, render-by-frame-number callback
> (`FramePlayer` state machine, idempotent dropping of replayed/old frames), post-apply ACK, and automatic `resync` to pull missing frames. End-to-end tests
> `ChorusClientTests` (3 cases, including a real /ws).
>
> Honest boundary: the SDK is a **minimal entry point** (single connection, single callback, no automatic reconnect — the reconnect/backoff strategy is left to the
> integrator; the load test tool `ClientSimulation` demonstrates the full form). For the protocol see [Protocol Reference](../reference/protocol.md); for frame sync
> semantics see [Frame Sync Design](../design/frame-sync.md).

---

## Three-Line Integration (SDK)

```csharp
await using var client = await ChorusClient.ConnectAsync(
    ChorusClientOptions.Default("ws://localhost:5083/ws", "s-1", "c-1"),
    state => RenderYourGame(state));   // 回调只收已应用的最新帧，按帧号严格递增
```

The callback runs on the receive loop, **do not block it**; `ChorusClientOptions` lets you tune ACK sampling (`AckEveryFrames`, default 2) and the `resync` cooldown (default 2s).

---

## Protocol Steps (The SDK Does These for You; Reference for the Raw Protocol Approach)

1. **Connect** `ws://<host>/ws` (for the port see [Configuration Reference](../reference/config.md); the single-process default is `http://localhost:5083`).
2. **Send `hello`**: with `sessionId` / `clientId` / `protocolVersion`.
3. **Receive `welcome`**: read the authoritative cadence configuration (tick parameters); do not keep local defaults.
4. **Render loop**: receive `snapshot`, use `tickId` (frame number) to render `state`; drop frames whose number ≤ the applied frame (idempotent; a replayed window won't render twice).
5. **Send `ack`**: send `lastAppliedTickId` **after the snapshot is actually applied** (not "received").
6. **Gap detection**: frame number > applied frame + 1 → frames were lost, send `resync` (`fromFrameId` = applied frame + 1) to pull every frame in the window.
7. **Lag handling**: receive a `forced=true && reason="lag"` or `reason="resync-gap"` snapshot → align immediately, do not backfill history.
8. **One callback**: `Action<JsonElement>` handles `state`; the rendering layer subscribes to it.

---

## Raw Protocol Skeleton (C#; for a semantic reference implementation see `Chorus.Client`)

```csharp
// 依赖：System.Net.WebSockets（.NET 10 内置）
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://localhost:5083/ws"), CancellationToken.None);

// 2. hello
await Send(ws, new { type = "hello", sessionId = "s-1", clientId = "c-1", protocolVersion = 1 });

long lastAppliedFrameId = -1;   // 已应用的权威帧号
var render = new Action<JsonElement>(state =>
{
    // 7. 你的渲染逻辑：按帧号插值渲染 state
    Console.WriteLine($"frame={lastAppliedFrameId} state={state}");
});

var buffer = new byte[64 * 1024];
while (ws.State == WebSocketState.Open)
{
    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
    if (result.MessageType == WebSocketMessageType.Close) break;

    var doc = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(buffer, 0, result.Count));
    if (!doc.TryGetProperty("type", out var t)) continue;

    switch (t.GetString())
    {
        case "welcome":
            // 3. 读权威节拍（示例只打日志）
            Console.WriteLine($"welcome: {doc}");
            break;

        case "snapshot":
            // 4. 按帧号渲染；7. 强制快照在此对齐
            var frameId = doc.GetProperty("tickId").GetInt64();
            var forced = doc.TryGetProperty("forced", out var f) && f.GetBoolean();
            var state = doc.GetProperty("state");

            if (frameId <= lastAppliedFrameId) break;   // 幂等：丢弃旧帧与回放帧
            var gapFrom = lastAppliedFrameId >= 0 && frameId > lastAppliedFrameId + 1
                ? lastAppliedFrameId + 1 : -1;          // 6. 缺口检测
            lastAppliedFrameId = frameId;
            render(state);

            // 6. 拉取窗口内每一帧；forced 快照本身就是对齐结果，不要再拉
            if (gapFrom >= 0 && !forced)
            {
                await Send(ws, new { type = "resync", sessionId = "s-1", clientId = "c-1", fromFrameId = gapFrom });
            }

            // 5. 应用完成后 ACK
            await Send(ws, new { type = "ack", sessionId = "s-1", clientId = "c-1", lastAppliedTickId = frameId });
            break;
    }
}

static async Task Send(ClientWebSocket ws, object payload)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}
```

> The example above is about 45 lines and **untested** (raw-protocol reference code); the **tested** implementation of the same semantics is
> `Chorus.Client` (`ChorusClient` = handshake + replay + ACK sampling + resync cooldown; `FramePlayerTests` 7 cases + `ChorusClientTests` 3 cases), and the load test tool
> `ClientSimulation` also uses it. For the `forced` branch and the full catch-up strategy of "lag > maxLagTicks waiting for resync", see [Frame Sync Design](../design/frame-sync.md) §4.5.

---

## Key Constraints

- **Frame number = `tickId`**: monotonically increasing, delivered with the snapshot (`ServerSnapshot.cs:8`). The client sorts and renders by frame number.
- **ACK semantics**: `lastAppliedTickId` = the frame number **actually applied**, not "received" (`protocol.md:121-130`).
- **ACK rate limiting**: `Faces:StreamerWs:AckPerMinute` defaults to **600/connection/minute** (`src/Chorus.Gateway.Core/Faces/StreamerWsFaceOptions.cs:21`),
  over-limit ACKs are silently dropped (the `/faces` `streamerWs.ackDropped` counter). With the default `tickDurationMs=66` (≈15.2 snapshots/minute),
  one ACK per frame is about 909/minute, which **will hit the limit** — the skeleton should sample ACKs or raise the value.
- **Forced snapshots**: `forced=true && reason="lag"` → align immediately, no history backfill (`protocol.md:104-105`); `reason="resync-gap"` → your `resync` request
  window has been evicted, this frame is the latest alignment, **do not pull again**.
- **resync rate limiting**: two layers (`Faces:StreamerWs:ResyncPerMinute` default 30/connection/minute + the engine cooldown `Session:ResyncRequestCooldownMs`
  default 1000ms). Send it once only on a real gap; replayed frames are idempotently dropped.
- **Idempotency**: snapshots with frame number ≤ the applied one are dropped outright.
- **Auth**: `/ws` is **fail-closed** by default — requires `Faces:StreamerWs:AuthTokens` or explicitly enabling `AllowAnonymous` ([Configuration Reference](../reference/config.md)).

---

## Related Pages

- [Frame Sync Design](../design/frame-sync.md) — frame number semantics / frame backfill / catch-up strategy
- [Protocol Reference](../reference/protocol.md) — `hello` / `welcome` / `snapshot` / `ack` / `resync` messages
- [API Reference](../reference/api.md) — backend/admin endpoints
- [Versioning Policy](../reference/versioning.md) — compatibility rules
