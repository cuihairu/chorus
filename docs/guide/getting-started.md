---
title: Getting Started
---


# Getting Started

## Environment

- .NET SDK: `10.0.x` (see `global.json`)
- Node.js: a supported LTS release (used to build the VitePress documentation site; see `package.json` for the package manager scripts)

## Downloading Nightly Builds

Daily builds are published to the rolling
[`nightly` prerelease](https://github.com/cuihairu/chorus/releases/tag/nightly):

| Asset | Contents | Platform |
|-------|----------|----------|
| `chorus-nightly-linux-x64.tar.gz` | `Chorus.Server`, self-contained — unpack and run `Chorus.Server` | Linux x64 |
| `chorus-server_<version>_amd64.deb` | `Chorus.Server`, installs to `/opt/chorus-server` (`chorus-server` on PATH) | Debian/Ubuntu x64 |
| `chorus-server-0.0.0-1.nightly<date>.x86_64.rpm` | `Chorus.Server`, same layout as the deb | RHEL/Fedora/openSUSE x64 |
| `chorus-nightly-windows-x64.zip` | `Chorus.Server` + `Chorus.NodeAgent`, self-contained | Windows x64 |
| `SHA256SUMS` | SHA-256 checksums for all assets above | — |

The tar.gz and the Windows zip are self-contained (no .NET runtime needed). The deb and rpm are
framework-dependent and declare `dotnet-runtime-10.0` + `aspnetcore-runtime-10.0` as dependencies —
install a .NET 10 runtime first (Microsoft package feed or your distro's). Verify downloads with
`sha256sum -c SHA256SUMS`. There is no GUI client to distribute: clients connect to the server over
WebSocket (see below).

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
pnpm install --frozen-lockfile
pnpm docs:build
```

Output directory: `docs/.vitepress/dist`

> **Toolchain note**: the documentation site is built with VitePress — `package.json` scripts `docs:dev` = `vitepress dev docs` and `docs:build` = `vitepress build docs`; the Pages deploy uploads `docs/.vitepress/dist` (`.github/workflows/docs.yml`).
