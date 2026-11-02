---
title: 快速上手
---


# 快速上手

## 环境

- .NET SDK：`10.0.x`（见 `global.json`）
- Node.js：受支持的 LTS 版本（用于构建 VitePress 文档站；包管理脚本见 `package.json`）

## 本地运行服务端

```bash
dotnet run --project src/Chorus.Server
```

服务端口取决于 `src/Chorus.Server/Properties/launchSettings.json`（默认类似 `http://localhost:5083`，也可用 `ASPNETCORE_URLS` 覆盖）：

- `GET /health`：健康检查
- `GET /sessions`：会话/客户端指标
- `GET /metrics`：Prometheus 文本指标（Server 与 Gateway 都提供）
- `GET /faces`：监听面计数器
- `GET /matches`：房间统计（仅单进程模式）
- `POST /ingress/{platform}`：注入测试事件（模拟平台事件，`test` 为示例）
- `WS /ws`：主播客户端连接入口

## WebSocket：连接并接收快照

连接：`ws://localhost:<serverPort>/ws`

第一条消息必须发 `hello`：

```json
{"type":"hello"}
```

服务端返回 `welcome`，其中含 `sessionId` 与 tick 参数。

此后服务端持续下发 `snapshot`（当前默认每 tick 一次）；客户端应用快照后应回 `ack`：

```json
{"type":"ack","sessionId":"<sessionId>","clientId":"<clientId>","lastAppliedTickId":123}
```

客户端落后超过 `maxLagTicks` 时，服务端额外推送 `snapshot(forced=true, reason=\"lag\")`，提示客户端立即按该快照对齐 tick。

## 注入测试事件（模拟平台）

```bash
curl -X POST http://localhost:<serverPort>/ingress/test \
  -H 'content-type: application/json' \
  -d '{"sessionId":"<sessionId>","platform":"test","userId":"u1","kind":"Like"}'

说明：

- v1 统一入口：`POST /ingress/{platform}`
- `test` 平台示例：`POST /ingress/test`，body 需包含 `sessionId`
```

## 构建文档站（静态页）

```bash
pnpm install --frozen-lockfile
pnpm docs:build
```

输出目录：`docs/.vitepress/dist`

> **工具链说明**：文档站用 VitePress 构建——`package.json` 脚本 `docs:dev` = `vitepress dev docs`、`docs:build` = `vitepress build docs`；Pages 部署上传 `docs/.vitepress/dist`（`.github/workflows/docs.yml`）。
