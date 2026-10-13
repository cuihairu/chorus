# TODO

## 0. 仓库基础

- [x] 建立 solution 与包结构（11 csproj + 测试工程，`d32e1bc`）
- [x] 建立 docs/ 目录结构与索引（`3291add`）

## 1. Tick/帧同步

- [x] SessionActor 单线程邮箱 + 固定 tick（`8a49c1b`）
- [x] AudienceEvent 统一模型 + 稳定排序（`8a49c1b`）
- [x] inputDelayTicks 缓冲（`8a49c1b`）
- [x] maxEventsPerTick 合并/降采样（`8a49c1b`）
- [x] WebSocket 协议 hello/welcome/ack/snapshot（`8a49c1b`）
- [x] per-client ack 跟踪与落后检测（`499f01c`）
- [x] 快照机制（JSON 全量）（`96310c5`）

## 2. 平台接入

- [x] IPlatformEventMapper / IPlatformEventVerifier 接口（`8a49c1b`）
- [x] Platform Ingress HTTP endpoint（`8f18bde`）
- [x] 去重机制（EventDeduplicator，`78f2597`）

## 3. Match/阵营

- [x] Match / Faction / Session 关系建模（`8a49c1b`）
- [x] 多客户端订阅同一 Match（`8226727`）
- [x] 观众加入阵营规则接口（`8a49c1b`）

## 3.5 通信与部署模式

- [x] MessageBus/ActorRef/Router 抽象（`0eac8ac`）
- [x] InProc / NamedPipe / UDS / TCP / HTTP 五种 transport（`8f18bde`）
- [x] 共享会话生命周期 SessionRegistry（`a6eae7f`）
- [x] streamer-ws 门面合一（`e0501bc`/`3a10f9d`）
- [x] platform-ingress 门面合一（`0eac8ac`）
- [x] 广播快照按 tick 去重（`50f0045`，232 测试）

## 3.6 拆分模式

- [x] Chorus.Gateway 与 Chorus.EngineHost 两个进程（`8f18bde`）
- [x] IPC 双向通道（`8f18bde`）
- [x] 会话参数 EngineHost 下发 Gateway（`8f18bde`）

## 4. 可观测性与压测

- [x] /metrics Prometheus 文本（`b86f5a8`）
- [x] 结构化日志 sessionId/tickId/clientId（`7209d91`）
- [x] 本地压测工具 Chorus.LoadTool（`578415b`）
- [x] 峰值测量支持（`4ea4273`，242 测试）

## 5. 文档定稿

- [x] spec.md 第一版规格（`0409cf2`）
- [x] protocol.md 协议字段与示例（`7a1b64f`）
- [x] ops/deployment/config/tuning 文档（`8a49c1b`）

## 6. 文档站

- [x] VuePress 文档站（`docs/.vuepress/`）
- [x] GitHub Pages 自动部署（`docs.yml`）

## 7. CI / 覆盖率 / 发布

- [x] CI build + test（`ci.yml`）
- [x] 覆盖率上传 Codecov（`ci.yml`）
- [x] Tag 自动 Release（`release.yml`）

## 8. 实现分析与改进

### P0 平台对接与安全

- [x] 抖音平台对接（`e2304b2`，172 测试）
- [x] 快手平台对接（`e2304b2`，172 测试）
- [x] 两个平台联调用例（`e2304b2`，172 测试）
- [x] WebSocket 接入鉴权（`8f18bde`）
- [x] Ingress 真验签 fail-closed（`def21d2`）

### P1 正确性与容量

- [x] SessionActor 反压（`5592d62`）
- [x] 会话空闲回收（`362b435`）
- [x] 合并重复代码（`e2304b2`）
- [x] MessageBus/ActorRef/Router 抽象（`0eac8ac`）

### P2 可观测性

- [x] /metrics Prometheus（`b86f5a8`）
- [x] 结构化日志（`7209d91`）
- [x] 本地压测脚本（`578415b`）

### P3 性能/测试/运维

