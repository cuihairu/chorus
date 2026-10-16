---
title: 共享内存 IPC 传输评估（MemoryMappedFile + FlatBuffers）
---


<p align="center">
  <a href="./shared-memory-transport.md">English</a> | 简体中文
</p>

# 背景

todo §3.5 要求评估并规划共享内存 transport（MemoryMappedFile + FlatBuffers）。
本文给出：现状每条消息的真实成本、方案轮廓、更便宜的替代项、风险与触发条件。
结论先行：**暂不实现**；先把两个零风险的替代优化做掉，共享内存等明确的性能
瓶颈出现再上（触发条件见文末）。

# 现状：一条 Envelope 的真实成本

拆分模式下 eng↔gw 每条消息走（`Chorus.Messaging` / `JsonEnvelopeCodec` /
`LengthPrefixedStream`）：

1. `Envelope`（含 `body`）→ UTF-8 JSON 序列化（发送端）
2. 4 字节长度前缀 + 帧写入 socket（发送端一次 `SendAsync`）
3. 内核拷贝 ×2（写 UDS、读 UDS）
4. 接收端读帧 → JSON 反序列化 → 入 `_inbox` Channel

可量化的成本主要是 **JSON 编解码 + 内核态拷贝 + 每连接 pump 线程调度**。
心跳（默认 5s 一条）可以忽略；大头是快照。**优化前**每个客户端每 tick 一条 `eng.snapshot`（按 (session, client) 逐条发布），默认 66ms tick、10 个客户端 ≈ 每秒 ~150 条 JSON 往返。**优化后（50f0045）**广播快照仅由 `BroadcastOwnerRegistry` 选举的 owner 每 session 每 tick 发布一条（`EngineBusSessionClient` 发布过滤），10 客户端 ≈ 每秒 ~15 条。

# 更便宜的替代（先做，零新依赖）

按性价比排序，以下两项做完之前不建议上共享内存：

1. **eng→gw 快照按 tick 去重发布**。广播快照对同 session 所有客户端内容
   相同（Gateway 侧 `DispatchSnapshotAsync` 本来就按 session 扇出），但引擎
   侧每个 `EngineBusSessionClient` 各发布一条完整 JSON。改为每个 session
   每 tick 发布一条（客户端差异只存在于定向 resync 快照），eng→gw 的消息量
   直接从「客户端数 × tick 频率」降为「tick 频率」，JSON 编码同样省掉 N-1 份。
   ✅ **已交付（50f0045）**：`EngineBusSessionClient` 只在两种情况下发布——
   无 `TargetClientId` 的广播快照仅由该 session 的**广播所有者**发布
   （`BroadcastOwnerRegistry`：首个接入者当 owner，owner 掉线转移到剩余
   客户端，最后一个掉线清空、重连重选；转移窗口内当 tick 广播可跳过一次，
   下一 tick 追上）；定向 resync 快照仅由被定向的那个客户端发布。Gateway
   零改动（null-target 本就扇出全 session）。回归守卫：
   `BroadcastOwnerRegistryTests`（5 例选举/转移/独立）、
   `EngineBusSessionClientTests`（3 例发布过滤）、
   `SplitModeEndToEndTests.TwoClientsSameSession_BothReceiveSnapshots`
   （双客户端同 session 均收到广播的端到端验收）。
2. **JSON → 二进制 envelope（MessagePack/FlatBuffers）**。快照 wire 格式
   （streamer-ws 面）已评估暂不引入（见 `docs/reference/tuning.md`
   「快照序列化成本」）；IPC 面的 envelope 格式与 wire 格式解耦，可以单独换，
   但会引入新依赖 + 双端版本兼容要求，收益同样需要先有基准数据。

# 方案轮廓（若做）

## RingBuffer

- **通道**：沿用现有 eng→gw / gw→eng 双通道方向，每方向一块
  `MemoryMappedFile`，内部为定长 ring buffer（SPSC：引擎单写、网关单读）。
