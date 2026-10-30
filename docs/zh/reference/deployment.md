---
title: 部署与安全
---


# 部署与安全配置

Chorus 有两种部署形态，共享同一套配置模型与安全语义。

相关文档：

- [拆分模式](../guide/split-mode.md)
- [配置参考](./config.md)
- [可观测性](./observability.md)
- [故障排查](./troubleshooting.md)


> 当前部署：使用 **Chorus.PlatformGateway**（`/ingress/{platform}`）与
> **Chorus.StreamerGateway**（`/ws`）作为独立可执行文件。两者都用
> `Chorus.Gateway.Runtime` 库；互不依赖对方可执行文件，也不使用
> `Gateway:Roles`。见[独立网关部署](../guide/independent-gateways.md)。
> 下文的 `Chorus.Gateway` 合体形态与角色切换示例保留用于兼容。

## 形态

| 形态 | 进程 | 适用 |
| --- | --- | --- |
| 单进程 | `Chorus.Server` | 默认、调试、小规模；入口/WS/引擎同进程 |
| 拆分（单网关进程） | `Chorus.Gateway` + `Chorus.EngineHost`；配置 `Control:Url` 时加 `Chorus.ControlHost` | 网关与权威逻辑分机/分容器；IPC 相连；ControlHost 提供共享房间权威与持久检查点 |
| 拆分网关角色 | 两个 `Chorus.Gateway` 实例（`Gateway:Roles=platform` / `streamer`）+ `Chorus.EngineHost`；受控房间加 `Chorus.ControlHost` | 平台边缘与主播边缘分离，共享 worker IPC 扇出；多 worker 路由与持久恢复用 ControlHost，见[进程拓扑](../design/process-topology.md) §4 |

Gateway 的 `/ingress`、`/ws`、`/faces`、`/metrics` 面在两种拆分形态下完全一致。`/sessions` 是 `Chorus.Server` 的诊断端点；ControlHost 另有自己带鉴权的 `/api/matches` 管理 API。

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

配置优先级：`appsettings.json` → `appsettings.{Environment}.json` → 环境变量（`__` 作分隔符，如 `Session__MaxSessions`）→ 命令行。

## 房间控制存储

`Chorus.ControlHost` 开发与单机部署默认使用 SQLite。多 worker 生产请配置 PostgreSQL 16+ 权威库；Worker 与 Gateway 仍只经 ControlHost HTTP 访问，不得直接打开共享数据库文件。

```bash
Control__Provider=PostgreSql
Control__ConnectionString='Host=control-db.internal;Database=chorus;Username=chorus;Password=<from-secret-store>;SSL Mode=VerifyFull'
```

连接串经部署密钥库下发，开启 TLS 与 SCRAM 认证，并按[房间控制存储](../architecture/room-control-storage.md)预备同步备库。省略 `Control__Provider` 时仍默认 SQLite。PostgreSQL 集成测试要求 `CHORUS_TEST_POSTGRES` 指向隔离测试库；CI 为该测试预备 PostgreSQL 16。

## 端点与端口

| 端点 | 用途 | 单进程 | 拆分 |
| --- | --- | --- | --- |
| `GET /health` | 存活探针 | ✅ | ✅（Gateway / EngineHost 各自） |
| `GET /ready` | **就绪探针（仅拆分 Gateway）**：`Ipc:ReadinessMaxSilenceMs` 内收到任何引擎消息（心跳/welcome/快照）→ 200，否则 503 | ❌ | ✅（Gateway） |
| `GET /sessions` | 会话/客户端指标 JSON（每客户端 lag/ack 明细） | ✅ | ✅（EngineHost；`c7846a6` 补齐——拆分模式引擎是会话权威，此前未暴露） |
| `GET /matches` | 对局级聚合视图 JSON | ✅ | ❌ 权威 `MatchRegistry` 只在单进程；用 `/metrics` |
| `GET /faces` | 监听面计数器 JSON | ✅ | ✅（Gateway） |
| `GET /metrics` | Prometheus 文本（`chorus_*`） | ✅ | ✅（三进程各自） |
| `POST /ingress/{platform}` | 平台事件入口 | ✅ | ✅（Gateway） |
| `WS /ws` | 主播/观众客户端 | ✅ | ✅（Gateway） |

