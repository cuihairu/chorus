---
title: Load Testing
---


# Load Testing (Standalone Load Test Tool)

`src/Chorus.LoadTool` is a standalone load test tool targeting a running Gateway: multiple Sessions × audience events (HTTP ingress) × variable-speed WS clients (fast/normal/slow ACK tiers) × random disconnects and reconnects.

Related docs:

- [Split Mode](../guide/split-mode.md)
- [Observability](../reference/observability.md)
- [Configuration Reference](../reference/config.md)

## Running

```bash
dotnet run --project src/Chorus.LoadTool -- --platform test --url http://127.0.0.1:5168 --duration 60
```

Prerequisite: Gateway or Server is already running. The tool constructs two payload shapes, selected by `--platform`:

- `--platform test` (recommended by default): `TestPlatformEventMapper` shape (camelCase fields), which goes through the full pipeline with the unsigned demo verifier when `Ingress:AllowAll=true`, no signature required.
- `--platform douyin`: `DouyinPlatformEventMapper` shape (`session_id`/`event_id`/`event`…). Note the fail-closed semantics: platforms with a registered `DouyinPlatformEventVerifier` always prefer real signature verification; `Ingress:AllowAll` only covers platforms without a registered verifier, so douyin requires configuring `Ingress:Douyin:AppSecret` and having the caller sign; use `--sign <secret>` (or `CHORUS_LOAD_SIGN_SECRET`) to have the tool sign per the placeholder scheme (HMAC-SHA256 over the raw body, lowercase hex, `X-Douyin-Signature` header).

The WS side requires `--token`, or configuring `Faces:StreamerWs:AllowAnonymous=true` on Gateway/Server (both are development demo switches).

## Parameters

| Parameter | Default | Meaning |
| --- | --- | --- |
| `--url` | `http://127.0.0.1:5168` | ingress face address (Gateway / Server both work) |
| `--ws-url` | derived from `--url` | `/ws` face address. Derived from `--url` by default (http→ws, https→wss, appending the `/ws` path); under the split-role topology (`Gateway:Roles=platform` and `=streamer` in separate processes) the two faces are not on the same port, use this parameter to explicitly specify the process hosting `/ws` |
| `--platform` | `douyin` | ingress platform name (`test` uses the unsigned demo pipeline) |
| `--sessions` | 4 | number of concurrent Sessions |
| `--rate` | 50 | events/second, **total across all Sessions** (spread across producers, round-robin covering all Sessions). `RatePacer` aligns each producer's send cadence to an absolute schedule (before 2026-10-16 it was evenly divided intervals, truncated to 0 at ≥1000/s/producer, silently disabling `--rate`); when a producer can't keep up with the target cadence it sends at its actual response speed, and the long-run rate approaches the target. To actually hit the target rate, the number of producers must be ≥ target rate × average per-request latency |
| `--clients` | 3 | number of WS clients per Session |
| `--producers` | 4 | number of parallel HTTP producer connections |
| `--driver-processes` | 1 | number of independent driver processes. The parent splits `--rate` evenly, prefixes event/client ids per shard, and partitions the session set across shards (shard k owns session slots where slot % n = k; a starved shard with no owned sessions sends no events). This removes the single LoadTool process as the driver-side bottleneck without multiplying the WS fan-out (2026-10-20; before this, every shard recreated the full session set) |
| `--ack-mix` | `fast,normal,slow` | ACK tier mix (fast≈real-time / normal≈a few ticks / slow≈lag of ten-plus ticks) |
| `--disconnect-every` | 20 | average client disconnect interval (seconds, 0=never) |
| `--reconnect-ms` | 1000 | reconnect backoff after disconnect |
| `--duration` | 60 | run duration (seconds) |
| `--users` | 5000 | simulated viewer pool size |
| `--gift-rate` | 0.05 | gift event proportion |
| `--dup-rate` | 0.02 | proportion of duplicate event ids (triggers deduplication) |
| `--match` | empty | shared match id (multiple Sessions in the same match) |
| `--sign` | empty | sign ingress (douyin placeholder HMAC-SHA256 / kuaishou official sha1 scheme; or environment variable `CHORUS_LOAD_SIGN_SECRET`, alias `CHORUS_LOAD_DOUYIN_SECRET`) |
| `--token` | empty | WS auth token (or environment variable `CHORUS_WS_TOKEN`) |
| `--engine-metrics` | empty | Engine `/metrics` URL; sampled every `--report-every` during the run and peak values taken |
| `--report-every` | 5 | progress report interval (seconds) |

