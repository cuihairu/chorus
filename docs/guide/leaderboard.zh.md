---
title: 排行榜
---


<p align="center">
  <a href="./leaderboard.md">English</a> | 简体中文
</p>

# 排行榜

封装状态：**本次接入**（2026-10-15）。存储 `LeaderboardStore` 早已在库里（`src/Chorus.Data/Leaderboard.cs:40`），
但此前无 DI 注册、无端点、无调用方；本轮把它接进两个会话宿主（Server / EngineHost），
并让参考玩法（`Chorus.SampleGame`）成为第一个权威写分方。测试：`tests/Chorus.Engine.Tests/LeaderboardEndpointTests.cs`（8 例）。

## 数据模型

四种作用域（`Leaderboard.cs:10-16`），`scopeId` 是榜键：

| scope | scopeId | 典型来源 |
|---|---|---|
| `room` | 会话 id | 引擎侧模块逐 tick 投影（本页「写分」） |
| `match` | match id | 单局结算（同上，由玩法决定投影时机） |
| `period` | 周期标签（如 `day:2026-10-15`） | 异步聚合方（未接线，见边界） |
| `global` | 固定键 | 异步聚合方（未接线，见边界） |

排序：分数降序、同分按参与者 id 字典序（结果不抖动），并列共享竞赛名次（1,2,2,4），
实现在 `Leaderboard.cs:137-157`。`Freeze`（`:104`）冻结即封榜：之后一切写入被拒绝（fail-visible），
查询恒返回冻结时刻名次；`Query`（`:124`）返回独立快照，未知榜返回空列表（不是错误）。

## 写分：引擎侧唯一写入口

设计规则（`Leaderboard.cs:21-38` 类注释，审计 §9.2）：**只有引擎侧能写分，客户端不能自报**。
落到代码是一条缝：

```
IGameModule.ApplyEvents（游戏规则，权威状态）
    → ISessionScoreSink.ProjectScore(sessionId, participantId, score)   ← 模块只推"当前权威总分"
    → LeaderboardScoreSink → LeaderboardStore.UpsertScore(Room, sessionId, participantId, score)
```

- 缝接口：`src/Chorus.Engine/Abstractions/ISessionScoreSink.cs:11`（单方法 `ProjectScore`，`:17`；
  在 tick 路径上调用，实现方不得阻塞、不得抛）。
- 宿主实现：`src/Chorus.Gateway.Core/Leaderboard/LeaderboardHost.cs:16` ——把分数替换式写进
  room 榜。**替换语义**是关键：同参与者再投影是覆盖不是累加，天然幂等，没有重复加分问题。
- 宿主注册（DI 单例）：`src/Chorus.Server/Program.cs:26-27`、`src/Chorus.EngineHost/Program.cs:14-15`。
- 参考写法：`src/Chorus.SampleGame/SampleGameModule.cs` ——每 tick 只投影本批**被触达**的参与者
  （`:40`），规则为 `likes + gifts + comments`（`:89`，"谁点燃了房间"而不是礼物金额）；
  会话从快照恢复时重投影全部参与者重建 room 榜（`:205`）。

自己写玩法时实现同样的模式即可：

```csharp
// 模块构造（经 IGameModuleFactory 注入 sink；sink 为 null 时零开销、不投影）
public MyGameModule(string sessionId, ISessionScoreSink? scores)
    => _scores = scores;

// ApplyEvents 内：参与者权威分变化后推"当前总分"（替换，不是增量）
_scores?.ProjectScore(sessionId, userId, authoritativeTotal);
```

## 读分：只读查询端点

`GET /leaderboard/{scope}/{scopeId}?top=N`，由两个**持有会话**的宿主暴露
（`src/Chorus.Server/Program.cs:145`、`src/Chorus.EngineHost/Program.cs:53`；
网关不持有会话状态，不设此端点）：

```bash
curl http://localhost:5083/leaderboard/room/s1?top=10
```