默认端口见各自的 `Properties/launchSettings.json`：Server `5083`、Gateway `5168`、EngineHost `5067`。

拆分模式 IPC 端口（`Ipc:TcpGatewayToEnginePort=27500`、`Ipc:TcpEngineToGatewayPort=27501`，HTTP 传输用 `27600/27601`）。

### 拆分模式就绪探针（自 2026-10-12）

EngineHost 每 `Ipc:HeartbeatIntervalMs`（默认 5000，`0` 关闭）在 eng→gw 通道发一条 `eng.heartbeat`；
Gateway 收到任何引擎消息（心跳/welcome/快照）即刷新静默计时器。
Gateway 的 `GET /ready`：

- 从未收到引擎消息，或静默超过 `Ipc:ReadinessMaxSilenceMs`（默认 15000）→ **503**（fail-closed，
  编排器不切流量）；响应体带 `engineSilenceMs` 供诊断；
- 窗口内有心跳 → 200。

| 配置 | 默认值 | 说明 |
|---|---|---|
| `Ipc:HeartbeatIntervalMs`（EngineHost） | `5000` | 引擎心跳间隔；`0` 关闭（关闭后 Gateway 只能在客户端连接产生 welcome/快照时判就绪） |
| `Ipc:ReadinessMaxSilenceMs`（Gateway） | `15000` | 就绪判定的最大静默窗，建议 ≥ 3× 心跳间隔 |

单进程形态没有 IPC 跳数，不暴露 `/ready`（`/health` 足够）。

## 安全配置（fail-closed）

| 配置 | 默认值 | 生产要求 |
| --- | --- | --- |
| `Ingress:AllowAll` | `false` | **必须保持 false**。true 时所有平台事件跳过验签，仅开发演示 |
| `Ingress:Douyin:AppSecret` / `SignatureHeader` | 空 | 空 → 所有抖音回调 401 |
| `Ingress:Kuaishou:VerifyToken` / `SignatureHeader` | 空 | 空 → 所有快手回调 401 |
| `Ingress:DedupTtlSeconds` | `600` | 按平台重试窗设置；不得小于平台最大重试间隔 |
| `Faces:PlatformIngress:MaxBodyBytes` | `65536` | 按平台回调最大 body 调整；超限 413 |
| `Faces:PlatformIngress:RateLimitPerMinute` | `1200` | 按平台 QPS 设置；超限 429 |
| `Faces:PlatformIngress:TimeoutMs` | `5000` | 客户端超时预算；超时 504 |
| `Faces:StreamerWs:AuthTokens` | 空 | **必须配置**；空且非匿名 → 所有 WS 拒绝 |
| `Faces:StreamerWs:AllowAnonymous` | `false` | **必须保持 false**。true 时不要求 token，仅开发演示 |
| `Faces:StreamerWs:MaxMessageBytes` | `65536` | 客户端消息上限；超限关闭连接 |
| `Faces:StreamerWs:AckPerMinute` | `600` | 每连接 ACK 限流；超限丢弃（`chorus_ws_acks_dropped_total`） |

### WS Token 格式

`Faces:StreamerWs:AuthTokens` 支持多个 token，分号分隔，可选过期时间（unix 毫秒）：

```json
{
  "Faces": {
    "StreamerWs": {
      "AuthTokens": "dev-token-1;prod-token@1893456000000"
    }
  }
}
```

`dev-token-1` 永不过期；`prod-token` 在 `1893456000000` 后过期（过期判定为 `token_expired`）。拒绝原因：`missing_token` / `invalid_token` / `token_expired`。

### fail-closed 矩阵

