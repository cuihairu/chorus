# 文档–代码一致性审计（2026-10-13）

> 历史审计快照。其 41 项声明的计数描述的是 2026-10-13 的代码树，而非当前欠账；
> 双语修正与当前文档状态在根 `todo.md` 中跟踪。

审计范围：README.md、README.zh.md、docs/ 下全部 30 页 + .vuepress/config.js + 公共面（GitHub About / homepageUrl / topics）。
方法：枚举文档面 → 逐条提取声明 → 逐条以 `file:line` 证据核对（在断言"未实现"之前先确认搜索路径存在）→ 归入三类 → 以代码为事实源修复。

## 分桶计数

| 类 | 计数 |
| --- | --- |
| 文档超前于代码（危险） | 6 |
| 代码超前于文档（缺失） | 7 |
| 描述不符 | 28 |
| **合计需改** | **41** |

> 注：部分发现跨多行（legacy 4 行、review 10 行）；41 按声明计数。

## P0 清单

**无 P0。** 每个端口、端点、配置键与默认值声明都与源码逐字相符。

## P1 清单（3 项）

| ID | 页面 | 问题 |
| --- | --- | --- |
| O1 | observability.md | 声称"Gateway 与 EngineHost 各自计数发布失败"，但 EngineHost 既不计数也不暴露 `chorus_ipc_publish_failures_total` |
| P1 | platform-douyin.md | 声称"已知限制：TryMark 非原子，发布失败被计为重复"，但代码已原子化（TryReserve + Rollback） |
| P2 | platform-kuaishou.md | 同 P1 |

## 审计表

