<p align="center">
  <a href="./audit-doc-consistency.md">English</a> | 简体中文
</p>

# 文档-代码一致性审计（2026-10-13）

审计范围：README.md、README.zh.md、docs/ 全部 30 页 + .vuepress/config.js + 公开门面（GitHub About / homepageUrl / topics）。
审计方法：枚举文档面 → 抽主张 → 逐条 `file:line` 取证（断言「没有实现」前先确认搜索路径存在）→ 三类分桶 → 以码为准修复。

## 三桶计数

| 类别 | 数量 |
| --- | --- |
| 文档超前于码（危险） | 6 |
| 码超前于文档（缺失） | 7 |
| 描述不符 | 28 |
| **合计需改** | **41** |

> 注：部分发现为多行合并（legacy 4 行、review 10 行），按主张计 41 条。

## P0 清单

**无 P0。** 所有端口、端点、配置键与默认值主张均与源码逐字一致。

## P1 清单（3 条）

| ID | 页 | 问题 |
| --- | --- | --- |
| O1 | observability.md | 声称「Gateway 与 EngineHost 各自统计发布失败」，但 EngineHost 不计数、不暴露 `chorus_ipc_publish_failures_total` |
| P1 | platform-douyin.md | 声称「已知限制：TryMark 非原子、发布失败被记为重复」，但代码已原子化（TryReserve + Rollback） |
| P2 | platform-kuaishou.md | 同 P1 |

## 审计表

