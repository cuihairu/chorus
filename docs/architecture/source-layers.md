---
title: Source Layer Map
---

# Source Layer Map

This page is deliberately responsibility-based. Directory names overlap because the repository contains a redesign path plus compatibility assemblies. Definitions are in the [source layer glossary](../reference/source-layer-glossary.md); the mirrored Chinese page is [源码分层解说](../zh/architecture/source-layers.md).

## Public Edge

- **Implementation:** `src/Chorus.PlatformGateway/Program.cs:121` assembles platform modules; `src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31` exposes the webhook face. `src/Chorus.StreamerGateway/Program.cs:54` builds the WSS edge and `src/Chorus.StreamerGateway/EdgeWs.cs:22` handles the public protocol.
- **Configuration schema:** platform selection is `Ingress:Platforms` in `src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:20`; WSS knobs are parsed in `src/Chorus.StreamerGateway/EdgeOptions.cs:12`.
- **Dedicated protocol files:** `src/Chorus.StreamerGateway/Messages/EdgeTypes.cs:9`, `src/Chorus.StreamerGateway/Messages/EdgeMessages.cs:7`, and `src/Chorus.Rpc/Protos/m4.proto:13`.
- **Copy:** `src/Chorus.StreamerGateway/Messages/EdgeTypes.cs:63` holds edge error copy; protocol semantics are documented in `docs/reference/protocol.md:1`.

## Business Coordination

- **Implementation:** `src/Chorus.ControlServer/Program.cs:56` wires control stores and coordination services; `src/Chorus.ControlServer/MatchCoordinatorService.cs:34` is the term-based match state machine. `src/Chorus.StreamerServer/Program.cs:132` wires the login coordinator and actor registry.
- **Configuration schema:** control cadence comes from environment parsing in `src/Chorus.ControlServer/Program.cs:110`; streamer tenant/application and platform credentials are parsed in `src/Chorus.StreamerServer/Program.cs:37`.
- **Dedicated protocol files:** `src/Chorus.Rpc/Protos/m1.proto:437` defines streamer identity/session faces, and `src/Chorus.Rpc/Protos/m3.proto:96` defines match, routing, deployment, takeover, and end-operation faces.
- **Copy:** admin RPC copy is owned by `src/Chorus.ControlServer/AdminGrpcService.cs:34`; architecture copy is in `docs/architecture/admin-history.md:1`.

## Game Runtime

- **Implementation:** `src/Chorus.GameServer/RoomRuntime.cs:18` owns room lifecycle and recovery; `src/Chorus.GameServer/RoomActor.cs:160` runs one serial tick executor; `src/Chorus.GameServer/Program.cs:116` maps the runtime RPC faces.
- **Configuration schema:** host limits are parsed in `src/Chorus.GameServer/Program.cs:51`; module-package selection and input translation are parsed at `src/Chorus.GameServer/Program.cs:146` and `src/Chorus.GameServer/Program.cs:156`.
- **Dedicated protocol files:** `src/Chorus.Rpc/Protos/m2.proto:171` defines `GameHost`; `src/Chorus.Rpc/Protos/m2.proto:224` defines `RoundSync`; `src/Chorus.Rpc/Protos/m2.proto:207` defines room-event delivery (the service-domain split leaves messages in `m2.round.proto` and six sibling files).
- **Copy:** room-state names and reasons are owned in `src/Chorus.GameServer/RoomActor.cs:16`; recovery narrative is documented in `docs/design/room-recovery.md:1`.

## Gameplay Module

- **Implementation:** `src/Chorus.ExampleGame/IRedesignGameModule.cs:1` is the module contract; `src/Chorus.ExampleGame/ExampleGameModule.cs:9` declares manifest, state, effects, and results. `src/Chorus.GameServer/RedesignModuleCatalog.cs:37` indexes shipped packages by hash. The legacy-engine sample set is `src/Chorus.SampleGame/SampleGameModule.cs:6` (old `IGameModule` contract, frozen compatibility surface consumed only by the legacy hosts `Chorus.Server`/`EngineHost`).
- **Configuration schema:** `CHORUS_MODULE_PACKAGES` and `CHORUS_INPUT_TRANSLATION` are validated in `src/Chorus.GameServer/Program.cs:146`.
- **Dedicated protocol files:** module identity is carried by `src/Chorus.Rpc/GameModuleIdentity.cs:14`; module inputs/effects are protobuf fields in `src/Chorus.Rpc/Protos/m2.frame.proto:46` (`ClientInputRequest`) and `src/Chorus.Rpc/Protos/m2.frame.proto:146` (`FrameEffectProto`).
- **Copy:** module guide is `docs/guide/game-module.md:1`; module-facing validation copy is embedded in `src/Chorus.GameServer/Program.cs:169`.

