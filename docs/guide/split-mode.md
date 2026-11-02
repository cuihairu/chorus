---
title: Split Mode (Gateway + EngineHost)
---


# Split Mode (Gateway + EngineHost)

This guide covers the basic IPC split and the controlled multi-worker mode. Without `Control:Url`, the gateway hosts route over the statically configured IPC workers; when `Control:Url` is set on the gateway hosts and EngineHost, `Chorus.ControlHost` supplies the shared room authority, durable checkpoints, and dynamic owner/epoch routing.

- `Chorus.PlatformGateway` / `Chorus.StreamerGateway`: expose the platform entry point `/ingress/{platform}` and WebSocket `/ws` externally, respectively
- `Chorus.EngineHost`: runs the authoritative tick + state snapshots (SessionActor + GameModule), and provides `GET /health` and `GET /metrics` (local `http://localhost:5067`)
- The two communicate over local IPC (default: prefer UDS, fall back to NamedPipe on failure; optional: TCP)


> Current deployment: the gateway edge is **Chorus.PlatformGateway** (`/ingress/{platform}`) and
> **Chorus.StreamerGateway** (`/ws`) as separate executables, both using the
> `Chorus.Gateway.Runtime` library and neither depending on the other executable.
> The legacy single `Chorus.Gateway` host was removed (2026-10-31, `48623a5`); the steps below
> target the independent hosts. See [independent gateway deployment](./independent-gateways.md).

## 1) Start EngineHost

```bash
dotnet run --project src/Chorus.EngineHost
```

By default it starts the IPC server: `Gateway -> Engine` (command channel), and connects as an IPC client to `Engine -> Gateway` (event channel).

## 2) Start the gateway hosts

`src/Chorus.Gateway` no longer exists (removed in `48623a5`). The gateway edge is two hosts,
`Chorus.PlatformGateway` and `Chorus.StreamerGateway`, started from environment configuration
(`CHORUS_CLUSTER_ID`, `CHORUS_ROOT_CA_PEM` / `CHORUS_NODE_CA_PEM`, `CHORUS_TLS_CERT_PEM` / `CHORUS_TLS_KEY_PEM`,
ports via `CHORUS_PORT` / `CHORUS_INGRESS_PORT` / `CHORUS_WSS_PORT`; see the env surface at the top of
`src/Chorus.PlatformGateway/Program.cs` and `src/Chorus.StreamerGateway/Program.cs`, and
[independent gateway deployment](./independent-gateways.md)).

## 3) WebSocket Connection

The client-facing port comes from the host's environment: `Chorus.StreamerGateway` serves `/ws` on the WSS listener (`CHORUS_WSS_PORT`, default `7443`); `Chorus.PlatformGateway` opens the `/ingress/{platform}` listener only when `CHORUS_INGRESS_PORT` is set (internal `CHORUS_PORT`, defaults `7504` / `7530`).

Send the first message:

```json
{"type":"hello"}
```

After that you will receive `welcome` (the authoritative tick parameters sent down by EngineHost), and continuously receive `snapshot`.

## 4) Inject Test Events (Simulating a Platform)

```bash
curl -X POST http://localhost:<ingress-port>/ingress/test \
  -H 'content-type: application/json' \
  -d '{"sessionId":"<sessionId>","userId":"u1","kind":"Like"}'
```

## IPC Configuration

Both sides support the following configuration (see each side's `appsettings.json`):

- `Ipc:Transport`: `Auto|UnixDomainSocket|NamedPipe|Tcp|Http`
- `Ipc:UnixSocketDir`: default `/tmp/chorus`
- `Ipc:NamedPipePrefix`: default `chorus`
- `Ipc:TcpHost`: default `127.0.0.1`
- `Ipc:TcpGatewayToEnginePort`: default `27500`
- `Ipc:TcpEngineToGatewayPort`: default `27501`
- `Ipc:HttpGatewayToEngineUrl`: default `http://127.0.0.1:27600/gw-to-eng/`
- `Ipc:HttpEngineToGatewayUrl`: default `http://127.0.0.1:27601/eng-to-gw/`
- `Ipc:GatewayIds`: default `gateway` (engine side; comma-separated for multiple gateways, see [Process Topology](../design/process-topology.md) §4)
- `Ipc:GatewayId`: default `gateway` (Gateway side; distinguishes identity with multiple gateways)
- `Ipc:HeartbeatIntervalMs`: default `5000` (engine→gateway heartbeat; `0` disables it)
- `Ipc:ReadinessMaxSilenceMs`: default `15000` (Gateway `/ready` decision window; see [Deployment](../reference/deployment.md))

If you want to split Gateway/EngineHost into two containers, you typically set `Ipc:Transport` to `Tcp` and map those two ports in the container port mapping.

## Multiple Gateways (Split Gateway Roles)

A single EngineHost can host multiple Gateway processes (platform-gw / streamer-gw, see [Process Topology](../design/process-topology.md) §4):

- Each Gateway configures `Gateway:Id` (e.g. `platform-gw` / `streamer-gw`); its own eng→gw channel name automatically carries the id suffix
- EngineHost configures `Ipc:GatewayIds=platform-gw,streamer-gw` (comma-separated); the engine fans out publishes on each channel
- The two gateways can share the same `Ipc:UnixSocketDir`; channel names are distinguished by id and do not overwrite each other
- When `Gateway:Id` / `Ipc:GatewayIds` are not configured, behavior matches the old version (single-gateway channel names)

## Multiple Workers

For controlled rooms, configure the same `Control:Url` and `Control:Key` on Gateway and each EngineHost, give every EngineHost a unique `Ipc:WorkerId`, and run one shared ControlHost. ControlHost assigns rooms by live worker capacity; Gateway refreshes the owner/epoch routing snapshot and selects the matching worker endpoint. This is the path used for durable checkpoints and recovery.

The legacy no-ControlHost mode also supports static `Ipc:WorkerIds`/`Ipc:WorkerId` routing. It uses the `RoomAffinity` prototype and does not provide ControlHost leases, durable checkpoints, or failover fencing. Use distinct IPC endpoint IDs and reachable worker endpoints for the selected transport.
