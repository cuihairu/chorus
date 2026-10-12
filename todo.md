# TODO（按实现顺序）

## 0. 仓库基础

- [x] 建立 solution：`Cohort.sln`（当前目标框架为 `net9.0`；如需 LTS 可改为 `net8.0`）
- [x] 建立包结构（建议）：
  - `src/Cohort.Server`（HTTP + WebSocket + DI）
  - `src/Cohort.Engine`（Session actor、tick、buffer、backpressure）
  - `src/Cohort.Protocol`（消息与序列化）
  - `src/Cohort.Adapters.Abstractions`（平台适配器接口）
  - `src/Cohort.SampleGame`（最小示例 Game Module，用于验证帧同步链路）
  - `tests/...`（至少覆盖 tick/缓冲/降采样）
- [x] 加入 `docs/` 的目录结构与索引（已完成初稿）

## 1. Tick/帧同步（第一阶段：打通闭环，v1=权威状态快照）

- [x] 实现 `SessionActor`（单线程邮箱模型 + 固定 tick）
- [x] 实现 `AudienceEvent` 统一模型 + 稳定排序规则
- [x] 实现 `inputDelayTicks` 缓冲（按 tick 结算事件）
- [x] 实现 `maxEventsPerTick` + 合并/降采样策略（简单合并：同用户同类型累加）
- [x] 实现 WebSocket 协议：`hello/welcome/ack/snapshot`
- [x] 实现 per-client ack 跟踪与落后检测（`maxLagTicks`）与指标输出（`/sessions`）
- [x] 实现快照机制（全量 snapshot；序列化用 JSON）

## 2. 平台接入（开源只做接口与最小 stub）

- [x] `IPlatformEventMapper` / `IPlatformEventVerifier` 接口
- [x] `Platform Ingress`：HTTP endpoint 接收 event（示例 payload + 验签 stub）
- [x] 去重机制（基于 platformEventId + TTL）

## 3. Match/阵营对抗抽象

- [x] `Match` / `Faction` / `Session` 的关系建模
- [x] 多客户端订阅同一 `Match` 的状态（两个主播客户端看到同一局）
- [x] “观众加入阵营”的通用规则接口（弹幕口令/礼物映射）

## 3.5 通信与部署模式

- [ ] 抽象 `MessageBus/ActorRef/Router`（支持 Monolith 与拆分）
- [x] 提供 InProc bus（进程内）
- [x] 提供 NamedPipe bus（本机 IPC 示例）
- [x] 提供 Unix Domain Socket bus（本机 IPC 示例）
- [x] 提供 TCP transport（localhost / 容器）
- [x] 提供 HTTP transport（远程 / 网关层）
- [ ] 评估并规划共享内存 transport（MemoryMappedFile + FlatBuffers）

## 3.6 拆分模式（Gateway + EngineHost）

- [x] 新增 `Cohort.Gateway` 与 `Cohort.EngineHost` 两个进程
- [x] IPC 双向通道（Gateway->Engine 命令，Engine->Gateway 快照）
- [x] 把会话参数从 EngineHost 下发到 Gateway（避免两边配置漂移）

## 4. 可观测性与压测

- [ ] 指标：tick 耗时、事件数、丢弃/合并数、ack 延迟、落后 tick
- [ ] 日志结构化（最少：sessionId、tickId、clientId）
- [ ] 提供一个本地压测脚本（模拟 N 个观众事件 + 2 个客户端）

## 5. 文档定稿

- [ ] 把 `docs/spec.md` 定稿为“第一版可实现规格”（根据你确认的取舍：权威状态同步 vs 输入帧广播）
- [ ] 增加 `docs/protocol.md`（协议字段与示例）
- [ ] 增加 `docs/ops.md`（部署与参数调优：tick/inputDelay/maxEvents 等）

## 6. 文档站（VuePress）

- [x] 建立 VuePress 文档站（`docs/.vuepress` + `npm run docs:build`）
- [x] 整理架构/帧同步/旧服分析文档到 `docs/`
- [x] GitHub Pages 自动部署（`docs.yml`）

## 7. CI / 覆盖率 / 发布

- [x] CI：build + test（`ci.yml`）
- [x] 覆盖率上传（Codecov）
- [x] Tag 自动 Release（`release.yml`）


---

## 8. 实现分析与改进建议（2026-10-12 clone 分析，按优先级）

