---
title: 帧同步（权威 Tick + 快照）
---


# 帧同步（权威 Tick + 快照）

本文给出 chorus 的**帧同步语义方案**：帧号从哪来、如何随消息下发、客户端以帧号为渲染/排序依据、乱序与丢帧如何回填、重连与回放如何对齐、客户端节拍如何追赶。

> 状态：**方案先行成文，本轮未写代码**。文中每条论断都带 `file:line` 或来源；本仓未实测的部分显式标注 `未实测`。

---

## 1. 业界调研（带来源）

直播弹幕系统普遍**原生没有"帧号"**——弹幕消息携带的是**时间戳**（毫秒或 ISO 8601），客户端按时间戳排序。帧号/tick 是**游戏层**概念，由权威服务端产出。下面逐条给出来源。

### 1.1 B 站

- **Web 逆向通道**（非官方，法律风险见 §1.4）：弹幕消息携带**毫秒时间戳**。blivedm 的 `DanmakuMessage` 有 `timestamp: int`，docstring 为 "timestamp (milliseconds)"，取自协议的 `info[0][4]`。
  来源：[blivedm models/web.py](https://raw.githubusercontent.com/xfgryujk/blivedm/dev/blivedm/models/web.py)（`DanmakuMessage` dataclass 字段表）。
- **开放平台通道**（官方）：[open-live.bilibili.com](https://open-live.bilibili.com/)；blivedm 的 `open_live` 接口连该平台的 WebSocket（`wss://live-open.biliapi.com`），事件含 `DANMU_MSG`/`SEND_GIFT`/`INTERACT_WORD_V2` 等。
  来源：[blivedm README](https://github.com/xfgryujk/blivedm)（列出 `web端` 与 `B站直播开放平台` 两个接口）。
- **顺序保证**：B 站弹幕协议本身**没有帧号/序号**，只有时间戳；顺序由客户端按时间戳 + 到达顺序推断。这正是 chorus 在游戏层引入帧号的原因（见 §2.1）。

### 1.2 YouTube Live Chat（官方 API）

- 每条消息携带 `snippet.publishedAt`（`datetime`，ISO 8601），描述为 "The date and time when the message was originally published"。
  来源：[YouTube liveChatMessages 资源文档](https://developers.google.com/youtube/v3/live/docs/liveChatMessages)。
- 轮询节奏由 `liveChatMessages.list` 响应中的 `pollingIntervalMillis`（`unsigned integer`）控制，描述为 "how long your API client should wait before requesting additional results"。
  来源：[YouTube liveChatMessages/list 方法文档](https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list)。
- **顺序保证**：同样只有时间戳、没有帧号；官方建议按 `publishedAt` 排序 + 以 `pollingIntervalMillis` 控频。

### 1.3 Twitch（官方 EventSub WebSocket）

- WebSocket 消息类型：`session_welcome` / `session_keepalive` / `notification` / `session_reconnect` / `revocation`。
  来源：[Twitch EventSub WebSocket reference](https://dev.twitch.tv/docs/eventsub/websocket-reference/)。
- **重连**：边缘节点故障切换前 30 秒下发 `session_reconnect`，携带 `reconnect_url`；"Use this URL as is; do not modify it. The connection automatically includes the subscriptions from the old connection."。旧连接持续下发事件，直到新连接收到 `session_welcome`。
  来源：[Twitch Handling WebSocket events](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/)。
- **顺序保证**：Twitch 消息有 `metadata.message_id` 与 `metadata.message_timestamp`（UTC），但**没有单调帧号**；重连后的续传依赖 `reconnect_url` + `message_id` 去重。

### 1.4 开源实现

- **blivedm**（Python，B 站弹幕/礼物/进场）：[github.com/xfgryujk/blivedm](https://github.com/xfgryujk/blivedm)。同时接 Web 逆向与官方开放平台两通道；事件模型见 `blivedm/models/`。
- **bilibili-API-collect**（逆向文档集合，20.2k★）：[github.com/SocialSisterYi/bilibili-API-collect](https://github.com/SocialSisterYi/bilibili-API-collect)（`docs/live/message_stream.md` 记录 wss `/sub`、5 秒内发鉴权包、心跳 30s/60s 掉线、16 字节包头、op 2/3/5/7/8）。**注意**：该项目 2026-01-28 收到 B 站律师函，2026-01-30 归档/删库（原仓库 README 可查）→ Web 逆向通道有真实的法律追责先例。
- **OpenDANMU**：GitHub 搜索只有 [zhengcai/opendanmu](https://github.com/zhengcai/opendanmu)（C# "第三方弹幕工具"，0 star，2022-01 起失修），**不是帧同步方案**。作为帧同步参考实现：**未找到公开来源**。
- **弹幕游戏帧同步的公开文章**：**未找到公开来源**（没有找到"直播弹幕 + 帧同步"的公开架构文章；已知竞品大多做"弹幕事件 → 游戏输入"的即时映射，没有帧级插值）。

### 1.5 调研对 chorus 的含义

| 平台/实现 | 排序依据 | 丢包恢复 | 重连 | 对 chorus 的含义 |
| --- | --- | --- | --- | --- |
| B 站 Web 逆向 | 毫秒时间戳 | 无（类 UDP；丢了就丢了） | 重连后重新鉴权 | 顺序必须在游戏层重建 |
| YouTube | `publishedAt` + `pollingIntervalMillis` | 轮询窗口内天然覆盖 | 重新拉取 | 官方通道有明确控频 |
| Twitch | `message_id` + `message_timestamp` | `reconnect_url` 续传 | 官方重连流程 | 续传语义可借鉴 |
| blivedm | 时间戳 | 无 | 重连 | 事件归一化参考 |

**共同点**：上游只给时间戳；**帧号由 chorus 自己的游戏层产出**。这与本仓现状一致（`tickId` 已在引擎内单调递增，见 §2.1）。

---

## 2. 现状（已实现，file:line）

帧同步的**引擎侧骨架已在代码里**；本节逐条给证据。

| 能力 | 证据 |
| --- | --- |
| 帧号（tickId）单调递增 | `src/Chorus.Engine/Session/SessionActor.cs:52`(the `TickId` property)、`:391`（`TickId++`） |
| 默认 tick 周期 66ms（≈15.15 tick/s） | `src/Chorus.Engine/Session/SessionConfig.cs:4`（`TickDurationMs = 66`） |
| 输入延迟：事件调度到 `tickId + inputDelayTicks` | `SessionConfig.cs:5`（`InputDelayTicks = 2`）、`SessionActor.cs:379`（`targetTick = TickId + _config.InputDelayTicks`） |
| 每 tick 喂给游戏模块事件 | `SessionActor.cs:444`（`_game.ApplyEvents(TickId, tickEvents)`） |
| 周期快照：每 `snapshotEveryTicks` 一次 | `SessionActor.cs:447`（`SnapshotEveryTicks > 0 && (TickId % SnapshotEveryTicks) == 0`） |
| 快照携带帧号 `tickId` | `src/Chorus.Protocol/Messages/ServerSnapshot.cs:8`（`long TickId`）、`SessionActor.cs:478`/`:497`/`:526`(the three send paths) |
| 快照 schema 版本 `V = 1` | `ServerSnapshot.cs:20`(`int V = 1`; 注释指向 versioning.md §2) |
| ACK：客户端上报 `lastAppliedTickId` | `SessionActor.cs:156`(`AckAsync`)、`:346-371`(单调守卫)、`:371`(更新 `LastAckTickId`) |
| 强制重同步：落后 > `maxLagTicks` 时下发 | `SessionConfig.cs:8`(`MaxLagTicks = 50`)、`SessionActor.cs:550-569`(lag 检查 + 冷却)、`:500`/`:529`(`Forced=true`) |
| resync 限流 | `SessionConfig.cs:9`（`ResyncCooldownMs = 2000`）、`SessionActor.cs:564` |
| 客户端连接基线 = 当前帧 | `SessionActor.cs:314-315`（`ConnectedTickId = TickId`、`LastAckTickId = TickId`） |
| 事件去重 | `src/Chorus.Server/Program.cs:73-77`(`EventDeduplicator`, `DedupTtlSeconds` 默认 600) |
| 有界事件通道 + 有界等待 | `SessionActor.cs:75`（`MaxPendingEvents`）、`:175`（`EventEnqueueMaxWaitMs`） |
| 每 tick 事件上限 | `SessionConfig.cs:7`（`MaxEventsPerTick = 200`） |
| 快照字节上限 | `SessionConfig.cs:21`（`MaxSnapshotBytes = 1_048_576`） |
| **检查点持久化不是回放** | ControlHost 存已提交房间检查点，但不保留/重放确定性事件/帧日志；快照历史不是录制流 |

**结论**：帧号生成、随快照下发、ACK、强制重同步、背压、去重——**都已实现**。缺口在 §3。

---

## 3. 缺口（计划中 / 未实现）

| # | 缺口 | 现状证据 | 状态 |
| --- | --- | --- | --- |
| G1 | 客户端拿不到"每个事件属于哪一帧" | 快照只带 `tickId`（`ServerSnapshot.cs:8`），没有逐事件帧归属；事件按 tick 排空后即删（`SessionActor.cs:413-415`） | **已实现（P2）**：`frameEvents` 信封字段（§4.2） |
| G2 | 没有帧历史缓冲 | 引擎只保留当前 tick 的事件；无环形缓冲 | **已实现（P2）**：`FrameHistory` 按字节封顶的环形缓冲（§4.3） |
| G3 | 没有客户端"按帧号取帧"消息 | 线协议只有服务端→客户端 `snapshot`；`ClientAck` 只把 `lastAppliedTickId` 作为进度字段（另有 `sessionId`/`clientId`/`clientTimeMs`，`src/Chorus.Protocol/Messages/ClientAck.cs:3-9`） | **已实现（P2）**：`resync` 消息 + 完整 IPC 链（§4.3） |
| G4 | 没有回放/录像对齐 | 检查点恢复房间当前状态，但不存持久帧/事件日志 | **显式不做** |
| G5 | 没有客户端 SDK | 本仓 src 全是服务端；没有客户端库 | **已实现（P3）**：`Chorus.Client.ChorusClient`（最小入口：握手 + 回调 + ACK 采样 + resync 冷却；无自动重连，见[快速上手](../guide/quickstart-frame-sync.md)） |
| G6 | 客户端节拍/插值未实测 | 没有可运行的客户端实现 | **未实测** |

---

## 4. 方案

### 4.1 帧号语义

**帧号 ≡ tickId**（服务端单调 `long`，每 tick +1，`SessionActor.cs:391`）。

- **不另设帧 id**：避免双时钟（帧 id 与 tickId 漂移时客户端无所适从）。tickId 已权威、单调、随快照下发——直接复用。
- **帧 = tick**：一帧即一个 tick 节拍。`tickId` 就是帧号。
- 客户端以帧号为渲染与排序依据：收到 `snapshot(tickId=F)` → 渲染第 F 帧；第 F 帧的权威状态就是该快照的 `state`。

### 4.2 帧号如何随消息下发

帧号**已经**随快照下发（`ServerSnapshot.cs:8`）。需要补的是**逐事件帧归属**（G1）与**客户端按帧号回放**。

**P1 —— 帧号语义 + 客户端按帧号回放（已实现）**

- 帧号 = `tickId`，随快照下发；客户端按帧号排序渲染（§4.1）。
- 客户端回放状态机 `src/Chorus.LoadTool/FramePlayer.cs`：帧号 ≤ 已应用帧 → 丢弃（幂等；重放窗口不会渲染两遍）；帧号 > 已应用帧 + 1 → 应用并上报首个缺口帧号供 `resync` 拉取。单元测试 `FramePlayerTests`（7 例）；`ClientSimulation` 已接线（`LoadTool` 输出 `resyncs=` 计数）。
- `state.frameEvents` 约定**已废弃**：原计划把帧归属塞进 `state`（零协议改动）；P2 的信封字段是更优解（一等协议字段，客户端无需知晓 `state` 约定），G1 直接由 P2 关闭。

**P2 —— 协议扩展（增量可选，已实现）**

- 为 `ServerSnapshot` 新增**可选** `frameEvents` 信封字段（`List<FrameEvent>?`，默认 null → 线上省略）。
- 依 `versioning.md:60-65`：线协议新增可选字段 = 允许（0.x 与 1.0+ 均允许）。
- **不抬升 `v`**：增量可选不改变字段语义；`v` 只在字段**语义变化**时抬升（`versioning.md:42-43`：拒绝未知 v + 强制重同步留待首个 v2 设计时定）。
- 需按 `versioning.md:43` 复核：该字段是增量的，不触发"拒绝未知 v"语义。
- **实现**：`src/Chorus.Protocol/Messages/FrameEvent.cs`（`FrameId` / `EventId` / `Kind` / `UserId`）；`SessionSnapshot.FrameEvents`；`WireSnapshot.cs` 带到线上（null → 省略）。`SessionActor.DrainFrameEvents` 每个快照节拍排空一次累计器（按 tick 累计，封顶 `MaxPendingFrameEvents = 4096` 时丢最旧节拍）。单元测试：`WireSnapshotFrameEventsTests`（省略 / camelCase / 旧 JSON 兼容）。

### 4.3 乱序 / 丢帧

**客户端缺口检测**：跟踪 `lastAppliedFrameId`；收到快照的 `tickId` 与预期不符（`tickId > lastAppliedFrameId + 1`）→ 中间有丢帧。实现见 `Chorus.LoadTool.FramePlayer`（`FramePlayerTests` 覆盖：连续/重复/重放丢弃/缺口上报）。

**帧回填策略**：

| 阶段 | 策略 | 说明 |
| --- | --- | --- |
| P1（无历史） | 等下一个周期快照 / 触发强制重同步 | **无法回填历史帧**——引擎没有历史（G2）。既有机制：周期快照（`SessionActor.cs:447`）+ 强制重同步（`:550-569`）。诚实标注：丢帧即跳帧；客户端直接对齐到最新帧。 |
| P2（已实现） | 引擎帧历史环形缓冲 + 客户端拉取 | 见下。单元 + e2e 覆盖（`FrameHistoryTests` / `SessionActorFrameHistoryTests` / `WireSnapshotFrameEventsTests`）。 |

**P2 回填实现（已落地）**：

- 引擎侧：每会话帧历史环形缓冲（内存），`src/Chorus.Engine/Session/FrameHistory.cs`。
- **按字节封顶，而非按帧数**：N 拍 × 平均快照字节数不可控；最坏 `MaxSnapshotBytes`（1 MiB，`SessionConfig.cs:21`）× N 会打爆内存，故按字节封顶——`Session:FrameHistoryBytes`（默认 8 MiB/会话），超限逐出最旧，**但不逐出刚加入的帧**（`Add` 的逐出条件排除最新一项）；单帧超过整个容量时，该帧作为会话唯一帧保留。
  - 内存估算（未实测）：8 MiB/会话 × `MaxSessions`（200，`SessionConfig.cs:17`）= 最坏 1.6 GiB。**上限需在实测平均快照字节数后设定**；`FrameHistoryBytes = 0` 完全关闭（窗口查询恒未命中）。
- 客户端侧：新增 `resync` 消息类型（客户端→服务端）携带 `fromFrameId`；`ProtocolTypes.Resync = "resync"` 与 `ClientResync` 模型。
  - 依 `versioning.md:60-65`：线协议新增消息类型 = 允许。
  - 引擎回放 `[fromFrameId, 当前 tickId]` 窗口内的快照序列；窗口查询拒绝**部分重放**（`fromFrameId` 早于最旧帧 → 未命中，因为从保留尾部起重放会掩盖缺口）。
  - 窗口外缺口（已被逐出 / 历史关闭）→ 回放最新快照 + `forced=true` + `reason="resync-gap"`，客户端对齐到最新。
- **重放走直发通道**：`OutboundClientQueue.SendReplayAsync` 绕过容量 1 的 latest-wins 槽（DropOldest 会把重放帧挤得只剩最后一帧）；它与周期发送经 `_sendGate` 逐帧串行，整批经 `_replayGate` 互斥；重放以 detached 方式执行，传输慢不拖 tick。周期快照仍可被丢——**重放帧不可**。
- 诊断计数器：`TotalResyncRequests` / `TotalResyncReplays` / `TotalResyncWindowMisses` / `TotalResyncThrottled` / `TotalResyncSnapshotsSent`、`FrameHistoryBytes`。
- **完整传输链**：`StreamerWsFace`（`resync` 分支 + 固定窗口限流）→ `IStreamerSessionBinding.ResyncAsync` → 单机 `MonolithSessionRouter`（直调 `SessionActor`）/ 网关 `IpcSessionRouter`（`GatewayIpcService.PublishResyncAsync` → `gw.resync` → `GatewayResyncCmd` → `EngineIpcService` → `SessionActor.ResyncAsync`）。

**幂等与去重**：

- 事件级去重已存在：`EventDeduplicator`（`Program.cs:73-77`，`DedupTtlSeconds` 默认 600）。
- 快照级幂等：客户端按 `lastAppliedFrameId` 去重——帧号 ≤ 已应用帧的快照直接丢弃（`protocol.md:121` 已定义 `lastAppliedTickId` 为"真正应用完成的权威 tick"）。
- 回填窗口内的重复快照：同一 `tickId` 只应用一次（按帧号幂等）。

**背压策略（已实现）**：

- 有界事件通道：`MaxPendingEvents`（`SessionConfig.cs:11`，默认 8000）。
- 有界等待：礼物等不可丢事件在通道满时额外等待 `EventEnqueueMaxWaitMs`（`SessionConfig.cs:13`，默认 50ms）；仍超时才丢弃。
- 每 tick 事件上限：`MaxEventsPerTick`（`SessionConfig.cs:7`，默认 200）。
- 回填请求的背压（P2，已实现，两层）：
  - 传输层固定窗口：`Faces:StreamerWs:ResyncPerMinute`（默认 30/分钟）；超限直接丢弃并计入 `WsResyncDropped`。
  - 引擎层按客户端冷却：`Session:ResyncRequestCooldownMs`（默认 1000ms）；冷却内重复请求计入 `TotalResyncThrottled` 后立即返回。**与服务端推送 lag 重同步的 `ResyncCooldownMs`（2000ms）不共用**——前者是客户端主动拉，后者是引擎按 ACK 判断推送。

### 4.4 重连 / 回放

**重连（按帧号续传）**：

- 客户端重连 → 发 `hello` → 引擎取**当前 tickId** 作基线（`SessionActor.cs:314-315`：`ConnectedTickId = TickId`、`LastAckTickId = TickId`）。
- **续传 = 从最新帧开始**，不回填历史（没有历史可回填，G2）。这与 Twitch `reconnect_url` 的"旧订阅持续下发、新连接续接"语义一致（[Twitch 文档](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/)），但 chorus 没有跨连接订阅状态——重连即新基线。
- **P2 已落地**：重连后客户端可直接发 `resync { fromFrameId }` 拉取窗口内历史帧；窗口外（已被逐出）则回放最新快照 + `forced=true`/`reason="resync-gap"` 对齐到最新。重连仍取**当前 tickId**作基线；resync 是可选的帧回填，不改变基线语义。

**回放 / 录像对齐**：

- **显式不做**。没有持久化（`versioning.md:72-74`）；快照不落盘，没有可对齐的帧序列。
- 未来若要做回放，前提是：先落地快照恢复契约（`docs/design/snapshot-restore.md`），再谈按帧对齐。
- 业界的回放对齐做法（Twitch `reconnect_url`、YouTube 轮询窗口）都是**直播续传**，不是**录像对齐**；弹幕游戏的录像对齐**未找到公开来源**。

### 4.5 客户端节拍

**渲染帧率与 tick 周期**：

- 60fps 渲染（16.67ms/帧）对 tick 66ms（≈15.15 tick/s）→ **1 tick ≈ 3.96 渲染帧**。
- 渲染层在 F-1 与 F 帧之间插值（未实测；本仓没有客户端 SDK，G5/G6）。

**追赶策略**：

| 情形 | 策略 | 依据 |
| --- | --- | --- |
| 落后 ≤ `maxLagTicks`（50） | 有控制的追赶：加快渲染/插值节奏 | `protocol.md:135` "轻微落后时可接受有控制的追赶" |
| 落后 > `maxLagTicks` | 等强制重同步快照并立即对齐 | `SessionActor.cs:550-569` + `protocol.md:104-105` "收到强制快照后，不应再长时间回填历史帧" |
| 领先 | 不渲染超出最新两帧 | `protocol.md:136` "严重落后时应等待或请求最新快照" |

**未实测**：上述策略**在本仓没有客户端实现可验证**（G5/G6）。P3 SDK 落地后需实测 tick 周期与渲染帧率的实际偏差。

---

## 5. 分期与验收标准

| 阶段 | 内容 | 验收标准 |
| --- | --- | --- |
| **P1** | 帧号语义成文 + 客户端按帧号回放状态机 | **已完成**：帧号 = tickId 成文（§4.1）；客户端回放状态机 `FramePlayer` 落地并被 `LoadTool` 使用，`FramePlayerTests` 7 例（连续/重复/重放丢弃/缺口上报）；真 /ws e2e 端到端冒烟通过。`state.frameEvents` 约定废弃（由 P2 信封字段取代） |
| **P2** | 帧历史环形缓冲 + `resync` 拉取消息 + 快照 `frameEvents` 信封字段 | **已完成**：单元测试 `FrameHistoryTests`（7 例：回填窗口 / 按字节逐出 / 超大帧保留 / 零容量关闭）+ `SessionActorFrameHistoryTests`（6 例：窗口命中重放 / 窗口未命中 → `resync-gap` / 冷却节流 / 未知客户端 / `frameEvents` 携带已应用帧号 / 无事件时省略）+ `WireSnapshotFrameEventsTests`（5 例：线上省略 / camelCase / 旧 JSON 兼容 / `ClientResync` 反序列化 / 真 /ws resync 回填 e2e）；全量套件 260 绿 |
| **P3** | 客户端 SDK（入口 + 一个回调） | **已完成**：`Chorus.Client` 三行接入（`ChorusClient.ConnectAsync` + 一个回调）；回调收到按帧号严格递增的状态；`ChorusClientTests` 3 例（真 /ws 渲染+ACK、回调阻塞下的幂等重放、握手失败抛出）+ `FramePlayerTests` 7 例；全量套件 270 绿 |

---

## 6. 相关页面

- [协议参考](../reference/protocol.md) —— `snapshot` / `ack` / `hello` / `welcome` 消息
- [版本策略](../reference/versioning.md) —— `state` schema 版本 `v`、兼容性规则
- [配置参考](../reference/config.md) —— `Session:*` 参数（`tickDurationMs` / `maxLagTicks` 等）
- [帧同步快速上手](../guide/quickstart-frame-sync.md) —— N 行接入
- [API 参考](../reference/api.md) —— 后端/管理端点
