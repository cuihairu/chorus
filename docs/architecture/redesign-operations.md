---
title: 重设计部署与故障恢复手册
---

# 重设计部署与故障恢复手册

版本：1.0，2026-10-25。配套[完整设计](architecture.md)。命令名与端口为目标设计约定，当前仓库不保证已有对应 CLI。

恢复目标顺序见[游戏房间恢复完整方案](../zh/design/room-recovery.md)，包括持久准备、路由确认及激活屏障，并列明尚缺双 Gateway 屏障、恢复后直接 Start 的差距（恢复前提前 Confirm Active 已在并行开发中移除）；术语见[术语表](../zh/reference/glossary.md)。本手册简要步骤不能替代这些屏障。

## 1. 部署形态

### 1.1 开发与单机

一个 PostgreSQL 实例、一个 NodeAgent，全部七种角色可放同一台机器。允许开发宿主把模块放同进程，但不能换成另一套内存事务语义。附带数据库建表迁移、配置模板、平台模拟器和两个主播对战示例。

单机不承诺机器故障可用性；重启后从本地持久数据恢复。Linux 与 Windows 都要具备不依赖 Docker 的安装方法；Docker Compose 可作方便选项。

### 1.2 首期生产参考拓扑

| 故障域 | 建议角色 | 说明 |
|---|---|---|
| 应用节点 A | NodeAgent、ControlServer-1、DataServer-1、两类 Gateway、StreamerServer、GameServer | 可按资源拆开 |
| 应用节点 B | NodeAgent、ControlServer-2、DataServer-2、两类 Gateway、StreamerServer、GameServer | 与 A 不同物理故障域 |
| 数据节点 C | PostgreSQL primary | 受控数据库服务 |
| 数据节点 D | PostgreSQL synchronous standby | 与 C 不同物理故障域 |
| 接入层 | 健康检查的负载均衡 / DNS 入口 | 自身冗余独立配置 |
| 备份目标 | 数据库备份、WAL 归档、游戏包仓库 | 不放在唯一运行节点上 |

角色允许混合 Linux/Windows，房间跨 OS 恢复受模块兼容矩阵约束。资源不足时两台应用机可缩成一台，但不能继续宣称跨机接管；两容器同物理机不是两个故障域。

NodeAgent 的 bootstrap manifest 记录本机 seed slots、Control/Data 静态种子终点、集群 CA、包哈希及允许配置版本。ControlServer 不经过 DataServer 即可访问控制库，DataServer 启动也不依赖 ControlServer 发起的普通动态调度，从而解开启动循环。

### 1.3 端口和暴露面

| 接口 | 默认端口示例 | 暴露范围 |
|---|---|---|
| 主播 WSS | 443 → 7443 | 公网，TLS |
| 平台回调 | 443 → 7444 | 公网，按平台协议验签 |
| Admin HTTPS | 7445 | 受控管理网络 |
| Control gRPC | 7500 | 内网 mTLS |
| Data gRPC | 7501 | 内网 mTLS |
| Streamer / Game / Platform 内部 gRPC | 7510 / 7520 / 7530 起 | 实例端口由计划分配 |
| NodeAgent | 7540 | 内网 mTLS，亦可 Agent 主动建控制流 |
| PostgreSQL | 5432 | 仅 Control/Data/数据库管理节点 |

端口可配置，不允许多个 slot 默认抢占同一端口。Agent 启动计划分配端口并探测冲突，不能随机换端口后不更新注册。

## 2. OS 执行与自举

### 2.1 Linux

systemd 只监管 NodeAgent 自身；Agent 使用受限服务账户启动业务子进程。业务进程放入可识别的独立进程组，资源限制通过可用的 cgroup 能力设置；缺少权限时显示资源隔离能力缺失，不能假装限制有效。

Stop 先请求业务排空，等待 deadline 后终止整个进程组。控制子进程所属组的身份必须核对，不能只记 PID。Agent 崩溃后由 systemd 重启，启动时核对本机持久日志与实际实例，回收不能安全认领的遗留进程。

