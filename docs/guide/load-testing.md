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
- `--platform douyin`：`DouyinPlatformEventMapper` 形状（`session_id`/`event_id`/`event`…）。注意 fail-closed 语义：已注册 `DouyinPlatformEventVerifier` 的平台始终优先用真实验签，`Ingress:AllowAll` 只覆盖未注册验签器的平台，所以 douyin 需要配置 `Ingress:Douyin:AppSecret` 并由调用方签名；工具当前不签名。

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
| `--token` | 空 | WS 鉴权 token（或环境变量 `CHORUS_WS_TOKEN`） |
| `--engine-metrics` | 空 | 结束时抓取的 Engine `/metrics` URL |
| `--report-every` | 5 | 进度报告间隔（秒） |

也可以用环境变量：`CHORUS_LOAD_URL`、`CHORUS_LOAD_PLATFORM`、`CHORUS_WS_TOKEN`、`CHORUS_LOAD_ENGINE_METRICS`。

## 观察什么

进度行每 `--report-every` 秒打印一次：

```
[loadtool] t=5s ingress(sent=1500,ok=1468,dup=29,429=3,503=0,err=0) ws(snaps=4123,acks=980,maxLag=8,reconnects=2)
```

- `ingress.ok` / `dup` / `429` / `503` / `err`：入口受理、判重、限流、事件道满、其他错误——对应 `chorus_ingress_*`。
- `ws.snaps` / `acks` / `maxLag` / `reconnects`：客户端收到快照、发出 ACK、最大滞后 tick、断连次数。
- 结束时汇总并（若给了 `--engine-metrics`）抓取引擎侧 `chorus_sessions`、`chorus_events_applied_total`、`chorus_snapshots_sent_total`、`chorus_tick_overruns_total`、`chorus_tick_errors_total`、`chorus_mailbox_depth`。

典型用法：先低速率跑通（`--rate 10 --duration 30`），再逐步加到目标速率；观察 `503`（事件道满，说明速率超过引擎消费能力）、`429`（ingress 限流）与 `maxLag`（慢客户端是否靠 resync 追回）。

## 测试覆盖

工具的纯逻辑（payload 形状、重复 id 行为、选项解析）由 `LoadToolTests` 覆盖；端到端行为靠对真实 Gateway/Server 的运行验证（冒烟：`--platform test` 下 ingress 受理/判重、WS 快照/ACK/重连、引擎侧 `chorus_events_applied_total` 与 `chorus_snapshots_sent_total` 均按预期增长）。
