---
title: 可观测性
---

# 可观测性

Chorus 的可观测面由三部分组成：**Prometheus 文本指标**（`chorus_*`）、**JSON 诊断端点**（`/sessions`、`/matches`）、**结构化日志**（含关联 ID）。

相关文档：

- [运维与观测](./operations.md)
- [配置参考](./config.md)
- [故障排查](./troubleshooting.md)

## 指标端点

三个进程都暴露 `GET /metrics`，返回 Prometheus 文本格式（`text/plain; version=0.0.4; charset=utf-8`），可直接被 Prometheus 抓取：

```yaml
# prometheus.yml 示例
scrape_configs:
  - job_name: chorus-server
    static_configs:
      - targets: ["localhost:5100"]        # Chorus.Server（单进程模式）
  - job_name: chorus-engine
    static_configs:
      - targets: ["localhost:5200"]        # Chorus.EngineHost
  - job_name: chorus-gateway
    static_configs:
      - targets: ["localhost:5300"]        # Chorus.Gateway
```

单进程模式（Chorus.Server）没有 IPC 跳，`chorus_ipc_publish_failures_total` 恒为 0。拆分模式下两侧各自输出本方向的发布失败：Gateway 面计数 Gateway→Engine 事件（`src/Chorus.Gateway/GatewayIpcService.cs`），EngineHost 面计数 Engine→Gateway 快照与欢迎（`src/Chorus.EngineHost/EngineIpcService.cs`，由 `RenderEngine` 输出）。引擎侧快照发布是 best-effort（发布失败不杀 tick，见 `EngineBusSessionClient.SafePublishAsync`），但失败会计数并告警，不再静默吞掉（`8e87a47`）。

## 指标清单

### 会话与事件（Engine 侧聚合）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_sessions` | gauge | 当前存活 Session 数 |
| `chorus_session_start_failures_total` | counter | 拒绝启动的 Session 数（快照恢复 fail-closed / 模块构造抛异常）。这类失败没有 actor 诊断可读，`/sessions` 看不到——本计数是唯一暴露面（Server 与 EngineHost 均输出） |
| `chorus_session_idle_evictions_total` | counter | 空闲清扫累计驱逐数（无客户端且超过 `IdleEvictAfterMs`）。`chorus_sessions` gauge 下降无法区分正常结束与被驱逐，本计数给出驱逐侧真值；异常升高说明空闲阈值过激或客户端批量流失 |
| `chorus_session_capacity_evictions_total` | counter | 达到 `MaxSessions` 上限驱逐最旧会话累计数（被逐客户端按新 session id 重连）。异常升高说明容量配小了或遭遇会话风暴 |
| `chorus_events_ingested_total` | counter | 进入 Session 事件道的事件数（去重后、调度前） |
| `chorus_events_applied_total` | counter | 在 tick 中结算进权威状态的事件数 |
| `chorus_events_merged_total` | counter | 被归并合并的事件数（归并不丢计数，见 [帧同步设计](../design/frame-sync.md)） |
| `chorus_events_dropped_total` | counter | 因背压丢弃的事件总数 |
| `chorus_events_dropped_overflow_total` | counter | 事件道溢出丢弃（非礼物） |
| `chorus_events_dropped_gifts_total` | counter | 礼物事件等待入队超时丢弃（可重试语义，见 [规格 §3.6](../design/spec.md)） |
| `chorus_tick_overruns_total` | counter | tick 处理超时（超过 `TickTimeoutMs`）次数，只检测不取消 |
| `chorus_tick_errors_total` | counter | GameModule 抛异常的 tick 数（actor 存活，诊断照常推进） |
| `chorus_tick_duration_ms` | gauge | 最近 tick 处理耗时（取所有 Session 最大值） |
| `chorus_mailbox_depth` | gauge | 邮箱深度（控制道 + 事件道，取最大值） |
| `chorus_ack_lag_ticks` | gauge | 客户端 ACK 滞后 tick 数（取所有客户端最大值） |
| `chorus_process_cpu_percent` | gauge | worker 进程 CPU 占机器总容量百分比（自上次抓取以来的均值，全核忙=100；仅 EngineHost /metrics 输出，首抓无前值时省略该行）。峰值测量仪器（todo §4）：配合 LoadTool `--engine-metrics` 周期采样，进程拓扑阶段三触发条件「单 worker 高峰 CPU > 60%」由此可数 |