| ID | 页 | 主张 | 源码证据(file:line) | 类别 | 严重度 | 修法 |
| --- | --- | --- | --- | --- | --- | --- |
| R-1 | README.md | `.NET net10.0 (see global.json)` | `global.json:2-3` 锁 SDK `10.0.401`；`net10.0` 在 `src/*/*.csproj:4` | 描述不符 | P2 | 改为「TargetFramework 见 csproj；global.json 锁 SDK」 |
| R-2 | README.md | 「there is no automated release」 | `release.yml:4-8,33-35` 在 `v*` tag 推送时自动建 Release | 描述不符 | P2 | 改为「无定时发版；release.yml 仅在推 v* tag 时运行」 |
| RZ-1 | README.zh.md | 同 R-1 | 同 R-1 | 描述不符 | P2 | 同 R-1 |
| RZ-2 | README.zh.md | 同 R-2 | 同 R-2 | 描述不符 | P2 | 同 R-2 |
| DC-1 | decisions.md | D1「Adapters.Platforms 仅引用 Protocol」 | `Chorus.Adapters.Platforms.csproj:16-17` 引用 Abstractions + Protocol | 描述不符 | P2 | 改为「引用 Protocol 与 Adapters.Abstractions」 |
| DC-2 | decisions.md | D5「日志分类 Chorus.Ingress / Chorus.StreamerWs」 | 无 `Chorus.Ingress` 类别（已搜 src/） | 超前于码 | P2 | 改为「Chorus.StreamerWs；Ingress 面暂无独立日志类别」 |
| AO-1 | architecture-overview.md | mermaid `SA -.->|权威快照 V=1 上线路由| GM` | 快照从 GM 读状态（`SessionActor.cs:451`）→ SA 发客户端 | 描述不符 | P2 | 反转为 `GM -.-> SA` |
| AO-2 | architecture-overview.md | mermaid `SA --> CPU` | `ProcessCpuGauge` 全进程水位，仅 EngineHost `/metrics` 采样 | 描述不符 | P2 | 改为 `ENGIPC -.-> CPU` |
| C1 | client-resync-state-machine.md | 推荐 maxLagTicks 3~5 / >5~10 | `SessionConfig.cs:8` = 50 | 描述不符 | P1 | 注明代码默认 50 |
| C2 | client-resync-state-machine.md | 服务端配合要求未提已暴露字段 | `SessionClientDiagnostics` 有 lagTicks/lastAckAgeMs/resyncCount/lastResyncAgeMs | 码超前于文档 | P2 | 补「服务端已按客户端维度暴露诊断字段」 |
| B1 | frame-sync-best-practices.md | 推荐 maxLagTicks 6~10 | `SessionConfig.cs:8` = 50 | 描述不符 | P1 | 注明代码默认 50 |
| B2 | frame-sync-best-practices.md | 推荐 snapshotEveryTicks 2~5 | `SessionConfig.cs:6` = 1 | 描述不符 | P2 | 注明代码默认 1 |
| B3 | frame-sync-best-practices.md | 可观测性需「单客户端出站队列积压」 | `OutboundClientQueue.Dropped` 未进 SessionDiagnostics/指标 | 超前于码 | P2 | 注明当前未暴露，待导出 |
| F1 | frame-sync.md | 「ClientAck 只有 lastAppliedTickId」 | `ClientAck.cs:3-9` 另有 sessionId/clientId/clientTimeMs | 描述不符 | P2 | 改为「只带 lastAppliedTickId 作为进度字段」 |
| F2 | frame-sync.md | 链接 `guides/quickstart-frame-sync.md` | 实际路径 `docs/guide/quickstart-frame-sync.md` | 描述不符 | P2 | 改为 `../guide/quickstart-frame-sync.md` |
| L1-L4 | legacy-real-server-analysis.md | 旧项目 MultGameInfo/ServerLogic/MaxServerFrame | 旧项目源码未随仓附带（src/ tests/ 零命中） | 超前于码 | P2 | 文首加「外部项目、未随仓附带」说明 |
| P1 | process-topology.md | §4 引用 §5.1/§5.2/§5.3 小节标题 | §5 无此三级标题 | 描述不符 | P2 | 补 §5.1/§5.2/§5.3 标题 |
| R1-R10 | review-2026-10-12-chatgpt.md | 正文 10 条问题断言已被阶段一/二修复 | 78f2597/5592d62/499f01c/df71d26/a5f23b8/362b435/def21d2 等 | 描述不符 | P2 | 文首加「评审时状态」时效说明 |
| S1 | shared-memory-transport.md | 现状 150 条/秒；阶段 A 未标已交付 | 50f0045 后 ~15 条/秒；阶段 A 已交付 | 描述不符 | P2 | 改为优化后口径 + 标阶段 A 已交付 |
| SR1 | snapshot-restore.md | EngineIpcService.cs:125 | 实际 :152（CreateActor:150-156） | 描述不符 | P2 | 改为 :152 |
| SP1 | spec.md | 「所有下发数据都带 tickId 与 serverTimeMs」 | welcome/pong 无 tickId；仅 snapshot 有 | 描述不符 | P2 | 改为「serverTimeMs 全带；tickId 随 snapshot」 |
| BI1 | bilibili-danmaku-adapter.md | `kind=enter` | `AudienceEventKind` 仅 Unknown/Comment/Like/Gift | 超前于码 | P2 | 注明需扩 Enter 成员 |
| BI2 | bilibili-danmaku-adapter.md | 「每个平台 = 一对 mapper + verifier」 | 还有 IPlatformNamedEventVerifier + PlatformEventVerifierRouter | 码超前于文档 | P2 | 补 named-verifier / router 缝 |
| GS-01 | getting-started.md | 端点清单缺 /metrics /matches /faces | `Program.cs:109/105/106` | 码超前于文档 | P2 | 补三个端点 |
| GS-02 | getting-started.md | Node.js 建议 >= 20 | package.json 无 engines；vuepress 1.9.10 支持 node >= 8 | 超前于码 | P2 | 改为「任意受支持 LTS，未设下限」 |
| SM-01 | split-mode.md | EngineHost 仅 tick + 快照 | 还有 /health + /metrics（:5067） | 码超前于文档 | P2 | 补 HTTP 面 |
| SM-02 | split-mode.md | IPC 配置缺 HeartbeatIntervalMs/ReadinessMaxSilenceMs | `EngineIpcService.cs:61`、`GatewayIpcService.cs:68` | 码超前于文档 | P2 | 补两个键 + Gateway:Id/GatewayIds |
| LT-01 | load-testing.md | `--engine-metrics` 结束时抓取 | 每 `--report-every` 采样取峰值（`Program.cs:93-105`） | 码超前于文档 | P2 | 改为「运行期采样取峰值」 |
| GM-01 | game-module.md | 「本 tick 状态不会推进」 | TickId 已推进（:391），快照未发送（异常在 :478 前） | 描述不符 | P2 | 改为「本 tick 不会下发快照」 |
| QF-01 | quickstart-frame-sync.md | 每帧回一个 ack | AckPerMinute=600；66ms tick ≈ 909/min | 码超前于文档 | P2 | 补 ACK 频控约束 |
| A1 | api.md | userId 必填=是 | 映射器缺省填 anonymous | 描述不符 | P2 | 改为「否（缺省 anonymous）」 |
| A2 | api.md | eng.snapshot 出处 EngineIpcService.cs + WireSnapshot | 实际 EngineBusSessionClient.cs:49-59 直接构造 | 描述不符 | P2 | 改出处 |
| D1 | deployment.md | /ready = 心跳在窗口内 | 任意引擎消息刷新（GatewayIpcService.cs:158-160） | 描述不符 | P2 | 改为「任意引擎消息」 |
| O1 | observability.md | Gateway 与 EngineHost 各自统计发布失败 | EngineHost SafePublishAsync 吞异常不计数（:64-70） | 描述不符 | P1 | 改为「仅 Gateway 面输出」 |
| O2 | observability.md | ingress_timed_out_total 仅 503 | 也计 504（PlatformIngressFace.cs:133） | 描述不符 | P2 | 补「以及 504」 |
| P1 | platform-douyin.md | 已知限制：TryMark 非原子 | 已改 TryReserve（TryAdd 原子）+ Rollback | 描述不符 | P1 | 删限制，标已修复 |
| P2 | platform-kuaishou.md | 同 P1 | 同 P1 | 描述不符 | P1 | 同 P1 |
| P3 | platform-kuaishou.md | 「替换 MapInner」 | 无 MapInner；内层字段抽取在 TryMap | 描述不符 | P2 | 改为 TryMap |
| R1 | protocol.md | welcome 示例 inputDelayTicks:3 snapshotEveryTicks:2 | 默认 2/1（SessionConfig.cs:5,7） | 描述不符 | P2 | 改为 2/1 |
| T1 | troubleshooting.md | serverTick | 实际字段名 tickId | 超前于码 | P2 | 改为 tickId |
| T2 | troubleshooting.md | 每 tick 事件数 | 无此字段（仅累计计数） | 超前于码 | P2 | 改为相邻差值 |
| N1 | tuning.md | lastSnapshotBytes 若暴露 | 未暴露（仅 TotalSnapshotsOversize） | 码超前于文档 | P2 | 注明未暴露，先用 oversize 指标 |

## 公开门面

| 项 | 值 | 结论 |
| --- | --- | --- |
| description | "Server-authoritative interactive battle game framework..." | ✅ 英文、准确、当前 |
| homepageUrl | `https://cuihairu.github.io/chorus/` | ⚠️ 指向本仓 VuePress 构建的 Pages 站；删除 VuePress 会死链（用户已拍板前不动） |
| topics | authoritative-server/danmaku/game-server/.../kuaishou | ✅ 全部当前 |
| README 首段 | 英文主文档 + 中文互链 | ✅ 双语互指 |

## 是否新建 vitepress

不适用——本仓已有 VuePress 1.x 站点（`docs/.vuepress/`，30 页 + mermaid 插件 + Pages 自动部署 `docs.yml`）。

## 修复原则

全部以码为准：删超前主张、补缺失描述、改不符措辞；**未改任何代码**（本轮纯文档修复）。
