---
title: M0 验收证据
---

# M0 验收证据

更新：2026-10-27。本文记录可复跑结果，不将构建结果视为宿主运行或双系统验收。

## Linux / PostgreSQL

| 证据 | 结果 | 实际覆盖 |
|---|---|---|
| 空库 SchemaTool | PASS | 在独立 PostgreSQL 数据库执行 `Chorus.SchemaTool`，迁移版本 1–4 全部落表且含 SHA-256 checksum；随后删除临时数据库。 |
| 真实 host 集成 | PASS | `RedesignM0HostTests.EmptyDatabase_RealGrpcHostsEnforceRoleTenantEpochAndDurableReceipt` 为每次运行创建空数据库、应用迁移并启动 ControlServer/DataServer 两个独立 dotnet 进程；通过 mTLS gRPC 验证 readiness、streamer/game 角色隔离、租户隔离、资产提交/原 key 查收据/重试、owner epoch handoff 后拒绝旧 owner，以及 NodeAgent 进程报告。 |
| 事务存储 | PASS | `RedesignM0StoreTests` 覆盖 profile CAS、资产账本与幂等冲突、owner fencing/接管及 EntityRecovery 与 ProcessReady 分离。 |
| 身份证书 | PASS | `RedesignProcessCertificateTests` 覆盖节点授权范围及进程 SPIFFE 身份校验。 |
| Release 全量测试 | PASS | `CHORUS_TEST_POSTGRES=… dotnet test Chorus.sln -c Release -m:1 --verbosity minimal`，1150/1150。 |
| Release 全解方案构建 | PASS | `dotnet build Chorus.sln -c Release -m:1 --verbosity minimal`，0 errors；仅现有 NU1510 包引用提示。 |
| Linux NodeAgent 安装/冷启动 | NOT RUN | Agent 的 systemd host 和 Linux 进程树代码已构建；本轮未以 BootstrapTool 产物运行完整离线冷启动、Agent 崩溃回收验收。 |

## Windows

| 证据 | 结果 | 实际覆盖 |
|---|---|---|
| Windows 目标构建 | PASS（交叉编译） | `dotnet build src/Chorus.NodeAgent/Chorus.NodeAgent.csproj -c Release -r win-x64 --verbosity quiet -m:1`，0 warnings、0 errors。 |
| Windows Service / Job Object 实机 | NOT RUN | 当前执行环境为 Linux；交叉编译不证明 Windows Service 启停、Job Object 孙进程回收、状态目录 ACL 或证书存储行为。 |
| Windows 空库冷启动 | NOT RUN | 没有 Windows 主机上的 PostgreSQL/DataServer/ControlServer/NodeAgent 端到端验收。 |

## 仍未通过的 M0 出口

M0 总体验收仍保持 open。关闭前还需要：

1. 在 Linux 用离线签名 manifest + BootstrapTool 实际冷启动 NodeAgent，验证 Control/Data seed slots、ControlServer 离线时本地监管、Agent 崩溃/重启回收和状态恢复。
2. 在 Windows 主机执行空库启动、Windows Service、Job Object 子进程树、受限状态目录 ACL 与证书保护验收。
3. 补齐计划中列出的平台模拟器和可复现安装配置；目前有 BootstrapTool/SchemaTool 骨架，没有完整安装/升级包或平台模拟器。

其余 AT18/23/27 的基础部分及 AT32 身份隔离已有上述 Linux PostgreSQL/真实宿主进程证据，但不替代以上缺项或 M0 完整出口。详情见[验收计划](redesign-acceptance.md)与[M0 实现边界](redesign-m0-implementation.md)。
