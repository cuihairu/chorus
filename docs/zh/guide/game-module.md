---
title: 游戏模块
---


# 游戏模块（最小示例）

游戏模块是 Chorus 中唯一写玩法的地方：`IGameModule` 只接收"一批已去重、已按 tick 排队的观众事件"，
输出"一份权威状态快照"。其余一切（帧调度、ACK、背压、快照下发、去重）都由引擎处理。

相关文档：

- [快速上手](../guide/getting-started.md)
- [帧同步设计](../design/frame-sync.md)
- [配置参考](../reference/config.md)

## 契约

```csharp
namespace Chorus.Engine.Abstractions;

public interface IGameModule : IDisposable
{
    string Name { get; }
    void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events);
    object GetStateSnapshot();
}
```

注册（DI）：

```csharp
builder.Services.AddSingleton<IGameModuleFactory, BattleGameModuleFactory>();
```

`IGameModuleFactory.Create(string sessionId)` 为每个 Session 创建一个模块实例。同一对局的多个 Session，模块间
可以共享一个进程内展示级聚合存储（`MatchStateStore`，见 [Spec §2.3](../design/spec.md)）。

## 仓内参考实现

`src/Chorus.SampleGame` 提供可运行的参考模块（`SampleGameModule` + `SampleGameModuleFactory`），
`Chorus.Server` 与 `Chorus.EngineHost` 都注册了它：

```csharp
builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
```

它演示了本文强调的两件事：

- **展示级聚合共享**：`MatchStateStore` 以 `matchId` 为键聚合多个 Session 的 `(sessionId, factionId)`，
  即 [Spec §2.3](../design/spec.md) 的"展示聚合非权威"——权威判定仍由引擎按 Session 执行；
- **有界快照**：`GetStateSnapshot` 只返回总数 + top-10 列表，不返回全量用户表，避免超过 `MaxSnapshotBytes`。

接入新玩法时，以它为模板改，或另建一个项目实现 `IGameModuleFactory`，然后把
`Chorus.Server/Program.cs` 与 `Chorus.EngineHost/Program.cs` 里的那一行
`AddSingleton<IGameModuleFactory, SampleGameModuleFactory>()` 换成自己的工厂即可。
引擎、协议、客户端都不用动。

## 调用模型（必须遵守）

- `ApplyEvents` 只从该 Session 的 Actor 单线程调用；**不加锁、不异步、不做 I/O、不 await**。
- 一次 `ApplyEvents` 收到的是本 tick 结算的已去重观众事件；直接喂给 reducer/计数器即可。
- 抛出的异常由引擎捕获并计入 `chorus_tick_errors_total`；Actor 存活，但**本 tick 不下发快照**
  （`TickId` 已推进，异常发生在快照下发之前）——因此模块必须内部兜异常（至少 `try/catch` + 记日志）；
  别让一条脏事件打掉整个 tick。
- `GetStateSnapshot` 返回会被 JSON 序列化的 `object`。**匿名对象或 DTO 都行**，但要控制大小：超过
  `Session:MaxSnapshotBytes`（默认 1MB）的快照被跳过并计入 `chorus_snapshots_oversize_total`，客户端只能靠 resync 兜底。
- 状态必须有界：沿观众维度逐出（LRU/窗口）；别让 `Dictionary<userId, ...>` 无界增长。

## 最小示例：阵营 PK 比分

```csharp
using Chorus.Engine.Abstractions;
using Chorus.Protocol.Models;

namespace MyGame;

// 每 tick 结算一批观众事件，输出红蓝比分与领先方。
public sealed class BattleGameModule : IGameModule
{
    private readonly string _sessionId;
    private readonly Dictionary<string, long> _giftScoreByFaction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _likeCountByFaction = new(StringComparer.Ordinal);
    private long _tickId;

    public BattleGameModule(string sessionId)
    {
        _sessionId = sessionId;
    }

    public string Name => "battle";

    public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
    {
        _tickId = tickId;
        foreach (var e in events)
        {
            var faction = e.FactionId ?? "none";
            switch (e.Kind)
            {
                case AudienceEventKind.Gift:
                    _giftScoreByFaction[faction] = _giftScoreByFaction.TryGetValue(faction, out var g)
                        ? g + (e.GiftValue ?? 0)
                        : e.GiftValue ?? 0;
                    break;
                case AudienceEventKind.Like:
                    _likeCountByFaction[faction] = _likeCountByFaction.TryGetValue(faction, out var l) ? l + 1 : 1;
                    break;
                case AudienceEventKind.Comment:
                    // 表现型事件：默认不结算（也可只记展示计数）
                    break;
            }
        }
    }

    public object GetStateSnapshot()
    {
        var red = _giftScoreByFaction.TryGetValue("red", out var r) ? r : 0;
        var blue = _giftScoreByFaction.TryGetValue("blue", out var b) ? b : 0;
        return new
        {
            game = Name,
            sessionId = _sessionId,
            tickId = _tickId,
            score = new { red, blue },
            leader = red == blue ? "tie" : red > blue ? "red" : "blue",
            likes = new
            {
                red = _likeCountByFaction.TryGetValue("red", out var lr) ? lr : 0,
                blue = _likeCountByFaction.TryGetValue("blue", out var lb) ? lb : 0,
            },
        };
    }

    public void Dispose()
    {
    }
}

public sealed class BattleGameModuleFactory : IGameModuleFactory
{
    public IGameModule Create(string sessionId) => new BattleGameModule(sessionId);
}
```

注册：

```csharp
builder.Services.AddSingleton<IGameModuleFactory, BattleGameModuleFactory>();
```

接入方只需替换这一行；引擎/协议/客户端全部不变。

## 单测建议

- 一批礼物事件按阵营正确累计，重复 userId 只计一次（按 `(S,M,F,U[,GiftId])` 分组聚合，见 [Spec §3.6](../design/spec.md)）。
- `GetStateSnapshot` 在空状态、红蓝平局、单方领先三种情形下字段正确。
- 模块自身 `try/catch` 兜底：构造 `Kind=Unknown` 的脏事件不抛。

## 常见反模式

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| `ApplyEvents` 里 `await` HTTP/DB | tick 超时，`chorus_tick_overruns_total` 飙升 | 同步内存计算；外部数据先摄取进状态 |
| `Dictionary<userId,...>` 只增不减 | 内存无界，最终 `MaxSessions`/OOM | 有界 LRU / 周期逐出 |
| 快照返回全表对象 | 超过 `MaxSnapshotBytes` 被跳过，客户端 resync 风暴 | 只返回客户端需要的字段 |
| 依赖事件到达顺序 | 引擎只保证 tick 内有序，不保证跨 tick | 用 `tickId` + 事件 id 作幂等键 |