### 2.2 Windows

Windows Service Control Manager 自举 NodeAgent；Agent 为受管实例建立 Job Object，配置适当的子进程回收策略，记录实例标识和进程创建时间。优雅停止通过管理 RPC，deadline 后回收 Job 中进程；不假设 Windows 支持 Unix signal。

若选择 Job 的 kill-on-close 策略，Agent 崩溃会连带终止业务子进程：这是明确故障语义，所有权恢复仍由控制面执行。验收必须覆盖该行为及后台子进程，不能只测试一个无子进程的 demo。

### 2.3 首次启动

1. 安装数据库、初始化 schema 和受限账户，配置备份；HA 部署验证同步复制。
2. 在受控管理端创建 ClusterId、CA、应用配置和节点一次性注册凭据。
3. 安装 NodeAgent 与签名 bootstrap manifest，启动系统服务。
4. bootstrap slots 启动 Control/Data，报告实例；管理员验证版本和依赖 readiness。
5. 提交 DeploymentSpec，Agent 安装校验包、启动普通角色。
6. 配置平台应用与回调地址、启用已验证适配器、执行真实测试直播。
7. 先运行示例对局与恢复验收，再开放普通流量。

安装流程输出每步状态与失败理由。AppSecret、DB 密码、注册秘密不能出现在示例仓库、命令历史或日志中。

### 2.4 证书自举

受控离线安装工具生成或导入 Cluster root，签发 NodeId 证书和节点受限签发资格，同时签署允许的角色/节点/包清单。节点私钥放入 Windows 受限证书存储或 Linux 仅服务账户可读的秘密文件；根私钥不留在普通节点。

Agent 启动每个进程时先生成 ProcessInstanceId，为该实例生成短期证书（默认 24 小时），经受限本地 IPC/秘密文件交付，不通过启动参数传私钥。证书包含 NodeId、ClusterId、Role、ProcessInstanceId；接收方额外检查根签名节点授权清单，限制节点签发者只能给自己的允许角色发证。应用级角色检查是必要步骤，不能只依赖 X.509 链验证。

bootstrap Control/Data seed 使用这条离线信任链，无需在线发证服务。节点证书续期与权限变更在线审核；短期进程证书可由已授权 Agent 在节点授权有效期内续签。节点授权或证书过期时停止授予新权限并告警，不自动关闭 TLS 验证。CA 轮换使用新旧根重叠窗口，撤销事件进入控制面与本地缓存，失联时遵守证书期限。

## 3. 故障行为矩阵

