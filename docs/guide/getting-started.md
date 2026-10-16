---
title: Getting Started
---

<p align="center">
  English | <a href="getting-started.zh.md">简体中文</a>
</p>

# Getting Started

## Environment

- .NET SDK: `10.0.x` (see `global.json`)
- Node.js: any supported LTS (used to build the VuePress documentation site; this repo sets no `engines` lower bound)

## Running the Server (Locally)

```bash
dotnet run --project src/Chorus.Server
```

The service port depends on `src/Chorus.Server/Properties/launchSettings.json` (by default something like `http://localhost:5083`, and can also be overridden with `ASPNETCORE_URLS`):

- `GET /health`: health check
- `GET /sessions`: session/client metrics
- `GET /metrics`: Prometheus text metrics (provided by both Server and Gateway)
- `GET /faces`: listener face counters
- `GET /matches`: room statistics (single-process mode only)
- `POST /ingress/{platform}`: inject test events (simulate platform events, `test` is the example)
- `WS /ws`: streamer client connection entry point

## WebSocket: Connecting and Receiving Snapshots

Connect: `ws://localhost:<serverPort>/ws`

The first message must send `hello`:

```json
{"type":"hello"}
```

The server returns `welcome`, which contains `sessionId` and the tick parameters.

After that, the server continuously sends `snapshot` (currently once per tick by default); after applying the snapshot, the client should reply with `ack`:

```json
{"type":"ack","sessionId":"<sessionId>","clientId":"<clientId>","lastAppliedTickId":123}
```

When the client falls behind by more than `maxLagTicks`, the server additionally pushes `snapshot(forced=true, reason=\"lag\")`, prompting the client to immediately align its tick with that snapshot.

## Injecting Test Events (Simulating a Platform)

```bash
curl -X POST http://localhost:<serverPort>/ingress/test \
  -H 'content-type: application/json' \
  -d '{"sessionId":"<sessionId>","platform":"test","userId":"u1","kind":"Like"}'

说明：

- v1 统一入口：`POST /ingress/{platform}`
- `test` 平台示例：`POST /ingress/test`，body 需包含 `sessionId`
```

## Building the Documentation Site (Static Pages)

```bash
npm install
npm run docs:build
```

Output directory: `docs/.vuepress/dist`

> **Dependency pin note**: The documentation site is an old VuePress 1.x + webpack 4 chain, and `package.json`'s `overrides` has a batch of security pins. When changing pins, verify locally that both `npm ci` and `npm run docs:build` pass before committing — two pitfalls already hit: an override key in the form `"a>b"` is pnpm syntax, and npm rejects it with `EINVALIDTAGNAME` (nested objects are the npm form); webpack-log 3.0.2 declares `nanoid: ^2.0.3` and calls `require('nanoid')` as a function, so pinning to nanoid 3.x makes the build fail at `nanoid is not a function`.
