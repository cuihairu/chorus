
# 模块快照恢复契约（阶段三恢复半侧，方案 A 已代理决策并落地）

> 状态：**方案 A 已代理决策（2026-10-14）并落地**（`062cc68`，11 个测试）。本文保留当时的选项、
> 权衡与推荐作为决策记录；当前实现见文末"当前实现"一节，验收见 §7。
> 当前 ControlHost 检查点接线与存储边界见[多机房间](../architecture/multi-machine-rooms.md)与[房间控制存储](../architecture/room-control-storage.md)。历史决策章节在下面分别标注。

## 结论先行

`IGameModule` 目前只有读出（`GetStateSnapshot()`），没有
"从权威快照重建模块状态"的写回入口——这是落地恢复闭环前要先关闭的第一条契约缺口。本文给出三种接口形态选项 + 四个必须一并代理决策的语义点，**推荐方案 A**（写回入口进 `IGameModule`，作为必选成员）。

## 原始状态盘点（2026-10-14 代码事实）

- `IGameModule`（`src/Chorus.Engine/Abstractions/IGameModule.cs`）有四个成员：
  `Name` / `ApplyEvents(tickId, events)` / `GetStateSnapshot()` / `Dispose`。
- 模块出生点有两个，都挂在 `SessionActor` 构造上：
  单机 `src/Chorus.Server/SessionManager.cs:34`、拆分 `src/Chorus.EngineHost/EngineIpcService.cs:152`，
  都走 `IGameModuleFactory.Create(sessionId)`。
- `GetStateSnapshot()` 返回的 `object` 经 `ProtocolJson` 序列化为线协议 `state`
  （`JsonElement`）；快照已携带 schema 版本 `v = 1`（线协议 `ServerSnapshot.v`，2d157fa）。
- 大小闸门 `Session:MaxSnapshotBytes`（默认 1 MB）在 `SessionActor` 编码路径生效
  （`SessionActor.cs:457`）；超限快照跳过并计入 `SessionDiagnostics`。
- 原始发现，现已作废：快照只在内存里——**"权威快照从哪来"（持久介质与落盘时机）当时是另一个
  后续决策，本文既不选择也不预设**。本文只定义"给定权威快照，模块如何重建"。
- 单 worker 现状：进程重启即清空，重连客户端拿到全新会话。恢复闭环是
  [process-topology.md §5](./process-topology.md) 列出的两个拆分前置之一。

## 无论选哪个方案都必须一并代理决策的语义点

1. **恢复输入形态**：线协议 JSON 形态（`JsonElement` + `v`）还是活对象。
   拆分模式下权威快照必然经过 IPC/线协议，**活对象只在单进程内成立**；且
   `GetStateSnapshot()` 已要求 JSON 可序列化，因此以线协议形态为输入是诚实的契约
   （推荐），同时把"模块状态必须能过 JSON 往返"从隐式约定升为显式规则。
2. **与 `ApplyEvents` 的时序**：恢复完成前不得投递任何事件——tick 循环与
   入口路径都要阻塞（`SessionActor` 在首个 tick 前、`EngineIpcService`
   在首次 ingest 前）。
3. **应用失败的处理**：fail-closed（恢复失败 → 会话拒绝启动，客户端得到明确错误）
   还是降级（空状态启动 + 日志 + 指标）。本仓安全侧一贯 fail-closed（默认拒绝）；
   "带着半恢复状态默默服务"比拒绝更危险，**故推荐 fail-closed**。
4. **版本与大小校验**：未知 `v` → 拒绝（服务端先适用 versioning.md §2 预留的
   未知版本拒绝语义）；超过 `MaxSnapshotBytes` → 拒绝（复用既有闸门）。

## 三种接口形态选项

### A. 写回入口进 `IGameModule`（必选成员）

```csharp
public interface IGameModule
{
    // 既有成员不变
    void RestoreState(long version, JsonElement state); // 新增必选
}
```

- 收益：恢复**不是可选能力**——§5 把恢复列为拆分前置，能力可选会让"忘了实现恢复"
  变成只在运行时才暴露的部署地雷；编译期强制让每个模块必须回答"如何重建"。
- 成本：`IGameModule` 破坏性变更（0.x 窗口内最便宜，且当前实现面只在仓内：
  `SampleGameModule` + 测试桩）；`JsonElement` 进 `Chorus.Engine.Abstractions`
  需引用 `System.Text.Json`（已是 BCL，无新依赖）。
- 恢复调用时机（两个出生点相同）：`Create(sessionId)` 之后、任何
  `ApplyEvents`/tick 之前，权威快照由 `SessionManager` / `EngineIpcService` 传入。

### B. 恢复走工厂（出生参数）

```csharp
public interface IGameModuleFactory
{
    IGameModule Create(string sessionId, SnapshotRecovery? recovery); // 签名变更
}
```

