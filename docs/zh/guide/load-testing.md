---
title: 压测
---


# 压测（独立压测工具）

`src/Chorus.LoadTool` 是面向运行中 Gateway 的独立压测工具：多 Session × 观众事件（HTTP 入口）× 变速 WS 客户端（快/普通/慢 ACK 档）× 随机断线重连。

相关文档：

- [分离模式](../guide/split-mode.md)
- [可观测性](../reference/observability.md)
- [配置参考](../reference/config.md)

## 运行

```bash
dotnet run --project src/Chorus.LoadTool -- --platform test --url http://127.0.0.1:5168 --duration 60
```

前置：Gateway 或 Server 已在运行。工具构造两种载荷形态，按 `--platform` 选择：

- `--platform test`（推荐默认）：`TestPlatformEventMapper` 形态（camelCase 字段），在 `Ingress:AllowAll=true` 时走完整管线 + 无签名演示校验器，无需签名。
- `--platform douyin`：`DouyinPlatformEventMapper` 形态（`session_id`/`event_id`/`event`…）。注意 fail-closed 语义：已注册 `DouyinPlatformEventVerifier` 的平台始终优先真实签名校验；`Ingress:AllowAll` 只覆盖没有注册校验器的平台，因此 douyin 需要配置 `Ingress:Douyin:AppSecret` 并由调用方签名；用 `--sign <secret>`（或 `CHORUS_LOAD_SIGN_SECRET`）让工具按占位方案签名（对原始 body 做 HMAC-SHA256，小写 hex，`X-Douyin-Signature` 头）。

WS 侧需要 `--token`，或在 Gateway/Server 上配置 `Faces:StreamerWs:AllowAnonymous=true`（两者都是开发演示开关）。

## 参数

| 参数 | 默认 | 含义 |
| --- | --- | --- |
| `--url` | `http://127.0.0.1:5168` | 入口面地址（Gateway / Server 皆可） |
| `--ws-url` | 由 `--url` 推导 | `/ws` 面地址。默认由 `--url` 推导（http→ws、https→wss，追加 `/ws` 路径）；在分离角色拓扑（`Gateway:Roles=platform` 与 `=streamer` 分进程）中两个面不在同一端口，用该参数显式指定承载 `/ws` 的进程 |
| `--platform` | `douyin` | 入口平台名（`test` 走无签名演示管线） |
| `--sessions` | 4 | 并发 Session 数 |
| `--rate` | 50 | 事件/秒，**所有 Session 合计**（在生产者间摊分，轮转覆盖所有 Session）。`RatePacer` 把每个生产者的发送节奏对齐到绝对时间表（2026-10-16 之前是均分间隔，在 ≥1000/s/生产者时被截断为 0，静默禁用了 `--rate`）；生产者跟不上目标节奏时按其实际响应速度发送，长期速率逼近目标。要真正打满目标速率，生产者数必须 ≥ 目标速率 × 平均单请求延迟 |
| `--clients` | 3 | 每 Session 的 WS 客户端数 |
| `--producers` | 4 | 并行 HTTP 生产者连接数 |
| `--driver-processes` | 1 | 独立驱动进程数。父进程均分 `--rate`、给事件/client id 加分片前缀、把会话集合按分片切分（分片 k 拥有 slot % n = k 的会话槽；没有属己会话的饥饿分片不发事件）。这消除了单个 LoadTool 进程作为驱动侧瓶颈，又不放大 WS 扇出（2026-10-20；此前每个分片都重建完整会话集） |
| `--ack-mix` | `fast,normal,slow` | ACK 档位配比（fast≈实时 / normal≈落后几 tick / slow≈落后十几 tick） |
| `--disconnect-every` | 20 | 客户端平均断线间隔（秒，0=从不） |
| `--reconnect-ms` | 1000 | 断线后重连退避 |
| `--duration` | 60 | 运行时长（秒） |
| `--users` | 5000 | 模拟观众池大小 |
| `--gift-rate` | 0.05 | 礼物事件占比 |
| `--dup-rate` | 0.02 | 重复事件 id 占比（触发去重） |
| `--match` | 空 | 共享比赛 id（多个 Session 同属一场比赛） |
| `--sign` | 空 | 入口签名（douyin 占位 HMAC-SHA256 / kuaishou 官方 sha1 方案；或环境变量 `CHORUS_LOAD_SIGN_SECRET`，别名 `CHORUS_LOAD_DOUYIN_SECRET`） |
| `--token` | 空 | WS 认证 token（或环境变量 `CHORUS_WS_TOKEN`） |
| `--engine-metrics` | 空 | Engine `/metrics` URL；运行期间每 `--report-every` 采样一次并取峰值 |
| `--report-every` | 5 | 进度上报间隔（秒） |

