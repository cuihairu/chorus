# 房间控制存储决策

**状态：** 由 patrol-1018-chorus.md 中的委托决策批准  
**日期：** 2026-10-19  
**范围：** ROOM-11 初始生产存储决策

## 决策

采用 PostgreSQL 16 或更新版本作为多 Worker ControlHost 权威存储。生产拓扑为一个可写主库与一个同步备库，分置不同物理主机，并启用 PostgreSQL 同步提交。保留现有 SQLite provider 用于本地开发与单主机部署。Worker 与 Gateway 进程继续只通过 ControlHost HTTP 访问权威；绝不允许挂载或打开共享数据库文件。

首个生产容量门槛为 100 个活动房间在 66 ms tick、最多 1,000 个持久输入事件/s、事件写入前最多 1,516 次检查点提交/s。检查点载荷目标为 p95 ≤16 KiB、p99 ≤64 KiB、硬上限 256 KiB。在该负载下，PostgreSQL 提交延迟须保持 p95 ≤20 ms、p99 ≤40 ms、p99.9 ≤60 ms；任何超过其 66 ms 预算的 tick 提交都是门槛失败。这些是初始发布目标，不是实测结果或容量承诺。负载测试须在生产激活前实测实际载荷分布、WAL 体积、锁等待、连接池饱和与提交延迟。

操作仅在事务提交后被确认。启用同步复制与 `synchronous_commit=on` 时，备库已刷写已确认事务，在主库/备库对保持完好期间为已确认事件与检查点提供 RPO 0。初始故障切换为手动，RTO 目标 15 分钟；在脑裂与路由收敛故障测试通过前，自动提升保持禁用。

为每个数据库主机预算 2 TiB 快速本地存储用于数据库文件与有界本地 WAL 暂存，另加 24 TiB 独立对象存储用于七天 WAL 归档与基础备份。该归档上限假设 p95 检查点大小与所述写入速率持续，并为事件写入与索引/WAL 开销留出余量；发布前根据观测到的 WAL 生成量修订。在本地存储、WAL 暂存与归档预算的 70% 告警、85% 寻呼。这七天 WAL/基础备份窗口用于数据库恢复，不是审计行删除。审计记录采用仅追加留存，不自动删除；当前策略与 256 MiB 管理告警见[审计留存](audit-retention.md)。尚未配置归档目的地。

## 备选方案

| 选项 | 评估 |
|---|---|
| 多 Worker 生产继续用 SQLite | 拒绝。所有 tick 提交串行化在单个本地文件写入器后；没有同步远程备库，无法满足所选多主机 RPO 目标。保留用于本地/单主机。 |
| PostgreSQL 主库 + 异步副本 | 初始生产拒绝。改善并发写，但提升期间可能丢失已确认检查点与事件提交，违反所选 RPO 0。 |
| PostgreSQL 主库 + 同步备库 | 选定。支持并发 Worker 提交与第二主机上的持久确认。接受的成本是复制延迟与同步备库无法确认时的写不可用。 |
| 分片/分布式 SQL 或事件日志架构 | 推迟。在单 PostgreSQL 写入器对初始门槛实测之前，增加分区、重放与跨分片正确性工作。 |

## 实施与发布规则

- 房间 tick 提交与可靠事件写入在一个事务中保留 owner/epoch 围栏（fencing）、检查点/inbox 变更与回执。PostgreSQL 在这些热路径上用 `READ COMMITTED` + 房间记录上的 `SELECT ... FOR UPDATE`；围栏在取得该行锁后检查。SQLite 继续用其 immediate 写事务。其他控制写入保持 `SERIALIZABLE` 隔离。
- PostgreSQL 序列化失败与死锁只能通过重跑完整事务重试；回调不得产生外部副作用。
- 设置 `Control:Provider=PostgreSql` 并通过密钥配置提供 `Control:ConnectionString`。不得把凭据放入已检入的配置。
- 主库与备库分置不同物理主机，同步复制、TLS、SCRAM 认证与已测试的手动提升运行手册。不得把单主机上的两个容器当作 HA。
- 在 ROOM-07/09/12 网络分区、陈旧 owner、ControlHost 重启与路由收敛测试通过前，自动故障切换保持禁用。
- 在单独批准的替代重放/丢失窗口设计出现前，保持 66 ms 持久检查点契约。

## 测试环境与当前限制

当前可用的 PVE 集群只有一个物理节点。其唯一客户机是编码 VM；其 Docker LXC 是共享服务主机。本地 LVM-thin 池已用 94.2%，约剩 105 GB，无法安全承载所选 2 TiB + 2 TiB 生产磁盘，也无法作为独立主机 HA 测试床。不要在那里创建持久大 VM 磁盘。

本地为 provider 集成测试启动了一个隔离的、临时的 PostgreSQL 16 Docker 服务。它使用私有 Docker 桥、仅回环端口发布、2 GiB 内存限制与 tmpfs 数据。这仅验证 SQL/provider 行为；不是独立主机、复制、故障切换或容量证据。ROOM-07/09/12 仍需第二物理主机与可控网络隔离。PVE 测试计划为私有桥上的两个一次性 2-vCPU/4-GiB 节点，各配 2 TiB 预置存储，加独立 24 TiB 对象存储目标；当前 thin 池无法预备该实验室。

## 后果与开放验证

provider 决策不再开放。ROOM-11 保持 open，直到 PostgreSQL 完成与 SQLite 的全存储契约对等、多 Worker 负载运行达到延迟/WAL 门槛、生产存储/复制资源就绪。PostgreSQL 16 集成覆盖验证可靠事件持久化、跨 Store 实例的检查点围栏、重复回执处理、并发分配唯一性、迁移与连接生命周期行为、事件热度确认。provider 对等回归 `Providers_MatchForSummaryHistoryAuditAndEventHeatRetention` 对比 SQLite/PostgreSQL 的汇总、历史、审计记录/用量与事件热度留存/裁剪。初始实现用同步 ADO.NET API 以匹配现有 ControlDatabase 调用图；在采用移除同步 I/O 支持的驱动版本前，把控制持久化路径迁移到异步 API。