**现状快照（读码核实，非推测）**
- 11 个 csproj、`src+tests` 约 4100 行 C#；只有 1 个测试工程 `tests/Cohort.Engine.Tests`（9 个文件：reducer / faction rule / MatchRegistry / mapper / SampleGame + InProc/NamedPipe/TCP/HTTP/UDS 五种 transport）。
- 拆分模式已跑通：Gateway（`/ws`、`/ingress/{platform}`）+ EngineHost（权威 tick）+ `Cohort.Messaging` 四种 IPC transport，`/health` 三处都有，**`/metrics` 一处没有**。
- `Ingress/EventDeduplicator.cs`、`Ingress/TestPlatformEventMapper.cs`、`WebSocketHelpers.cs` 在 `Cohort.Server` 与 `Cohort.Gateway` 各存一份（diff 除命名空间外一致）。

### P0 — 平台对接与安全（先做）

- [ ] **抖音平台对接**：`Cohort.Adapters.Abstractions` 已有 `IPlatformEventMapper` / `IPlatformEventVerifier` / `IAudienceFactionRule`，但仓库里只有 `TestPlatformEventMapper`（拼装测试数据）与 `AllowAllPlatformEventVerifier`（无条件放行）。新增 `DouyinPlatformEventMapper` + `DouyinPlatformEventVerifier`：抖音开放平台直播事件回调接入（弹幕/礼物/点赞→`AudienceEvent`），回调验签与事件类型映射按官方文档实现；`/ingress/douyin` 路由挂上，去重继续走 `EventDeduplicator`（platformEventId+TTL）。
- [ ] **快手平台对接**：同上，`KuaishouPlatformEventMapper` + `KuaishouPlatformEventVerifier`，`/ingress/kuaishou`；两家的鉴权材料（app secret / 回调加密）走配置，不硬编码。
- [ ] **两个平台的联调用例**：官方样例 payload 进仓做金测（签名正确通过、篡改拒绝、重复 event id 去重、未知事件类型不崩）。
- [ ] **WebSocket 接入鉴权**：`hello` 目前无凭据，任何人可连 `/ws` 并收快照（`docs/design/spec.md:31` 把鉴权列为会话管理项但未实现）。加 token（服务端校验 + 过期），失败直接关连接。
- [ ] **Ingress 真验签**：替换 `AllowAllPlatformEventVerifier` 为按平台的 HMAC 校验（stub 保留为显式的 `allowAll=true` 配置，且默认关闭）。

### P1 — 正确性与容量

- [ ] **`SessionActor` 反压**：`SessionActor.cs:19` 是 `Channel.CreateUnbounded`（无界邮箱）——慢消费/事件洪峰时内存无上界，与 §1 勾选的「backpressure」不符。改有界邮箱 + 溢出策略（按 `maxEventsPerTick` 已有的合并/丢弃口径），并把 `SessionDiagnostics` 的 dropped 计数接出去。
- [ ] **会话空闲回收**：`SessionManager` 只有 `DisposeAsync`，没有空闲超时/驱逐；长跑会话与僵尸 session 会一直占 tick。加 idle timeout + 主动驱逐（并同步给 Gateway 注销）。
- [ ] **合并重复代码**：把 `Ingress/*` 与 `WebSocketHelpers.cs` 从 Server/Gateway 抽到共享工程（`Cohort.Gateway.Core` 或并入 `Cohort.Protocol`），两处 `#if`/拷贝改为一次引用；补单测只写一份。
- [ ] **`MessageBus/ActorRef/Router` 抽象**（§3.5 唯一未勾项）与进程内路由表：现在 Server 单进程直连 actor，拆分模式各写一套。

### P2 — 可观测性（§4 全空）

- [ ] `/metrics`（Prometheus 文本格式）：tick 耗时直方图、事件 ingested/applied/merged/dropped、ack 延迟、落后 tick 客户数、快照与重同步数——`SessionDiagnostics` 已经在收集这些字段，只差导出。
- [ ] 结构化日志：sessionId / tickId / clientId 三件套进日志（现在是裸 `Console`/`ILogger` 文案）。
- [ ] 本地压测脚本：N 个观众事件 + 2 个主播客户端，输出 tick 抖动与丢弃率（放 `scripts/`）。

### P3 — 性能 / 测试 / 运维

