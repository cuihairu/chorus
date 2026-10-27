---
title: M0 骨架实现边界与验收
---

# M0 骨架实现边界与验收

此页把[验收计划](redesign-acceptance.md)的 M0 落到可运行边界。M0 只证明空库、自举、基础资料/资产、进程报告、所有权隔离与身份认证的最短链路；匹配、真实平台、房间 Tick、结算和 HA 属后续阶段。

## 1. 存储与迁移

- PostgreSQL 是唯一业务写入存储。`Chorus.SchemaTool` 读取 `CHORUS_DATABASE_CONNECTION_STRING`，显式执行带 SHA-256 校验的有序迁移；DataServer 与 ControlServer 启动只检查版本，不执行 DDL。
- `0001_operation_receipt.sql` 建立跨租户/游戏/类型/目标的操作收据，`0002_core_schema.sql` 建立目标模型的逻辑表和关键唯一约束；`0003_ledger_target_scope.sql`、`0004_ownership_scope.sql` 以追加迁移修正账本目标作用域和 owner 租户/游戏作用域。结构存在不等于每张表已有业务服务。
- 迁移使用数据库事务和 advisory lock 串行化；同版本 SQL 内容改变时拒绝继续。新版本只能追加迁移，不改已应用脚本。
- DataServer 的关键业务写入与收据同事务；ControlServer 的所有权与节点状态只由控制边界写入。DataServer 需要校验所有权时锁 `control.ownership`，不依赖 Gateway 路由通知。

## 2. M0 最小服务契约

| 边界 | M0 方法 | 持久结果 | 授权角色 |
|---|---|---|---|
| DataServer | Get/PatchProfile | 版本化 `player.profile`，CAS 冲突显式返回 | StreamerServer |
| DataServer | Get/ChangeAsset | `player.asset` 与 `player.ledger` 同事务，余额不得负数 | StreamerServer |
| DataServer | GetOperation | 原 OperationId、请求指纹查询收据 | 原调用角色及租户 |
| DataServer | CommitFencedProbe | 锁 ownership 后校验 owner/epoch/租约，收据与副作用同事务 | GameServer |
| ControlServer | Register/ReportNode | Agent incarnation 与递增报告序号 | NodeAgent |
| ControlServer | ReportProcess | 实例和序号、ProcessReady 与 EntityRecovery 分列 | NodeAgent |
| ControlServer | Claim/GetOwnership | 原子 epoch 变更、旧 owner 写入失效 | ControlServer 协调者；只读查询按角色限制 |

内部方法走 TLS 上 gRPC/Protobuf。服务器必须验证客户端证书链、ClusterId、NodeId、Role、ProcessInstanceId 和节点授权范围；业务 tenant 来自经认证身份或已验证映射，不能由不可信请求自报。M0 可用离线签发的 bootstrap seed 身份，不能在测试配置中关闭认证来宣称 AT32 通过。

TLS 握手回调验证完整 X.509 链、节点授权扩展和 SPIFFE 身份，并将已验证身份按叶证书指纹暂存到进程内注册表；gRPC 请求上下文提供的叶证书可能不含中间证书，因此业务层只接受该注册表中、仍在有效期内的指纹。DataServer 另按调用角色限制 OperationKind：StreamerServer 可查 profile/asset 收据，GameServer 只能查 fenced operation。

## 3. NodeAgent 骨架

Linux 的 systemd、Windows Service 只启动 Agent；Agent 验证签名 bootstrap manifest，再拉起固定 Control/Data seed slots。普通动态计划在 Control 可用后获取。每个启动实例生成新 ProcessInstanceId，记录 PID、创建时间与 slot；停止时回收整个进程组（Linux）或 Job Object（Windows）。Agent 重启时不能只凭 PID 认领旧进程。

M0 骨架必须显式报告缺失的 cgroup/Job 限制能力，不得将未生效的资源限制显示为成功。当前 Agent 使用 Linux 进程树管理和 Windows Job Object 的 close-kill 行为；资源限额仍未执行。Windows 可交叉编译但不能以 Linux 上的构建结果代替 Windows 实机启动、子进程树与证书存储验收。可复现安装配置位于 `deploy/linux/`（systemd 单元 + 安装脚本：受限服务账号、0700 状态目录、占位符解析 dotnet 路径）与 `deploy/windows/`（服务账号随机服务密码、ACL、服务注册、失败自动重启）；两者只做到脚本/单元语法级验证，实机安装与启动验收见证据记录。

## 4. 验收证据口径

在空数据库上记录迁移、Control/Data seed 冷启动、NodeAgent 控制面离线启动、资料 CAS/资产并发、提交响应丢失后的原收据查询、旧 owner 写入拒绝、错误角色/租户证书拒绝，以及进程 Ready 但业务恢复失败的分列状态。证据需要真实 PostgreSQL 与实际宿主进程，不能只测存储私有方法。

每条证据标 PASS、FAIL 或 NOT RUN；Linux 与 Windows 单独报告。只在[验收计划](redesign-acceptance.md)列出的 M0 出口全部 PASS 时勾选 `todo.md` 第 21 节。

本轮实际证据见 [M0 验收记录](redesign-m0-evidence.md)。该记录区分空库真实 host 集成测试、构建检查与尚未执行的双系统 Agent 安装/冷启动门槛。
