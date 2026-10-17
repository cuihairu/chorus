---
title: 压测
---


<p align="center">
  <a href="./load-testing.md">English</a> | 简体中文
</p>

# 压测（独立压测工具）

`src/Chorus.LoadTool` 是针对运行中 Gateway 的独立压测工具：多 Session × 观众事件（HTTP ingress）× 变速 WS 客户端（fast/normal/slow ACK 档位）× 随机断连重连。

相关文档：

- [拆分模式](../guide/split-mode.zh.md)
- [可观测性](../reference/observability.zh.md)
- [配置参考](../reference/config.zh.md)

## 运行

```bash
dotnet run --project src/Chorus.LoadTool -- --platform test --url http://127.0.0.1:5168 --duration 60
```

前提：Gateway 或 Server 已启动。工具构造两种 payload 形状，由 `--platform` 选择：

- `--platform test`（默认推荐）：`TestPlatformEventMapper` 形状（camelCase 字段），配合 `Ingress:AllowAll=true` 的未签名演示验证器走完整链路，无需签名。
- `--platform douyin`：`DouyinPlatformEventMapper` 形状（`session_id`/`event_id`/`event`…）。注意 fail-closed 语义：已注册 `DouyinPlatformEventVerifier` 的平台始终优先用真实验签，`Ingress:AllowAll` 只覆盖未注册验签器的平台，所以 douyin 需要配置 `Ingress:Douyin:AppSecret` 并由调用方签名；用 `--sign <secret>`（或 `CHORUS_LOAD_SIGN_SECRET`）让工具按占位方案签名（HMAC-SHA256 原始 body 小写 hex，`X-Douyin-Signature` 头）。

WS 侧需要 `--token`，或 Gateway/Server 配置 `Faces:StreamerWs:AllowAnonymous=true`（均为开发演示开关）。

## 参数

| 参数 | 默认 | 含义 |
| --- | --- | --- |
| `--url` | `http://127.0.0.1:5168` | ingress 面地址（Gateway / Server 亦可） |
| `--ws-url` | 由 `--url` 派生 | `/ws` 面地址。默认从 `--url` 派生（http→ws、https→wss、补 `/ws` 路径）；拆分角色拓扑（`Gateway:Roles=platform` 与 `=streamer` 分进程）下两面不在同一端口，用此参数显式指定 `/ws` 所在进程 |
| `--platform` | `douyin` | ingress 平台名（`test` 走未签名演示链路） |
| `--sessions` | 4 | 并发 Session 数 |
| `--rate` | 50 | 事件/秒，**全部 Session 合计**（跨 producer 摊分，round-robin 覆盖所有 Session）。`RatePacer` 按绝对时刻表对齐每个 producer 的发送节奏（2026-10-16 前是整除间隔，≥1000/s/producer 时截断为 0、`--rate` 静默失效）；producer 跟不上目标节奏时按实际响应速度发送，长程速率趋近于目标。要实际打到目标速率，producer 数需 ≥ 目标速率 × 单请求平均延迟 |
| `--clients` | 3 | 每 Session WS 客户端数 |
| `--producers` | 4 | 并行 HTTP 生产者连接数 |
| `--driver-processes` | 1 | 独立驱动进程数。父进程均分 `--rate`，并为每个分片的 event/client id 加前缀；用于移除 LoadTool 单进程这一侧的瓶颈 |
| `--ack-mix` | `fast,normal,slow` | ACK 档位混合（fast≈实时 / normal≈几 tick / slow≈十几 tick 滞后） |
| `--disconnect-every` | 20 | 客户端平均断连间隔（秒，0=不断） |
| `--reconnect-ms` | 1000 | 断连后重连退避 |
| `--duration` | 60 | 运行时长（秒） |
| `--users` | 5000 | 模拟观众池大小 |
| `--gift-rate` | 0.05 | 礼物事件占比 |
| `--dup-rate` | 0.02 | 重复事件 id 占比（触发去重） |
| `--match` | 空 | 共享 match id（多 Session 同场） |
| `--sign` | 空 | 签名 ingress（douyin 占位 HMAC-SHA256 / kuaishou 官方 sha1 方案；或环境变量 `CHORUS_LOAD_SIGN_SECRET`，别名 `CHORUS_LOAD_DOUYIN_SECRET`） |
| `--token` | 空 | WS 鉴权 token（或环境变量 `CHORUS_WS_TOKEN`） |
| `--engine-metrics` | 空 | Engine `/metrics` URL；运行期每 `--report-every` 采样并取峰值 |
| `--report-every` | 5 | 进度报告间隔（秒） |

