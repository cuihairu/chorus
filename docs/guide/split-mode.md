---
title: 拆分模式（Gateway + EngineHost）
---

# 拆分模式（Gateway + EngineHost）

本模式用于验证“单进程/拆分兼容”的架构：

- `Chorus.Gateway`：对外提供 WebSocket `/ws` 与平台入口 `/ingress/{platform}`
- `Chorus.EngineHost`：仅运行权威 tick + 状态快照（SessionActor + GameModule）
- 两者通过本机 IPC 通信（默认：优先 UDS，失败时降级 NamedPipe；可选：TCP）

## 1) 启动 EngineHost

```bash
dotnet run --project src/Chorus.EngineHost
```

默认会启动 IPC server：`Gateway -> Engine`（命令通道），并作为 IPC client 连接 `Engine -> Gateway`（事件通道）。

## 2) 启动 Gateway

```bash
dotnet run --project src/Chorus.Gateway
```

Gateway 会启动 IPC server：`Engine -> Gateway`（事件通道），并作为 IPC client 连接 `Gateway -> Engine`（命令通道）。

## 3) WebSocket 连接

连接端口取决于 `src/Chorus.Gateway/Properties/launchSettings.json`（默认类似 `ws://localhost:5168/ws`）。

第一条消息发送：

```json
{"type":"hello"}
```

之后会收到 `welcome`（由 EngineHost 下发的权威 tick 参数），并持续收到 `snapshot`。

## 4) 注入测试事件（模拟平台）

```bash
curl -X POST http://localhost:5168/ingress/test \
  -H 'content-type: application/json' \
  -d '{"sessionId":"<sessionId>","userId":"u1","kind":"Like"}'
```

## IPC 配置

两边都支持以下配置（见各自 `appsettings.json`）：

- `Ipc:Transport`: `Auto|UnixDomainSocket|NamedPipe|Tcp`
- `Ipc:UnixSocketDir`: 默认 `/tmp/chorus`
- `Ipc:NamedPipePrefix`: 默认 `chorus`
- `Ipc:TcpHost`: 默认 `127.0.0.1`
- `Ipc:TcpGatewayToEnginePort`: 默认 `27500`
- `Ipc:TcpEngineToGatewayPort`: 默认 `27501`

如果要把 Gateway/EngineHost 拆到两个容器里，通常会把 `Ipc:Transport` 设为 `Tcp`，并为上述两个端口做容器端口映射。
