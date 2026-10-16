---
title: 游戏模块
---


<p align="center">
  <a href="./game-module.md">English</a> | 简体中文
</p>

# 游戏模块（最小示例）

游戏模块是 Chorus 里唯一写玩法的地方：`IGameModule` 只接收「一批已去重、按 tick 排队的观众事件」，输出「一份权威状态快照」。其余（帧调度、ACK、背压、快照下发、去重）全部由引擎负责。

相关文档：

- [快速开始](../guide/getting-started.zh.md)
- [帧同步设计](../design/frame-sync.zh.md)
- [配置参考](../reference/config.zh.md)

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

`IGameModuleFactory.Create(string sessionId)` 为每个 Session 创建一个模块实例。多 Session 同场时，各模块内部可共享一个进程内的展示级聚合仓库（`MatchStateStore`，见 [规格 §2.3](../design/spec.zh.md)）。

## 仓内参考实现

`src/Chorus.SampleGame` 已提供一份可运行的参考模块（`SampleGameModule` + `SampleGameModuleFactory`），
`Chorus.Server` 与 `Chorus.EngineHost` 都注册了它：

```csharp
builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
```

它演示了两件本文档强调的事：

- **展示级聚合共享**：`MatchStateStore` 以 `matchId` 为键把多 Session 的 `(sessionId, factionId)` 聚合起来，
  即 [规格 §2.3](../design/spec.zh.md) 的「展示聚合非权威」——权威判定仍由引擎按 Session 执行；
- **快照有界**：`GetStateSnapshot` 只返回 totals + top-10 列表，不返回全量用户表，避免 `MaxSnapshotBytes` 超限。

接入新玩法时，把它当模板改，或另建工程实现 `IGameModuleFactory`，然后把 `Chorus.Server/Program.cs`
与 `Chorus.EngineHost/Program.cs` 里那一行 `AddSingleton<IGameModuleFactory, SampleGameModuleFactory>()`
换成自己的工厂。引擎、协议、客户端都不用动。

## 调用模型（必须遵守）

- `ApplyEvents` 只从该 Session 的 Actor 单线程调用，**不要加锁、不要 async、不要 I/O、不要 await**。
- 一次 `ApplyEvents` 收到的就是本 tick 结算的、去重后的观众事件；直接进 reducer/计数即可。
- 抛异常会被引擎捕获并计入 `chorus_tick_errors_total`，Actor 存活，但**本 tick 不会下发快照**（`TickId` 已推进，异常发生在快照发送之前）——所以模块内部要自己兜住异常（至少 `try/catch` + 日志），不要让单条脏事件打挂整 tick。
- `GetStateSnapshot` 返回的 `object` 会被 JSON 序列化。**返回匿名对象或 DTO 都行**，但要控制大小：超过 `Session:MaxSnapshotBytes`（默认 1MB）的快照会被跳过并计入 `chorus_snapshots_oversize_total`，客户端只能靠 resync 兜底。
- 状态必须有界：观众维度要做淘汰（LRU/窗口），不要 `Dictionary<userId, ...>` 无界增长。

## 最小示例：阵营 PK 积分

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

接入方只需替换这一行，引擎/协议/客户端全部不变。

## 单元测试建议

- 一批礼物事件按阵营正确累加，重复 userId 不重复计（归并按 `(S,M,F,U[,GiftId])` 分组，见 [规格 §3.6](../design/spec.zh.md)）。
- `GetStateSnapshot` 在空状态、红蓝打平、单边领先三种情况下字段正确。
- 模块自身 `try/catch` 兜底：构造一条 `Kind=Unknown` 的脏事件不抛异常。

## 常见反模式

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| `ApplyEvents` 里 `await` HTTP/DB | tick 超时、`chorus_tick_overruns_total` 飙升 | 同步内存计算；外部数据先ingest 进状态 |
| `Dictionary<userId,...>` 只增不减 | 内存无界，最终被 `MaxSessions`/OOM 收场 | 有界 LRU / 定时淘汰 |
| 快照返回整库对象 | 超 `MaxSnapshotBytes` 被跳过，客户端 resync 风暴 | 只返回客户端需要的字段 |
| 依赖事件到达顺序 | 引擎只保证 tick 内有序，跨 tick 不保证 | 用 `tickId` + 事件 id 做幂等键 |
