---
title: Current Architecture Overview (Process Topology and Components)
---


# Current Architecture Overview

Code-verified topology (refreshed 2026-10-19). Standalone `Chorus.Server` runs without the control plane. Durable split mode uses `Chorus.ControlHost` as shared room authority alongside one or more `Chorus.Gateway` instances and `Chorus.EngineHost`; production multi-host failure and capacity gates remain open.

## Split Mode With ControlHost

```mermaid
flowchart TB
  subgraph EXT["外部"]
    P["抖音 / 快手 / B站 open-live"]
    C["主播 / 直播伴侣客户端"]
  end

  subgraph GW["Chorus.Gateway 进程（可多实例，Gateway:Id 区分）"]
    INGRESS["PlatformIngressFace<br/>POST /ingress/{platform}"]
    WS["StreamerWsFace<br/>WS /ws"]
    OPS["/health /ready /faces /metrics"]
    GWIPC["GatewayIpcService<br/>eng→gw 通道 server（每实例一条）<br/>gw→eng 通道 client"]
  end

  subgraph WORKER["Chorus.EngineHost（room-worker）"]
    ENGIPC["EngineIpcService<br/>gw→eng 通道 server（共享）<br/>FanOutMessageBus → 每网关一条 eng→gw client"]
    REG["SessionRegistry<br/>房间 → SessionActor 路由"]
    SA["SessionActor × N<br/>单线程邮箱，固定 tick 节拍"]
    GM["IGameModule<br/>权威玩法逻辑"]
    CPU["ProcessCpuGauge<br/>worker CPU 水位"]
  end

  subgraph CONTROL["Chorus.ControlHost（共享控制面）"]
    ROOMDB["RoomControlStore<br/>owner / epoch / checkpoint / inbox"]
    ROUTING["ControlRoutingSnapshot<br/>房间与连接路由快照"]
  end

  LOADTOOL["Chorus.LoadTool<br/>压测采样 /metrics"]
  SDK["Chorus.Client<br/>ChorusClient + FramePlayer<br/>客户端 SDK（帧同步 P3）"]

  P -->|"HTTPS 平台回调"| INGRESS
  C <-->|"hello / welcome / ack / snapshot / resync"| WS
  SDK <-->|"同一 /ws 协议"| WS
  INGRESS -->|"快照路由 + 去重投递"| SA
  WS -->|"welcome / snapshot"| GWIPC
  GWIPC <-->|"gw→eng 命令通道（所有网关共享）"| ENGIPC
  ENGIPC <-->|"eng→gw 快照 / 欢迎 / 心跳（按 Gateway:Id 后缀，引擎扇出）"| GWIPC
  ENGIPC --> REG --> SA --> GM
  GWIPC -.->|"分配 / 新鲜路由快照"| ROOMDB
  ENGIPC -.->|"worker lease / tick checkpoint"| ROOMDB
  ROOMDB --> ROUTING
  GM -.->|"每 tick 读状态打包快照 V=1（SessionActor.cs:451 → WireSnapshot）"| SA
  ENGIPC -.->|"进程级水位，仅 /metrics 采样"| CPU
  LOADTOOL -.->|"HTTP 采样"| OPS
```

Solid lines are realtime execution paths; dashed lines are control-plane RPCs and snapshot refreshes rather than per-message calls. Independent-machine failure acceptance remains open.

## Single-Process Form (Default)

`Chorus.Server` packs the Gateway side and the EngineHost side of the diagram above into the same process: `/ingress/{platform}`、`/ws`、`/faces`、`/metrics`、`/sessions`、`/matches` reach `SessionManager` directly via `InProc` routing within the same process, with no IPC hop. `/sessions` and `/matches` are exposed only in the single-process form.

## Component Index (file:line)

| Component | Location |
|---|---|
| Gateway routing table (/ingress, /ws, /ready, etc.) | `src/Chorus.Gateway/Program.cs:100` |
| EngineHost routing table (/health, /metrics) | `src/Chorus.EngineHost/Program.cs:34` |
| Server routing table (including /sessions, /matches) | `src/Chorus.Server/Program.cs:102` |
| `PlatformIngressFace` (rate limiting/size/timeout/verification/deduplication/Tell) | `src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs` |
| Bilibili open-live bridge (wss → `/ingress/bilibili` signature forwarding, started only when credentials are complete) | `src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveClient.cs` |
| `StreamerWsFace` (authentication/version gate/ACK rate limiting) | `src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs` |
| `GatewayIpcService` (eng→gw server + gw→eng client) | `src/Chorus.Gateway/GatewayIpcService.cs:149` |
| `EngineIpcService` (gw→eng server + fanout client) | `src/Chorus.EngineHost/EngineIpcService.cs:88` |
| `FanOutMessageBus` (per-gateway publishing, failure aggregation) | `src/Chorus.Messaging/FanOutMessageBus.cs` |
| IPC channel naming (Gateway:Id suffix) | `src/Chorus.Messaging/Ipc/IpcEndpointBuilder.cs:22` |
| `SessionRegistry` (routing + lifecycle) | `src/Chorus.Engine/Session/SessionRegistry.cs:15` |
| `SessionActor` (single-threaded mailbox + fixed tick) | `src/Chorus.Engine/Session/SessionActor.cs:8` |
| `IGameModule` (authoritative gameplay contract) | `src/Chorus.Engine/Abstractions/IGameModule.cs:5` |
| Room assignment / durable authority / routing snapshot | `src/Chorus.Control/RoomControlStore.cs`, `src/Chorus.Control/ControlRoutingSnapshot.cs` |
| Historical `RoomAffinity` / `RoomRouteTable` prototype (not used by ControlHost mode) | `src/Chorus.Engine/Routing/RoomAffinity.cs:12`, same file `:53` |
| Snapshot version V (wire protocol) | `src/Chorus.Protocol/Messages/ServerSnapshot.cs:20` |
| `ProcessCpuGauge` (worker CPU water level) | `src/Chorus.EngineHost/ProcessCpuGauge.cs` |
| `Chorus.LoadTool` (load testing and peak sampling) | `src/Chorus.LoadTool/Program.cs` |

## Snapshot and Restore Contract

The engine produces a wire snapshot every tick (full JSON, schema version `V=1`). In ControlHost mode, a committed tick is also stored as a fenced durable checkpoint and restored by EngineHost on assignment; standalone mode has no durable restart recovery. Independent-host failover remains an open rollout gate. See [multi-machine room architecture](../architecture/multi-machine-rooms.md) and [storage decision](../architecture/room-control-storage.md).

## Related Pages

- [Process Topology Design (Phases and Trigger Conditions)](./process-topology.md)
- [Communication Abstraction and IPC Channel Naming](./ipc.md)
- [Deployment and Security](../reference/deployment.md)
- [Split Mode Guide](../guide/split-mode.md)
