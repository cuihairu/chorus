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

- **This `Matchmaker` library has no host join endpoint or room-start routing**: it remains an in-process helper for forming matches. Separately,
  `Chorus.ControlHost` exposes an authenticated streamer queue and runs durable control-plane matchmaking; that API groups by rating tiers and
  versioned mode/region/platform rules, and records match results. It allocates a room in `AwaitingReady`; the caller reads `GET /api/matches` using its authorized
  management session, then connects participants to the returned `roomIds[0]` via `/ws`.
  Match start requires both streamer connections and a committed Worker checkpoint. There is no
  automatic WebSocket redirect or platform-session rebinding; callers must route those inputs explicitly.
- **This library has no skill rating**: its balancing is based on headcount, mode and wait strategy. Rating and tier rules belong to the separate
  ControlHost matcher (`src/Chorus.Control/MatchControlStore.cs`).
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

## Control-plane admission and settlement

New control matches start in `AwaitingReady`, not `Running`. The one-second maintenance loop
starts a match only when both participants are connected to its room, its owner lease is valid,
and a durable tick exists. Admission expires after 120 seconds. `POST /api/matches/{id}/cancel`
requires the `operate` permission and can cancel only pending matches. Cancellation requeues
participants still online outside the retiring room, preserving their original queue age.
Offline participants must reconnect and join again. Cancelled proposals do not trigger opponent cooldown.

`finish` rejects unstarted/cancelled matches. Settlement is idempotent by match and winner;
a conflicting winner is rejected. Opponent cooldown is measured from settlement for finished matches.
Retirement blocks new input and connections, disconnects clients through Gateway reconciliation,
and waits for accepted input and connection leases to drain before closing/destroying the room.
It never discards pending durable input to free capacity.

Control-plane `FillBots=true` is rejected until a game-runtime bot adapter is integrated.
The independent in-process `Matchmaker` bot factory remains supported. Existing stored bot settings
cannot produce fictional bot matches. The FIFO/nearest-rating policy, tier boundary and fixed score
increments are unchanged; this is not an optimal-cardinality or Elo matcher.

New board idempotency receipts are stored in `board_receipts(board_id, source_id)` in the same
transaction as score updates. Legacy embedded `Sources` remain recognized. There is no 100,000-source
hard stop on new settlements; receipt retention requires a separate lifecycle policy.
