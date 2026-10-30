# 多主机房间验收运行手册

**状态：** 仅准备；尚未运行多主机验收。
**范围：** ROOM-07、ROOM-09、ROOM-11 与 ROOM-12 发布证据。

本运行手册把开放的发布门槛转化为可重复的实验会话。它不改变所选架构，也不标记任何门槛完成。每次实验运行记录一份带日期的证据表副本；附加原始日志与指标导出，而不是用摘要替换它们。

## 进入标准

在以下所有条件为真之前不要开始故障场景：

| 要求 | 需记录的证据 |
|---|---|
| 两个独立 Worker 主机与一个独立的 ControlHost/数据库故障域 | 主机 ID、provider/地区、OS/内核、CPU/RAM、磁盘类型/容量；证明它们不是同一物理主机上的 VM 或容器 |
| PostgreSQL 16+ 主库与同步备库分置不同主机；`synchronous_commit=on` | PostgreSQL 版本、复制模式/状态、同步备库名、TLS/认证模式、实测复制延迟 |
| 隔离测试网络，带可逆的、目标范围的分区控制 | 接口/子网、规则 owner、精确源/目的/端口、添加/移除命令、带外恢复路径 |
| 房间与输入工作负载可复现 | 构建 commit、去除密钥的配置哈希、房间数、tick 间隔、载荷大小、事件速率、客户端数、运行时长、负载生成主机 |
| 可观测性与时间对齐在每个节点可用 | UTC 时钟偏移、抓取目标、日志/指标留存、trace/运行 ID |
| 恢复与清理已演练 | 数据库备份/快照 ID、进程重启命令、网络规则回滚、预期资源清理 |

只使用一次性基础设施。绝不在生产环境注入分区或提升数据库。整个测试期间保持自动故障切换禁用；所选初始恢复模式为手动。

### 已知候选环境评估（2026-10-20，22:29 +0800 复探）

今天唯一可达的基础设施不满足进入标准；记录在此以免同一路径被再次提出：

