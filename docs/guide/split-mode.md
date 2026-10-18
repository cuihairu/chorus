---
title: Split Mode (Gateway + EngineHost)
---


# Split Mode (Gateway + EngineHost)

This guide covers the basic IPC split and the controlled multi-worker mode. Without `Control:Url`, Gateway routes over the statically configured IPC workers; when `Control:Url` is set on Gateway and EngineHost, `Chorus.ControlHost` supplies the shared room authority, durable checkpoints, and dynamic owner/epoch routing.

- `Chorus.Gateway`: exposes WebSocket `/ws` and the platform entry point `/ingress/{platform}` externally
- `Chorus.EngineHost`: runs the authoritative tick + state snapshots (SessionActor + GameModule), and provides `GET /health` and `GET /metrics` (local `http://localhost:5067`)
- The two communicate over local IPC (default: prefer UDS, fall back to NamedPipe on failure; optional: TCP)

## 1) Start EngineHost

```bash
dotnet run --project src/Chorus.EngineHost
```

By default it starts the IPC server: `Gateway -> Engine` (command channel), and connects as an IPC client to `Engine -> Gateway` (event channel).

## 2) Start Gateway

```bash
dotnet run --project src/Chorus.Gateway
```

Gateway starts the IPC server: `Engine -> Gateway` (event channel), and connects as an IPC client to `Gateway -> Engine` (command channel).

## 3) WebSocket Connection

The connection port depends on `src/Chorus.Gateway/Properties/launchSettings.json` (by default something like `ws://localhost:5168/ws`).

Send the first message:

```json
{"type":"hello"}
```

After that you will receive `welcome` (the authoritative tick parameters sent down by EngineHost), and continuously receive `snapshot`.

## 4) Inject Test Events (Simulating a Platform)

```bash
curl -X POST http://localhost:5168/ingress/test \
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