也可以用环境变量：`CHORUS_LOAD_URL`、`CHORUS_LOAD_WS_URL`、`CHORUS_LOAD_PLATFORM`、`CHORUS_LOAD_SIGN_SECRET`（别名 `CHORUS_LOAD_DOUYIN_SECRET`）、`CHORUS_WS_TOKEN`、`CHORUS_LOAD_ENGINE_METRICS`。

拆分角色拓扑示例（两网关共用一个 EngineHost，见 [拆分模式](./split-mode.zh.md) 的 `Gateway:Roles`/`Gateway:Id`/`Ipc:GatewayIds`）：

```bash
dotnet run --project src/Chorus.LoadTool -- --url http://127.0.0.1:5168 \
  --ws-url ws://127.0.0.1:5178/ws --duration 60
```

## 观察什么

进度行每 `--report-every` 秒打印一次：

```
[loadtool] t=5s ingress(sent=1500,ok=1468,dup=29,429=3,503=0,err=0) ws(snaps=4123,acks=980,maxLag=8,reconnects=2)
```

- `ingress.ok` / `dup` / `401` / `429` / `503` / `err`：入口受理、判重、签名拒绝、限流、事件道满、其他错误——对应 `chorus_ingress_*`。健康运行下 `dup` 应约等于配置的 `--dup-rate` 占比；`dup` 远高于它说明 eventId 在客户端侧互撞（2026-10-15 修复的缺陷：producer 各自编号导致大量事件被判重吞掉，事件根本没进引擎——此时 CPU 测量毫无意义）。
- `ws.snaps` / `acks` / `maxLag` / `reconnects`：客户端收到快照、发出 ACK、最大滞后 tick、断连次数。
- 结束时汇总；若给了 `--engine-metrics`，另打印引擎侧三个峰值（`engine peaks: sessions=… tickMs=… cpuPercent=…`，对应阶段三触发条件「单 worker 峰值 CPU > 60%」「单机房间数达数百量级」），并可（`PrintEngineMetricsAsync`）打印一次 `/metrics` 原文。注意：`cpuPercent` 依赖 `chorus_process_cpu_percent`，**只有拆分模式的 EngineHost `/metrics` 发该指标**（`RenderEngine`）——指向单体 Server 的 `/metrics` 时恒为 `-`；阶段三门控测量必须跑 Gateway+EngineHost 并把 `--engine-metrics` 指向 EngineHost。

典型用法：先低速率跑通（`--rate 10 --duration 30`），再逐步加到目标速率；观察 `503`（事件道满，说明速率超过引擎消费能力）、`429`（ingress 限流）与 `maxLag`（慢客户端是否靠 resync 追回）。

当目标速率超过单个驱动进程时，可以把同一次压测拆到多个独立进程：

```bash
dotnet run --project src/Chorus.LoadTool -- --driver-processes 4 --rate 30000
```

每个子进程拿到自己的速率目标和 `shardN-` event/client id 命名空间，并在进度/汇总日志中标注 `shardN`；按标签汇总各分片输出，再与网关指标中的总发送/受理数对照。

结束时有零受理告警：`sent>0 && accepted=0` 时在 stderr 打 `[loadtool] ALARM: ...` 并以非零退出码结束——典型成因是网关配了 `Ingress:Douyin:AppSecret` 而请求没签名（纯 401）；未签名运行 douyin/kuaishou 时启动也会先打一条 unsigned 警告。

## 测试覆盖

- 纯逻辑（payload 形状、重复 id 行为、CLI 旗标/环境变量/错误路径解析）由 `LoadToolTests` 覆盖。
- `IngressDriver` 的状态分桶（200/判重/429/503/其他）、并行 producer 下的计数精确性与「多 producer eventId 绝不互撞」（共享全局序号，dedup 键不被客户端自撞）由 `IngressDriverTests` 覆盖（stub handler，计数器为 Interlocked）。
- Douyin 占位签名（`--sign`/`CHORUS_LOAD_SIGN_SECRET`，kuaishou 亦同）由 `IngressSignerTests`（独立硬编码向量 + 与网关验签器验签通过）与 `IngressDriverTests` 的签名头/401 分桶用例覆盖；零受理告警与退出码由 `IngressDiagnosticsTests` 覆盖。
- `ClientSimulation` 经传输缝（`connect` 委托，同 `ChorusClient` 惯例）注入测试服务器内存 WS 通道，覆盖 hello/welcome/快照回放/ACK 与断连重连完整握手循环（`ClientSimulationTests`）。
- 对真实 Gateway/Server 的运行验证（冒烟：`--platform test` 下 ingress 受理/判重、WS 快照/ACK/重连、引擎侧 `chorus_events_applied_total` 与 `chorus_snapshots_sent_total` 均按预期增长）。
