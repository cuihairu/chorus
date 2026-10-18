---
title: 匹配（Matchmaker）
---

# 匹配（Matchmaker）

封装状态：**库级已封装**（`Chorus.Engine.Matchmaking`），**宿主接线未内置**——
本仓没有 join 端点、没有开局路由，调用方需要自己驱动。边界与原因见
[本文末「边界」](#边界不做什么)。

能力定位：把"谁排进队列、够不够人数、要不要机器人补齐"这一段从玩法代码里剥出来，
时间由调用方注入（不读墙钟），所以整段逻辑可以确定性单测。

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

| 类型 / 成员 | 签名要点 | 出处 |
|---|---|---|
| `MatchmakingPolicy` | `(mode, minParticipants, maxParticipants, fillMode, waitTimeoutMs)`；构造期校验，不合规直接 `ArgumentException` | `Matchmaker.cs:17-65` |
| `MatchmakingFillMode` | `WaitForRealPlayers`（等真人，超时后机器人补齐）/ `FillWithBotsImmediately`（有人即补机器人开局） | `Matchmaker.cs:4-11` |
| `Matchmaker.Join` | `(requestId, playerId, nowMs) → bool`；已在队列则返回 false（不静默去重） | `Matchmaker.cs:147` |
| `Matchmaker.Leave` | `(playerId) → bool`；不在队列返回 false | `Matchmaker.cs:172` |
| `Matchmaker.TryFormMatch` | `(nowMs) → MatchmakingResult`；真实人数 FIFO 至多 `maxParticipants`，不足按 fill mode 决策 | `Matchmaker.cs:191` |
| `Matchmaker.PendingCount` | 当前排队真人数 | `Matchmaker.cs:132` |
| `IBotParticipantFactory` | `Create(count, requestId, nowMs) → MatchParticipant[]` | `Matchmaker.cs:95-99` |
| `MatchParticipant` | `(ParticipantId, IsBot, PlayerId?, BotId?)`；真人与机器人同一 ParticipantId 命名空间 | `Matchmaker.cs:75-79` |
| `MatchmakingResult` | `(Started, FilledWithBots, Participants)`；未开局复用 `NotStarted`（`Participants` 为空） | `Matchmaker.cs:82-88` |

## 错误与异常

| 情况 | 行为 | 出处 |
|---|---|---|
| `mode` 空白 / `min<1` / `max<min` / 等待真人模式缺 `waitTimeoutMs` | `ArgumentException`（构造期） | `Matchmaker.cs:26-45` |
| `Join`/`Leave` 空 id | `ArgumentException` | `Matchmaker.cs:150`、`:175` |
| 同一 `playerId` 重复排队 | `Join` 返回 `false` | `Matchmaker.cs:161-163` |
| 人数不足且未触发机器人填充 | 返回 `MatchmakingResult.NotStarted` | `Matchmaker.cs:217-219` |
| 机器人工厂产出数量不符 / 缺 `ParticipantId` 或 `BotId` / `ParticipantId` 与真人重复 | `InvalidOperationException`（拒绝组局，不拿残缺参与者开局） | `Matchmaker.cs:237-257` |

组局规则细节：真人 FIFO、一局最多 `maxParticipants`、超额者留队列等下一局
（`Matchmaker.cs:200-211`）；机器人只补到 `minParticipants`，不是 `max`（`:222`）。

## 机器人怎么动起来

`Matchmaker` 只负责组局。让机器人在游戏里行动是另外两个类型：

- `IBotStrategy` / `BotWorldView` / `BotStrategyConfig`（`src/Chorus.Engine/BotStrategy.cs:66-78`、`:56`、`:6`）
  ——按 tick 从世界视图算出 `BotAction`。
- `BotRuntime`（`src/Chorus.Engine/BotRuntime.cs:158`）——按 tick 拉取策略输出、经有界队列
  `BotActionQueue`（`:61`）提交给会话，`Drain`/`DrainUpTo` 控制消费节奏（`:166`、`:180`）；
  内置工厂 `DefaultBotParticipantFactory` 在 `:8`。

## 边界（不做什么）

- **本页的 `Chorus.Engine.Matchmaker` 库没有 join / 开局 HTTP 端点，也不负责创建会话**：它需要调用方驱动。
  另有独立的 ControlHost 主播排队与匹配 API（`POST /api/streamers/{id}/join`、`GET /api/queue`、
  `GET /api/matches`），由持久化控制面按评分与层级规则组局。该 API 不等同于本页介绍的进程内库；
  两者的职责和接口见 [部署参考](../reference/deployment.md) 与 [房间存储架构](../../architecture/room-control-storage.md)。
- **不做技能评估**：没有 elo/MMR/分段，只有"人数 + 模式 + 等待策略"。
- **不创建会话/房间**：拿到 `MatchmakingResult` 之后如何把参与者映射到会话，由调用方负责
  （类注释同样明确此职责在外部，`Matchmaker.cs:113-114`）。
- **时间不自管**：所有 `nowMs` 由调用方传入，`Matchmaker` 不读墙钟。
- **`GET /matches` 不是匹配队列**：`MatchRegistry` 只是 match 级**诊断聚合**
  （`src/Chorus.Server/Program.cs:21` 注册、`:144` 暴露），阵营计数恒为 0，且只在单进程形态存在
  （拆分形态无此端点），与排队/组局无关。

## 相关页面

- [总体架构](../design/overall-architecture.md) §6 匹配与机器人
- [对战](./battle.md)（开局后会话与快照怎么走）
- [架构说明](../architecture.md)（会话权威与多 worker 边界）
