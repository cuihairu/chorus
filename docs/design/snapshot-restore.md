<p align="center">
  English | <a href="snapshot-restore.zh.md">简体中文</a>
</p>

# Module Snapshot Recovery Contract (Phase Three Recovery Half, Option A Proxy-Decided and Landed)

> Status: **Option A proxy-decided (2026-10-14) and landed** (`062cc68`, 11 tests). This document retains the options,
> tradeoffs and recommendation as a decision record; the current implementation is in the "Current Implementation" section at the end, and acceptance is in §7.
> See the upstream problem in [process-topology.md §5](./process-topology.md).

## Conclusion First

`IGameModule` currently only has read-out (`GetStateSnapshot()`), with no write-back entry to
"rebuild module state from an authoritative snapshot"——this is the first contract gap to close before landing the recovery loop. This document gives three interface shape options + the four semantic points that must be
proxy-decided together, **recommending Option A** (write-back entry goes into `IGameModule`, as a required member).

## Current State Inventory (code facts)

- `IGameModule` (`src/Chorus.Engine/Abstractions/IGameModule.cs`) has four members:
  `Name` / `ApplyEvents(tickId, events)` / `GetStateSnapshot()` / `Dispose`.
- There are two module birthpoints, both attached to `SessionActor` construction:
  monolith `src/Chorus.Server/SessionManager.cs:34`, split `src/Chorus.EngineHost/EngineIpcService.cs:152`,
  both going through `IGameModuleFactory.Create(sessionId)`.
- The `object` returned by `GetStateSnapshot()` is serialized via `ProtocolJson` into the line protocol `state`
  (`JsonElement`); the snapshot already carries schema version `v = 1` (line protocol `ServerSnapshot.v`, 2d157fa).
- The size gate `Session:MaxSnapshotBytes` (default 1 MB) is in effect on the `SessionActor` encoding path
  (`SessionActor.cs:457`); oversized snapshots are skipped and counted into `SessionDiagnostics`.
- Snapshots exist only in memory with no persistence——**"where the authoritative snapshot comes from" (persistence medium and write-to-disk timing) is a separate
  next decision, which this document neither chooses nor presupposes**. This document only defines "given an authoritative snapshot, how does the module rebuild".
- Single-worker current state: a process restart empties everything, and reconnecting clients get a brand-new session. The recovery loop is
  one of the two pre-split prerequisites listed in [process-topology.md §5](./process-topology.md).

## Semantic Points That Must Be Proxy-Decided Together, Regardless of Which Option Is Chosen

1. **Recovery input shape**: line-protocol JSON shape (`JsonElement` + `v`) or live object.
   In split mode the authoritative snapshot necessarily passes through IPC/line protocol, **a live object only holds within a single process**; and
   `GetStateSnapshot()` already requires JSON-serializability, so taking the line-protocol shape as input is an honest contract
   (recommended), while also elevating "module state must survive a JSON round trip" from an implicit convention to an explicit rule.
2. **Timing relative to `ApplyEvents`**: no events may be delivered before recovery completes——both the tick loop and
   the ingress path must be blocked (`SessionActor` before its first tick, `EngineIpcService`
   before its first ingest).
3. **Application failure handling**: fail-closed (recovery fails → the session refuses to start, the client gets a clear error)
   or degradation (start with an empty state + log + metric). This repo's security side has consistently been fail-closed (deny by default);
   "silently serving with a half-recovered state" is more dangerous than refusing, **so fail-closed is recommended**.
4. **Version and size validation**: unknown `v` → reject (the server side applies the
   unknown-version rejection semantics reserved in versioning.md §2 first); size over `MaxSnapshotBytes` → reject (reusing the existing gate).

## Three Interface Shape Options

### A. Write-Back Entry Goes Into `IGameModule` (Required Member)

```csharp
public interface IGameModule
{
    // 既有成员不变
    void RestoreState(long version, JsonElement state); // 新增必选
}
```

- Benefit: recovery is **not an optional capability**——§5 lists recovery as a pre-split condition, and making the capability optional would turn "forgot to implement recovery"
  into a deployment landmine that only surfaces at runtime; compile-time enforcement makes every module answer "how to rebuild".
- Cost: a breaking change to `IGameModule` (cheapest within the 0.x window, and the current implementation surface is only in-repo:
  `SampleGameModule` + test fakes); `JsonElement` entering `Chorus.Engine.Abstractions`
  requires referencing `System.Text.Json` (already BCL, no new dependency).
- Recovery call timing (the same at both birthpoints): after `Create(sessionId)`, before any
  `ApplyEvents`/tick, with the authoritative snapshot passed in by `SessionManager` / `EngineIpcService`.

### B. Recovery Goes Through the Factory (Birth Parameter)

```csharp
public interface IGameModuleFactory
{
    IGameModule Create(string sessionId, SnapshotRecovery? recovery); // 签名变更
}
```

