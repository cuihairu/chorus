---
title: 压测
---

# 压测（独立压测工具）

`src/Chorus.LoadTool` 是针对运行中 Gateway 的独立压测工具：多 Session × 观众事件（HTTP ingress）× 变速 WS 客户端（fast/normal/slow ACK 档位）× 随机断连重连。

相关文档：

- [拆分模式](../guide/split-mode.md)
- [可观测性](../reference/observability.md)
- [配置参考](../reference/config.md)

## 运行

```bash
dotnet run --project src/Chorus.LoadTool -- --platform test --url http://127.0.0.1:5168 --duration 60
```

前提：Gateway 或 Server 已启动。工具构造两种 payload 形状，由 `--platform` 选择：

- `--platform test`（默认推荐）：`TestPlatformEventMapper` 形状（camelCase 字段），配合 `Ingress:AllowAll=true` 的未签名演示验证器走完整链路，无需签名。
- `--platform douyin`：`DouyinPlatformEventMapper` 形状（`session_id`/`event_id`/`event`…）。注意 fail-closed 语义：已注册 `DouyinPlatformEventVerifier` 的平台始终优先用真实验签，`Ingress:AllowAll` 只覆盖未注册验签器的平台，所以 douyin 需要配置 `Ingress:Douyin:AppSecret` 并由调用方签名；用 `--sign <secret>`（或 `CHORUS_LOAD_DOUYIN_SECRET`）让工具按占位方案签名（HMAC-SHA256 原始 body 小写 hex，`X-Douyin-Signature` 头）。

WS 侧需要 `--token`，或 Gateway/Server 配置 `Faces:StreamerWs:AllowAnonymous=true`（均为开发演示开关）。

## 参数

| 参数 | 默认 | 含义 |
| --- | --- | --- |
| `--url` | `http://127.0.0.1:5168` | Gateway 地址（Server 亦可） |
| `--platform` | `douyin` | ingress 平台名（`test` 走未签名演示链路） |
| `--sessions` | 4 | 并发 Session 数 |
| `--rate` | 50 | 每 Session 事件/秒 |
| `--clients` | 3 | 每 Session WS 客户端数 |
| `--producers` | 4 | 并行 HTTP 生产者连接数 |
| `--ack-mix` | `fast,normal,slow` | ACK 档位混合（fast≈实时 / normal≈几 tick / slow≈十几 tick 滞后） |
| `--disconnect-every` | 20 | 客户端平均断连间隔（秒，0=不断） |
| `--reconnect-ms` | 1000 | 断连后重连退避 |
| `--duration` | 60 | 运行时长（秒） |
| `--users` | 5000 | 模拟观众池大小 |
| `--gift-rate` | 0.05 | 礼物事件占比 |
| `--dup-rate` | 0.02 | 重复事件 id 占比（触发去重） |
| `--match` | 空 | 共享 match id（多 Session 同场） |
| `--sign` | 空 | 按 Douyin 占位方案签名 ingress（HMAC-SHA256 原始 body 小写 hex，`X-Douyin-Signature` 头；或环境变量 `CHORUS_LOAD_DOUYIN_SECRET`） |
| `--token` | 空 | WS 鉴权 token（或环境变量 `CHORUS_WS_TOKEN`） |
| `--engine-metrics` | 空 | Engine `/metrics` URL；运行期每 `--report-every` 采样并取峰值 |
| `--report-every` | 5 | 进度报告间隔（秒） |

也可以用环境变量：`CHORUS_LOAD_URL`、`CHORUS_LOAD_PLATFORM`、`CHORUS_LOAD_DOUYIN_SECRET`、`CHORUS_WS_TOKEN`、`CHORUS_LOAD_ENGINE_METRICS`。

## 观察什么

进度行每 `--report-every` 秒打印一次：

```
[loadtool] t=5s ingress(sent=1500,ok=1468,dup=29,429=3,503=0,err=0) ws(snaps=4123,acks=980,maxLag=8,reconnects=2)
```

- `ingress.ok` / `dup` / `429` / `503` / `err`：入口受理、判重、限流、事件道满、其他错误——对应 `chorus_ingress_*`。
- `ws.snaps` / `acks` / `maxLag` / `reconnects`：客户端收到快照、发出 ACK、最大滞后 tick、断连次数。
- 结束时汇总；若给了 `--engine-metrics`，另打印引擎侧三个峰值（`engine peaks: sessions=… tickMs=… cpuPercent=…`，对应阶段三触发条件「单 worker 峰值 CPU > 60%」「单机房间数达数百量级」），并可（`PrintEngineMetricsAsync`）打印一次 `/metrics` 原文。

典型用法：先低速率跑通（`--rate 10 --duration 30`），再逐步加到目标速率；观察 `503`（事件道满，说明速率超过引擎消费能力）、`429`（ingress 限流）与 `maxLag`（慢客户端是否靠 resync 追回）。

结束时有零受理告警：`sent>0 && accepted=0` 时在 stderr 打 `[loadtool] ALARM: ...` 并以非零退出码结束——典型成因是网关配了 `Ingress:Douyin:AppSecret` 而请求没签名（纯 401）。

## 测试覆盖

- 纯逻辑（payload 形状、重复 id 行为、CLI 旗标/环境变量/错误路径解析）由 `LoadToolTests` 覆盖。
- `IngressDriver` 的状态分桶（200/判重/429/503/其他）与并行 producer 下的计数精确性由 `IngressDriverTests` 覆盖（stub handler，计数器为 Interlocked）。
- Douyin 占位签名（`--sign`/`CHORUS_LOAD_DOUYIN_SECRET`）由 `IngressSignerTests`（硬编码向量 + 与网关验签器构造一致 + 验签通过）与 `IngressDriverTests` 的签名头用例覆盖；零受理告警由 `ZeroAcceptedAlarmTests` 覆盖。
- `ClientSimulation` 经传输缝（`connect` 委托，同 `ChorusClient` 惯例）注入测试服务器内存 WS 通道，覆盖 hello/welcome/快照回放/ACK 与断连重连完整握手循环（`ClientSimulationTests`）。
- 对真实 Gateway/Server 的运行验证（冒烟：`--platform test` 下 ingress 受理/判重、WS 快照/ACK/重连、引擎侧 `chorus_events_applied_total` 与 `chorus_snapshots_sent_total` 均按预期增长）。