| 场景 | 行为 |
| --- | --- |
| 未配置密钥 + 关闭匿名 | ingress 401，所有 WS 关闭 |
| 未配置密钥 + `AllowAll=true` | 不验签放行（**告警**，生产不要开启） |
| 配置了错误密钥/verifyToken | 401 |
| token 过期 | 关闭连接（`token_expired`） |
| 平台没有对应具名验证器 | 401（即使 `AllowAll=true`，具名验证器优先） |

### 反向代理

- 开启 WS 长连接：`proxy_http_version 1.1`、`Upgrade`、`Connection` 头；`proxy_read_timeout` 远大于 `Session:TickDurationMs`（建议 ≥ 1 小时）。
- 转发到源站的 body 限额 ≥ `MaxBodyBytes` + 头部余量（如 64KB + 8KB）。
- `/ingress` 只放行平台回调源 IP 白名单，或前置验签网关。
- `/metrics`、`/sessions`、`/matches` 不含敏感状态，但建议仅内网访问。
- 真实 TLS 终止在代理层；Chorus 自身不监听 HTTPS。
- ControlHost 位于 TLS 终止之后时，把 `Admin:PublicOrigin` 设为精确的公网 origin（例如 `https://admin.example.com`）。该值用于非安全请求的 Origin 校验并强制 Secure 管理 cookie；该校验不要信任任意转发头。

## 资源与生命周期

- `Session:MaxSessions=200`：到达容量后拒绝新建 Session；不逐出既有 Actor。接收更多房间前先降载或扩容。
- `Session:IdleEvictAfterMs=600000`：全部客户端断开后闲置 10 分钟即回收。
- `Session:MaxPendingEvents=8000` / `EventEnqueueMaxWaitMs=50`：事件通道容量与有界礼物等待，超限 503 可重试。
- `Session:MaxSnapshotBytes=1048576`：超限快照跳过发送并计入 `chorus_snapshots_oversize_total`；客户端重同步兜底。

## 日志与关联

日志使用结构化模板；关键字段：`sessionId`、`clientId`、`tickId`、`eventId`、`messageId`（IPC 投递）、`SourceEventId`（事件关联）。IPC 失败、ACK 异常、客户端重连都会带这些字段。跨进程关联见[可观测性](./observability.md)的"关联 ID"。

## 状态与重启

- GameModule 状态在 worker 运行期间位于内存。EngineHost 使用 ControlHost 持久化时，每个已提交 tick 同时存一份带围栏的检查点，接替 owner 会恢复；无控制面持久化的独立模式没有重启恢复。
- ControlHost 是所配置 Gateway/EngineHost 进程的共享房间权威。Gateway 实例用不同 `Gateway:Id` 通道后缀可共享一个 EngineHost；不要在同一 IPC 通道名上跑两套部署。
- 优雅停机：进程退出立即终止会话；上游应处理重连。

## 拆分模式容器化

- 拆分网关角色：配置两个 Gateway（`Gateway:Roles=platform` / `streamer`，未选监听返回 404）与不同 `Gateway:Id` 共享 EngineHost 扇出 IPC。受控房间还需 Gateway/EngineHost 配置 `Chorus.ControlHost` 与 `Control:Url`；ControlHost 路由可用各 Worker 自报的 TCP/HTTP 端点。多网关静态 IPC 仍需 UDS/NamedPipe 以避免端口冲突。
- 两容器：Gateway 与 EngineHost；`Ipc:Transport=Tcp`，映射 `27500/27501`（HTTP 则 `27600/27601`）。
- 健康检查分别探 `/health`（存活）；**Gateway 就绪探 `/ready`**（200 = 窗口内收到过引擎消息），
  EngineHost 侧用 `/metrics` 的 `chorus_snapshots_sent_total` 判断引擎是否在产快照。
- 顺序不强制：Gateway 启动时 EngineHost 未就绪，事件发布失败计入 `chorus_ipc_publish_failures_total`（客户端侧幂等重试），EngineHost 就绪后自动恢复。