| 参数 | 含义 | 约束 |
|---|---|---|
| `scope` | `room` / `match` / `period` / `global`（大小写不敏感） | 未知值 → 400 `unknown_scope` |
| `scopeId` | 榜键（room 榜 = 会话 id；period 榜 = 周期标签） | 单路径段（不能含 `/`）；空白 → 400 `invalid_scope_id` |
| `top` | 前 N 名，缺省返回全量 | 1–1000（`LeaderboardHost.cs:38`），越界 → 400 `invalid_top` |

响应（200）：

```json
{
  "scope": "room",
  "scopeId": "s1",
  "frozen": false,
  "entries": [
    { "participantId": "u1", "score": 42, "rank": 1 },
    { "participantId": "u2", "score": 42, "rank": 1 },
    { "participantId": "u3", "score": 7,  "rank": 3 }
  ]
}
```

| 状态码 | 条件 |
|---|---|
| 200 | 查询成功（从未写过的榜返回空 `entries`，`frozen:false`） |
| 400 `{error:"unknown_scope"}` | scope 不在四种之内 |
| 400 `{error:"invalid_scope_id"}` | scopeId 空白 |
| 400 `{error:"invalid_top",top,max}` | `top` 越界 |

端点实现：`src/Chorus.Gateway.Core/Leaderboard/LeaderboardHost.cs:48`（路由）、`:53`（处理）。
端点无鉴权，与其他诊断面同属内网假设（见 [接口参考](../reference/api.zh.md) 鉴权一节）。

## 库级直用（不经 HTTP）

`LeaderboardStore` 是线程安全的纯内存库，可直接引用 `Chorus.Data` 使用：
`UpsertScore`（`:55`，替换）/ `AddDelta`（`:75`，幂等增量）/ `Freeze`（`:104`）/
`IsFrozen`（`:113`）/ `Query`（`:124`）。

```csharp
using Chorus.Data;

var store = new LeaderboardStore();
store.UpsertScore(LeaderboardScope.Room, "s1", "u1", 100);   // 替换式投影（room/match 榜）
store.AddDelta(LeaderboardScope.Global, "all", "u1", 10,     // 幂等增量（period/global 榜）
    sourceId: "cmd-17");                                     // 同 sourceId 重放不重复加分
store.Freeze(LeaderboardScope.Room, "s1");                   // 结算封榜

foreach (var entry in store.Query(LeaderboardScope.Room, "s1", top: 10))
{
    Console.WriteLine($"{entry.Rank}. {entry.ParticipantId} = {entry.Score}");
}
```

两个 API 的选择规则（类注释 §9.2）：**权威状态投影用 `UpsertScore`**（再投影即覆盖）；
**重放流用 `AddDelta`**（必须给 sourceId 作幂等锚点，先占锚点再改分，`:91`）。

## 边界（不做什么）

- **内存态，无持久化**：榜随进程存在，重启即清空。room 榜会由会话恢复路径重建
  （`SampleGameModule.cs:205`）；period/global 榜重启即失——需要跨重启结算的产品要自行落地。
- **客户端不能写分**：写入口只有引擎侧 `ISessionScoreSink`；HTTP 面只读。自报分数在这个设计里不存在。
- **period/global 聚合未接线**：`AddDelta` 及其 sourceId 台账为异步聚合方（设计上的 Data Worker）准备，
  本仓没有调用方；`DataCommandOutbox` 同样是交接缓冲而非持久化。
- **`AddDelta` 台账无界**：每榜每 sourceId 一条去重记录（`Leaderboard.cs:45`），长周期高频源会持续增长内存。
  实时投影一律走 `UpsertScore`；用 `AddDelta` 的聚合方要自己管理周期换 key / 台账规模。
- **平台原生榜未接线**：抖音榜单上报适配器（`DouyinRankUploadAdapter`、`PlatformLeaderboardRegistry`）
  在库里，无宿主接线，见 [抖音小游戏平台参考](../reference/platform-douyin-mini-game.zh.md)。
- **网关不设此端点**：拆分模式直连 EngineHost 查询；榜单与会话同生命周期地留在引擎侧。

## 相关页面

- [架构说明](../architecture.zh.md) 决策 8（替换式投影 vs 实时聚合）
- [对战](./battle.zh.md)（分数从哪来：观众事件 → tick → 模块）
- [接口参考](../reference/api.zh.md)（端点总表）
