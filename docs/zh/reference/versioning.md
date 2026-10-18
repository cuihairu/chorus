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
- 0.x 阶段允许破坏性变更，变更记录在提交信息与 `docs/design/review-*.md` 中。
- 1.0 起承诺：同一大版本内只允许向后兼容的变更。
- **正式发布只由维护者显式给定版本号触发**：打 `v*` tag → CI 构建+测试 → 通过后才创建 GitHub Release。没有自动打 tag，也不会把未通过测试的二进制放进 Releases。每日构建/滚动二进制如启用，仅作为滚动产物发布，不带正式版本号。

## 兼容性面

### 1) WebSocket 线协议（客户端 ↔ Gateway）

| 消息 | 方向 | 字段演进规则 |
| --- | --- | --- |
| `hello` | C→S | 新增可选字段 OK；改语义/删除字段 = 破坏性 |
| `welcome` | S→C | 同上；tick 参数只增不删 |
| `ack` | C→S | 同上 |
| `snapshot` | S→C | 同上；`state` 内容由 `game` 模块决定 |
| `ping` / `pong` / `error` | 双向 | 同上 |

- `ClientHello.ClientVersion` 是自由字符串，**不是**协议版本协商字段。
- 未知 `type` 的消息被拒绝/忽略，不导致连接关闭。
- `hello.protocolVersion` / `welcome.protocolVersion`（2026-10-12 起，见 [协议参考](./protocol.md)）：
  缺省视为旧版客户端放行；传入且不等于服务端 `ProtocolVersionPolicy.Current` → `protocol_mismatch` 拒绝。
  仍是**已知缺口**的部分：该字段为单值检查而非区间协商（服务端不能声明「支持 ≤N」），
  服务端升级大版本时旧客户端需要读 `welcome.protocolVersion` 主动断开。

### 2) 快照状态 Schema

- 引擎侧快照带 `V = 1`（schema 版本）。
- 版本号已上到线协议 `ServerSnapshot` 的 `v` 字段（2026-10-13，2d157fa）：引擎快照透传（广播路径 `WireSnapshot` 与 eng→gw IPC 路径 `EngineBusSessionClient` 均带上，网关原样转发），当前恒为 `1`；旧发送端缺省该字段时接收端按默认 `1` 读入，双向兼容。
- 剩余半边（1.0）：未知 `v` 的明确拒绝 + 强制 resync 语义——需等首个 v2 schema 设计时定，当前只有一个版本、无从触发；过渡期客户端遇到不认识的 `v` 按丢弃 + 保持 ACK + 等 resync 兜底处理即可。
- 恢复语义（2026-10-14，`062cc68`）：模块从快照重建走 `IGameModule.RestoreState(version, JsonElement)`，输入即线协议 JSON，`version` 与线协议 `v` 同一语义；未知版本/超 `Session:MaxSnapshotBytes`/模块异常一律 fail-closed 拒绝会话启动。schema 演进规则同线协议（新增可选字段兼容，删除/改语义破坏性）。

### 3) Ingress 接口（平台 ↔ Gateway）

- URL 形态 `POST /ingress/{platform}` 不变；平台名新增属兼容。
- body 结构由各平台映射器定义；新增可选字段兼容，删除/改语义破坏性。

### 4) IPC（Gateway ↔ EngineHost）

- `Envelope` 增加可选字段（如 `SourceEventId`）向后兼容；新增 `Type` 由接收方按未知类型处理。
- `Ipc:Transport` 变更需两端同步重启。

### 5) 配置

- 未知键忽略；删除/改语义的键 = 破坏性变更，需在发布说明中标注。
- `Session:*` 默认值变更属非破坏性（不配置即生效）；显式配置以配置为准。

### 6) 模块契约（`IGameModule`，引擎内部）

- `RestoreState(long version, JsonElement state)` 为 `IGameModule` 必选成员（2026-10-14，
  `062cc68`）：破坏性变更（0.x 窗口，实现面仅仓内 `SampleGameModule` + 测试假体）。恢复语义
  见上 §2；引擎不内置默认恢复实现，模块作者自己负责状态重建。
- 1.0+ 起 `IGameModule` 契约变更（新增/改语义成员）视为破坏性，需发布说明标注。

## 兼容性判定规则

| 变更类型 | 0.x | 1.0+ |
| --- | --- | --- |
| 线协议新增可选字段 | 允许 | 允许 |
| 线协议新增消息类型 | 允许 | 允许 |
| 删除/改名字段 | 允许（发布说明标注） | 禁止 |
| 配置新增键 | 允许 | 允许 |
| 配置删除/改语义 | 允许（发布说明标注） | 禁止 |
| 新增平台适配器 | 允许 | 允许 |
| `Session:*` 默认值变更 | 允许（发布说明标注） | 谨慎（显式配置优先） |
| `IGameModule` 契约变更 | 允许（0.x，发布说明标注） | 禁止 |

## 无持久化 = 无数据迁移

会话状态纯内存（明确非目标），不存在数据 schema 与迁移问题。所有兼容性风险集中在**线协议**与**配置**两面。

## 升级流程（运维视角）

1. 先升 `EngineHost`，再升 `Gateway`（新旧 Gateway 可并存，因为它们不共享内存状态；但 IPC 配置必须一致）。
2. 灰度顺序建议：单进程形态先行，再拆形态。
3. 回滚 = 重启旧版本进程；客户端自动重连 resync，无需协调。