- [ ] 快照序列化：现为 JSON 全量快照（§1），每 tick 对多客户端重复序列化。改为按 tick 序列化一次复用 + `ArrayPool` 租借；评估 MessagePack/FlatBuffers（`docs/design/frame-sync.md` 已提）。
- [ ] 测试缺口：Gateway/EngineHost 的 WS→IPC→Engine 端到端、Ingress HTTP 面、IPC 断连重连与降级（UDS→NamedPipe/TCP 回退）目前**零测试**；这三条是拆分模式的主干路径。
- [ ] 协议版本化：`hello/welcome` 无版本字段，后续加字段会破坏旧客户端。在 `Cohort.Protocol` 加 `protocolVersion`，welcome 回显，不匹配明确拒绝。
- [ ] 运维：split 模式下 Gateway 应有对 EngineHost 的 readiness（现在只有各自 `/health` 常返 200）；补 compose 健康检查与优雅停机（先把在跑的 session 快照落盘）。


## 9. 外部评审采纳（Chorus，2026-10-12）

> 来源与全文：[docs/design/review-2026-10-12-chatgpt.md](./docs/design/review-2026-10-12-chatgpt.md)。按用户指令：先更新文档，再按阶段执行。**每项落地必须同步该文末尾的「落地跟踪」与本表**。

### 阶段一：正确性与安全边界（最高优先）
- [ ] 归并跨阵营串数据：`DefaultAudienceEventReducer` 分组键补 `SessionId/MatchId/FactionId`（点赞 `(S,M,F,U)`、礼物 `(S,M,F,U,GiftId)`），混合阵营计数守恒用例
- [ ] ACK 单调性与 Tick 上界：拒绝负数/未来 Tick/倒退，绑定 Client 身份，分类计数
- [ ] 生产默认拒绝未验证 Ingress：`AllowAllPlatformEventVerifier` 退为显式 dev 开关，缺验证器即拒
- [ ] 去重原子化：`EventDeduplicator.TryMark` 改原子占位；IPC 发布失败不得被记成重复（投递状态 Received/Accepted/Applied/Failed）
- [ ] Match 权威语义定性：展示聚合 vs 权威对抗状态（结论写进 `docs/design/spec.md`，不先上 MatchActor）

### 阶段二：稳定性与过载保护
- [ ] 有界邮箱 + 事件缓冲（Session 总量/每 Tick/最长等待），控制命令与观众事件分道，礼物类不可静默丢弃
- [ ] 权威 Tick 去阻塞发送：每客户端有界出站队列/最新快照覆盖，慢客户端不拖累 Tick（含 Gateway `DispatchSnapshotAsync`）
- [ ] Tick 调度：单调时钟截止 + 超时策略，统一 `TickDurationMs` 默认值与文档建议
- [ ] Session 生命周期：空闲回收、上限、异常隔离（GameModule 抛异常不得静默退出 Actor）
- [ ] 故障注入测试：慢客户端 / 事件洪峰 / 混合阵营

### 阶段三：可观测性与恢复
- [ ] `chorus_*` 指标 12 项（tick_duration/lag、ingress/applied/merged/dropped/duplicates、mailbox_depth、ack_lag、snapshot_send/resync、ipc_publish_failures）+ 事件关联 ID
- [ ] 快照版本与大小上限、应用失败路径、客户端重同步闭环
- [ ] IPC 断连/重启/回退（UDS↔NamedPipe 协商）验证
- [ ] 独立压测工具（多 Session × 观众事件 × 变速客户端 × 随机断连）

### 阶段四：发布与生态
- [ ] **Cohort → Chorus 命名收尾**（仓库已改名）：`Chorus.sln`、各 `Chorus.*` 工程与命名空间、项目引用、CI 工作流、文档站标题、启动命令、README 徽章；不得纯文本替换，改完全量构建+测试
- [ ] 最小游戏模块示例、平台适配器模板与接入契约
- [ ] 部署/安全/故障排查文档与版本策略

### 十一类故障验证测试清单
- [ ] 归并（混合阵营/Match/超限计数）· 去重并发 · 投递失败重试 · Tick 过载 · 慢客户端 · ACK 安全 · Match 并发 · 生命周期 · 快照恢复 · 安全（未验证事件/超大请求体/非法 WS）· IPC 故障

### 明确不做（边界）
- 不引入分布式 Match 集群、Kafka/RabbitMQ、增量快照/回滚同步/确定性重放；不把平台私有适配放进核心；不发展成账号/运营/支付/编辑器综合平台；单体保持默认部署形态。
