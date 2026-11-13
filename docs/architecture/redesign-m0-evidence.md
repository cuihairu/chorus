---
title: M0 验收证据
---

# M0 验收证据

更新：2026-11-13。本文记录可复跑结果，不将构建结果视为宿主运行或双系统验收。

## Linux / PostgreSQL

| 证据 | 结果 | 实际覆盖 |
|---|---|---|
| 空库 SchemaTool | PASS | 在独立 PostgreSQL 数据库以 `CHORUS_DATABASE_CONNECTION_STRING` 执行 `Chorus.SchemaTool`，迁移版本 1–4 全部落表且各含 64 位 SHA-256 checksum；随后删除临时数据库。 |
| 真实 host 集成 | PASS | `RedesignM0HostTests.EmptyDatabase_RealGrpcHostsEnforceRoleTenantEpochAndDurableReceipt` 为每次运行创建空数据库、应用迁移并启动 ControlServer/DataServer 两个独立 dotnet 进程；通过 mTLS gRPC 验证 readiness、streamer/game 角色隔离、租户隔离、资产提交/原 key 查收据/重试、owner epoch handoff 后拒绝旧 owner，以及 NodeAgent 进程报告。 |
| 事务存储 | PASS | `RedesignM0StoreTests` 覆盖 profile CAS、资产账本与幂等冲突、owner fencing/接管及 EntityRecovery 与 ProcessReady 分离。 |
| 身份证书 | PASS | `RedesignProcessCertificateTests` 覆盖节点授权范围及进程 SPIFFE 身份校验。 |
| Release 全量测试 | PASS | `CHORUS_TEST_POSTGRES=… dotnet test Chorus.sln -c Release --verbosity minimal`，1152/1152。Chorus.sln 仅含 Chorus.Engine.Tests 一个测试工程，故等价于该工程全量运行；结果含 M0 定向集成组 7/7、两个真实 host 集成用例与身份存储用例。 |
| Release 全解方案构建 | PASS | `dotnet build Chorus.sln -c Release -m:1 --verbosity minimal`，0 errors；仅现有 NU1510 包引用提示。 |
| Linux NodeAgent 离线冷启动/崩溃恢复 | PASS | `RedesignM0HostTests.SignedBootstrapManifest_StartsAndReclaimsSeedHostsAfterAgentCrash` 使用 BootstrapTool 生成签名 manifest；NodeAgent 启动真实 Control/Data seed host 并报告 Ready，强杀 Agent 后新 incarnation 回收旧 slot 进程并重新注册两个 Ready 进程。本轮修复了测试对 stdout EOF 的无界等待（setsid 子进程继承重定向管道导致挂起），并为 dispose 增加基于 process-journal 的孤儿 seed 进程回收，两用例实跑通过（20 s）。 |
| Linux 实机安装与升级（systemd，2026-11-13） | PASS | 本机（Linux KVM 虚拟机，systemd 运行中，本机 PostgreSQL 18）真实执行：`sudo CHORUS_ENV_TEMPLATE=… install-node-agent.sh` 七项 [OK] 全过（系统用户/组 nologin、`/var/lib/chorus/agent` 0700、NodeAgent 发布至 `/opt/chorus/node-agent`（root:chorus-agent 0750/文件 0640）、unit 安装（ExecStart=/usr/local/bin/dotnet）、env 文件 0640 root:chorus-agent、`systemd-analyze verify` + daemon-reload），EXIT 0。部署材料：ControlServer/DataServer/SchemaTool/BootstrapTool 以 linux-x64 framework-dependent 发布至 `/opt/chorus/{seeds,tools}`（同权限模型）；PostgreSQL 专用角色 `chorus-agent` + 自有数据库（peer auth：OS 用户=角色）；以 chorus-agent 身份跑 SchemaTool → "schema is current"（52 张表 / 10 个命名 schema 全部 chorus-agent 属主）。BootstrapTool（root）对最终 seed 路径铸签 cluster `chorus-local`/node `node-1`，trust 材料装 `/etc/chorus`（0640 root:chorus-agent），offline root.pfx 不落节点。`systemctl enable --now` → **active**：agent 以 chorus-agent 身份在 unit 加固集（ProtectSystem=strict/ProtectHome/PrivateTmp/NoNewPrivileges）下运行；journal 显示两 slot（node-1/control-server、node-1/data-server）启动、冷启动控制面短暂 Unavailable 自愈、对双 seed 报 Ready（m0.ControlService/Ready 200）；seeds 以 chorus-agent 跑真实 TLS gRPC（control→data MatchQueue/Match 200 流量、7500/7501 监听）；状态目录 0700 内有 per-instance 证书目录 + process-journal.json。遥测入 `ops.machine_sample`/`ops.process_sample`（control-server、data-server）/`ops.machine_disk_sample`。**升级验收**：重跑安装脚本七项 [OK] 幂等复过 + seeds 重发布 + `systemctl restart` → active；新 agent incarnation（新证书目录、新 agent_instance_id 续报遥测、旧 incarnation 最后话语留存）、对既有库 Ready 复报（VerifyAsync 幂等过）。诚实边界：单台开发/KVM 节点（非专属干净目标机）；升级为同位重装 + 重启（幂等 reinstall/restart，无版本偏斜/数据迁移升级路径）；仅 Linux（Windows 各行维持 NOT RUN）；验收后已完整 teardown（服务/unit/env/trust 材料/安装树/用户组/数据库角色全清，仓库树零 root 属主残留、git 零变更）。journal 存档 /home/cui/env07_install_journal.log（2518 行）、/home/cui/env07_upgrade_journal.log（2740 行）。 |