| 故障 | 检测者 | 业务行为 | 恢复动作 | 必须保持的不变量 |
|---|---|---|---|---|
| 主播网络断开 | Gateway | 当前局继续，个人实体离线保留，取消未提交排队默认策略 | resume 找回原局并快照同步 | 不重新匹配、不重复奖励 |
| Gateway 进程崩溃 | Agent、客户端 | 连接断开；平台输入与游戏继续 | 连接其他 Gateway，SessionVersion 接管 | 旧连接无控制权 |
| StreamerServer 崩溃 | Agent、Control | 个人命令暂不可用；GameServer 已运行对局继续 | 新 epoch 加载资料、挂载、会话和待办流程 | 不因实体恢复重开局 |
| GameServer 崩溃 | Agent、Control | 房间暂停输出，可靠输入留存 | 新 owner 恢复快照和帧 | 已确认输入不丢、旧 owner 不写 |
| 平台接入实例崩溃 | Agent、负载均衡 | callback 转其他实例；任务等待接管 | 从 Task/Outbox 恢复并查外部状态 | 不把结果未知等同调用失败 |
| 单个 DataServer 崩溃 | 调用端、Agent | 保持 OperationId 改连其他实例 | 查询 receipt 后重试 | 不重复写入或结算 |
| 所有 DataServer 不可用 | 调用端 | 暂停新确认和权威帧 | Agent/bootstrap 恢复，补查未知结果 | 不用本地内存假确认 |
| 一个 ControlServer 崩溃 | 租约继任者 | 有效 owner 继续运行 | term 接管协调任务 | 不重复调度或提交匹配 |
| 全部 ControlServer 不可用 | 各角色 | 租约有效期内继续；到期暂停；不新匹配 | bootstrap 拉起，恢复目录 | 不本地自选 owner |
| 节点与控制面分区，但能访问 Data | Control / 角色 | 有效租约内提交，到期拒绝 | 新 epoch 接管后旧实例必须失效 | DB fencing 不依赖踢下线送达 |
| 节点与 Data 分区，但可达客户端 | 角色 | 不发布未提交状态 | 恢复链路或异机恢复 | 不产生无日志胜负 |
| Agent 崩溃 | OS 服务 | 按 OS 子进程策略运行或终止；不擅自判定全体业务死亡 | 重启 Agent、核对 incarnation | PID 重用不误杀 |
| 机器失电 | Control | 所有本机房间待恢复，N+1 容量承接 | 其他机器分配新 epoch | 接管不等待原机回来 |
| 数据库主库失效 | 运维、Data | 停止持久确认与权威提交 | 隔离旧主，确认同步备库，受控晋升 | 唯一可写主库 |
| 同步备库失联 | 数据库监控 | 关键提交可能阻塞，应用超时查询结果 | 修复备库；不自动降级异步承诺 | 不偷偷降低 RPO |
| 平台 API 限流或失效 | PlatformGateway | 退避，区分已运行能力与新启动能力 | token 单飞刷新、任务对账 | 不在全实例并发刷 token |
| 快照损坏 | GameServer | Recovering，拒绝展示恢复成功 | 前一快照+连续日志，失败转人工/补偿 | 不忽略哈希错误 |
| 模块 Tick 卡死 | watchdog、Agent | 整进程房间暂停 | 终止实例、恢复其他进程、隔离错误包 | 不无限自动重启风暴 |
| 结算中宕机 | 结算器继任者 | 显示 SettlementPending | 重试未完成 grant | 每笔奖励最多生效一次 |
| 磁盘压力 | Data/Control | 停止新匹配，限制新可靠接收并告警 | 扩容、合规归档、恢复服务 | 不删未处理可靠输入 |

主播断线取消排队的边界：Queued 可取消；Reserved 是否取消由同一 ticket 事务决定；Committed 已属当前局，按离线保留执行。掉线通知必须携 session/connection incarnation，旧连接的迟到断线不能把新连接标成离线。

## 4. 恢复 SLO 与边界

这些是验收目标，尚非实测 SLA。

- 客户端重连到健康入口后，基础模型下 p95 ≤3 秒拿到当前完整快照，不含客户端网络不可用时间。
- 已确认进程退出、数据库和其他容量正常时，Room 恢复目标 p95 ≤15 秒；仅能靠租约判断的机器失联，包含检测在内 p95 ≤30 秒。
- 恢复目标按 100 房间基线及约 100 帧重放窗口测试；大模块和多节点同时故障必须另报结果。
- 数据库人工切换目标 ≤15 分钟，明确不等于房间计算进程 30 秒目标。需要更高数据库可用性时可采用经过验证的托管 HA，但仍须唯一主库和已确认数据保证。
- 已确认关键输入、帧和奖励在同步副本承诺的单故障范围内 RPO=0；全存储域损毁的 RPO 由备份/WAL 恢复点决定，不能宣称仍为 0。
- 平台从未送达或平台重试耗尽而 Chorus 未确认的数据不在本地 RPO 保证内；可补拉类别须对账，无法补拉类别必须暴露遗漏风险和业务处理结果。

## 5. 操作手册

### 5.1 GameServer 排空