- **帧格式**：`[len:u32][seq:u64][payload]`；ring 头部存
  `writeIndex/readIndex`（u64，单调递增，对 buffer 容量取模），读写索引用
  `Interlocked`/`Volatile` 更新——**不引入锁**。
- **唤醒**：写端写完置 seq 后 `EventWaitHandle`（跨平台）/ eventfd（Linux）
  通知读端；读端也可退化成 0.5–1ms 轮询，简单但费 CPU。
- **背压**：ring 满 = 消费慢，写端**覆盖最旧未读帧不可行**（会撕裂半读帧），
  正确做法是丢弃本条并计数（快照可丢、事件不可丢需分流——与现有
  SessionActor 双车道语义对齐）。

## FlatBuffers 的角色

- 收益点：**零拷贝读取**（读端按 offset 访问字段，不整体物化）+ schema
  演进（字段可增删）。
- 代价点：schema 文件 + codegen 进入构建流程；`object` 类型的动态
  `body`（现有 `Envelope.Body` 是 `JsonElement`）需要改成强类型 union，
  **这是对 `IMessageBus` 契约的破坏性重构**，不是纯传输层替换。

# 成本与风险

| 项 | 说明 |
| --- | --- |
| 跨平台 | Windows/Linux 的命名共享内存与命名语义不同（`MemoryMappedFile.CreateNew` vs `/dev/shm`），命名规则与生命周期（谁创建谁释放）要两端一致 |
| 崩溃恢复 | 一端 kill -9 会留下半写帧/悬挂索引；读端必须能检测 seq 断裂并整段重建 ring（等价于现有 UDS 断连重连逻辑的加强版） |
| 可调试性 | socket 可用 `ss`/tcpdump 观察，共享内存完全黑盒；排障要靠自建 seq/心跳监控 |
| 版本滚动 | eng 先升 gw 后升（见 `docs/reference/versioning.md`）要求 ring 头部带格式版本与容量协商，不兼容时回退 UDS |
| 实现量 | 双通道 ring + 唤醒 + 背压 + 崩溃恢复 + fallback + 基准，估算在「周」级，远超现有任一 transport |

# 触发条件与阶段计划

满足任一再开工，避免为不存在的瓶颈付复杂度：

- 基准显示单机拆分模式下 IPC 编解码 + 拷贝占 tick 预算（当前 66ms）的
  **>5%**（用 `/metrics` 的 `LastTickProcessMs` 分位 + 压测脚本对比 Monolith
  与拆分模式得出）；
- 单机会话数 × 客户端数使 eng→gw 消息速率达到 UDS 吞吐瓶颈（表现为
  `MailboxDepth` 持续增长或 `TotalTickOverruns` 上升）且
  「按 tick 去重发布」优化后仍不够。

阶段划分（届时执行）：

1. **阶段 A**：eng→gw 快照按 tick 去重（替代项 1，独立收益）——**✅ 已交付（50f0045）**：广播快照仅由 owner 每 session 每 tick 发布一条，Gateway 零改动，eng→gw 消息量降为「tick 频率」；守卫 `BroadcastOwnerRegistryTests` 5 例 + `EngineBusSessionClientTests` 3 例 + `TwoClientsSameSession_BothReceiveSnapshots`。阶段 B/C 仍按触发条件等待真实瓶颈；
2. **阶段 B**：`Ipc:Transport=SharedMemory` 原型，仅快照通道（可丢语义），
   事件/控制通道仍走 UDS；与 UDS 做 A/B 基准；
3. **阶段 C**：基准胜出且崩溃恢复验证通过后，才考虑默认切换与 FlatBuffers。

# 结论

共享内存是「最后 20% 性能」的工具，当前仓库没有触发条件。先做替代项 1
（按 tick 去重发布，见上），其余按触发条件等待真实瓶颈。