| ID | 页面 | 声明 | 源码证据（file:line） | 类 | 严重级 | 修复 |
| --- | --- | --- | --- | --- | --- | --- |
| R-1 | README.md | `.NET net10.0 (see global.json)` | `global.json:2-3` 固定 SDK `10.0.401`；`net10.0` 在 `src/*/*.csproj:4` | 描述不符 | P2 | 改为"TargetFramework 在 csproj；global.json 固定 SDK" |
| R-2 | README.md | "there is no automated release" | `release.yml:4-8,33-35` 在 `v*` tag 推送时自动创建 Release | 描述不符 | P2 | 改为"无定时发版；release.yml 仅在 v* tag 推送时运行" |
| RZ-1 | README.zh.md | 同 R-1 | 同 R-1 | 描述不符 | P2 | 同 R-1 |
| RZ-2 | README.zh.md | 同 R-2 | 同 R-2 | 描述不符 | P2 | 同 R-2 |
| DC-1 | decisions.md | D1 "Adapters.Platforms references Protocol only" | `Chorus.Adapters.Platforms.csproj:16-17` 引用 Abstractions + Protocol | 描述不符 | P2 | 改为"引用 Protocol 与 Adapters.Abstractions" |
| DC-2 | decisions.md | D5 "log categories Chorus.Ingress / Chorus.StreamerWs" | 无 `Chorus.Ingress` 类目（搜过 src/） | 文档超前于代码 | P2 | 改为"Chorus.StreamerWs；入口面暂无独立日志类目" |
| AO-1 | architecture-overview.md | mermaid `SA -.->|authoritative snapshot V=1 wire routing| GM` | 快照从 GM 读状态（`SessionActor.cs:451`）→ SA 发给客户端 | 描述不符 | P2 | 反转为 `GM -.-> SA` |
| AO-2 | architecture-overview.md | mermaid `SA --> CPU` | `ProcessCpuGauge` 是进程级且仅 EngineHost `/metrics` 采样 | 描述不符 | P2 | 改为 `ENGIPC -.-> CPU` |
| C1 | client-resync-state-machine.md | 推荐 maxLagTicks 3~5 / >5~10 | `SessionConfig.cs:8` = 50 | 描述不符 | P1 | 注明代码默认 50 |
| C2 | client-resync-state-machine.md | 服务端配合要求漏掉已暴露的字段 | `SessionClientDiagnostics` 有 lagTicks/lastAckAgeMs/resyncCount/lastResyncAgeMs | 代码超前于文档 | P2 | 加"服务端已暴露每客户端诊断字段" |
| B1 | frame-sync-best-practices.md | 推荐 maxLagTicks 6~10 | `SessionConfig.cs:8` = 50 | 描述不符 | P1 | 注明代码默认 50 |
| B2 | frame-sync-best-practices.md | 推荐 snapshotEveryTicks 2~5 | `SessionConfig.cs:6` = 1 | 描述不符 | P2 | 注明代码默认 1 |
| B3 | frame-sync-best-practices.md | 可观测性需要"每客户端出站队列积压" | `OutboundClientQueue.Dropped` 不在 SessionDiagnostics/指标中 | 文档超前于代码 | P2 | 注明尚未暴露，待导出 |
| F1 | frame-sync.md | "ClientAck carries only lastAppliedTickId" | `ClientAck.cs:3-9` 还有 sessionId/clientId/clientTimeMs | 描述不符 | P2 | 改为"仅把 lastAppliedTickId 作为其进度字段" |
| F2 | frame-sync.md | 链接 `guides/quickstart-frame-sync.md` | 实际路径是 `docs/guide/quickstart-frame-sync.md` | 描述不符 | P2 | 改为 `../guide/quickstart-frame-sync.md` |
| L1-L4 | legacy-real-server-analysis.md | 旧项目 MultGameInfo/ServerLogic/MaxServerFrame | 旧项目源码未随本仓库发布（src/ tests/ 零命中） | 文档超前于代码 | P2 | 顶部加"外部项目，不随本仓库发布" |
| P1 | process-topology.md | §4 引用 §5.1/§5.2/§5.3 标题 | §5 无这些三级标题 | 描述不符 | P2 | 加上 §5.1/§5.2/§5.3 标题 |
| R1-R10 | review-2026-10-12-chatgpt.md | 正文十条问题断言在一/二期已修复 | 78f2597/5592d62/499f01c/df71d26/a5f23b8/362b435/def21d2 等 | 描述不符 | P2 | 顶部加"评审时点状态"时效注记 |
| S1 | shared-memory-transport.md | 当前状态 150 条消息/秒；阶段 A 未标记交付 | 50f0045 后约 15 条消息/秒；阶段 A 已交付 | 描述不符 | P2 | 改为优化后数字并标记阶段 A 已交付 |
| SR1 | snapshot-restore.md | EngineIpcService.cs:125 | 实为 :152（CreateActor:150-156） | 描述不符 | P2 | 改为 :152 |
| SP1 | spec.md | "all downstream data carries tickId and serverTimeMs" | welcome/pong 无 tickId；仅快照有 | 描述不符 | P2 | 改为"处处带 serverTimeMs；tickId 随快照" |
| BI1 | bilibili-danmaku-adapter.md | `kind=enter` | `AudienceEventKind` 只有 Unknown/Comment/Like/Gift | 文档超前于代码 | P2 | 注明需要新增 Enter 成员 |
| BI2 | bilibili-danmaku-adapter.md | "each platform = one mapper + one verifier" | 还有 IPlatformNamedEventVerifier + PlatformEventVerifierRouter | 代码超前于文档 | P2 | 补上命名校验器/路由缝 |
| GS-01 | getting-started.md | 端点列表漏 /metrics /matches /faces | `Program.cs:109/105/106` | 代码超前于文档 | P2 | 加这三个端点 |
| GS-02 | getting-started.md | Node.js 推荐 >= 20 | package.json 无 engines；vuepress 1.9.10 支持 node >= 8 | 文档超前于代码 | P2 | 改为"任意受支持的 LTS，未设下限" |
| SM-01 | split-mode.md | EngineHost 只做 tick + 快照 | 它还有 /health + /metrics (:5067) | 代码超前于文档 | P2 | 加 HTTP 面 |
| SM-02 | split-mode.md | IPC 配置漏 HeartbeatIntervalMs/ReadinessMaxSilenceMs | `EngineIpcService.cs:61`，`GatewayIpcService.cs:68` | 代码超前于文档 | P2 | 加这两个键 + Gateway:Id/GatewayIds |
| LT-01 | load-testing.md | `--engine-metrics` 结束时抓取 | 运行期间每 `--report-every` 采样取峰值（`Program.cs:93-105`） | 代码超前于文档 | P2 | 改为"运行期间采样取峰值" |
| GM-01 | game-module.md | "this tick's state does not advance" | TickId 会推进（:391）；只是未发送快照（异常在 :478 之前） | 描述不符 | P2 | 改为"本 tick 未发快照" |
| QF-01 | quickstart-frame-sync.md | 每帧一次 ack | AckPerMinute=600；66ms tick ≈ 909/分钟 | 代码超前于文档 | P2 | 加 ACK 速率限制约束 |
| A1 | api.md | userId required=yes | 映射器默认其为匿名 | 描述不符 | P2 | 改为"否（默认匿名）" |
| A2 | api.md | eng.snapshot 源自 EngineIpcService.cs + WireSnapshot | 实际直接构造于 EngineBusSessionClient.cs:49-59 | 描述不符 | P2 | 修正来源 |
| D1 | deployment.md | /ready = 窗口内心跳 | 任何引擎消息都会刷新它（GatewayIpcService.cs:158-160） | 描述不符 | P2 | 改为"任何引擎消息" |
| O1 | observability.md | Gateway 与 EngineHost 各自计数发布失败 | EngineHost 的 SafePublishAsync 吞异常不计（:64-70） | 描述不符 | P1 | 改为"仅 Gateway 面发出" |
| O2 | observability.md | ingress_timed_out_total 只计 503 | 它也计 504（PlatformIngressFace.cs:133） | 描述不符 | P2 | 加"和 504" |
| P1 | platform-douyin.md | 已知限制：TryMark 非原子 | 已改为 TryReserve（原子 TryAdd）+ Rollback | 描述不符 | P1 | 删掉限制，标记已修复 |
| P2 | platform-kuaishou.md | 同 P1 | 同 P1 | 描述不符 | P1 | 同 P1 |
| P3 | platform-kuaishou.md | "replace MapInner" | 无 MapInner；内部字段提取在 TryMap | 描述不符 | P2 | 改为 TryMap |
| R1 | protocol.md | welcome 示例 inputDelayTicks:3 snapshotEveryTicks:2 | 默认是 2/1（SessionConfig.cs:5,7） | 描述不符 | P2 | 改为 2/1 |
| T1 | troubleshooting.md | serverTick | 实际字段名是 tickId | 文档超前于代码 | P2 | 改为 tickId |
| T2 | troubleshooting.md | events per tick | 无此字段（只有累计计数） | 文档超前于代码 | P2 | 改为相邻差值 |
| N1 | tuning.md | lastSnapshotBytes 若暴露 | 未暴露（只有 TotalSnapshotsOversize） | 代码超前于文档 | P2 | 注明未暴露；暂用超限指标 |

## 公共面

| 项 | 值 | 判定 |
| --- | --- | --- |
| description | "Server-authoritative interactive battle game framework..." | ✅ 英文、准确、当前 |
| homepageUrl | `https://cuihairu.github.io/chorus/` | ⚠️ 指向由此仓库 VuePress 构建的 Pages 站点；移除 VuePress 会断链（留待用户决定） |
| topics | authoritative-server/danmaku/game-server/.../kuaishou | ✅ 全部当前 |
| README 引言 | 英文主文档 + 中文交叉链接 | ✅ 双语交叉引用 |

## 新 vitepress 实例

不适用——本仓库已有 VuePress 1.x 站点（`docs/.vuepress/`，30 页 + mermaid 插件 + `docs.yml` 中的 Pages 自动部署）。

## 修复原则

全程以代码为事实源：删除超前于代码的声明、补上缺失描述、改写不符处；**不改任何代码**（本轮纯文档修复）。