### 快照

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_snapshots_sent_total` | counter | 下发快照数 |
| `chorus_snapshots_resync_total` | counter | 强制 resync 快照数（客户端 lag 超 `MaxLagTicks`） |
| `chorus_snapshots_oversize_total` | counter | 序列化后超过 `MaxSnapshotBytes` 被跳过的快照数 |
| `chorus_snapshot_bytes` | gauge | 最近一次下发的快照线字节数（取所有 Session 最大值；无快照时为 0）。调优信号：快照体积漂移无需 profiler 即可观察（`f301aa3`），`/sessions` 的 `lastSnapshotBytes` 为同值的每 session 明细 |
| `chorus_client_queue_dropped_total` | counter | 客户端出站 latest-wins 槽被替换掉的快照数（跨 Session 累加）。单客户端积压信号：容量为 1 的槽只在发送落后超过一拍时丢弃，`/sessions` 的每客户端 `clientQueueDropped` 为明细（`6d738b7`） |

### 帧同步 / resync（Engine 侧聚合）

frame-sync P2 的 resync 路径计数此前只在 `/sessions` 可见，现同步导出到 `/metrics`：

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_resync_requests_total` | counter | 客户端发起的 resync 请求数（冷却过滤前） |
| `chorus_resync_replays_total` | counter | 从帧历史窗口答复的 resync 请求数 |
| `chorus_resync_window_misses_total` | counter | 窗口起点缺失（以最新快照 + 强制回复）的 resync 请求数。调优信号：持续增长说明帧历史窗口过小 |
| `chorus_resync_throttled_total` | counter | 被 per-client 冷却拒绝的 resync 请求数 |
| `chorus_frame_history_bytes` | gauge | 当前帧历史编码字节占用（取所有 Session 最大值） |

### ACK（Engine 侧分类计数）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_acks_accepted_total` | counter | 被接受（推进水位）的 ACK 数 |
| `chorus_acks_duplicate_total` | counter | 重复 ACK 数 |
| `chorus_acks_stale_total` | counter | 落后于当前水位的 ACK 数 |
| `chorus_acks_invalid_total` | counter | 非法 ACK（客户端绑定校验失败等） |

### Ingress（平台入口面，Server 与 Gateway 都有）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_ingress_accepted_total` | counter | 验签+映射+去重通过、进入调度的事件数 |
| `chorus_ingress_duplicates_total` | counter | 去重命中的重复事件数（幂等返回 `duplicated=true`） |
| `chorus_ingress_unverified_total` | counter | 验签未通过被拒的事件数（生产默认 fail-closed） |
| `chorus_ingress_unmapped_total` | counter | 无法映射到 Session 的事件数 |
| `chorus_ingress_rate_limited_total` | counter | 触发限流的事件数 |
| `chorus_ingress_too_large_total` | counter | 请求体超限被拒的事件数 |
| `chorus_ingress_timed_out_total` | counter | 事件道满回滚去重的次数（503）与单请求超时（504） |