- Benefit: `IGameModule` stays narrow (friendlier to module authors); recovery is one kind of "birth mode",
  semantically fitting the factory.
- Cost: factory signature change (a slightly smaller breaking surface, but breaking all the same); every factory must handle recovery
  → the module state rebuild logic is pushed up to the factory layer, adding one implicit coupling between the module and the factory (the factory must understand how to build the module's
  internal state).

### C. Optional Capability Interface

```csharp
public interface ISnapshotRestorable { void RestoreState(long version, JsonElement state); }
// 引擎侧：module is ISnapshotRestorable restorable ? restore : 降级
```

- Benefit: zero breakage, gradual adoption.
- Cost: it creates two tiers of modules (recoverable / non-recoverable), and non-recoverable modules can only be covered in the restart scenario by
  documentation conventions——exactly the "silent landmine" shape; the recovery loop is a pre-split condition and should not be opt-in.

## Recommendation

**Option A**. Reasons: recovery is a pre-split condition rather than an optional capability (A > C); module authors take direct responsibility for rebuilding
their own state, and the state rebuild logic does not leak out to the factory (A > B); the 0.x breaking window + an in-repo-only implementation surface
is the only cheap moment. The input shape is the line-protocol JSON (semantic point 1), and failure is fail-closed (semantic point 3).

→ **2026-10-14 proxy decision: Option A**, fully landed per the §7 acceptance checklist (`062cc68`, 11 tests).
Implementation is in the next section.

## Current Implementation (2026-10-14, `062cc68`)

- **Contract member**: `IGameModule.RestoreState(long version, JsonElement state)` is a required member
  (`src/Chorus.Engine/Abstractions/IGameModule.cs`)——compile-time enforcement makes every module answer "how to rebuild".
- **Recovery input**: line-protocol JSON (round-tripping `GetStateSnapshot()`), with `version` being the line protocol `ServerSnapshot.v`.
- **Single-point executor**: `GameModuleRestorer.Restore(module, sessionId, source, config)`
  (`src/Chorus.Engine/Session/GameModuleRestorer.cs`). Both birthpoints (monolith `SessionManager.CreateActor`
  / split `EngineIpcService.CreateActor`) call the same executor after `IGameModuleFactory.Create` and before `SessionActor`
  construction → **parity by construction**; recovery happens inside the actor factory, structurally
  guaranteeing "no tick/ingress events reach the module before recovery completes" and "a failure leaves no half-state actor".
- **Fail-closed**: unknown `version` (< 1) / over `MaxSnapshotBytes` (reusing the same size gate as the tick side) /
  module recovery throwing an exception——all throw `SnapshotRestoreException` (carrying the SessionId) → the session refuses to start,
  counted into `SessionRegistry.StartFailures` for observability (a failed session has no actor diagnostics to read).
- **No snapshot = fresh start**: `ISnapshotRecoverySource.TryGet` returning false means a fresh start, so today's default behavior is unchanged.
- **Sample module**: `SampleGameModule.RestoreState` only restores session-private state (`tickId`/`matchId` +
  the three kinds of per-user counters); the `match` section is a cross-session store projection and is not part of the module's private recovery contract (it is rebuilt as new events arrive after recovery).
- **No production wiring**: the two `Program.cs` files do not register a default recovery source, and the constructor parameter defaults to `null` = today's behavior is completely
  unchanged. The persistence medium and write-to-disk timing are a separate follow-up decision (production wiring comes with the topology work); `ISnapshotRecoverySource`
  is the seam——which implementation to inject is given by a future decision.

The §7 acceptance checklist items 1-6 are gold-covered by `ModuleRestoreTests` (11 tests); item 7 is
recorded in the compatibility surface of [versioning.md](../reference/versioning.md) (the breaking change to `IGameModule` + recovery semantics).

## Acceptance Test Checklist (all complete, `062cc68`)

1. The first `GetStateSnapshot()` after recovery is semantically consistent with the authoritative snapshot (including `v` pass-through).
2. `ApplyEvents` is not called before recovery completes: holds even when tick and ingress arrive concurrently
   (race guard, following the "poll and assert the property itself" discipline).
3. Unknown `v` → the session refuses to start (fail-closed), and the error is observable.
4. `state` over `MaxSnapshotBytes` → rejected, counted into diagnostics.
5. Module recovery throws an exception → the session refuses to start, and no half-state `SessionActor` is left behind.
6. The two birthpoints (monolith `SessionManager` / split `EngineIpcService`) behave consistently
   (parity guard, following the seam-style divergence precedent).
7. `versioning.md` compatibility surface update: the breaking change record for `IGameModule` + recovery semantics.

## Explicitly Not Done (Against the Boundaries)

Persistence medium selection and write-to-disk timing (the next decision), cross-machine snapshot replication, incremental snapshots/deterministic replay
(spec "explicitly not done"), and snapshot compression (spec §2.3 "extendable later").