- 工作区主机 `coding` 是 hypervisor `shanghai-pve`（i9-10880H，16 vCPU / 62 GiB；`qm list` 显示 VM 101 是唯一运行的客户机）上的 VM 101。该 hypervisor 上的第二台 VM 满足"两台机器"但**按定义不满足进入标准 1**——同一物理主机、同一故障域。
- Hypervisor 余量：local-lvm 已用 94.21%，约剩 100.9 GiB，可用 RAM 约 12 GiB——比 ROOM-11 存储预算（每节点 2 TiB NVMe + 24 TiB 七天 WAL/对象存储）低一个数量级。
- 集群对端 `win-worker`（Windows）只暴露 DM 通道，无 shell；只读能力探测无回应，故其承载任何角色的能力未验证。
- 用户批准的同一 hypervisor 实验仍可演练迁移/围栏机制，但其结果必须作为同故障域证据报告，永远不能关闭独立主机、同步复制、容量或分区门槛（见[报告](#reporting-and-gate-closure)）。

## 运行协议

1. 记录 commit、配置、主机映射、UTC 开始时间与基线健康。验证所有 Worker 有不同节点与实例 ID，每个房间恰有一个 owner/epoch。
2. 无故障运行负载至少 10 分钟。保存每节点 CPU、内存、网络、磁盘/WAL、数据库池/锁/提交延迟、tick 时长/超限、房间所有权、路由快照龄期、事件接受/应用计数、客户端重连/重同步计数。
3. 从下方矩阵一次执行一个场景。记录注入故障的开始/结束时间与精确规则/进程动作。每个场景独立通过前不要组合。
4. 移除故障后，继续运行负载直到路由、事件处理与数据库复制回到基线。记录收敛时间与任何需要的手动动作。
5. 每个场景从干净状态重复三次。保留每次尝试（含失败运行）。通过要求所有三次尝试中所有断言成立。

## 场景矩阵

| 门槛 / 场景 | 注入 | 必需的安全与恢复断言 | 证据 / 通过条件 |
|---|---|---|---|
| ROOM-07：Worker 与 ControlHost 分区，旧进程保持存活 | 将某个 Worker 与 ControlHost 隔离，同时保持其进程与客户端面存活；保持新 owner 路径可用 | 旧 owner 在租约/epoch 围栏后不能提交 tick 或可靠输入；不丢失任何已确认状态；替换 owner 只恢复已提交检查点；无重叠的权威 owner | 带时间戳的 owner/epoch 与检查点历史、提交响应、数据库行、日志、客户端可见状态；零陈旧 owner 提交与零重复权威 tick |
| ROOM-07：ControlHost 重启 | 稳态负载下重启 ControlHost，然后在房间迁移处于 freeze 与 Ready 之间时重复 | 权威不可用时写 fail-closed；重启后持久 owner/epoch 与迁移状态被恢复；陈旧路由不能授权提交 | 重启窗口、API 错误、数据库恢复日志、owner/epoch 时间线；无陈旧提交且成功回到单一权威 |
| ROOM-09：分区下迁移与路由收敛 | 开始房间迁移，在 freeze/交接点隔离旧 Worker，并单独在 Ready 前中断目标；测试中止与重试两种情况 | 输入按契约作为可重试或持久排队被拒绝；旧 owner 保持被围栏；目标恢复已提交检查点/inbox 游标；Gateway 仅在新 owner/epoch 快照后路由；中止回到单个被围栏 owner | 每房间迁移时间线、快照龄期、接受/应用的事件 ID、客户端重连、实测收敛时长；无分裂权威、缺失已确认事件或永久陈旧路由 |
| ROOM-11：PostgreSQL 容量与同步复制 | 对主库/备库对运行所选 100 房间、66 ms 负载，最多 1,000 持久事件/s | 检查点与事件提交延迟满足 p95 ≤20 ms、p99 ≤40 ms、p99.9 ≤60 ms；无 tick 提交超过 66 ms；已确认事务在同步备库刷写；无无界队列/池/WAL 增长 | 至少三个 10 分钟稳态窗口；载荷分布、检查点/s、事件/s、延迟直方图、WAL 字节/s、锁/池等待、复制状态/延迟、tick 超限；每项陈述的限制都满足 |
| ROOM-11：主库丢失与手动提升 | 停止或隔离主库，验证写入停止，然后按文档化的操作员流程提升同步备库 | 不丢失任何已确认事务；仅在单一主库建立且 ControlHost 重连后写入恢复；对照 15 分钟实测手动 RTO | 提升命令记录、前后事务 ID、恢复日志、中断与恢复时间戳；零已确认数据丢失且 RTO ≤15 分钟 |
| ROOM-12：一个 Worker 对比两个 Worker | 先在单 Worker 上运行相同房间数、事件/客户端负载、硬件等级与时长，再用两个 Worker；包含新房间分配与一次显式迁移 | 迁移前现有房间保留其记录 owner；新房间按容量分配；迁移保留状态与事件回执；比较吞吐、延迟、每节点利用率与路由收敛 | 两次运行的配置/构建哈希与原始指标；报告归一化吞吐与 p95/p99 延迟。若两个 Worker 未改善实测负载，直说；不因单次比较断言线性扩展 |

## 证据记录

每次运行复制此块并附加原始产物：

```text
Run ID / UTC date:
Commit and configuration hash:
Host map (Worker, ControlHost, primary, standby, load generator):
Fault scenario / exact injection and rollback action:
Workload (rooms, tick, durable events/s, clients, duration):
Baseline and fault-window metric artifact paths:
Owner/epoch and acknowledged/applied event evidence:
Fault start / removed / convergence timestamps:
Pass or fail, assertion, and supporting artifact:
Operator actions, retries, and deviations:
Cleanup and final health:
```

## 报告与门槛关闭

逐门槛报告为 **PASS**、**FAIL** 或 **NOT RUN**。本地 Docker 数据库、单主机多进程、TestServer、单元测试或成功的 provider 集成测试可支持实现信心，但不能关闭独立主机、同步复制、容量或分区门槛。仅当完整证据附加且所有必需重复通过时，才更新对应 `todo.md` 条目。在此之前，ROOM-07/09/11/12 保持阻塞且自动故障切换禁用。

相关决策与当前证据：[多机房间架构](multi-machine-rooms.md)、[ControlHost 存储决策](room-control-storage.md)与根目录 `todo.md` §19。
