---
title: 可观测性
---


# 可观测性

Chorus 的可观测面由三部分组成：**Prometheus 文本指标**（`chorus_*`）、**JSON 诊断端点**（`/sessions`、`/matches`）与**结构化日志**（带关联 ID）。

相关文档：

- [运维与可观测性](./operations.md)
- [配置参考](./config.md)
- [故障排查](./troubleshooting.md)

## 指标端点

三进程都暴露 `GET /metrics`，返回 Prometheus 文本格式（`text/plain; version=0.0.4; charset=utf-8`），Prometheus 可直接抓取：

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

单进程模式（Chorus.Server）没有 IPC 跳数，`chorus_ipc_publish_failures_total` 恒为 0。拆分模式两侧各输出自己方向的发布失败：Gateway 侧统计 Gateway→Engine 事件（`src/Chorus.Gateway/GatewayIpcService.cs`），EngineHost 侧统计 Engine→Gateway 快照与 welcome（`src/Chorus.EngineHost/EngineIpcService.cs`，由 `RenderEngine` 渲染）。引擎侧快照发布是尽力而为（发布失败不杀 tick，见 `EngineBusSessionClient.SafePublishAsync`），但失败会被计数与告警，不再被静默吞掉（`8e87a47`）。

## 指标清单

### 会话与事件（引擎侧聚合）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_sessions` | gauge | 当前存活 Session 数 |
| `chorus_session_start_failures_total` | counter | 拒绝启动的 Session（快照恢复 fail-closed / 模块构造抛异常）。这类失败没有 actor 诊断可读、在 `/sessions` 里不可见——该计数器是唯一暴露面（Server 与 EngineHost 都输出） |
| `chorus_session_idle_evictions_total` | counter | 闲置清扫器（无客户端且超过 `IdleEvictAfterMs`）累计逐出数。`chorus_sessions` gauge 的下降区分不了正常结束与逐出；该计数器给出逐出侧的真值；异常上升说明闲置阈值过激或客户端在批量流失 |
| `chorus_session_capacity_evictions_total` | counter | 兼容计数器；当前 `SessionRegistry` 满容量不逐出，恒为 0。`MaxSessions` 改为拒绝新会话；拒建请看 `chorus_session_capacity_rejections_total` |
| `chorus_session_capacity_rejections_total` | counter | `MaxSessions` 处拒绝的新建 Session 累计；上升说明容量偏小或正在发生会话风暴 |
| `chorus_events_ingested_total` | counter | 进入 Session 事件通道的事件（去重后、调度前） |
| `chorus_events_applied_total` | counter | 在 tick 内结算进权威状态的事件 |
| `chorus_events_merged_total` | counter | 被合并策略合并的事件（合并不丢计数，见[帧同步设计](../design/frame-sync.md)） |
| `chorus_events_dropped_total` | counter | 因背压丢弃的事件总数 |
| `chorus_events_dropped_overflow_total` | counter | 事件通道溢出丢弃（非礼物） |
| `chorus_events_dropped_gifts_total` | counter | 入队等待超时丢弃的礼物事件（可重试语义，见[Spec §3.6](../design/spec.md)） |
| `chorus_tick_overruns_total` | counter | 处理超时（超过 `TickTimeoutMs`）的 tick 计数；只检测，不取消 |
| `chorus_tick_errors_total` | counter | GameModule 抛异常的 tick（actor 存活；诊断照常推进） |
| `chorus_tick_duration_ms` | gauge | 最近一次 tick 处理耗时（所有 Session 取最大） |
| `chorus_mailbox_depth` | gauge | 信箱深度（控制通道 + 事件通道，取最大） |
| `chorus_ack_lag_ticks` | gauge | 客户端 ACK 落后的 tick 数（所有客户端取最大） |
| `chorus_process_cpu_percent` | gauge | worker 进程 CPU 占整机能力的百分比（上次抓取以来的平均；全核打满 = 100；仅 EngineHost /metrics 输出，首次抓取无前值时省略该行）。峰值测量仪表（todo §4）：与 LoadTool `--engine-metrics` 一起周期采样后，进程拓扑第三阶段触发条件"单 worker 峰值 CPU > 60%"即可量化 |