## Durable Data

- **Implementation:** `src/Chorus.DataServer/Program.cs:38` instantiates separate stores per aggregate; `src/Chorus.DataServer/RedesignRoomStore.cs:30` is one room transaction owner and `src/Chorus.DataServer/RedesignFrameStore.cs:1` owns frames/snapshots/recovery material.
- **Configuration schema:** database connection and capacity are environment inputs in `src/Chorus.DataServer/Program.cs:12` and `src/Chorus.DataServer/Program.cs:47`.
- **Dedicated protocol files:** data-service and streamer-identity RPCs are in `src/Chorus.Rpc/Protos/m0.proto:40` and `src/Chorus.Rpc/Protos/m1.proto:452`; SQL schema starts at `src/Chorus.Data/Schema/0002_core_schema.sql:1`.
- **Copy:** storage rejection/status strings are stored in `src/Chorus.DataServer/RedesignRoomStore.cs:14`; retention/audit narrative is `docs/architecture/audit-retention.md:1`.

## Platform Integration

- **Implementation:** abstractions live in `src/Chorus.Adapters.Abstractions/IPlatformEventVerifier.cs:3`; Bilibili, Douyin, Kuaishou, and MiniGame implementations live under `src/Chorus.Adapters.Platforms/`; task-side gateways are wired in `src/Chorus.PlatformGateway/Program.cs:121`.
- **Configuration schema:** `src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:20` selects modules and `src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:137` refuses incomplete credentials.
- **Dedicated protocol files:** Bilibili OpenLive constants are isolated in `src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveProtocol.cs:12`; inbound data contracts normalize into `src/Chorus.Protocol/Models/AudienceEvent.cs:10`.
- **Copy:** per-platform integration copy is `docs/reference/platform-bilibili.md:1`, `docs/reference/platform-douyin.md:1`, `docs/reference/platform-kuaishou.md:1`, and `docs/reference/platform-mini-game.md` is represented by `docs/reference/platform-douyin-mini-game.md:1`.

## Cluster Supervision

- **Implementation:** `src/Chorus.NodeAgent/BootstrapAgent.cs:30` supervises child processes; `src/Chorus.ControlServer/ControlGrpcService.cs:15` accepts node/process reports; `src/Chorus.Rpc/ProcessSelfReporter.cs:17` publishes readiness and room progress.
- **Configuration schema:** node roles, tenants, ports, and bootstrap certificates are read in `src/Chorus.BootstrapTool/Program.cs:48`; lease timing is parsed in `src/Chorus.ControlServer/OwnershipRenewalService.cs:16`.
- **Dedicated protocol files:** `src/Chorus.Rpc/Protos/m0.proto:108` defines `ControlService`; process certificate helpers live in `src/Chorus.Rpc/ProcessIdentity.cs:1`.
- **Copy:** deployment copy is `docs/reference/deployment.md:1`; service install copy is `deploy/linux/chorus-node-agent.service:1`.

## Contract & Serialization

- **Implementation:** protobuf inputs and generated clients are declared in `src/Chorus.Rpc/Chorus.Rpc.csproj:13`; portable JSON records live in `src/Chorus.Protocol/Messages/` and `src/Chorus.Protocol/Models/`.
- **Configuration schema:** none; this layer is generated contracts plus small policy types such as `src/Chorus.Protocol/ProtocolVersionPolicy.cs:5`.
- **Dedicated protocol files:** `src/Chorus.Rpc/Protos/m0.proto:1` through `src/Chorus.Rpc/Protos/m4.proto:1`.
- **Copy:** wire names are embedded in proto messages; public protocol documentation is `docs/reference/protocol.md:1`.

## Client, Tooling & Operator