### WebSocket（Gateway 主播/观众连接面）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_ws_connections_total` | counter | WS 连接建立数 |
| `chorus_ws_unauthorized_total` | counter | hello 鉴权失败被关闭的连接数 |
| `chorus_ws_protocol_mismatch_total` | counter | hello 协议版本不匹配被拒绝的连接数（`protocolVersion` 非空且 ≠ 服务端当前版本） |
| `chorus_ws_message_too_large_total` | counter | 消息体超限被拒的次数 |
| `chorus_ws_acks_accepted_total` | counter | 被接受（未触发限流）的 WS ACK 数 |
| `chorus_ws_acks_dropped_total` | counter | 触发限流被丢弃的 WS ACK 数 |
| `chorus_ws_resync_dropped_total` | counter | 网关 `/ws` resync 固定窗口限流（`Faces:StreamerWs:ResyncPerMinute`）丢弃的请求数（`c896855`）。与引擎侧 `chorus_resync_throttled_total` 互补：该计数发生在请求到达引擎之前，异常升高说明客户端在打转（频繁重连/全量 resync） |

### IPC

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_ipc_publish_failures_total` | counter | IPC 发布失败次数。Gateway 面计 Gateway→Engine 事件，EngineHost 面计 Engine→Gateway 快照/欢迎（`8e87a47`） |
| `chorus_ipc_engine_silence_ms` | gauge | Gateway 距最近一次收到 EngineHost 消息（心跳/欢迎/快照）的毫秒数；从未收到为 `-1`。`/ready` 依此判定（阈值 `Ipc:ReadinessMaxSilenceMs`） |

## 关联 ID（correlation）

一次平台事件在系统内的追踪链：

```
平台 EventId ──ingress──> Envelope.SourceEventId ──IPC──> Engine 结算 ──tick──> 快照(sessionId+tickId) ──WS──> 客户端
```

- **`AudienceEvent.EventId`**：平台事件 id（去重键的一部分），ingress 响应与 `duplicated=true` 都带它。
- **`Envelope.SourceEventId`**：IPC 信封上可选的源事件 id。Gateway 的 `/ingress/{platform}` 发布观众事件时写入 `ev.EventId`；Engine 收到后校验一致性，不一致打 warning 日志（`Correlation mismatch`）。
- **`Envelope.MessageId`**：一次投递的唯一 id（`m_<guid>`），用于定位「这一跳」的发布失败（`chorus_ipc_publish_failures_total` 的日志都带 `message=`）。
- **快照关联**：快照带 `sessionId` + `tickId` + `serverTimeMs`；客户端 ACK 回带 `lastAppliedTickId`，即完成「哪个事件 → 哪个 tick → 哪个客户端确认」的闭环。
- **ACK 关联**：WS ACK 按 `sessionId|clientId` 绑定连接，`GatewayAckCmd` 携带 `lastAppliedTickId` 与 `ClientTimeMs`（客户端测漂移）。

排障时：先在 ingress 面按 `eventId` 找到是否被接受/判重/拒绝；再用 `SourceEventId` 追 IPC 是否发布失败；最后在 Engine 侧看 `applied/merged/dropped` 与 `tickId` 水位。

## JSON 诊断端点

- `GET /sessions`：`SessionDiagnostics` 列表（tick、事件计数、lag、ack 分类计数、邮箱深度、快照计数等）。
- `GET /matches`：match 级聚合视图（session 列表、阵营、累计事件数、最后事件时间）。
- `GET /health`：存活探针（三进程都有）。

## 日志关键字段

| 字段 | 出现位置 | 含义 |
| --- | --- | --- |
| `eventId` / `SourceEventId` | ingress、engine 事件道 | 平台事件关联 |
| `messageId` | IPC 发布失败、engine 收包 | 投递关联 |
| `sessionId` / `clientId` | 全链路（WS 拒绝、回路错误、快照发送失败均携带） | 会话与客户端 |
| `tickId` | tick 过载、快照发送失败、dispatch 失败路径 | 权威 tick 水位 |

WS 回路与快照发送的失败日志（`WebSocket loop error`、`Failed to send snapshot`）均带
`session=` / `clientId=` / `tick=` 结构化字段，可与 `/sessions` 诊断的 lag/ack 字段对齐排查。