### 快照

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_snapshots_sent_total` | counter | 已投递快照 |
| `chorus_snapshots_resync_total` | counter | 强制重同步快照（客户端落后超过 `MaxLagTicks`） |
| `chorus_snapshots_oversize_total` | counter | 序列化后超过 `MaxSnapshotBytes` 而跳过发送的快照 |
| `chorus_snapshot_bytes` | gauge | 最近投递的线快照字节数（所有 Session 取最大；无快照为 0）。调参信号：不用 profiler 也能观察快照大小漂移（`f301aa3`）；`/sessions` 的 `lastSnapshotBytes` 是同一值的每会话明细 |
| `chorus_client_queue_dropped_total` | counter | 被更新快照挤出客户端出站 latest-wins 槽的快照（跨 Session 累计）。每客户端积压信号：容量 1 的槽只有在发送落后超过一拍时才丢；`/sessions` 的每客户端 `clientQueueDropped` 是明细（`6d738b7`） |

### 帧同步 / resync（引擎侧聚合）

帧同步 P2 的 resync 路径计数器此前只在 `/sessions` 可见；现已同时导出到 `/metrics`：

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_resync_requests_total` | counter | 客户端发起的 resync 请求（冷却过滤前） |
| `chorus_resync_replays_total` | counter | 由帧历史窗口应答的 resync 请求 |
| `chorus_resync_window_misses_total` | counter | 窗口起点缺失的 resync 请求（以最新快照 + forced 应答）。调参信号：持续增长说明帧历史窗口太小 |
| `chorus_resync_throttled_total` | counter | 被按客户端冷却拒绝的 resync 请求 |
| `chorus_resync_replay_capped_total` | counter | 窗口超过 `Session:MaxResyncReplayTicks`、以单条强制快照对齐的 resync 请求（2026-10-15 重放上界）。调参信号：占比高说明大量客户端落后超过重放上界（重连风暴/长时间断线）；配合 `chorus_ws_resync_dropped_total` 看客户端是否在空转 |
| `chorus_frame_history_bytes` | gauge | 当前帧历史编码字节占用（所有 Session 取最大） |

### ACK（引擎侧分类计数）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_acks_accepted_total` | counter | 接受的 ACK（推进水位） |
| `chorus_acks_duplicate_total` | counter | 重复 ACK |
| `chorus_acks_stale_total` | counter | 落后于当前水位的 ACK |
| `chorus_acks_invalid_total` | counter | 无效 ACK（客户端绑定校验失败等） |

### 入口（平台入口面，Server 与 Gateway 都有）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_ingress_accepted_total` | counter | 通过验签 + 映射 + 去重并进入调度的事件 |
| `chorus_ingress_duplicates_total` | counter | 被去重命中的重复事件（幂等返回 `duplicated=true`） |
| `chorus_ingress_unverified_total` | counter | 验签失败被拒的事件（生产默认 fail-closed） |
| `chorus_ingress_unmapped_total` | counter | 无法映射到 Session 的事件 |
| `chorus_ingress_rate_limited_total` | counter | 触发限流的事件 |
| `chorus_ingress_too_large_total` | counter | 超过请求体上限被拒的事件 |
| `chorus_ingress_timed_out_total` | counter | 因事件通道满而回滚去重（503）的次数与单请求超时（504） |

