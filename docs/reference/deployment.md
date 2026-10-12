---
title: 部署与安全
---

# 部署与安全配置

Chorus 有两种部署形态，共用同一套配置模型与安全语义。

相关文档：

- [拆分模式](../guide/split-mode.md)
- [配置参考](./config.md)
- [可观测性](./observability.md)
- [故障排查](./troubleshooting.md)

## 形态

| 形态 | 进程 | 适用 |
| --- | --- | --- |
| 单进程 | `Chorus.Server` | 默认、调试、小规模；ingress/WS/引擎同一进程 |
| 拆分 | `Chorus.Gateway` + `Chorus.EngineHost` | 网关与权威逻辑分机/分容器；走 IPC |

两种形态的 `/ingress`、`/ws`、`/faces`、`/metrics` 完全一致；`/sessions`、`/matches` 只在单进程形态暴露。

## 构建与运行

```bash
# 发布（Release）
dotnet publish src/Chorus.Server -c Release -o ./publish
dotnet run --project src/Chorus.Server -c Release --no-build   # 或直接 dotnet ./publish/Chorus.Server.dll

# 端口
ASPNETCORE_URLS="http://0.0.0.0:5083" dotnet ./publish/Chorus.Server.dll

# 环境（默认 Production 只读 appsettings.json；Development 会叠加 appsettings.Development.json 的开发开关）
DOTNET_ENVIRONMENT=Production dotnet ./publish/Chorus.Server.dll
```

配置优先级：`appsettings.json` → `appsettings.{Environment}.json` → 环境变量（`__` 作为分隔符，如 `Session__MaxSessions`）→ 命令行。

## 端点与端口

| 端点 | 用途 | 单进程 | 拆分 |
| --- | --- | --- | --- |
| `GET /health` | 存活探针 | ✅ | ✅（Gateway / EngineHost 各自） |
| `GET /sessions` | 会话/客户端指标 JSON | ✅ | ❌ 仅 EngineHost 侧有权威数据，但**未暴露**；用 `/metrics` |
| `GET /matches` | match 级聚合视图 JSON | ✅ | ❌ 同上 |
| `GET /faces` | listener face 计数器 JSON | ✅ | ✅（Gateway） |
| `GET /metrics` | Prometheus 文本（`chorus_*`） | ✅ | ✅（三端各自） |
| `POST /ingress/{platform}` | 平台事件入口 | ✅ | ✅（Gateway） |
| `WS /ws` | 主播/观众客户端 | ✅ | ✅（Gateway） |

默认端口见各自 `Properties/launchSettings.json`：Server `5083`、Gateway `5168`、EngineHost `5067`。

拆分模式 IPC 端口（`Ipc:TcpGatewayToEnginePort=27500`、`Ipc:TcpEngineToGatewayPort=27501`，HTTP 传输用 `27600/27601`）。

## 安全配置（fail-closed）

| 配置 | 默认 | 生产要求 |
| --- | --- | --- |
| `Ingress:AllowAll` | `false` | **必须保持 false**。true 时所有平台事件免验签，仅开发演示 |
| `Ingress:Douyin:AppSecret` / `SignatureHeader` | 空 | 空则全部 Douyin 回调 401 |
| `Ingress:Kuaishou:VerifyToken` / `SignatureHeader` | 空 | 空则全部快手回调 401 |
| `Ingress:DedupTtlSeconds` | `600` | 按平台重试窗口设置，勿小于平台最大重试间隔 |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | 按平台回调最大 body 调大；超限 413 |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | 按平台 QPS 设置；超限 429 |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | 客户端超时预算；超时 504 |
| `Faces:StreamerWs:AuthTokens` | 空 | **必须配置**；空且非匿名则所有 WS 被拒 |
| `Faces:StreamerWs:AllowAnonymous` | `false` | **必须保持 false**。true 则免 token，仅开发演示 |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | 客户端消息上限；超限关闭连接 |
| `Faces:StreamerWs:AckPerMinute` | `600` | 单连接 ACK 限流；超限丢弃（`chorus_ws_acks_dropped_total`） |

### WS token 格式

`Faces:StreamerWs:AuthTokens` 支持多个 token，分号分隔，可带过期时间（unix 毫秒）：

```json
{
  "Faces": {
    "StreamerWs": {
      "AuthTokens": "dev-token-1;prod-token@1893456000000"
    }
  }
}
```

`dev-token-1` 永不过期；`prod-token` 在 `1893456000000` 之后过期（过期判 `token_expired`）。拒绝原因：`missing_token` / `invalid_token` / `token_expired`。

### fail-closed 矩阵

| 场景 | 行为 |
| --- | --- |
| 未配密钥 + 匿名关 | ingress 401、WS 全部关闭 |
| 未配密钥 + `AllowAll=true` | 免验签放行（**告警**，勿在生产开启） |
| 配错 secret/verifyToken | 401 |
| token 过期 | 连接关闭（`token_expired`） |
| 平台无对应具名验证器 | 401（即使 `AllowAll=true`，具名验证器优先） |

### 反向代理

- 开 WS 长连接：`proxy_http_version 1.1`、`Upgrade`、`Connection` 头；`proxy_read_timeout` 远大于 `Session:TickDurationMs`（建议 ≥ 1 小时）。
- 回源 body 上限 ≥ `MaxBodyBytes` + 头部余量（如 64KB + 8KB）。
- `/ingress` 只允许平台回调来源 IP 白名单，或前置签名校验网关。
- `/metrics`、`/sessions`、`/matches` 不含敏感状态，但建议仅内网可取。
- 真实 TLS 在代理层终止；Chorus 本身不监听 HTTPS。

## 资源与生命周期

- `Session:MaxSessions=200`：超限按最久未见驱逐，被驱逐的 Session 直接消失（客户端需重连 resync）。
- `Session:IdleEvictAfterMs=600000`：客户端全部断开后空闲 10 分钟回收。
- `Session:MaxPendingEvents=8000` / `EventEnqueueMaxWaitMs=50`：事件道容量与礼物有界等待，超限 503 可重试。
- `Session:MaxSnapshotBytes=1048576`：快照超限跳过发送并计 `chorus_snapshots_oversize_total`，客户端 resync 兜底。

## 日志与关联

日志为结构化模板，关键字段：`sessionId`、`clientId`、`tickId`、`eventId`、`messageId`（IPC 投递）、`SourceEventId`（事件关联）。IPC 失败、ACK 异常、客户端重连都会打这些字段。跨进程串联见 [可观测性](./observability.md) 的「关联 ID」。

## 状态与重启

- 会话状态纯内存，无持久化（明确非目标）。重启即全量丢失：所有客户端断开并重新 hello。
- 无跨进程状态共享：单进程与拆分两种形态各自独立，勿混用同一 `Ipc:UnixSocketDir` 前缀跑多组 Gateway/EngineHost。
- 优雅停机：进程退出即终止会话；上游应做好重连。

## 拆分模式容器化

- 两台容器：Gateway 与 EngineHost；`Ipc:Transport=Tcp`，映射 `27500/27501`（HTTP 则 `27600/27601`）。
- 健康检查分别探 `/health`；Gateway 就绪建议连 `/faces`（有 ingress 计数说明链路通），EngineHost 侧用 `/metrics` 的 `chorus_snapshots_sent_total` 判断引擎是否在出快照。
- 顺序无强制：Gateway 启动时 EngineHost 未就绪，事件发布会失败并计入 `chorus_ipc_publish_failures_total`（客户端侧幂等重试），EngineHost 就绪后自动恢复。