也可以用环境变量：`CHORUS_LOAD_URL`、`CHORUS_LOAD_WS_URL`、`CHORUS_LOAD_PLATFORM`、`CHORUS_LOAD_SIGN_SECRET`（别名 `CHORUS_LOAD_DOUYIN_SECRET`）、`CHORUS_WS_TOKEN`、`CHORUS_LOAD_ENGINE_METRICS`。

分离角色拓扑示例（两个 gateway 共享一个 EngineHost，`Gateway:Roles`/`Gateway:Id`/`Ipc:GatewayIds` 见[分离模式](./split-mode.md)）：

```bash
dotnet run --project src/Chorus.LoadTool -- --url http://127.0.0.1:5168 \
  --ws-url ws://127.0.0.1:5178/ws --duration 60
```

## 观察什么

每 `--report-every` 秒打印一行进度：

```
[loadtool] t=5s ingress(sent=1500,ok=1468,dup=29,429=3,503=0,err=0) ws(snaps=4123,acks=980,maxLag=8,reconnects=2)
```

- `ingress.ok` / `dup` / `401` / `429` / `503` / `err`：入口接受、去重、签名拒绝、限流、事件队列满、其他错误——对应 `chorus_ingress_*`。健康运行下 `dup` 应大致等于配置的 `--dup-rate` 占比；`dup` 远高于它意味着客户端侧 eventId 冲突（2026-10-15 修复的缺陷：生产者各自独立编号导致大量事件被去重吞掉，事件从未进入引擎——此时 CPU 测量无意义）。
- `ws.snaps` / `acks` / `maxLag` / `reconnects`：客户端收到的快照、发出的 ACK、最大落后 tick、断线次数。
- 结束时打印汇总；若给了 `--engine-metrics`，还打印引擎侧三个峰值（`engine peaks: sessions=… tickMs=… cpuPercent=…`，对应三期触发条件"单 worker 峰值 CPU > 60%"与"单机房间数达数百"），还可（`PrintEngineMetricsAsync`）打印一次原始 `/metrics` 输出。注意：`cpuPercent` 依赖 `chorus_process_cpu_percent`，且**只有分离模式 EngineHost 的 `/metrics` 才发这个指标**（`RenderEngine`）——指向单体 Server 的 `/metrics` 时恒为 `-`；三期门槛测量必须跑 Gateway+EngineHost 并把 `--engine-metrics` 指向 EngineHost。

典型用法：先低速率跑通（`--rate 10 --duration 30`），再逐步升到目标速率；观察 `503`（事件队列满，说明速率超出引擎消费能力）、`429`（入口限流）与 `maxLag`（慢客户端是否靠重同步追上）。

超出单个驱动进程的速率，把同一次运行拆到独立进程：

```bash
dotnet run --project src/Chorus.LoadTool -- --driver-processes 4 --rate 30000
```

每个子进程拿到自己的速率目标与 `shardN-` 事件/client id 命名空间，进度/汇总输出带 `shardN` 标签。聚合带标签的汇总，把总发送/接受数与 gateway 指标对比。

末尾有一个零接受告警：当 `sent>0 && accepted=0` 时，向 stderr 打印 `[loadtool] ALARM: ...` 并以非零码退出——典型原因是 gateway 配了 `Ingress:Douyin:AppSecret` 而请求未签名（纯 401）；跑未签名 douyin/kuaishou 时启动也会打印未签名警告。

## 测试覆盖

- 纯逻辑（载荷形态、重复 id 行为、CLI 标志/环境变量/错误路径解析）由 `LoadToolTests` 覆盖。
- `IngressDriver` 的状态分桶（200/去重/429/503/其他）、并行生产者下的计数准确性，以及"多生产者 eventId 永不冲突"（共享全局序列号，去重键不在客户端侧自撞）由 `IngressDriverTests` 覆盖（stub handler，计数器用 Interlocked）。
- 抖音占位签名（`--sign`/`CHORUS_LOAD_SIGN_SECRET`，kuaishou 同）由 `IngressSignerTests`（独立硬编码向量 + 对 gateway 校验器验签通过）与 `IngressDriverTests` 的签名头/401 分桶用例覆盖；零接受告警与退出码由 `IngressDiagnosticsTests` 覆盖。
- `ClientSimulation` 经传输缝（`connect` 委托，与 `ChorusClient` 同约定）注入测试服务器的内存 WS 通道，覆盖 hello/welcome/快照回放/ACK 与断线/重连的完整握手循环（`ClientSimulationTests`）。
- 对真实 Gateway/Server 的运行时验证（冒烟：`--platform test` 下入口接受/去重、WS 快照/ACK/重连，以及引擎侧 `chorus_events_applied_total` 与 `chorus_snapshots_sent_total` 均按预期增长）。
