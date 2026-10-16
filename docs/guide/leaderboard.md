---
title: Leaderboard
---

<p align="center">
  English | <a href="leaderboard.zh.md">简体中文</a>
</p>

# Leaderboard

Encapsulation status: **integrated this round** (2026-10-15). The `LeaderboardStore` storage has long been in the library (`src/Chorus.Data/Leaderboard.cs:40`),
but previously had no DI registration, no endpoint, and no callers; this round wired it into the two session hosts (Server / EngineHost),
and made the reference gameplay (`Chorus.SampleGame`) the first authoritative score writer. Tests: `tests/Chorus.Engine.Tests/LeaderboardEndpointTests.cs` (8 cases).

## Data Model

Four scopes (`Leaderboard.cs:10-16`); `scopeId` is the leaderboard key:

| scope | scopeId | Typical Source |
|---|---|---|
| `room` | session id | engine-side module projects per tick ("Writing Scores" on this page) |
| `match` | match id | per-match settlement (same as above; the gameplay decides when to project) |
| `period` | period label (e.g. `day:2026-10-15`) | async aggregator (not wired, see Boundaries) |
| `global` | fixed key | async aggregator (not wired, see Boundaries) |

Ordering: score descending, ties broken by participant id lexicographic order (results don't jitter), ties share competition ranks (1,2,2,4),
implemented in `Leaderboard.cs:137-157`. `Freeze` (`:104`) freezing seals the leaderboard: all subsequent writes are rejected (fail-visible),
and queries always return the ranking at freeze time; `Query` (`:124`) returns an independent snapshot, with unknown leaderboards returning an empty list (not an error).

## Writing Scores: The Only Write Entry, on the Engine Side

Design rule (`Leaderboard.cs:21-38` class comment, audit §9.2): **only the engine side can write scores; clients cannot self-report**.
In code this lands as one seam:

```
IGameModule.ApplyEvents（游戏规则，权威状态）
    → ISessionScoreSink.ProjectScore(sessionId, participantId, score)   ← 模块只推"当前权威总分"
    → LeaderboardScoreSink → LeaderboardStore.UpsertScore(Room, sessionId, participantId, score)
```

- Seam interface: `src/Chorus.Engine/Abstractions/ISessionScoreSink.cs:11` (single method `ProjectScore`, `:17`;
  called on the tick path, implementations must not block and must not throw).
- Host implementation: `src/Chorus.Gateway.Core/Leaderboard/LeaderboardHost.cs:16` — writes scores replace-style into
  the room leaderboard. **Replace semantics** is the key: re-projecting the same participant overwrites rather than accumulates,
  naturally idempotent, with no double-scoring problem.
- Host registration (DI singleton): `src/Chorus.Server/Program.cs:26-27`, `src/Chorus.EngineHost/Program.cs:14-15`.
- Reference implementation: `src/Chorus.SampleGame/SampleGameModule.cs` — projects only the participants **touched** in the current batch each tick
  (`:40`), with the rule `likes + gifts + comments` (`:89`, "who lit up the room" rather than gift amounts);
  when the session restores from a snapshot it re-projects all participants to rebuild the room leaderboard (`:205`).

When writing your own gameplay, just implement the same pattern:

```csharp
// 模块构造（经 IGameModuleFactory 注入 sink；sink 为 null 时零开销、不投影）
public MyGameModule(string sessionId, ISessionScoreSink? scores)
    => _scores = scores;

// ApplyEvents 内：参与者权威分变化后推"当前总分"（替换，不是增量）
_scores?.ProjectScore(sessionId, userId, authoritativeTotal);
```

## Reading Scores: Read-Only Query Endpoint

`GET /leaderboard/{scope}/{scopeId}?top=N`, exposed by the two **session-holding** hosts
(`src/Chorus.Server/Program.cs:145`, `src/Chorus.EngineHost/Program.cs:53`;
the gateway holds no session state and has no such endpoint):

```bash
curl http://localhost:5083/leaderboard/room/s1?top=10
```

| Parameter | Meaning | Constraints |
|---|---|---|
| `scope` | `room` / `match` / `period` / `global` (case-insensitive) | unknown value → 400 `unknown_scope` |
| `scopeId` | leaderboard key (room leaderboard = session id; period leaderboard = period label) | single path segment (must not contain `/`); blank → 400 `invalid_scope_id` |
| `top` | top N; defaults to returning everything | 1–1000 (`LeaderboardHost.cs:38`), out of range → 400 `invalid_top` |

Response (200):

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

| Status Code | Condition |
|---|---|
| 200 | query succeeded (a leaderboard never written returns empty `entries`, `frozen:false`) |
| 400 `{error:"unknown_scope"}` | scope not among the four |
| 400 `{error:"invalid_scope_id"}` | scopeId blank |
| 400 `{error:"invalid_top",top,max}` | `top` out of range |

Endpoint implementation: `src/Chorus.Gateway.Core/Leaderboard/LeaderboardHost.cs:48` (routing), `:53` (handling).
The endpoint has no auth and, like the other diagnostic surfaces, falls under the intranet assumption (see the auth section of [API Reference](../reference/api.md)).

## Direct Library Use (Without HTTP)

`LeaderboardStore` is a thread-safe pure in-memory library, usable directly by referencing `Chorus.Data`:
`UpsertScore` (`:55`, replace) / `AddDelta` (`:75`, idempotent delta) / `Freeze` (`:104`) /
`IsFrozen` (`:113`) / `Query` (`:124`).

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

Rule for choosing between the two APIs (class comment §9.2): **use `UpsertScore` for authoritative state projection**
(re-projection overwrites); **use `AddDelta` for replay streams** (must supply a sourceId as the idempotency anchor,
claiming the anchor first and then changing the score, `:91`).

## Boundaries (What It Doesn't Do)

- **In-memory, no persistence**: leaderboards live and die with the process; a restart wipes them. The room leaderboard is rebuilt by the session-restore path
  (`SampleGameModule.cs:205`); period/global leaderboards are lost on restart — products that need cross-restart settlement must ship it themselves.
- **Clients cannot write scores**: the only write entry is the engine-side `ISessionScoreSink`; the HTTP surface is read-only. Self-reported scores do not exist in this design.
- **period/global aggregation not wired**: `AddDelta` and its sourceId ledger exist for an async aggregator (the Data Worker in the design);
  this repo has no callers; `DataCommandOutbox` is likewise a handoff buffer, not persistence.
- **`AddDelta` ledger is unbounded**: one deduplication record per sourceId per leaderboard (`Leaderboard.cs:45`); long-period high-frequency sources grow memory indefinitely.
  Real-time projection always uses `UpsertScore`; aggregators using `AddDelta` must manage period key rotation / ledger size themselves.
- **Platform-native leaderboards not wired**: the Douyin rank-upload adapters (`DouyinRankUploadAdapter`, `PlatformLeaderboardRegistry`)
  are in the library with no host wiring; see [Douyin Mini Game Platform Reference](../reference/platform-douyin-mini-game.md).
- **The gateway has no such endpoint**: in split mode, query EngineHost directly; leaderboards stay on the engine side for the lifetime of the session.

## Related Pages

- [Architecture Overview](../architecture.md) Decision 8 (replace-style projection vs real-time aggregation)
- [Battle](./battle.md) (where scores come from: audience events → tick → module)
- [API Reference](../reference/api.md) (endpoint table)
