---
title: Independent Gateway Hosts
---

# Independent gateway hosts

| Service | Project / published artifact | External entry point | Dedicated responsibility |
|---|---|---|---|
| Platform gateway `platform-gw` | `Chorus.PlatformGateway` | `POST /ingress/{platform}` | Platform signature verification, event mapping, rate limiting, de-duplication, Bilibili Open Live bridge |
| Streamer gateway `streamer-gw` | `Chorus.StreamerGateway` | WebSocket `/ws` | Streamer authentication, connections, snapshots, ACK, resynchronization |

The two are separate executables and can be published and scaled independently. Entry points are fixed per project; `Gateway:Roles` does not change them. The standalone hosts read required settings from `CHORUS_*` environment variables and take their instance identity from the process certificate; `Gateway:Id` only identifies gateways attached to an EngineHost over IPC (`GatewayIpcService.cs:71`), not replicas of these hosts — distinguish replicas by distinct process certificates.

Shared code is layered:

- `Chorus.Gateway.Runtime`: IPC, control-plane routing and connection coordination, shared diagnostic endpoints. No application entry point, and it registers neither platform-only nor streamer-only services.
- `Chorus.Gateway.Core`: entry-point protocol implementations and their interfaces — the faces shared across hosts (referenced directly by Chorus.Server / Chorus.EngineHost (via Chorus.Gateway.Core) by `Chorus.Server` / `Chorus.EngineHost`).
- `Chorus.Control` / `Chorus.Messaging` / `Chorus.Protocol`: control-plane client, communication, and message contracts.

The legacy single `Chorus.Gateway` host was removed (2026-10-31, `48623a5`); the two hosts above are the only gateway executables. The Core library still contains both faces, and platform adapter assemblies may still be copied into the publish directory. What is isolated is the host, service registration, endpoints, and configuration responsibility — not a complete removal of every transitive assembly.

## Running locally

Run these in three separate terminals. Both gateway hosts require the certificate env face (`Program.cs:9-15`) and build their Kestrel listeners from `CHORUS_*` ports, so `--urls` does not apply:

```bash
Ipc__GatewayIds=platform-gw,streamer-gw dotnet run --project src/Chorus.EngineHost

CHORUS_CLUSTER_ID=local CHORUS_ROOT_CA_PEM=./certs/root-ca.pem CHORUS_NODE_CA_PEM=./certs/node-ca.pem \
CHORUS_TLS_CERT_PEM=./certs/gw.pem CHORUS_TLS_KEY_PEM=./certs/gw.key CHORUS_DEV_PLATFORM_ENDPOINT=http://localhost:9000 \
CHORUS_PORT=7504 CHORUS_INGRESS_PORT=5168 dotnet run --project src/Chorus.PlatformGateway

CHORUS_CLUSTER_ID=local CHORUS_ROOT_CA_PEM=./certs/root-ca.pem CHORUS_NODE_CA_PEM=./certs/node-ca.pem \
CHORUS_TLS_CERT_PEM=./certs/gw.pem CHORUS_TLS_KEY_PEM=./certs/gw.key CHORUS_TENANT_APPLICATIONS=tenant-a:app-1 \
CHORUS_PORT=7530 CHORUS_WSS_PORT=5169 dotnet run --project src/Chorus.StreamerGateway
```

The platform endpoint is `https://localhost:5168/ingress/{platform}` (TLS with the server certificate; the listener exists only when `CHORUS_INGRESS_PORT` is set, `Program.cs:45-53`); the streamer endpoint is `wss://localhost:5169/ws`. `PlatformGateway/Program.cs:66` requires `CHORUS_DEV_PLATFORM_ENDPOINT`; `StreamerGateway/Program.cs:53` requires `CHORUS_TENANT_APPLICATIONS`. Platform secrets are configured in the platform host; `Faces:StreamerWs:AuthTokens` is configured in the streamer host. The shared diagnostic endpoints (`/health`, `/ready`, `/metrics`, `/faces`) are implemented in `Chorus.Gateway.Runtime` (`GatewayHosting.cs`); after the independent-host split they are still mapped only by `Chorus.Server` / `Chorus.EngineHost`, and the two gateway hosts do not expose them yet. Entry points a host does not own return 404.

## Multi-machine and independent publishing

```bash
dotnet publish src/Chorus.PlatformGateway -c Release -o publish/platform-gateway
dotnet publish src/Chorus.StreamerGateway -c Release -o publish/streamer-gateway
```

Across machines, use TCP/HTTP IPC and configure reachable listen/advertised endpoints plus a unique instance ID per host. Every gateway and EngineHost uses the same `Control:Url` / `Control:Key`. See [deployment](../reference/deployment.md) and [multi-host acceptance](../architecture/room-multihost-acceptance.md) for the concrete endpoints and lease parameters.

In control mode, one failing Worker makes only its own rooms unroutable; the failure of one room does not drag down the readiness of all gateways. Readiness is still reported as not-ready when the routing snapshot is stale or no healthy Worker exists.

`Control:AutomaticRecovery` defaults to false on the ControlHost. When enabled, the maintenance loop atomically takes over rooms whose lease has expired or whose instance has been replaced, increments the epoch, and moves them to Recovering; a healthy owner can never be preempted. Keep it disabled until the independent-host network-partition and synchronous-replication acceptance is complete. The per-tick persistence contract is unchanged, and publishing gateways independently does not imply the production capacity gate has been met.
