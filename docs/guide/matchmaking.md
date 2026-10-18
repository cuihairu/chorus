---
title: Matchmaking (Matchmaker)
---


# Matchmaking (Matchmaker)

Encapsulation status: **encapsulated at the library level** (`Chorus.Engine.Matchmaking`), **host wiring not built in** —
this repo has no join endpoint and no match-start routing; the caller must drive it. For boundaries and reasons see
[Boundaries at the end of this page](#boundaries-what-it-doesnt-do).

Capability positioning: it carves the "who queues up, is there enough headcount, should bots fill the gap" segment out of gameplay code,
with time injected by the caller (no wall clock reads), so the entire segment of logic can be deterministically unit-tested.

## Minimal Example

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

## Parameters and Returns

| Type / Member | Signature Highlights | Source |
|---|---|---|
| `MatchmakingPolicy` | `(mode, minParticipants, maxParticipants, fillMode, waitTimeoutMs)`; validated at construction, invalid input throws `ArgumentException` directly | `Matchmaker.cs:17-65` |
| `MatchmakingFillMode` | `WaitForRealPlayers` (wait for real players, bots fill after timeout) / `FillWithBotsImmediately` (fill with bots and start as soon as anyone is there) | `Matchmaker.cs:4-11` |
| `Matchmaker.Join` | `(requestId, playerId, nowMs) → bool`; returns false if already in the queue (no silent deduplication) | `Matchmaker.cs:147` |
| `Matchmaker.Leave` | `(playerId) → bool`; returns false if not in the queue | `Matchmaker.cs:172` |
| `Matchmaker.TryFormMatch` | `(nowMs) → MatchmakingResult`; real players FIFO up to `maxParticipants`, insufficient headcount decided per fill mode | `Matchmaker.cs:191` |
| `Matchmaker.PendingCount` | current number of real players queued | `Matchmaker.cs:132` |
| `IBotParticipantFactory` | `Create(count, requestId, nowMs) → MatchParticipant[]` | `Matchmaker.cs:95-99` |
| `MatchParticipant` | `(ParticipantId, IsBot, PlayerId?, BotId?)`; real players and bots share the same ParticipantId namespace | `Matchmaker.cs:75-79` |
| `MatchmakingResult` | `(Started, FilledWithBots, Participants)`; no-start reuses `NotStarted` (`Participants` empty) | `Matchmaker.cs:82-88` |

## Errors and Exceptions

| Case | Behavior | Source |
|---|---|---|
| `mode` blank / `min<1` / `max<min` / wait-for-real-players mode missing `waitTimeoutMs` | `ArgumentException` (at construction) | `Matchmaker.cs:26-45` |
| `Join`/`Leave` empty id | `ArgumentException` | `Matchmaker.cs:150`、`:175` |
| same `playerId` queued twice | `Join` returns `false` | `Matchmaker.cs:161-163` |
| insufficient headcount and bot fill not triggered | returns `MatchmakingResult.NotStarted` | `Matchmaker.cs:217-219` |
| bot factory produces wrong count / missing `ParticipantId` or `BotId` / `ParticipantId` duplicates a real player | `InvalidOperationException` (refuses to form the match; won't start with incomplete participants) | `Matchmaker.cs:237-257` |

Match-forming rule details: real players FIFO, at most `maxParticipants` per match, the overflow stays in the queue for the next match
(`Matchmaker.cs:200-211`); bots only fill up to `minParticipants`, not `max` (`:222`).

## How Bots Get Moving

`Matchmaker` is only responsible for forming matches. Making bots act in the game is the job of two other types:

- `IBotStrategy` / `BotWorldView` / `BotStrategyConfig` (`src/Chorus.Engine/BotStrategy.cs:66-78`, `:56`, `:6`)
  — computes a `BotAction` from the world view each tick.
- `BotRuntime` (`src/Chorus.Engine/BotRuntime.cs:158`) — pulls strategy output each tick, submits it to the session via the bounded queue
  `BotActionQueue` (`:61`), with `Drain`/`DrainUpTo` controlling the consumption pace (`:166`, `:180`);
  the built-in factory `DefaultBotParticipantFactory` is at `:8`.

## Boundaries (What It Doesn't Do)

- **No join / match-start endpoint, and no match-start routing**: none of this repo's hosts (Server/Gateway/EngineHost) register a
  `Matchmaker`, there are no DI bindings, and there is no "queue → session creation" wiring. The reason is that this part needs product semantics
  (who can enqueue, which mode a match starts with, which session/platform binding owns the room after the match starts), and these semantics are
  pending along with phase-three multi-worker routing (`todo.md:156`); the in-repo design documents likewise mark matchmaking and bots as follow-up items
  ([Overall Architecture](../design/overall-architecture.md) §6). The library can be finalized first; host wiring waits for the semantics to be settled.
- **No skill rating**: no elo/MMR/divisions, only "headcount + mode + wait strategy".
- **Does not create sessions/rooms**: how to map participants to a session after obtaining a `MatchmakingResult` is the caller's responsibility
  (the class comment likewise makes clear this responsibility is external, `Matchmaker.cs:113-114`).
- **Does not manage its own time**: all `nowMs` values are passed in by the caller; `Matchmaker` reads no wall clock.
- **`GET /matches` is not the matchmaking queue**: `MatchRegistry` is only a match-level **diagnostic aggregate**
  (registered at `src/Chorus.Server/Program.cs:21`, exposed at `:144`), faction counts are always 0, and it only exists in the single-process mode
  (the split mode has no such endpoint); it has nothing to do with queuing/match-forming.

## Related Pages

- [Overall Architecture](../design/overall-architecture.md) §6 Matchmaking and Bots
- [Battle](./battle.md) (how sessions and snapshots go after a match starts)
- [Architecture Overview](../architecture.md) (session authority and multi-worker boundaries)
