> 上游问题见 [process-topology.md §5](./process-topology.md)。

## 结论先行

`IGameModule` 当前只有读出（`GetStateSnapshot()`），没有「从权威快照重建模块状态」的
回写入口——这是恢复闭环落地前的第一个契约缺口。本文给出三个接口形状选项 + 必须一并
拍板的四个语义点，**推荐方案 A**（回写入口进 `IGameModule`，必选成员）。

## 现状盘点（代码事实）

- `IGameModule`（`src/Chorus.Engine/Abstractions/IGameModule.cs`）四成员：
  `Name` / `ApplyEvents(tickId, events)` / `GetStateSnapshot()` / `Dispose`。
- 模块出生点有两处，均随 `SessionActor` 构造：
  单体 `src/Chorus.Server/SessionManager.cs:34`、拆分 `src/Chorus.EngineHost/EngineIpcService.cs:152`，
  都经 `IGameModuleFactory.Create(sessionId)`。
- `GetStateSnapshot()` 返回的 `object` 经 `ProtocolJson` 序列化为线协议 `state`
  （`JsonElement`）；快照已带 schema 版本 `v = 1`（线协议 `ServerSnapshot.v`，2d157fa）。
- 体积门 `Session:MaxSnapshotBytes`（默认 1 MB）在 `SessionActor` 编码路径生效
  （`SessionActor.cs:457`），超限跳过并计入 `SessionDiagnostics`。
- 快照仅存在于内存、无持久化——**「权威快照从哪来」（持久化介质与写盘时机）是独立的
  下一个决策，本文不选、不预设**。本文只定「给定一份权威快照，模块如何重建」。
- 单 worker 现状：进程重启即全空，重连客户端拿到全新会话。恢复闭环是
  [process-topology.md §5](./process-topology.md) 列出的拆分前两个前置问题之一。

## 不论选哪个方案，都要一并拍板的语义点

1. **恢复输入形态**：线协议 JSON 形（`JsonElement` + `v`）还是活对象。
   拆分模式下权威快照必然走过 IPC/线协议，**活对象只在同进程成立**；且
   `GetStateSnapshot()` 本就要求可 JSON 序列化，因此输入取线协议形是诚实契约
   （推荐），同时把「模块状态必须能 JSON 往返」从隐含约定升为明文。
2. **与 `ApplyEvents` 的时序**：恢复完成前不得投递任何事件——tick 循环与
   ingress 两条路都要挡住（`SessionActor` 首个 tick 前、`EngineIpcService`
   首条 ingest 前）。
3. **应用失败处置**：fail-closed（恢复失败 → 会话拒绝启动，客户端收到明确错误）
   还是降级（空状态启动 + 日志 + 指标）。本仓安全面一贯 fail-closed（默认拒绝），
   「半恢复状态静默服务」比拒绝更危险，**推荐 fail-closed**。
4. **版本与体积校验**：`v` 不认识 → 拒绝（服务端侧先用上 versioning.md §2 预留的
   未知版本拒绝语义）；体积超 `MaxSnapshotBytes` → 拒绝（复用现有门）。

## 接口形状三选项

### A. 回写入口进 `IGameModule`（必选成员）

```csharp
public interface IGameModule
{
    // 既有成员不变
    void RestoreState(long version, JsonElement state); // 新增必选
}
```

- 优点：恢复**不是可选能力**——§5 把恢复列为拆分前置，能力可选化会让「忘了实现恢复」
  变成运行期才暴露的部署地雷；编译期强制每个模块回答「怎么重建」。
- 代价：`IGameModule` 破坏性变更（0.x 窗口内最便宜，且目前实现面只在仓内：
  `SampleGameModule` + 测试假体）；`JsonElement` 进 `Chorus.Engine.Abstractions`
  需引用 `System.Text.Json`（已是 BCL，无新依赖）。
- 恢复调用时机（两处出生点相同）：`Create(sessionId)` 之后、任何
  `ApplyEvents`/tick 之前，由 `SessionManager` / `EngineIpcService` 传入权威快照。

### B. 恢复走工厂（出生参数）

```csharp
public interface IGameModuleFactory
{
    IGameModule Create(string sessionId, SnapshotRecovery? recovery); // 签名变更
}
```

- 优点：`IGameModule` 保持窄（对模块作者更友好）；恢复是「出生方式」的一种，
  语义上贴工厂。
- 代价：工厂签名变更（破坏面小一点，但同样破坏）；每个工厂都要处理 recovery
  → 模块状态重建逻辑被推到工厂层，模块与工厂之间多一条隐性耦合（工厂必须懂模块
  内部状态怎么建）。