- **Implementation:** `src/Chorus.Client/ChorusClient.cs:19` is the public protocol client; `src/Chorus.LoadTool/Program.cs:9` drives load; `src/Chorus.ControlHost/Program.cs:64` maps the authenticated console API; `src/Chorus.RoundDemo/RoundDemoScenario.cs:40` is an end-to-end demo.
- **Configuration schema:** console users/providers are read in `src/Chorus.ControlHost/Program.cs:15`; load-tool flags are modeled in `src/Chorus.LoadTool/LoadToolOptions.cs:9`.
- **Dedicated protocol files:** the client consumes generated RPC in `src/Chorus.Rpc/` and legacy JSON messages in `src/Chorus.Protocol/`.
- **Copy:** console UI copy is `src/Chorus.ControlHost/wwwroot/admin.js:1`; guide copy is `docs/guide/load-testing.md:1`.

## Legacy Compatibility

- **Implementation:** the old single-process host is `src/Chorus.Server/Program.cs:115`; the old engine split is `src/Chorus.EngineHost/Program.cs:42`; gateway IPC and shared faces are `src/Chorus.Gateway.Runtime/GatewayHosting.cs:16` and `src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs:18`.
- **Configuration schema:** legacy appsettings are `src/Chorus.Server/appsettings.json:1` and `src/Chorus.EngineHost/appsettings.json:1`.
- **Dedicated protocol files:** IPC envelopes are `src/Chorus.Messaging/Envelope.cs:1`; shared WS messages are `src/Chorus.Protocol/Messages/`.
- **Copy:** the old layer map remains at `docs/architecture.md:26` with Chinese mirror `docs/zh/architecture.md:13`.

## Why These Layers Are Split

1. **Edge versus business state:** the webhook face explicitly has no database credentials and returns only durable data-plane outcomes, so listener policy cannot become room state ownership. Evidence: `src/Chorus.PlatformGateway/Program.cs:69`; `src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:24`.
2. **Coordination versus storage:** matching stores tickets and reservations through DataServer RPCs; ControlServer owns term/recovery loops but not direct queue SQL. Evidence: `src/Chorus.ControlServer/MatchCoordinatorService.cs:35`; `src/Chorus.DataServer/MatchGrpcService.cs:22`.
3. **Runtime versus gameplay:** GameServer selects a hash-addressed module package and refuses unknown hashes, while the module owns tick/checkpoint/offline semantics. Evidence: `src/Chorus.GameServer/RedesignModuleCatalog.cs:105`; `src/Chorus.ExampleGame/ExampleGameModule.cs:9`.
4. **Runtime versus durable data:** room commits and recovery loads are remote `FrameStore` calls, so a GameServer restart does not imply business recovery. Evidence: `src/Chorus.GameServer/RoomActor.cs:394`; `src/Chorus.GameServer/RoomRuntime.cs:1019`.
5. **Inbound adapter versus orchestration:** adapters only verify/map/deduplicate; routing and durable ingestion remain gateway/data-plane responsibilities. Evidence: `src/Chorus.Adapters.Abstractions/IPlatformEventVerifier.cs:3`; `src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31`.
6. **Supervision versus business recovery:** process reports are distinct from room progress and ownership; process liveness alone cannot activate a recovered room. Evidence: `src/Chorus.Rpc/ProcessSelfReporter.cs:17`; `src/Chorus.ControlServer/RedesignControlStore.cs:694`.
7. **Contract isolation:** every cross-process call and public WS edge has an explicit protocol artifact, reducing accidental in-memory assumptions. Evidence: `src/Chorus.Rpc/Chorus.Rpc.csproj:13`; `src/Chorus.StreamerGateway/Messages/EdgeTypes.cs:9`.
8. **Client/tool isolation:** load and demo flows drive public faces rather than importing production stores. Evidence: `src/Chorus.LoadTool/Chorus.LoadTool.csproj:12`; `src/Chorus.RoundDemo/RoundDemoScenario.cs:40`.
9. **Operator surface isolation:** the console binds its own authentication, CORS/origin guard, static UI, and legacy store rather than reusing admin RPC directly. Evidence: `src/Chorus.ControlHost/Program.cs:15`; `src/Chorus.ControlHost/Program.cs:64`.
10. **Compatibility isolation:** old monolith/split hosts remain independently runnable while the redesign hosts are not converted back into them. Evidence: `src/Chorus.Server/Program.cs:115`; `src/Chorus.PlatformGateway/Program.cs:121`.
