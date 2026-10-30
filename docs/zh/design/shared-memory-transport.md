---
title: 共享内存 IPC 传输评估（MemoryMappedFile + FlatBuffers）
---


# 背景

todo §3.5 要求评估并规划共享内存传输（MemoryMappedFile + FlatBuffers）。
本文给出：每消息今日的真实成本、方案概述、更廉价的替代、风险与触发条件。
先给结论：**暂不实现**；先落地两个零风险的替代优化，仅在具体性能瓶颈出现后再引入共享内存（触发条件见文末）。

# 现状：一个 Envelope 的真实成本

分离模式下每个 eng↔gw 消息经过（`Chorus.Messaging` / `JsonEnvelopeCodec` / `LengthPrefixedStream`）：

1. `Envelope`（含 `body`）→ UTF-8 JSON 序列化（发送侧）
2. 4 字节长度前缀 + 帧写入套接字（每个发送方一次 `SendAsync`）
3. 内核拷贝 ×2（写 UDS、读 UDS）
4. 接收侧读帧 → JSON 反序列化 → 进入 `_inbox` Channel

可量化的成本主要是 **JSON 编解码 + 内核态拷贝 + 每连接泵线程调度**。心跳（默认每 5s 一次）可忽略；主体是快照。**优化前**，每个客户端每 tick 收到一个 `eng.snapshot`（按 (session, client) 逐个发布），默认 66ms tick、10 客户端 ≈ 每秒约 150 次 JSON 往返。**优化后（50f0045）**，广播快照仅由 `BroadcastOwnerRegistry` 选出的 owner 发布，每会话每 tick 一个（`EngineBusSessionClient` 发布过滤），10 客户端 ≈ 每秒约 15 次。

# 更廉价的替代（先做这些，零新依赖）

按性价比排序，完成这两项前不引入共享内存：

1. **按 tick 去重发布 eng→gw 快照**。同一会话所有客户端的广播快照内容相同（Gateway 侧 `DispatchSnapshotAsync` 已按会话扇出），但引擎侧每个 `EngineBusSessionClient` 发布自己的完整 JSON。改为每会话每 tick 发布一个（客户端差异仅存在于定向重同步快照）直接把 eng→gw 消息量从"客户端数 × tick 频率"降到"tick 频率"，JSON 编码也省 N-1 份拷贝。✅ **已交付（50f0045）**：`EngineBusSessionClient` 现在只在两种情况下发布——无 `TargetClientId` 的广播快照仅由该会话的**广播 owner** 发布（`BroadcastOwnerRegistry`：首个连接者成为 owner，owner 断开时所有权转移给剩余客户端，最后一个断开时清除并在重连时重新选举；转移窗口内该 tick 的广播可能跳过一次，下一 tick 补上）；定向重同步快照仅由目标客户端发布。Gateway 零改动（null-target 已扇出到整个会话）。回归守卫：`BroadcastOwnerRegistryTests`（5 例：选举/转移/独立）、`EngineBusSessionClientTests`（3 例：发布过滤）、`SplitModeEndToEndTests.TwoClientsSameSession_BothReceiveSnapshots`（同一会话两个客户端都收到广播的端到端验收）。
2. **JSON → 二进制信封（MessagePack/FlatBuffers）**。快照线格式（streamer-ws 面）已评估并暂未引入（见 `docs/reference/tuning.md` "快照序列化成本"）；IPC 面信封格式与线格式解耦，可单独替换，但引入新依赖 + 双端版本兼容要求，收益同样需先有基线数据。

# 方案概述（若实现）

## RingBuffer

- **通道**：复用现有 eng→gw / gw→eng 双通道方向，每个方向一个 `MemoryMappedFile`，内部定长环形缓冲（SPSC：引擎单写、网关单读）。
- **帧格式**：`[len:u32][seq:u64][payload]`；环形头部存 `writeIndex/readIndex`（u64，单调递增，对缓冲容量取模），读写索引用 `Interlocked`/`Volatile` 更新——**不引入锁**。
- **唤醒**：写方完成设置 seq 后经 `EventWaitHandle`（跨平台）/ eventfd（Linux）信号读方；读方也可降级为 0.5–1ms 轮询，简单但 CPU 昂贵。
- **背压**：环形满 = 慢消费者；写方**覆盖最旧未读帧不可行**（会撕裂半读帧），正确做法是丢弃该消息并计数（快照可丢，事件不可丢且需独立通道——与现有 SessionActor 双通道语义一致）。

## FlatBuffers 的角色

- 收益点：**零拷贝读取**（读方按偏移访问字段，不必完整物化）+ schema 演进（可增删字段）。
- 成本点：schema 文件 + 代码生成进入构建流程；动态 `object` 类型 `body`（现有 `Envelope.Body` 是 `JsonElement`）需改为强类型联合，**这是对 `IMessageBus` 契约的破坏性重构**，不是纯传输层替换。

# 成本与风险

| 项 | 说明 |
| --- | --- |
| 跨平台 | Windows/Linux 命名共享内存与命名语义不同（`MemoryMappedFile.CreateNew` vs `/dev/shm`）；命名规则与生命周期（谁创建、谁释放）须两侧一致 |
| 崩溃恢复 | 一侧 kill -9 留下半写帧/悬挂索引；读方须能检测 seq 不连续并整体重建环形（等价于强化现有 UDS 断开/重连逻辑） |
| 可调试性 | 套接字可用 `ss`/tcpdump 观察；共享内存完全是黑盒；排障依赖自建 seq/心跳监控 |
| 版本滚动 | 先升级 eng 后升级 gw（见 `docs/reference/versioning.md`）要求环形头部携带格式版本与容量协商，不兼容时回退 UDS |
| 实现体量 | 双通道环形 + 唤醒 + 背压 + 崩溃恢复 + 回退 + 基线；估计以"周"计，远超任何现有传输 |

# 触发条件与阶段计划

仅在满足以下任一条件时开始，避免为不存在的瓶颈付出复杂度：

- 基线显示单机分离模式下 IPC 编解码 + 拷贝占 tick 预算（当前 66ms）的 **>5%**（由 Monolith 与分离模式之间 `/metrics` 的 `LastTickProcessMs` 百分位对比负载测试脚本推导）；
- 单机会话数 × 客户端数把 eng→gw 消息速率推到 UDS 吞吐瓶颈（表现为 `MailboxDepth` 持续增长或 `TotalTickOverruns` 上升），且"按 tick 去重发布"优化仍不足。

阶段拆分（届时执行）：

1. **阶段 A**：eng→gw 快照按 tick 去重发布（替代 1，独立收益）——**✅ 已交付（50f0045）**：广播快照仅由 owner 发布，每会话每 tick 一个，Gateway 零改动，eng→gw 消息量降到"tick 频率"；守卫 `BroadcastOwnerRegistryTests` 5 例 + `EngineBusSessionClientTests` 3 例 + `TwoClientsSameSession_BothReceiveSnapshots`。阶段 B/C 仍等待真实瓶颈的触发条件；
2. **阶段 B**：`Ipc:Transport=SharedMemory` 原型，仅快照通道（可丢弃语义），事件/控制通道仍在 UDS；与 UDS 做 A/B 基线；
3. **阶段 C**：仅在基线胜出且崩溃恢复验证通过后，才考虑默认切换与 FlatBuffers。

# 结论

共享内存是"最后 20% 性能"的工具，当前仓库无触发条件。先做替代 1（按 tick 去重发布，见上），其余等待触发条件直到真实瓶颈出现。