### WebSocket（Gateway 主播/观众连接面）

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_ws_connections_total` | counter | 建立的 WS 连接 |
| `chorus_ws_unauthorized_total` | counter | hello 鉴权失败被关闭的连接 |
| `chorus_ws_protocol_mismatch_total` | counter | hello 协议版本不匹配（`protocolVersion` 非空且 ≠ 服务端当前版本）被拒的连接 |
| `chorus_ws_message_too_large_total` | counter | 消息体超限被拒次数 |
| `chorus_ws_acks_accepted_total` | counter | 接受的 WS ACK（未触发限流） |
| `chorus_ws_acks_dropped_total` | counter | 触发限流被丢弃的 WS ACK |
| `chorus_ws_resync_dropped_total` | counter | 网关 `/ws` resync 固定窗口限流（`Faces:StreamerWs:ResyncPerMinute`）丢弃的请求（`c896855`）。与引擎侧 `chorus_resync_throttled_total` 互补：该计数发生在请求到达引擎之前；异常上升说明客户端在空转（频繁重连/全量重同步） |
| `chorus_ws_snapshot_dropped_total` | counter | 网关慢客户端丢弃策略计数器（2026-10-16，todo §10 阻塞项 ②）：每连接出站 latest-wins 槽（容量 1）中旧快照被新快照替换的次数，累计进进程级 `FaceCounters`，因此跨连接重连不丢历史。仅拆分模式有意义——单进程形态没有该跳数，恒为 0（其丢弃计入 `chorus_client_queue_dropped_total`）。配套读法：与 `chorus_ws_resync_dropped_total` 一起读——丢弃数上升说明客户端跟不上快照速率；丢掉的帧由客户端 resync 回填 |

### IPC

| 指标 | 类型 | 含义 |
| --- | --- | --- |
| `chorus_ipc_publish_failures_total` | counter | IPC 发布失败计数。Gateway 侧统计 Gateway→Engine 事件，EngineHost 侧统计 Engine→Gateway 快照/welcome（`8e87a47`） |
| `chorus_ipc_engine_silence_ms` | gauge | Gateway 最近一次收到 EngineHost 消息（心跳/welcome/快照）以来的毫秒数；从未收到为 `-1`。`/ready` 依此判定（阈值 `Ipc:ReadinessMaxSilenceMs`） |

## 关联 ID

一条平台事件在系统内的追踪链：

```
平台 EventId ──ingress──> Envelope.SourceEventId ──IPC──> Engine 结算 ──tick──> 快照(sessionId+tickId) ──WS──> 客户端
```

- **`AudienceEvent.EventId`**：平台事件 ID（去重键的一部分）；ingress 响应与 `duplicated=true` 都携带它。
- **`Envelope.SourceEventId`**：IPC 信封上的可选来源事件 ID。Gateway 的 `/ingress/{platform}` 发布观众事件时写入 `ev.EventId`；引擎收到时校验一致性，不匹配记警告日志（`Correlation mismatch`）。
- **`Envelope.MessageId`**：一次投递（`m_<guid>`）的唯一 ID，用于定位"这一跳"的发布失败（`chorus_ipc_publish_failures_total` 的日志都带 `message=`）。
- **快照关联**：快照带 `sessionId` + `tickId` + `serverTimeMs`；客户端 ACK 回 `lastAppliedTickId`，闭合"哪个事件 → 哪个 tick → 被哪个客户端确认"的闭环。
- **ACK 关联**：WS ACK 以 `sessionId|clientId` 绑定连接；`GatewayAckCmd` 带 `lastAppliedTickId` 与 `ClientTimeMs`（客户端测得的漂移）。

排障时：先在入口面按 `eventId` 查它被接受 / 去重 / 拒绝；再用 `SourceEventId` 追 IPC 发布是否失败；最后看引擎侧 `applied/merged/dropped` 与 `tickId` 水位。

## JSON 诊断端点

- `GET /sessions`：`SessionDiagnostics` 列表（tick、事件计数、lag、ACK 分类计数、信箱深度、快照计数等）。
- `GET /matches`：对局级聚合视图（会话列表、阵营、累计事件数、最近事件时间）。
- `GET /health`：存活探针（三进程都有）。

## 日志关键字段

| 字段 | 出现位置 | 含义 |
| --- | --- | --- |
| `eventId` / `SourceEventId` | 入口、引擎事件通道 | 平台事件关联 |
| `messageId` | IPC 发布失败、引擎收包 | 投递关联 |
| `sessionId` / `clientId` | 全链路（WS 拒绝、循环错误、快照发送失败都带） | 会话与客户端 |
| `tickId` | tick 超时、快照发送失败、分发失败路径 | 权威 tick 水位 |

WS 循环与快照发送的失败日志（`WebSocket loop error`、`Failed to send snapshot`）都带
`session=` / `clientId=` / `tick=` 结构化字段，可与 `/sessions` 诊断的 lag/ack 字段对齐排障。
