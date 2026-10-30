---
title: 匹配（Matchmaker）
---


# 匹配（Matchmaker）

封装状态：**库级已封装**（`Chorus.Engine.Matchmaking`），**未内建宿主接线**——
本仓没有加入端点、也没有开局路由；由调用方驱动。边界与原因见
[本页末尾的边界](#边界不做什么)。

能力定位：把"谁在排队、人数够不够、要不要机器人补位"这一段从玩法代码里切出来，
时间由调用方注入（不读墙钟），因此整段逻辑可以确定性单测。

## 最小示例

```csharp
using Chorus.Engine.Matchmaking;

// 1) 策略：模式、人数区间、机器人填充方式
var policy = new MatchmakingPolicy(
    mode: "1v1",
    minParticipants: 2,
    maxParticipants: 4,
    fillMode: MatchmakingFillMode.WaitForRealPlayers,   // 或 FillWithBotsImmediately
    waitTimeoutMs: 30_000);                              // 等待真人模式必填

// 2) 机器人工厂：数量不足时由它补齐（身份必须唯一，见下表校验）
IBotParticipantFactory bots = new CountingBotFactory("bot");

// 3) 驱动：每次有人加入或到达超时后尝试组局
var matcher = new Matchmaker(policy, bots);
long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

matcher.Join(requestId: "r-1", playerId: "p-1", nowMs);
matcher.Join(requestId: "r-2", playerId: "p-2", nowMs);

MatchmakingResult result = matcher.TryFormMatch(nowMs);

if (result.Started)
{
    foreach (var p in result.Participants)
    {
        Console.WriteLine($"{p.ParticipantId} bot={p.IsBot}");
    }
}
```

```csharp
/// <summary>示例机器人工厂（真实实现见 Chorus.Engine.BotRuntime 的 DefaultBotParticipantFactory）。</summary>
internal sealed class CountingBotFactory : IBotParticipantFactory
{
    private readonly string _prefix;
    public CountingBotFactory(string prefix) => _prefix = prefix;

    public IReadOnlyList<MatchParticipant> Create(int count, string requestId, long nowMs)
        => Enumerable.Range(0, count)
            .Select(i => new MatchParticipant(
                ParticipantId: $"{_prefix}-{requestId}-{i}",
                IsBot: true,
                PlayerId: null,
                BotId: $"{_prefix}-{requestId}-{i}"))
            .ToArray();
}
```

## 参数与返回

| 类型/成员 | 签名要点 | 出处 |
|---|---|---|
| `MatchmakingPolicy` | `(mode, minParticipants, maxParticipants, fillMode, waitTimeoutMs)`；构造即校验，非法输入直接抛 `ArgumentException` | `Matchmaker.cs:17-65` |
| `MatchmakingFillMode` | `WaitForRealPlayers`（等真人，超时后机器人补）/ `FillWithBotsImmediately`（有人就立即机器人补齐开局） | `Matchmaker.cs:4-11` |
| `Matchmaker.Join` | `(requestId, playerId, nowMs) → bool`；已在队列返回 false（不静默去重） | `Matchmaker.cs:147` |
| `Matchmaker.Leave` | `(playerId) → bool`；不在队列返回 false | `Matchmaker.cs:172` |
| `Matchmaker.TryFormMatch` | `(nowMs) → MatchmakingResult`；真人 FIFO 最多 `maxParticipants`，人数不足按填充模式定 | `Matchmaker.cs:191` |
| `Matchmaker.PendingCount` | 当前排队真人数 | `Matchmaker.cs:132` |
| `IBotParticipantFactory` | `Create(count, requestId, nowMs) → MatchParticipant[]` | `Matchmaker.cs:95-99` |
| `MatchParticipant` | `(ParticipantId, IsBot, PlayerId?, BotId?)`；真人与机器人共用同一 ParticipantId 命名空间 | `Matchmaker.cs:75-79` |
| `MatchmakingResult` | `(Started, FilledWithBots, Participants)`；未开局复用 `NotStarted`（`Participants` 为空） | `Matchmaker.cs:82-88` |

## 错误与异常

| 情形 | 行为 | 出处 |
|---|---|---|
| `mode` 空白 / `min<1` / `max<min` / 等真人模式缺 `waitTimeoutMs` | `ArgumentException`（构造时） | `Matchmaker.cs:26-45` |
| `Join`/`Leave` 空 id | `ArgumentException` | `Matchmaker.cs:150`、`:175` |
| 同一 `playerId` 排两次 | `Join` 返回 `false` | `Matchmaker.cs:161-163` |
| 人数不足且未触发机器人补位 | 返回 `MatchmakingResult.NotStarted` | `Matchmaker.cs:217-219` |
| 机器人工厂产出数量不对 / 缺 `ParticipantId` 或 `BotId` / `ParticipantId` 与真人重复 | `InvalidOperationException`（拒绝组局；绝不带残缺参与者开局） | `Matchmaker.cs:237-257` |

组局规则细节：真人 FIFO、每局至多 `maxParticipants`，溢出的留在队列等下一局
（`Matchmaker.cs:200-211`）；机器人只补到 `minParticipants`、不补到 `max`（`:222`）。

## 机器人怎么动起来

`Matchmaker` 只负责组局。让机器人在对局里行动是另外两个类型的职责：

- `IBotStrategy` / `BotWorldView` / `BotStrategyConfig`（`src/Chorus.Engine/BotStrategy.cs:66-78`、`:56`、`:6`）
  —— 每 tick 从世界视图算出一个 `BotAction`。
- `BotRuntime`（`src/Chorus.Engine/BotRuntime.cs:158`）—— 每 tick 拉取策略输出，经有界队列
  `BotActionQueue`（`:61`）提交进会话，`Drain`/`DrainUpTo` 控制消费节奏（`:166`、`:180`）；
  内建工厂 `DefaultBotParticipantFactory` 在 `:8`。

## 边界（不做什么）

- **本 `Matchmaker` 库没有宿主加入端点、也没有开局路由**：它是进程内组局助手。另有
  `Chorus.ControlHost` 暴露带鉴权的主播队列并做持久控制面匹配；该 API 按段位分层与
  版本化的模式/区服/平台规则分组，并记录对局结果。它在 `AwaitingReady` 状态分配房间；调用方用其授权
  管理会话读 `GET /api/matches`，再把参与者经 `/ws` 接到返回的 `roomIds[0]`。
  开局同时要求主播连接到位与 Worker 已提交检查点。没有自动
  WebSocket 重定向、也没有平台会话重绑定；这些输入须由调用方显式路由。
- **本库没有技能评分**：它的平衡只基于人数、模式与等待策略。评分与分层规则属于独立的
  ControlHost 匹配器（`src/Chorus.Control/MatchControlStore.cs`）。
- **不创建会话/房间**：拿到 `MatchmakingResult` 后如何把参与者映射到会话是调用方的职责
  （类注释同样明确该职责在外部，`Matchmaker.cs:113-114`）。
- **不自管时间**：所有 `nowMs` 都由调用方传入；`Matchmaker` 不读墙钟。
- **`GET /matches` 不是匹配队列**：`MatchRegistry` 只是对局级**诊断聚合**
  （`src/Chorus.Server/Program.cs:21` 注册、`:144` 暴露），阵营计数恒为 0，且只在单进程模式存在
  （拆分模式无此端点）；与排队/组局无关。

## 相关页面

- [总体架构](../design/overall-architecture.md) §6 匹配与机器人
- [对战](./battle.md)（开局后会话与快照如何走）
- [架构总览](../architecture.md)（会话权威与多 worker 边界）

## 控制面准入与结算

新控制面匹配从 `AwaitingReady` 起步，而非 `Running`。一秒一次的维护循环
只在双参与者都连入房间、owner 租约有效、且存在持久 tick 时开局。
准入 120 秒后过期。`POST /api/matches/{id}/cancel`
需要 `operate` 权限且只能取消待开局匹配。取消会把仍在退出房外在线的参与者
重新入队，保留其原排队时长。离线参与者须重连后重新加入。被取消的提案不触发对手冷却。

`finish` 拒绝未开局/已取消的匹配。结算按匹配与获胜者幂等；
冲突的获胜者被拒绝。已完成匹配的对手冷却从结算时起算。
退役（retirement）阻断新输入与新连接，经 Gateway 对账断开客户端，
并等已接受输入与连接租约排空后再关闭/销毁房间。
它绝不为了释放容量而丢弃待处理的持久输入。

控制面 `FillBots=true` 在游戏运行时机器人适配器接入之前被拒绝。
进程内独立的 `Matchmaker` 机器人工厂仍受支持。存量机器人设置
造不出虚构的机器人对局。FIFO/就近评分策略、段位边界与固定加分
不变；这不是最优基数或 Elo 匹配器。

新榜单幂等回执与分数更新同事务存入 `board_receipts(board_id, source_id)`。
旧的内嵌 `Sources` 仍被识别。新结算没有 10 万来源的硬停；回执保留需要单独的生命周期策略。