## Windows

| 证据 | 结果 | 实际覆盖 |
|---|---|---|
| Windows 目标构建 | PASS（交叉编译） | `dotnet build src/Chorus.NodeAgent/Chorus.NodeAgent.csproj -c Release -r win-x64 --verbosity quiet -m:1`，0 warnings、0 errors。 |
| Linux 安装配置 | PASS（语法级 + 实机） | `deploy/linux/chorus-node-agent.service` 通过 `systemd-analyze verify`（占位符替换真实 dotnet 路径后）；`deploy/linux/install-node-agent.sh` 通过 `bash -n` 与 `shellcheck` 零告警。实机安装/启动/升级已于 2026-11-13 在 Linux 节点执行并验收，见「Linux / PostgreSQL」表「Linux 实机安装与升级（systemd）」行。 |
| Windows 安装配置 | NOT RUN（脚本就绪） | `deploy/windows/install-node-agent.ps1`（服务账号随机服务密码不落盘、状态目录 ACL、服务注册、失败自动重启）通过 PowerShell 解析器语法检查；实际安装、服务启动、Job Object 回收与证书存储验收需要 Windows 实机，保持 NOT RUN。 |
| Windows Service / Job Object 实机 | NOT RUN | 当前执行环境为 Linux；交叉编译不证明 Windows Service 启停、Job Object 孙进程回收、状态目录 ACL 或证书存储行为。 |
| Windows 空库冷启动 | NOT RUN | 没有 Windows 主机上的 PostgreSQL/DataServer/ControlServer/NodeAgent 端到端验收。 |

## 仍未通过的 M0 出口

M0 总体验收仍保持 open。关闭前还需要：

1. Windows 主机执行空库启动、Windows Service、Job Object 子进程树、受限状态目录 ACL 与证书保护验收。
2. 双 OS 实机安装/升级验收：Linux 侧已于 2026-11-13 在 Linux 节点完成实机安装、systemd 启动与升级（重装幂等 + 重启）验收（见「Linux / PostgreSQL」表）；Windows 侧的实机安装、服务启动、升级包与签名包流程仍未执行（开发平台模拟器见 `docs/architecture/redesign-m1-implementation.md`）。

其余 AT18/23/27 的基础部分及 AT32 身份隔离已有上述 Linux PostgreSQL/真实宿主进程证据，但不替代以上缺项或 M0 完整出口。详情见[验收计划](redesign-acceptance.md)与[M0 实现边界](redesign-m0-implementation.md)。