设置 Admission=Draining → 停止分配新局 → 已有局自然结束；超出排空期限时按同版本快照迁移。迁移开始前停止本房间候选新帧，提交最后已确认帧和快照，控制面 CAS 换 epoch，新实例恢复后切换路由。无法完成则保留旧 owner 或暂停，不两端同时运行。

### 5.2 数据库主库切换

暂停入口确认与管理写入 → 隔离旧主网络和写权限（无法确认隔离则不晋升）→ 确认候选同步副本及可恢复 WAL → 晋升并更新受控数据库终点 → 重新连接所有 Control/Data 实例 → 查询超时 operation receipts → 核对所有权并恢复协调 → 验证一局和一笔幂等资产操作 → 开放流量。

不得在旧主可写且可达部分应用时修改 DNS 了事。已经响应成功的事务必须出现在新主；未收到响应的事务可能存在也可能不存在，保持同 OperationId 查询。

### 5.3 房间恢复失败

记录 RoomId、PackageHash、SnapshotId、失败 Tick 和异常 → 保持 Suspended → 尝试上一有效快照并重放 → 仍失败则保留输入和证据 → 执行玩法预定义 AbortResult 与补偿清单 → 幂等结算 → 对客户端发布中止原因。

补偿指游戏内资产或权益；平台退款只产生待处理凭据/任务，不自动调用未授权的支付能力。禁止运维直接改房间胜负 JSON 跳过账本。

### 5.4 外部任务结果未知

停止冲突任务 → 使用官方状态查询 → 已生效则记录原 Operation 完成 → 未生效且具备安全重试条件才重发 → 不可查询/不可幂等时进入 NeedsAttention，并允许受审计人工裁决。没有证据不能显示成功。

### 5.5 游戏版本回滚

停止新版本新局 → 隔离有问题包 → 已运行旧版本局继续 → 对新版本局执行兼容恢复或中止 → 只有 manifest/state schema 兼容才可切换二进制；数据库 contract 删除必须延后至无旧实例。

## 6. 可观测性与管理命令

管理页面应能从 StreamerId 找到当前登录、直播挂载、平台任务、匹配 ticket、RoomId、owner 实例与节点，并显示各信息的更新时间与来源。观察表不可反向修改权威记录。

提供命令：DrainNode、DrainProcess、RestartProcess、QuarantinePackage、RetryRoomRecovery、SuspendMatchQueue、ResumeMatchQueue、ReconcilePlatformTask、RetrySettlement、RotateCredential。破坏性操作要求显式 scope 和 reason，并写审计；不能通过手工 SQL 作为日常业务操作方式。

健康报告至少包含：连接/认证错误、平台 callback 延迟、收件箱积压年龄、Tick 耗时、帧提交耗时、快照大小、恢复进度、资产失败、匹配等待、预留冲突、路由版本落后、所有权拒绝、进程重启计数、CPU/内存/磁盘。按 GameId/节点/角色聚合，不把每个玩家作为指标标签。

## 7. 默认配置示例

以下为待实现配置格式，不应直接复制到当前应用并期待生效。

```yaml
cluster:
  region: local-region
  ownerLeaseSeconds: 15
  ownerRenewSeconds: 5
  controlSeeds: ["https://control-a:7500", "https://control-b:7500"]
  dataSeeds: ["https://data-a:7501", "https://data-b:7501"]
runtime:
  tickMilliseconds: 50
  snapshotIntervalSeconds: 5
  maxCatchupTicks: 2
  maxSnapshotBytes: 1048576
matching:
  reservationSeconds: 15
  startGraceSeconds: 10
  crossPlatform: false
persistence:
  provider: PostgreSql
  connectionSecretRef: chorus/database
  profileFlushSeconds: 5
  acknowledgeOnlyAfterCommit: true
agent:
  maxRestartsPerTenMinutes: 5
  maxRestartBackoffSeconds: 30
  requireSignedManifest: true
```

MaxRoundDuration、离线保留、每帧输入预算及平台必需能力属于玩法 manifest，不能所有游戏共享一个未经评估的全局值。
