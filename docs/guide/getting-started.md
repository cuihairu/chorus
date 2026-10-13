---
title: 快速开始
---

# 快速开始

## 环境

- .NET SDK：`10.0.x`（见 `global.json`）
- Node.js：任意受支持的 LTS（用于构建 VuePress 文档站；本仓未设 `engines` 下限）

## 运行服务器（本地）

```bash
dotnet run --project src/Chorus.Server
```

服务端口取决于 `src/Chorus.Server/Properties/launchSettings.json`（默认类似 `http://localhost:5083`，也可用 `ASPNETCORE_URLS` 覆盖）：

- `GET /health`：健康检查
- `GET /sessions`：会话/客户端指标
- `GET /metrics`：Prometheus 文本指标（Server 与 Gateway 均提供）
- `GET /faces`：listener face 计数器
- `GET /matches`：房间统计（仅单进程形态）
- `POST /ingress/{platform}`：注入测试事件（模拟平台事件，`test` 为示例）
- `WS /ws`：主播客户端连接入口

## WebSocket：连接与收快照

连接：`ws://localhost:<serverPort>/ws`

第一条消息必须发送 `hello`：

```json
{"type":"hello"}
```

服务器会返回 `welcome`，其中包含 `sessionId` 与 tick 参数。

之后服务器会持续下发 `snapshot`（当前默认每 tick 一次），客户端应在应用快照后回 `ack`：

```json
{"type":"ack","sessionId":"<sessionId>","clientId":"<clientId>","lastAppliedTickId":123}
```

当客户端落后超过 `maxLagTicks`，服务器会额外推送 `snapshot(forced=true, reason=\"lag\")`，提示客户端立即以该快照对齐 tick。

## 注入测试事件（模拟平台）

```bash
curl -X POST http://localhost:<serverPort>/ingress/test \
  -H 'content-type: application/json' \
  -d '{"sessionId":"<sessionId>","platform":"test","userId":"u1","kind":"Like"}'

说明：

- v1 统一入口：`POST /ingress/{platform}`
- `test` 平台示例：`POST /ingress/test`，body 需包含 `sessionId`
```

## 构建文档站（静态页面）

```bash
npm install
npm run docs:build
```

产物目录：`docs/.vuepress/dist`
