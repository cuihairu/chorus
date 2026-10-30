---
title: 版本策略与兼容性
---


# 版本策略与兼容性

相关文档：

- [部署与安全](./deployment.md)
- [协议参考](./protocol.md)
- [可观测性](./observability.md)

## 版本号

- 语义化版本 `MAJOR.MINOR.PATCH`（SemVer 2.0）。
- 0.x 阶段允许破坏性变更；变更记录在 commit 消息与 `docs/design/review-*.md` 中。
- 从 1.0 起承诺同一主版本内只允许向后兼容的变更。
- **只有维护者显式提供版本号时才触发正式发布**：推送 `v*` 标签 → CI 构建 + 测试 → 通过后才创建 GitHub Release。没有自动打标签，未通过测试的二进制不放入 Releases。每日构建/滚动二进制（若启用）只以滚动产物发布，不带正式版本号。

## 兼容面

### 1）WebSocket 线协议（客户端 ↔ Gateway）

| 消息 | 方向 | 字段演进规则 |
| --- | --- | --- |
| `hello` | C→S | 新增可选字段可以；改变语义/删除字段 = 破坏性 |
| `welcome` | S→C | 同上；tick 参数只增不减 |
| `ack` | C→S | 同上 |
| `snapshot` | S→C | 同上；`state` 内容由 `game` 模块决定 |
| `ping` / `pong` / `error` | 双向 | 同上 |

- `ClientHello.ClientVersion` 是自由字符串，**不是**协议版本协商字段。
- 未知 `type` 的消息被拒绝/忽略，不导致连接关闭。
- `hello.protocolVersion` / `welcome.protocolVersion`（自 2026-10-12，见[协议参考](./protocol.md)）：默认按旧版客户端放行；若发送且不等于服务端 `ProtocolVersionPolicy.Current` → 以 `protocol_mismatch` 拒绝。仍属**已知缺口**：该字段是单值检查而非范围协商（服务端无法声明"支持 ≤N"），且服务端升级到新主版本时旧客户端需读取 `welcome.protocolVersion` 并主动断开。

### 2）快照状态 Schema

- 引擎侧快照携带 `V = 1`（schema 版本）。
- 版本号已提升到线协议 `ServerSnapshot` 的 `v` 字段（2026-10-13，2d157fa）：引擎快照透传（广播路径 `WireSnapshot` 与 eng→gw IPC 路径 `EngineBusSessionClient` 都携带，Gateway 原样转发）；当前恒为 `1`；旧发送方省略该字段时，接收方按默认 `1` 读取，双向兼容。
- 剩余一半（1.0）：显式拒绝未知 `v` + 强制重同步语义 —— 待设计首个 v2 schema 时再定；当前只有一版，无触发条件；过渡期间，客户端遇到无法识别的 `v` 可丢弃 + 继续 ACK + 等待重同步作为回退。
- 恢复语义（2026-10-14，`062cc68`）：模块从快照恢复走 `IGameModule.RestoreState(version, JsonElement)`，输入为线协议 JSON，`version` 与线协议 `v` 语义相同；未知版本 / 超过 `Session:MaxSnapshotBytes` / 模块异常都 fail-closed 并拒绝会话启动。Schema 演进规则与线协议相同（新增可选字段兼容；删除/改变语义破坏性）。

### 3）入口接口（平台 ↔ Gateway）

- URL 形态 `POST /ingress/{platform}` 不变；新增平台名兼容。
- Body 结构由各平台映射器定义；新增可选字段兼容，删除/改变语义破坏性。

### 4）IPC（Gateway ↔ EngineHost）

- 给 `Envelope` 新增可选字段（如 `SourceEventId`）向后兼容；新的 `Type` 被接收方当作未知类型处理。
- 改变 `Ipc:Transport` 需要两侧同步重启。

### 5）配置

- 未知键忽略；被删除/改变语义的键 = 破坏性变更，必须在发布说明中记录。
- `Session:*` 默认值变更非破坏性（未配置时生效）；显式配置优先。

### 6）模块契约（`IGameModule`，引擎内部）

- `RestoreState(long version, JsonElement state)` 是 `IGameModule` 的必需成员（2026-10-14，`062cc68`）：破坏性变更（0.x 窗口；实现面仅仓内 `SampleGameModule` + 测试 fakes）。恢复语义见 §2；引擎不提供内置默认恢复实现，模块作者自行负责状态重建。
- 从 1.0+ 起，`IGameModule` 契约变更（新增/改变语义成员）视为破坏性，必须在发布说明中记录。

## 兼容性判定规则

| 变更类型 | 0.x | 1.0+ |
| --- | --- | --- |
| 线协议新增可选字段 | 允许 | 允许 |
| 线协议新增消息类型 | 允许 | 允许 |
| 删除/重命名字段 | 允许（发布说明记录） | 禁止 |
| 配置新增键 | 允许 | 允许 |
| 配置删除/改变语义 | 允许（发布说明记录） | 禁止 |
| 新增平台适配器 | 允许 | 允许 |
| `Session:*` 默认值变更 | 允许（发布说明记录） | 谨慎（显式配置优先） |
| `IGameModule` 契约变更 | 允许（0.x，发布说明记录） | 禁止 |

## 持久化与数据迁移

独立服务端会话状态在内存中，无重启恢复。ControlHost 模式持久化受围栏保护（fenced）的房间检查点，故此类部署升级时必须保持检查点 schema 与 `IGameModule.RestoreState` 兼容性。线协议与配置兼容规则仍独立适用。

## 升级流程（运维视角）

1. 先升级 `EngineHost`，再升级 `Gateway`（新旧 Gateway 可共存，因为不共享内存状态；但 IPC 配置必须一致）。
2. 建议发布顺序：先单进程形态，再分离形态。
3. 回滚 = 重启旧版本进程；客户端自动重连并重同步，无需协调。
