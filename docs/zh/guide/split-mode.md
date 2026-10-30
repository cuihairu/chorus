---
title: 分离模式（Gateway + EngineHost）
---


# 分离模式（Gateway + EngineHost）

本指南涵盖基础的 IPC 分离与受控的多 Worker 模式。未设置 `Control:Url` 时，Gateway 按静态配置的 IPC Worker 路由；在 Gateway 与 EngineHost 上都设置 `Control:Url` 时，`Chorus.ControlHost` 提供共享的房间权威、持久检查点以及动态 owner/epoch 路由。

- `Chorus.Gateway`：对外暴露 WebSocket `/ws` 与平台入口点 `/ingress/{platform}`
- `Chorus.EngineHost`：运行权威 tick + 状态快照（SessionActor + GameModule），并提供 `GET /health` 与 `GET /metrics`（本地 `http://localhost:5067`）
- 两者通过本地 IPC 通信（默认：优先 UDS，失败时回退到 NamedPipe；可选 TCP）


> 当前部署：使用 **Chorus.PlatformGateway**（`/ingress/{platform}`）与
> **Chorus.StreamerGateway**（`/ws`）作为独立可执行文件。两者都使用
> `Chorus.Gateway.Runtime` 库；都不依赖另一个可执行文件，也不使用 `Gateway:Roles`。
> 参见[独立网关部署](./independent-gateways.md)。
> 下文合并的 `Chorus.Gateway` 与角色切换示例为兼容性保留。

## 1）启动 EngineHost

```bash
dotnet run --project src/Chorus.EngineHost
```

默认启动 IPC 服务端：`Gateway -> Engine`（命令通道），并作为 IPC 客户端连接 `Engine -> Gateway`（事件通道）。

## 2）启动 Gateway

```bash
dotnet run --project src/Chorus.Gateway
```

Gateway 启动 IPC 服务端：`Engine -> Gateway`（事件通道），并作为 IPC 客户端连接 `Gateway -> Engine`（命令通道）。

## 3）WebSocket 连接

连接端口取决于 `src/Chorus.Gateway/Properties/launchSettings.json`（默认类似 `ws://localhost:5168/ws`）。

发送第一条消息：

```json
{"type":"hello"}
```

随后会收到 `welcome`（EngineHost 下发的权威 tick 参数），并持续收到 `snapshot`。

## 4）注入测试事件（模拟平台）

```bash
curl -X POST http://localhost:5168/ingress/test \
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