- [x] 快照序列化按 tick 编码一次（`96310c5`）
- [x] Gateway/EngineHost e2e 测试（`92d633b`）
- [x] 协议版本化 protocolVersion（`a5f23b8`，166 测试）
- [x] Gateway readiness 探针 /ready（`be9f264`）

## 9. 外部评审采纳

### 阶段一：正确性与安全

- [x] 归并跨阵营串数据（`9ad322d`）
- [x] ACK 单调性与 Tick 上界（`499f01c`）
- [x] 生产默认拒绝未验证 Ingress（`def21d2`）
- [x] 去重原子化（`78f2597`）
- [x] Match 权威语义定性（`0409cf2`）

### 阶段二：稳定性与过载

- [x] 有界邮箱 + 事件缓冲（`5592d62`）
- [x] Tick 去阻塞发送（`df71d26`）
- [x] Tick 调度单调时钟 + 超时（`362b435`）
- [x] Session 生命周期管理（`362b435`）

### 阶段三：可观测性与恢复

- [x] chorus_* 指标（`b86f5a8`）
- [x] 快照版本与大小上限（`768e668`）
- [x] 客户端重同步闭环（`8226727`）
- [x] IPC 断连/重启/回退（`d5d2d64`）
- [x] 独立压测工具（`578415b`）

### 阶段四：发布与生态

- [x] Cohort → Chorus 命名收尾（`8f18bde`）
- [x] 最小游戏模块示例与适配器模板（`8a49c1b`）
- [x] 部署/安全/故障排查/版本策略文档（`8a49c1b`）
- [x] Gateway:Roles 角色打包（`15a6dad`）
- [x] RoomAffinity + RoomRouteTable（`be2eff7`）
- [x] 快照 schema 版本上线（`2d157fa`）
- [x] CPU 峰值测量仪器（`4ea4273`）

## 10. 进程拓扑

- [ ] 阶段三多 worker：等单 worker 峰值 CPU > 60% 或单机房间数达数百量级（运行期数据，仓内无法自证）
- [ ] 恢复半边：IGameModule 快照恢复契约待拍板（见 docs/design/snapshot-restore.md）

## 11. 帧同步方案

方案见 docs/design/frame-sync.md，接入见 docs/guide/quickstart-frame-sync.md。

- [ ] P1 帧号语义落地：帧号=tickId 文档化；state.frameEvents 约定被示例/测试引用；客户端按帧号渲染示例可跑
- [ ] P2 帧历史与补帧：帧历史 ring buffer + resync 拉取消息 + 快照 frameEvents 信封字段；验收：单测补帧窗口 + resync 消息 + wire 兼容 + e2e
- [ ] P3 客户端 SDK：新项目 N 行代码接入（SDK 入口 + 一个回调）；验收：回调收 state、按帧号渲染、构建/测试绿

## 12. B 站弹幕源适配器

调研与方案见 docs/design/bilibili-danmaku-adapter.md。结论：部分支持。

- [ ] P1 open-live 接入 + 弹幕归一化 + 断线重连 + /ingress/bilibili；验收：金测 + e2e
- [ ] P2 礼物/进场/房间生命周期归一化（含 SEND_GIFT_V2 灰度）；验收：金测 + e2e
- [ ] P3 反向写入/互动指令：需平台允许才做，当前无官方通道，明确不做

## 13. 文档一致性

- [x] 文档-代码一致性审计（2026-10-13，41 条修复，见 docs/审计-文档一致性.md）
- [x] 架构图进文档（`89d2275`）
- [x] API 参考文档（`67daedc`）
- [x] README 双语互链（`f692034`）
- [x] 每日构建 workflow（`56e5f29`）
- [x] .NET 10 迁移（`d32e1bc`，242 测试）

## 明确不做

- 不引入分布式 Match 集群、Kafka/RabbitMQ、增量快照/回滚同步/确定性重放
- 不把平台私有适配放进核心
- 不发展成账号/运营/支付/编辑器综合平台
- 单体保持默认部署形态
- 帧同步不做回放/录播对齐（无持久化）