Environment variables can also be used: `CHORUS_LOAD_URL`, `CHORUS_LOAD_WS_URL`, `CHORUS_LOAD_PLATFORM`, `CHORUS_LOAD_SIGN_SECRET` (alias `CHORUS_LOAD_DOUYIN_SECRET`), `CHORUS_WS_TOKEN`, `CHORUS_LOAD_ENGINE_METRICS`.

Split-role topology example (two gateways sharing one EngineHost, see [Split Mode](./split-mode.md) for `Gateway:Roles`/`Gateway:Id`/`Ipc:GatewayIds`):

```bash
dotnet run --project src/Chorus.LoadTool -- --url http://127.0.0.1:5168 \
  --ws-url ws://127.0.0.1:5178/ws --duration 60
```

## What to Watch

A progress line is printed every `--report-every` seconds:

```
[loadtool] t=5s ingress(sent=1500,ok=1468,dup=29,429=3,503=0,err=0) ws(snaps=4123,acks=980,maxLag=8,reconnects=2)
```

- `ingress.ok` / `dup` / `401` / `429` / `503` / `err`: ingress accepted, deduplicated, signature rejected, rate limited, event queue full, other errors — corresponding to `chorus_ingress_*`. Under healthy operation `dup` should be roughly equal to the configured `--dup-rate` proportion; `dup` far above it means eventIds collide on the client side (the defect fixed on 2026-10-15: producers numbering independently caused a large number of events to be swallowed by deduplication, and the events never entered the engine — at that point CPU measurements are meaningless).
- `ws.snaps` / `acks` / `maxLag` / `reconnects`: snapshots received by clients, ACKs sent, maximum lagging tick, number of disconnects.
- A summary is printed at the end; if `--engine-metrics` is given, three engine-side peaks are also printed (`engine peaks: sessions=… tickMs=… cpuPercent=…`, corresponding to the phase-three trigger conditions "single worker peak CPU > 60%" and "room count on a single machine reaching the hundreds"), and it can also (`PrintEngineMetricsAsync`) print the raw `/metrics` output once. Note: `cpuPercent` depends on `chorus_process_cpu_percent`, and **only the split-mode EngineHost `/metrics` emits this metric** (`RenderEngine`) — when pointing at a monolithic Server's `/metrics` it is always `-`; phase-three gate measurements must run Gateway+EngineHost and point `--engine-metrics` at EngineHost.

Typical usage: first run through at a low rate (`--rate 10 --duration 30`), then gradually increase to the target rate; watch `503` (event queue full, meaning the rate exceeds the engine's consumption capacity), `429` (ingress rate limiting), and `maxLag` (whether slow clients catch up via resync).

For rates that exceed one driver process, split the same run across independent processes:

```bash
dotnet run --project src/Chorus.LoadTool -- --driver-processes 4 --rate 30000
```

Each child receives its own rate target and `shardN-` event/client id namespace, and labels its progress/summary output with `shardN`. Aggregate the labeled summaries to compare total sent/accepted counts with gateway metrics.

There is a zero-acceptance alarm at the end: when `sent>0 && accepted=0`, `[loadtool] ALARM: ...` is printed to stderr and the tool exits with a non-zero exit code — the typical cause is the gateway having `Ingress:Douyin:AppSecret` configured while the request is unsigned (pure 401); when running douyin/kuaishou unsigned, an unsigned warning is also printed at startup.

## Test Coverage

- Pure logic (payload shapes, duplicate id behavior, CLI flag/environment variable/error path parsing) is covered by `LoadToolTests`.
- `IngressDriver`'s status bucketing (200/deduplicated/429/503/other), counting accuracy under parallel producers, and "multiple producers' eventIds never collide" (shared global sequence numbers, dedup keys not self-colliding on the client side) are covered by `IngressDriverTests` (stub handler, counters are Interlocked).
- Douyin placeholder signing (`--sign`/`CHORUS_LOAD_SIGN_SECRET`, same for kuaishou) is covered by `IngressSignerTests` (standalone hardcoded vectors + verification passing against the gateway verifier) and `IngressDriverTests`'s signature header/401 bucketing cases; the zero-acceptance alarm and exit code are covered by `IngressDiagnosticsTests`.
- `ClientSimulation` injects the test server's in-memory WS channel through the transport seam (`connect` delegate, same convention as `ChorusClient`), covering the full handshake loop of hello/welcome/snapshot replay/ACK and disconnect/reconnect (`ClientSimulationTests`).
- Runtime verification against a real Gateway/Server (smoke: under `--platform test`, ingress accepted/deduplicated, WS snapshot/ACK/reconnect, and engine-side `chorus_events_applied_total` and `chorus_snapshots_sent_total` all grow as expected).
