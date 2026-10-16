---
title: Game Module
---

<p align="center">
  English | <a href="game-module.zh.md">简体中文</a>
</p>

# Game Module (Minimal Example)

The game module is the only place in Chorus that writes gameplay: `IGameModule` only receives "a batch of deduplicated, tick-queued audience events"
and outputs "one authoritative state snapshot". Everything else (frame scheduling, ACK, backpressure, snapshot delivery, deduplication) is all handled by the engine.

Related docs:

- [Getting Started](../guide/getting-started.md)
- [Frame Sync Design](../design/frame-sync.md)
- [Configuration Reference](../reference/config.md)

## Contract

```csharp
namespace Chorus.Engine.Abstractions;

public interface IGameModule : IDisposable
{
    string Name { get; }
    void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events);
    object GetStateSnapshot();
}
```

Registration (DI):

```csharp
builder.Services.AddSingleton<IGameModuleFactory, BattleGameModuleFactory>();
```

`IGameModuleFactory.Create(string sessionId)` creates one module instance per Session. With multiple Sessions in the same match, the modules
can share a process-internal presentation-level aggregation store (`MatchStateStore`, see [Spec §2.3](../design/spec.md)).

## In-Repo Reference Implementation

`src/Chorus.SampleGame` provides a runnable reference module (`SampleGameModule` + `SampleGameModuleFactory`),
and both `Chorus.Server` and `Chorus.EngineHost` register it:

```csharp
builder.Services.AddSingleton<IGameModuleFactory, SampleGameModuleFactory>();
```

It demonstrates the two things this document emphasizes:

- **Presentation-level aggregation sharing**: `MatchStateStore` uses `matchId` as the key to aggregate the `(sessionId, factionId)` of multiple Sessions,
  i.e. the "presentation aggregation is non-authoritative" of [Spec §2.3](../design/spec.md) — authoritative decisions are still executed by the engine per Session;
- **Bounded snapshots**: `GetStateSnapshot` only returns totals + a top-10 list, not the full user table, to avoid exceeding `MaxSnapshotBytes`.

When integrating a new gameplay, use it as a template and modify it, or create a separate project implementing `IGameModuleFactory`, then replace that one line in
`Chorus.Server/Program.cs` and `Chorus.EngineHost/Program.cs` — `AddSingleton<IGameModuleFactory, SampleGameModuleFactory>()` — with your own factory.
The engine, protocol, and clients all stay untouched.

## Invocation Model (Must Follow)

- `ApplyEvents` is called only from the single thread of that Session's Actor; **no locking, no async, no I/O, no await**.
- What one `ApplyEvents` receives is the deduplicated audience events settled for this tick; just feed them into the reducer/counters directly.
- Exceptions thrown are caught by the engine and counted into `chorus_tick_errors_total`; the Actor survives, but **no snapshot is delivered for this tick**
  (`TickId` has already advanced, and the exception happens before snapshot delivery) — so the module must catch exceptions internally (at least `try/catch` + logging);
  don't let a single dirty event take down the whole tick.
- `GetStateSnapshot` returns an `object` that gets JSON-serialized. **An anonymous object or a DTO both work**, but control the size: snapshots exceeding
  `Session:MaxSnapshotBytes` (default 1MB) are skipped and counted into `chorus_snapshots_oversize_total`, and clients can only fall back to resync.
- State must be bounded: evict along the audience dimension (LRU/window); don't let a `Dictionary<userId, ...>` grow unbounded.

## Minimal Example: Faction PK Scores

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

Registration:

```csharp
builder.Services.AddSingleton<IGameModuleFactory, BattleGameModuleFactory>();
```

Integrators only need to replace this one line; the engine/protocol/clients are all unchanged.

## Unit Test Suggestions

- A batch of gift events accumulates correctly by faction, with duplicate userId counted only once (aggregation grouped by `(S,M,F,U[,GiftId])`, see [Spec §3.6](../design/spec.md)).
- `GetStateSnapshot` has correct fields in the three cases: empty state, red-blue tie, and one-sided lead.
- The module's own `try/catch` fallback: a constructed dirty event with `Kind=Unknown` does not throw.

## Common Antipatterns

| Antipattern | Consequence | Correct Approach |
| --- | --- | --- |
| `ApplyEvents` with `await` HTTP/DB | tick overruns, `chorus_tick_overruns_total` spikes | synchronous in-memory computation; ingest external data into state first |
| `Dictionary<userId,...>` only ever grows | unbounded memory, ending in `MaxSessions`/OOM | bounded LRU / periodic eviction |
| snapshot returns whole-table objects | skipped for exceeding `MaxSnapshotBytes`, client resync storm | return only the fields the client needs |
| depending on event arrival order | the engine only guarantees ordering within a tick, not across ticks | use `tickId` + event id as idempotency keys |