### C. 能力接口可选实现

```csharp
public interface ISnapshotRestorable { void RestoreState(long version, JsonElement state); }
// 引擎侧：module is ISnapshotRestorable restorable ? restore : 降级
```

- 优点：零破坏，渐进 adoption。
- 代价：出现两级模块（可恢复 / 不可恢复），不可恢复模块在重启场景的行为只能靠
  文档约定——正是「静默地雷」形态；恢复闭环是拆分前置，不该是 opt-in。

## 推荐

**方案 A**。理由：恢复是拆分前置而非可选能力（A > C）；模块作者直接对自己状态的
重建负责，状态重建逻辑不外泄到工厂（A > B）；0.x 破坏窗口 + 实现面仅仓内，
是唯一便宜的时机。输入形态取线协议 JSON（语义点 1），失败 fail-closed（语义点 3）。

→ **2026-10-14 拍板：方案 A**，已按 §7 验收清单全部落地（`062cc68`，11 测试）。
实现见下节。

## 当前实现（2026-10-14，`062cc68`）

- **契约成员**：`IGameModule.RestoreState(long version, JsonElement state)` 为必选成员
  （`src/Chorus.Engine/Abstractions/IGameModule.cs`）——编译期强制每个模块回答「怎么重建」。
- **恢复输入**：线协议 JSON（`GetStateSnapshot()` 往返），`version` 即线协议 `ServerSnapshot.v`。
- **单点执行器**：`GameModuleRestorer.Restore(module, sessionId, source, config)`
  （`src/Chorus.Engine/Session/GameModuleRestorer.cs`）。两处出生点（单体 `SessionManager.CreateActor`
  / 拆分 `EngineIpcService.CreateActor`）在 `IGameModuleFactory.Create` 之后、`SessionActor`
  构造之前调用同一执行器 → **parity by construction**；恢复发生在 actor 工厂内，结构上
  保证「恢复完成前无任何 tick/ingress 事件到达模块」且「失败不留半状态 actor」。
- **fail-closed**：未知 `version`（< 1）/ 超 `MaxSnapshotBytes`（复用 tick 侧同口径体积门）/
  模块恢复抛异常，一律抛 `SnapshotRestoreException`（携带 SessionId）→ 会话拒绝启动，
  由 `SessionRegistry.StartFailures` 计入可观测（失败的会话没有 actor 诊断可读）。
- **无快照 = 全新启动**：`ISnapshotRecoverySource.TryGet` 返回 false 即全新启动，今天的默认行为不变。
- **样本模块**：`SampleGameModule.RestoreState` 只恢复会话私有状态（`tickId`/`matchId` +
  三类 per-user 计数）；`match` 节是跨会话 store 投影，不属模块私有恢复契约（恢复后随新事件重建）。
- **无生产接线**：两个 `Program.cs` 不注册默认恢复源，构造参数默认 `null` = 今天行为完全
  不变。持久化介质与写盘时机是独立后续决策（生产接线随拓扑工作）；`ISnapshotRecoverySource`
  是缝——注入哪个实现由未来决策给。

§7 验收清单 1-6 由 `ModuleRestoreTests`（11 测试）金测覆盖；第 7 条由
[versioning.md](../reference/versioning.md) 兼容面记录（`IGameModule` 破坏性变更 + 恢复语义）。

## 验收测试清单（已全部完成，`062cc68`）

1. 恢复后首个 `GetStateSnapshot()` 与权威快照语义一致（含 `v` 透传）。
2. 恢复完成前 `ApplyEvents` 不被调用：tick 与 ingress 并发打进来也成立
   （竞态守卫，沿用「poll 断言属性本身」纪律）。
3. `v` 未知 → 会话拒绝启动（fail-closed），错误可观测。
4. `state` 超 `MaxSnapshotBytes` → 拒绝，计入诊断计数。
5. 模块恢复抛异常 → 会话拒绝启动，不得留下半状态 `SessionActor`。
6. 两处出生点（单体 `SessionManager` / 拆分 `EngineIpcService`）行为一致
   （parity 守卫，沿缝式分叉先例）。
7. `versioning.md` 兼容面更新：`IGameModule` 破坏性变更记录 + 恢复语义。

## 明确不做（对照边界）

持久化介质选型与写盘时机（下一个决策）、跨机快照复制、增量快照/确定性重放
（spec「明确不做」）、快照压缩（spec §2.3「后续可扩展」）。
