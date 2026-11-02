---
title: 分离模式（Gateway + EngineHost）
---


# 分离模式（Gateway + EngineHost）

本指南涵盖基础的 IPC 分离与受控的多 Worker 模式。未设置 `Control:Url` 时，网关宿主按静态配置的 IPC Worker 路由；在网关宿主与 EngineHost 上都设置 `Control:Url` 时，`Chorus.ControlHost` 提供共享的房间权威、持久检查点以及动态 owner/epoch 路由。

- `Chorus.PlatformGateway` / `Chorus.StreamerGateway`：分别对外暴露平台入口点 `/ingress/{platform}` 与 WebSocket `/ws`
- `Chorus.EngineHost`：运行权威 tick + 状态快照（SessionActor + GameModule），并提供 `GET /health` 与 `GET /metrics`（本地 `http://localhost:5067`）
- 两者通过本地 IPC 通信（默认：优先 UDS，失败时回退到 NamedPipe；可选 TCP）


> 当前部署：网关边缘是 **Chorus.PlatformGateway**（`/ingress/{platform}`）与
> **Chorus.StreamerGateway**（`/ws`）两个独立可执行文件，都使用 `Chorus.Gateway.Runtime` 库，
> 互不依赖。
> 旧单体 `Chorus.Gateway` 宿主已于 2026-10-31 移除（`48623a5`）；下文步骤以独立宿主为准。
> 参见[独立网关部署](./independent-gateways.md)。

## 1）启动 EngineHost

```bash
dotnet run --project src/Chorus.EngineHost
```

默认启动 IPC 服务端：`Gateway -> Engine`（命令通道），并作为 IPC 客户端连接 `Engine -> Gateway`（事件通道）。

## 2）启动网关宿主

`src/Chorus.Gateway` 已不存在（`48623a5` 移除）。网关边缘是两个宿主：
`Chorus.PlatformGateway` 与 `Chorus.StreamerGateway`，从环境配置启动
（`CHORUS_CLUSTER_ID`、`CHORUS_ROOT_CA_PEM` / `CHORUS_NODE_CA_PEM`、`CHORUS_TLS_CERT_PEM` / `CHORUS_TLS_KEY_PEM`，
端口走 `CHORUS_PORT` / `CHORUS_INGRESS_PORT` / `CHORUS_WSS_PORT`；环境面见
`src/Chorus.PlatformGateway/Program.cs` 与 `src/Chorus.StreamerGateway/Program.cs` 顶部，以及
[独立网关部署](./independent-gateways.md)）。

## 3）WebSocket 连接

对外端口来自宿主环境：`Chorus.StreamerGateway` 的 `/ws` 在 WSS 监听上（`CHORUS_WSS_PORT`，默认 `7443`）；`Chorus.PlatformGateway` 仅在设置 `CHORUS_INGRESS_PORT` 时开启 `/ingress/{platform}` 监听（内部 `CHORUS_PORT`，默认 `7504` / `7530`）。

发送第一条消息：

```json
{"type":"hello"}
```

随后会收到 `welcome`（EngineHost 下发的权威 tick 参数），并持续收到 `snapshot`。

## 4）注入测试事件（模拟平台）

```bash
curl -X POST http://localhost:<ingress-port>/ingress/test \
  -H 'content-type: application/json' \
  -d '{"sessionId":"<sessionId>","userId":"u1","kind":"Like"}'
```

## IPC 配置

两侧都支持以下配置（见各自 `appsettings.json`）：

- `Ipc:Transport`：`Auto|UnixDomainSocket|NamedPipe|Tcp|Http`
- `Ipc:UnixSocketDir`：默认 `/tmp/chorus`
- `Ipc:NamedPipePrefix`：默认 `chorus`
- `Ipc:TcpHost`：默认 `127.0.0.1`
- `Ipc:TcpGatewayToEnginePort`：默认 `27500`
- `Ipc:TcpEngineToGatewayPort`：默认 `27501`
- `Ipc:HttpGatewayToEngineUrl`：默认 `http://127.0.0.1:27600/gw-to-eng/`
- `Ipc:HttpEngineToGatewayUrl`：默认 `http://127.0.0.1:27601/eng-to-gw/`
- `Ipc:GatewayIds`：默认 `gateway`（engine 侧；多个网关用逗号分隔，见[进程拓扑](../design/process-topology.md) §4）
- `Ipc:GatewayId`：默认 `gateway`（Gateway 侧；多个网关时区分身份）
- `Ipc:HeartbeatIntervalMs`：默认 `5000`（engine→gateway 心跳；`0` 禁用）
- `Ipc:ReadinessMaxSilenceMs`：默认 `15000`（Gateway `/ready` 判定窗口；见[部署](../reference/deployment.md)）

若要将 Gateway/EngineHost 拆分到两个容器，通常将 `Ipc:Transport` 设为 `Tcp`，并在容器端口映射中映射这两个端口。

## 多网关（拆分网关角色）

单个 EngineHost 可以承载多个 Gateway 进程（platform-gw / streamer-gw，见[进程拓扑](../design/process-topology.md) §4）：

- 每个 Gateway 配置 `Gateway:Id`（例如 `platform-gw` / `streamer-gw`）；其自身的 eng→gw 通道名自动携带 id 后缀
- EngineHost 配置 `Ipc:GatewayIds=platform-gw,streamer-gw`（逗号分隔）；engine 在每个通道上扇出发布
- 两个网关可以共享同一个 `Ipc:UnixSocketDir`；通道名按 id 区分，不会互相覆盖
- 未配置 `Gateway:Id` / `Ipc:GatewayIds` 时，行为与旧版本一致（单网关通道名）

## 多 Worker

对于受控房间，在 Gateway 与每个 EngineHost 上配置相同的 `Control:Url` 与 `Control:Key`，为每个 EngineHost 设置唯一的 `Ipc:WorkerId`，并运行一个共享的 ControlHost。ControlHost 按实时 Worker 容量分配房间；Gateway 刷新 owner/epoch 路由快照并选择匹配的 Worker 端点。这是持久检查点与恢复所使用的路径。

旧的无 ControlHost 模式也支持静态 `Ipc:WorkerIds`/`Ipc:WorkerId` 路由。它使用 `RoomAffinity` 原型，不提供 ControlHost 租约、持久检查点或故障切换围栏（fencing）。为所选传输方式使用不同的 IPC 端点 ID 与可达的 Worker 端点。