- 收益：`IGameModule` 保持窄（对模块作者更友好）；恢复是一种"出生模式"，
  语义上契合工厂。
- 成本：工厂签名变更（破坏面略小，但同样是破坏）；每个工厂都要处理恢复
  → 模块状态重建逻辑被推到工厂层，给模块与工厂之间加了一层隐式耦合（工厂必须懂如何构建模块的
  内部状态）。

### C. 可选能力接口

```csharp
public interface ISnapshotRestorable { void RestoreState(long version, JsonElement state); }
// 引擎侧：module is ISnapshotRestorable restorable ? restore : 降级
```

- 收益：零破坏、渐进采纳。
- 成本：它制造出两级模块（可恢复 / 不可恢复），不可恢复模块在重启场景只能靠
  文档约定兜底——恰是"静默地雷"形态；恢复闭环是拆分前置，不应 opt-in。

## 推荐

**方案 A**。理由：恢复是拆分前置而非可选能力（A > C）；模块作者直接负责重建
自己的状态、状态重建逻辑不外泄到工厂（A > B）；0.x 破坏窗口 + 实现面只在仓内
是唯一便宜的时刻。输入形态取线协议 JSON（语义点 1），失败取 fail-closed（语义点 3）。

→ **2026-10-14 代理决策：方案 A**，按 §7 验收清单全部落地（`062cc68`，11 个测试）。
实现在下一节。

## 当前实现（2026-10-14，`062cc68`）

- **契约成员**：`IGameModule.RestoreState(long version, JsonElement state)` 为必选成员
  （`src/Chorus.Engine/Abstractions/IGameModule.cs`）——编译期强制让每个模块回答"如何重建"。
- **恢复输入**：线协议 JSON（与 `GetStateSnapshot()` 往返一致），`version` 即线协议 `ServerSnapshot.v`。
- **单点执行器**：`GameModuleRestorer.Restore(module, sessionId, source, config)`
  （`src/Chorus.Engine/Session/GameModuleRestorer.cs`）。两个出生点（单机 `SessionManager.CreateActor`
  / 拆分 `EngineIpcService.CreateActor`）在 `IGameModuleFactory.Create` 之后、`SessionActor`
  构造之前调用同一执行器 → **构造即对齐**；恢复发生在 actor 工厂内部，结构上保证
  "恢复完成前没有 tick/入口事件到达模块"与"失败不留半状态 actor"。
- **Fail-closed**：未知 `version`（< 1）/ 超过 `MaxSnapshotBytes`（与 tick 侧同一大小闸门）/
  模块恢复抛异常——一律抛 `SnapshotRestoreException`（携带 SessionId）→ 会话拒绝启动，
  计入 `SessionRegistry.StartFailures` 可观测（失败会话没有 actor 诊断可读）。
- **无快照 = 全新开始**：`ISnapshotRecoverySource.TryGet` 返回 false 即全新开始，今天的默认行为不变。
- **示例模块**：`SampleGameModule.RestoreState` 只恢复会话私有状态（`tickId`/`matchId` +
  三类按用户计数）；`match` 段是跨会话存储投影，不属于模块私有恢复契约（恢复后随新事件到来重建）。
- **原"无接线"结论作废**：ControlHost 模式下 EngineHost 现从 RoomControlStore 检查点恢复；无 ControlHost 时会话仍全新启动。独立宿主接管与故障注入仍是 ROOM-07/09/12 open。

§7 验收清单第 1-6 项由 `ModuleRestoreTests`（11 个测试）金标覆盖；第 7 项
记录在 [versioning.md](../reference/versioning.md) 的兼容性面（`IGameModule` 破坏性变更 + 恢复语义）。

## 验收测试清单（全部完成，`062cc68`）

1. 恢复后首次 `GetStateSnapshot()` 与权威快照语义一致（含 `v` 透传）。
2. 恢复完成前不调用 `ApplyEvents`：tick 与入口并发到达也成立
   （竞争守卫，遵循"轮询断言性质本身"纪律）。
3. 未知 `v` → 会话拒绝启动（fail-closed），且错误可观测。
4. `state` 超过 `MaxSnapshotBytes` → 拒绝，计入诊断。
5. 模块恢复抛异常 → 会话拒绝启动，且不残留半状态 `SessionActor`。
6. 两个出生点（单机 `SessionManager` / 拆分 `EngineIpcService`）行为一致
   （对齐守卫，沿用缝式分歧先例）。
7. `versioning.md` 兼容性面更新：`IGameModule` + 恢复语义的破坏性变更记录。

## 显式不做（对照边界）

持久介质选择与落盘时机（下一个决策）、跨机快照复制、增量快照/确定性回放
（spec"显式不做"）、快照压缩（spec §2.3 "后续可扩展"）。
