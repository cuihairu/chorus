---
title: 帧同步（权威 Tick + 快照）
---

# 帧同步（权威 Tick + 快照）

本文给出 chorus 的**帧同步语义方案**：帧号从哪来、随消息怎么下发、客户端以帧编号为渲染/排序依据、乱序与丢帧怎么补、重连与回放怎么对齐、客户端节拍怎么追。

> 状态：**方案文档先行，未本轮写码**。文中每条论断都带 `file:line` 或出处；仓内没有实测的部分明确标 `未实测`。

---

## 1. 业界调研（带出处）

直播弹幕系统普遍**不自带「帧号」**——弹幕消息自带的是**时间戳**（毫秒或 ISO 8601），由客户端按时间戳排序。帧号/tick 是**游戏层**概念，由权威服务器产生。以下逐条给出出处。

### 1.1 B 站

- **web 逆向通道**（非官方，法律风险见 §1.4）：弹幕消息带**毫秒时间戳**。blivedm 的 `DanmakuMessage` 有 `timestamp: int`，docstring 为「时间戳（毫秒）」，取值自协议 `info[0][4]`。
  出处：[blivedm models/web.py](https://raw.githubusercontent.com/xfgryujk/blivedm/dev/blivedm/models/web.py)（`DanmakuMessage` dataclass 字段表）。
- **开放平台通道**（官方）：[open-live.bilibili.com](https://open-live.bilibili.com/)，blivedm 的 `open_live` 接口对接该平台的 WebSocket（`wss://live-open.biliapi.com`），事件含 `DANMU_MSG`/`SEND_GIFT`/`INTERACT_WORD_V2` 等。
  出处：[blivedm README](https://github.com/xfgryujk/blivedm)（列出 `web端` 与 `B站直播开放平台` 两个接口）。
- **顺序保证**：B 站弹幕协议本身**没有帧号/序号**，只有时间戳；顺序由客户端按时间戳 + 到达顺序推断。这正是 chorus 要在游戏层引入帧号的原因（见 §2.1）。

### 1.2 YouTube Live Chat（官方 API）

- 每条消息带 `snippet.publishedAt`（`datetime`，ISO 8601），描述为「The date and time when the message was originally published」。
  出处：[YouTube liveChatMessages 资源文档](https://developers.google.com/youtube/v3/live/docs/liveChatMessages)。
- 轮询节奏由 `liveChatMessages.list` 响应里的 `pollingIntervalMillis`（`unsigned integer`）控制，描述为「how long your API client should wait before requesting additional results」。
  出处：[YouTube liveChatMessages/list 方法文档](https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list)。
- **顺序保证**：同样只有时间戳，没有帧号；官方建议按 `publishedAt` 排序 + 按 `pollingIntervalMillis` 控频。

### 1.3 Twitch（官方 EventSub WebSocket）

- WebSocket 传输的消息类型：`session_welcome` / `session_keepalive` / `notification` / `session_reconnect` / `revocation`。
  出处：[Twitch EventSub WebSocket reference](https://dev.twitch.tv/docs/eventsub/websocket-reference/)。
- **断线重连**：边缘节点切换前 30 秒下发 `session_reconnect`，携带 `reconnect_url`；「Use this URL as is; do not modify it. The connection automatically includes the subscriptions from the old connection.」旧连接在新连接收到 `session_welcome` 前继续投递事件。
  出处：[Twitch Handling WebSocket events](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/)。
- **顺序保证**：Twitch 消息有 `metadata.message_id` 与 `metadata.message_timestamp`（UTC），但**没有单调帧号**；重连后靠 `reconnect_url` 续传 + `message_id` 去重。

### 1.4 开源实现

- **blivedm**（Python，B 站弹幕/礼物/进场）：[github.com/xfgryujk/blivedm](https://github.com/xfgryujk/blivedm)。同时对接 web 逆向与官方 open-live 两通道；事件模型见 `blivedm/models/`。
- **bilibili-API-collect**（逆向文档集，20.2k★）：[github.com/SocialSisterYi/bilibili-API-collect](https://github.com/SocialSisterYi/bilibili-API-collect)（`docs/live/message_stream.md` 记录 wss `/sub`、认证包 5 秒内发送、心跳 30s/60s 断、16 字节包头、op 2/3/5/7/8）。**注意**：该项目 2026-01-28 收到 B 站律师函、2026-01-30 归档删库（原仓 README 可证）→ web 逆向通道有被法律行动的现实先例。
- **OpenDANMU**：GitHub 搜索仅得 [zhengcai/opendanmu](https://github.com/zhengcai/opendanmu)（C#「外挂弹幕软件」，0 star，2022-01 停更），**不是帧同步方案**。作为帧同步参考实现：**未找到公开资料**。
- **帧同步在弹幕游戏里的公开文章**：**未找到公开资料**（未检索到「直播弹幕 + 帧同步」的公开架构文章；已知竞品多为「弹幕事件 → 游戏输入」的即时映射，不做帧级插值）。

### 1.5 调研结论对 chorus 的启示

| 平台/实现 | 顺序依据 | 丢包补齐 | 断线重连 | 对 chorus 的启示 |
| --- | --- | --- | --- | --- |
| B 站 web 逆向 | 毫秒时间戳 | 无（UDP-like，丢就丢） | 重连重新鉴权 | 顺序要在游戏层重建 |
| YouTube | `publishedAt` + `pollingIntervalMillis` | 轮询窗口内自然补齐 | 重拉 | 官方通道有明确频控 |
| Twitch | `message_id` + `message_timestamp` | `reconnect_url` 续传 | 官方 reconnect 流程 | 续传语义可借鉴 |
| blivedm | 时间戳 | 无 | 重连 | 事件归一化参考 |

**共同点**：上游都只给时间戳，**帧号是 chorus 游戏层自己产生的**。这与本仓现状一致（`tickId` 已在引擎内单调递增，见 §2.1）。

---

## 2. 现状（已实现，file:line）

引擎侧的帧同步**骨架已在码里**，本节逐条给出证据。

| 能力 | 证据 |
| --- | --- |
| 帧号（tickId）单调递增 | `src/Chorus.Engine/Session/SessionActor.cs:52`（`TickId` 属性）、`:391`（`TickId++`） |
| tick 周期默认 66ms（≈15.15 tick/s） | `src/Chorus.Engine/Session/SessionConfig.cs:4`（`TickDurationMs = 66`） |
| 输入延迟：事件调度到 `tickId + inputDelayTicks` | `SessionConfig.cs:5`（`InputDelayTicks = 2`）、`SessionActor.cs:379`（`targetTick = TickId + _config.InputDelayTicks`） |
| 每 tick 把事件喂给游戏模块 | `SessionActor.cs:444`（`_game.ApplyEvents(TickId, tickEvents)`） |
| 周期快照：每 `snapshotEveryTicks` 一拍 | `SessionActor.cs:447`（`SnapshotEveryTicks > 0 && (TickId % SnapshotEveryTicks) == 0`） |
| 快照带帧号 `tickId` | `src/Chorus.Protocol/Messages/ServerSnapshot.cs:8`（`long TickId`）、`SessionActor.cs:478`/`:497`/`:526`（三处发送路径） |
| 快照 schema 版本 `V = 1` | `ServerSnapshot.cs:20`（`int V = 1`，注释引 versioning.md §2） |
| ACK：客户端报 `lastAppliedTickId` | `SessionActor.cs:156`（`AckAsync`）、`:346-371`（单调性守卫）、`:371`（更新 `LastAckTickId`） |
| 强制 resync：落后 > `maxLagTicks` 时下发 | `SessionConfig.cs:8`（`MaxLagTicks = 50`）、`SessionActor.cs:550-569`（lag 判定 + cooldown）、`:500`/`:529`（`Forced=true`） |
| resync 限流 | `SessionConfig.cs:9`（`ResyncCooldownMs = 2000`）、`SessionActor.cs:564` |
| 客户端连接基线 = 当前帧 | `SessionActor.cs:314-315`（`ConnectedTickId = TickId`、`LastAckTickId = TickId`） |
| 事件去重 | `src/Chorus.Server/Program.cs:73-77`（`EventDeduplicator`，`DedupTtlSeconds` 默认 600） |
| 有界事件通道 + 有界等待 | `SessionActor.cs:75`（`MaxPendingEvents`）、`:175`（`EventEnqueueMaxWaitMs`） |
| 每 tick 事件上限 | `SessionConfig.cs:7`（`MaxEventsPerTick = 200`） |
| 快照字节上限 | `SessionConfig.cs:21`（`MaxSnapshotBytes = 1_048_576`） |
| **无持久化 = 无回放** | `docs/reference/versioning.md:72-74`（「会话状态纯内存，明确非目标」） |

**结论**：帧号产生、随快照下发、ACK、强制 resync、背压、去重——**都已实现**。缺口在 §3。

---

## 3. 缺口（计划中 / 未实现）

| # | 缺口 | 现状证据 | 状态 |
| --- | --- | --- | --- |
| G1 | 客户端拿不到「每条事件属于哪一帧」 | 快照只带 `tickId`（`ServerSnapshot.cs:8`），不带 per-event 帧归属；事件按 tick 排空后即删（`SessionActor.cs:413-415`） | **已实现（P2）**：`frameEvents` 信封字段（§4.2） |
| G2 | 没有帧历史缓冲 | 引擎只保留当前 tick 的事件，无 ring buffer | **已实现（P2）**：`FrameHistory` 按字节上限的 ring buffer（§4.3） |
| G3 | 没有客户端「拉取某帧」的消息 | 线协议只有 server→client 的 `snapshot`；`ClientAck` 只带 `lastAppliedTickId` 作为进度字段（另有 `sessionId`/`clientId`/`clientTimeMs`，`src/Chorus.Protocol/Messages/ClientAck.cs:3-9`） | **已实现（P2）**：`resync` 消息 + 完整 IPC 链（§4.3） |
| G4 | 没有回放/录播对齐 | 无持久化（`versioning.md:72-74`），快照不落盘 | **明确不做**（除非先落 snapshot-restore 契约） |
| G5 | 没有客户端 SDK | 仓内 src 全是 server 侧，无 client 库 | 计划中（P3） |
| G6 | 客户端节拍/插值未实测 | 无客户端实现可跑 | **未实测** |

---

## 4. 方案

### 4.1 帧号语义

**帧号 ≡ tickId**（服务器单调 `long`，每 tick +1，`SessionActor.cs:391`）。

- **不引入独立 frame id**：避免双时钟（frame id 与 tickId 漂移时客户端无所适从）。tickId 已是权威、单调、随快照下发，直接复用。
- **帧 = tick**：一帧就是一拍 tick。`tickId` 即帧号。
- 客户端以帧号为渲染与排序依据：收到 `snapshot(tickId=F)` → 渲染到帧 F；帧 F 的权威状态就是该快照的 `state`。

### 4.2 帧号随消息怎么下发

帧号**已经**随快照下发（`ServerSnapshot.cs:8`）。需要补的是 **per-event 帧归属**（G1）与**客户端按帧号回放**。

**P1 — 帧号语义 + 客户端按帧号回放（已实现）**

- 帧号 = `tickId`，随快照下发，客户端按帧号排序与渲染（§4.1）。
- 客户端回放状态机 `src/Chorus.LoadTool/FramePlayer.cs`：帧号 ≤ 已应用帧 → 丢弃（幂等，回放的窗口不会重复渲染）；帧号 > 已应用帧 + 1 → 应用并报首个缺口帧号供 `resync` 拉取。单测 `FramePlayerTests`（7），`ClientSimulation` 已接线（`LoadTool` 输出 `resyncs=` 计数）。
- `state.frameEvents` 约定**已废弃**：原计划把帧归属塞进 `state`（零协议变更），P2 的信封字段是更优解（一等协议字段，客户端不必了解 `state` 约定），G1 直接按 P2 收尾。

**P2 — 协议扩展（additive optional，已实现）**

- 在 `ServerSnapshot` 加**可选** `frameEvents` 信封字段（`List<FrameEvent>?`，缺省 null → wire 省略）。
- 依据 `versioning.md:60-65`：线协议新增可选字段 = 允许（0.x 与 1.0+ 均允许）。
- **不 bump `v`**：additive optional 不改字段语义；`v` 只在字段**语义变化**时才 bump（`versioning.md:42-43`：未知 v 的拒绝 + 强制 resync 待首个 v2 设计时定）。
- 需按 `versioning.md:43` 复核：本字段属 additive，不触发「未知 v 拒绝」语义。
- **实现**：`src/Chorus.Protocol/Messages/FrameEvent.cs`（`FrameId` / `EventId` / `Kind` / `UserId`）；`SessionSnapshot.FrameEvents`；`WireSnapshot.cs` 携带到 wire（null → 省略）。`SessionActor.DrainFrameEvents` 每快照拍清一次累积器（每 tick 累积、`MaxPendingFrameEvents = 4096` 封顶丢最旧拍）。单测：`WireSnapshotFrameEventsTests`（省略/ camelCase / 旧 JSON 兼容）。

### 4.3 乱序 / 丢帧

**客户端缺口检测**：跟踪 `lastAppliedFrameId`；收到快照的 `tickId` 与期望不符（`tickId > lastAppliedFrameId + 1`）→ 判定中间有丢帧。实现见 `Chorus.LoadTool.FramePlayer`（`FramePlayerTests` 覆盖：连续/重复/回放丢帧/缺口上报）。

**补帧策略**：

| 期次 | 策略 | 说明 |
| --- | --- | --- |
| P1（无历史） | 等下一个周期快照 / 触发强制 resync | **无法补历史帧**——引擎没有历史（G2）。已有机制：周期快照（`SessionActor.cs:447`）+ 强制 resync（`:550-569`）。诚实标注：丢帧 = 跳帧，客户端直接对齐到最新帧。 |
| P2（已实现） | 引擎帧历史 ring buffer + 客户端拉取 | 见下。单测 + e2e 覆盖（`FrameHistoryTests` / `SessionActorFrameHistoryTests` / `WireSnapshotFrameEventsTests`）。 |

**P2 补帧实现（已落地）**：

- 引擎侧：per-session 帧历史 ring buffer（内存），`src/Chorus.Engine/Session/FrameHistory.cs`。
- **按字节上限而非帧数上限**：N 拍 × 平均快照字节不可控，最坏 `MaxSnapshotBytes`（1 MiB，`SessionConfig.cs:21`）× N 会爆内存，故按字节封顶——`Session:FrameHistoryBytes`（默认 8 MiB/session），超顶 evict 最旧，**但不 evict 刚加入的那帧**（`Add` 的驱逐条件排除最新项）；单帧超过整容量时保留为该 session 的唯一帧。
  - 内存估算（未实测）：8 MiB/session × `MaxSessions`（200，`SessionConfig.cs:17`）= 最坏 1.6 GiB。**需实测快照平均字节数后定上限**；`FrameHistoryBytes = 0` 可整体关闭（窗口查询恒 miss）。
- 客户端侧：新增 `resync` 消息类型（client→server），带 `fromFrameId`；`ProtocolTypes.Resync = "resync"`、`ClientResync` 模型。
  - 依据 `versioning.md:60-65`：线协议新增消息类型 = 允许。
  - 引擎回吐 `[fromFrameId, 当前 tickId]` 窗口内的快照序列；窗口查询拒绝**部分回放**（`fromFrameId` 早于最旧帧 → miss，因为从保留尾部起回放会隐藏缺口）。
  - 窗口外的缺口（已 evict / 历史关闭）→ 回吐最新快照 + `forced=true` + `reason="resync-gap"`，客户端对齐到最新。
- **回放走直发通道**：`OutboundClientQueue.SendReplayAsync` 绕过容量 1 的 latest-wins slot（DropOldest 会把回放帧挤掉只剩最后一帧），与周期发送用 `_sendGate` 逐帧串行、整批用 `_replayGate` 互斥；回放 detach 执行，慢传输不拖 tick。周期快照仍可能被丢——**回放帧不会**。
- 诊断计数：`TotalResyncRequests` / `TotalResyncReplays` / `TotalResyncWindowMisses` / `TotalResyncThrottled` / `TotalResyncSnapshotsSent`、`FrameHistoryBytes`。
- **传输层全链路**：`StreamerWsFace`（`resync` 分支 + 固定窗口限流）→ `IStreamerSessionBinding.ResyncAsync` → 单体 `MonolithSessionRouter`（直调 `SessionActor`）/ 网关 `IpcSessionRouter`（`GatewayIpcService.PublishResyncAsync` → `gw.resync` → `GatewayResyncCmd` → `EngineIpcService` → `SessionActor.ResyncAsync`）。

**幂等与去重**：

- 事件级去重已有：`EventDeduplicator`（`Program.cs:73-77`，`DedupTtlSeconds` 默认 600）。
- 快照级幂等：客户端按 `lastAppliedFrameId` 去重——帧号 ≤ 已应用的快照直接丢弃（`protocol.md:121` 已把 `lastAppliedTickId` 定义为「已真正应用完成的权威 tick」）。
- 补帧窗口内重复快照：同一 `tickId` 只应用一次（按帧号幂等）。

**背压策略（已实现）**：

- 有界事件通道：`MaxPendingEvents`（`SessionConfig.cs:11`，默认 8000）。
- 有界等待：礼物等 must-not-lose 事件在通道满时额外等 `EventEnqueueMaxWaitMs`（`SessionConfig.cs:13`，默认 50ms），超时才丢。
- 每 tick 事件上限：`MaxEventsPerTick`（`SessionConfig.cs:7`，默认 200）。
- 补帧请求的背压（P2，已实现，两层）：
  - 传输层固定窗口：`Faces:StreamerWs:ResyncPerMinute`（默认 30/min），超限直接丢弃并计入 `WsResyncDropped`。
  - 引擎层 per-client 冷却：`Session:ResyncRequestCooldownMs`（默认 1000ms），冷却内的重复请求计入 `TotalResyncThrottled` 后直接返回。**与服务端推送 lag resync 的 `ResyncCooldownMs`（2000ms）互不复用**——前者是客户端主动拉，后者是引擎按 ACK 判断推送。

### 4.4 重连 / 回放

**重连（按帧号续传）**：

- 客户端重连 → 发 `hello` → 引擎以**当前 tickId** 为基线（`SessionActor.cs:314-315`：`ConnectedTickId = TickId`、`LastAckTickId = TickId`）。
- **续传 = 从最新帧开始**，不补历史（无历史可补，G2）。这与 Twitch `reconnect_url` 的「旧订阅继续投递、新连接续传」语义一致（[Twitch docs](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/)），但 chorus 没有跨连接的订阅状态——重连就是新基线。
- **P2 已落地**：重连后客户端可直接发 `resync { fromFrameId }` 拉取窗口内历史帧；窗口外（已 evict）回吐最新快照 + `forced=true`/`reason="resync-gap"` 对齐到最新。重连仍以**当前 tickId** 为基线，resync 是可选补帧、不改变基线语义。

**回放 / 录播对齐**：

- **明确不做**。无持久化（`versioning.md:72-74`），快照不落盘，没有帧序列可对齐。
- 若未来要回放，前置依赖：先落 snapshot-restore 契约（`docs/design/snapshot-restore.md`），再谈按帧对齐。
- 录播回放对齐的业界做法（Twitch `reconnect_url`、YouTube 轮询窗口）都是**直播续传**，不是**录播对齐**；录播对齐在弹幕游戏里**未找到公开资料**。

### 4.5 客户端节拍

**渲染帧率 vs tick 周期**：

- 渲染 60fps（16.67ms/帧）vs tick 66ms（≈15.15 tick/s）→ **1 tick ≈ 3.96 渲染帧**。
- 渲染层在帧 F-1 与 F 之间插值（未实测，仓内无客户端 SDK，G5/G6）。

**追帧策略**：

| 情形 | 策略 | 依据 |
| --- | --- | --- |
| 落后 ≤ `maxLagTicks`（50） | 受控追赶：加快渲染/插值节奏 | `protocol.md:135`「轻度 lag 时，可做受控追赶」 |
| 落后 > `maxLagTicks` | 等强制 resync 快照，立即对齐 | `SessionActor.cs:550-569` + `protocol.md:104-105`「收到强制快照后，不应继续长时间补历史帧」 |
| 超前 | 不渲染超过最后两帧 | `protocol.md:136`「重度 lag 时，应等待或请求最新快照」 |

**未实测**：以上策略在仓内**没有客户端实现可验证**（G5/G6）。P3 落 SDK 后需实测tick 周期与渲染帧率的实际偏差。

---

## 5. 分期与验收口径

| 期次 | 内容 | 验收口径 |
| --- | --- | --- |
| **P1** | 帧号语义文档化 + 客户端按帧号回放状态机 | **已完成**：帧号=tickId 文档化（§4.1）；客户端回放状态机 `FramePlayer` 落地并被 `LoadTool` 使用，`FramePlayerTests` 7 例（连续/重复/回放丢帧/缺口上报）；真实 /ws e2e 端到端冒烟通过。`state.frameEvents` 约定已废弃（被 P2 信封字段取代） |
| **P2** | 帧历史 ring buffer + `resync` 拉取消息 + 快照 `frameEvents` 信封字段 | **已完成**：单测 `FrameHistoryTests`（7：补帧窗口 / 逐字节驱逐 / 超量帧保留 / 零容量关闭）+ `SessionActorFrameHistoryTests`（6：窗口命中回放 / 窗口 miss → `resync-gap` / 冷却节流 / 未知 client / `frameEvents` 带应用帧号 / 无事件省略）+ `WireSnapshotFrameEventsTests`（5：wire 省略 / camelCase / 旧 JSON 兼容 / `ClientResync` 反序列化 / 真实 /ws resync 补帧 e2e）；全套 260 绿 |
| **P3** | 客户端 SDK（入口 + 一个回调） | 新项目 N 行代码内接入；回调收 state；构建/测试绿 |

---

## 6. 相关页面

- [协议参考](../reference/protocol.md) — `snapshot` / `ack` / `hello` / `welcome` 消息
- [版本策略](../reference/versioning.md) — `state` schema 版本 `v`、兼容性判定规则
- [配置参考](../reference/config.md) — `Session:*` 参数（`tickDurationMs` / `maxLagTicks` 等）
- [快速接入帧同步](../guide/quickstart-frame-sync.md) — N 行代码接入
- [接口参考](../reference/api.md) — 后台/管理端接口
