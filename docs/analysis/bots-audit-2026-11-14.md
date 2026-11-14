# 四仓 bots 能力现状审计（2026-11-14）

- 起因：chorus 落 BOTS 平台（模拟主播+观众机器人+控制面板+压力观测，todo.md §Bots），用户令问其余四仓（theseed/apollo/shield/mistlane）「都需要对应的 bots 吧」——**先审计再立项，不无脑铺**。
- 方法：四仓并行只读侦察（每仓一份独立清单：协议面/行为深度/剧本化/面板/对账/todo 规划），关键结论回本机抽查复核（文件存在性、行数、grep 命中、CLI 参数面），不凭目录名猜。
- 审计基线：各仓工作树当日状态（2026-11-14）。chorus 侧 BOTS-1/2/3 此刻仍为 todo 条目（未实施），本报告的「chorus 哲学」指 todo.md:620-623 声明的验收口径。
- 纪律：本报告只落账不改任何代码；各仓立项与否**等用户拍板后另行派单**。

## 结论速览

| 仓 | 已有 bots 面 | 协议覆盖 | 行为深度 | 剧本化差距 | 面板差距 | 对账差距 | 建议 |
|---|---|---|---|---|---|---|---|
| theseed | ✔ 完整 C++（BotClient/BotManager/BotsConfig，905 行 + E2E） | Login+Base 两个客户端面，真协议栈 | 浅：2 个动作（且服务端零 handler 绑定，语义产出 0） | 缺（18 键 INI，全员同行为） | 缺（stdout 单行；OpsServer /metrics+/dashboard 在但零集成） | 缺（`messagesIn>6` 宽松下界 + 实体计数，无 sent==received） | **立项（小）**：剧本化+对账+面板三补齐 |
| apollo | ✔ 单文件 Python（444 行，stdlib） | 真 wire（link Frame+CRC32C，TCP/WS） | 浅：5 个动词，无下行校验 | 缺（硬编码圆周巡走） | 服务端侧厚（Prometheus+/api/state）但与 bot 不打通 | 缺（退出码级「有流量」断言；README 明说对账靠人肉对照面板） | **立项（小）**：脚本化+自动对账+孤儿收编；拓扑剧本 BLOCKED |
| shield | ✘ 零（全仓 bot 词命中仅 third_party/crashpad 注释） | 客户端协议极简：idlen 帧头 4B + JSON | —（无 bots，但 82 行 client_demo.py 即协议参考） | — | — | — | **已立项**（shield 自有 todo BOTS-001 [P1] 在案），执行即可 |
| mistlane | ✔ 双重身份：ctest 夹具 + 已发布 CLI（`mistlane bot --scenario business`） | 完整（TCP 长度前缀帧+MLS1+MLE1 envelope，逐字节文档） | 中：login/joinRoom/move/heartbeat 四步确定性剧本 | 部分（CLI 参数化但方法集硬编码） | 已有内嵌 dashboard+/metrics（最小形态已在树内） | 部分自动化（metrics rx==tx 一致性断言） | **不立项**（research & learning 定位+容量验证 BLOCKED；business.bot 原样保留） |

---

## 1. theseed（KBEngine 系）

**已有面**：`tools/bots/`（BotClient 306 行 / BotManager 104 / BotsConfig 164 / main 95，静态库 `theseed_bots` + 可执行 `theseed-bots`）。走**真客户端协议栈**（复用 `theseed_login`/`theseed_runtime`，非旁路 mock）：LoginApp TCP（默认 20099，login→selectRealm）+ BaseApp TCP（20000，enterGame→Action 周期）。状态机 `Idle→LoginSent→SelectSent→BaseConnecting→EnterSent→InGame`，失败退 Backoff 重连。斜坡加压 + 全员 tick + RTT 近似采样 + reconnect/protoError 计数。E2E：`tests/bots/BotsE2ETest.cpp`（LoginApp+BaseApp+CellApp 全栈回环、临时 EntityDef、断言 ramp/进出/流量下界/断连重连/斜坡节奏），ctest 注册（Linux 门控）。todo 90 项全勾，bots 相关 3 行（:42/:92/:99）均为已完成的测试/修复性质。

**协议覆盖缺口**：只有「玩家进游戏」一条客户端链路。`QueryRealms` 不用、`ActionForward=114` 无发送方、CellApp 无客户端面（本就无）、DBApp/控制面 RPC/Ops HTTP 全部未被 bot 触达。另 `State::LoginConnecting` 是死状态（无处赋值）。

**行为深度（审计最大发现）**：InGame 后全部行为 = 两个可配置 EntityDef 方法名（默认 `onDamage`/`onHeal`）+ 两个间隔，位置是 ±100 线性反射的假坐标（从不消费 EntityEnter 下发位置）。更关键：**生产代码从不绑定 method handler**（`grep bindMethodHandler src apps` 仅 Entity.h 声明；只有测试绑过 castSpell/basePing）——bot 动作经 `EntityDef::findMethod` 校验通过后在 `Entity::dispatchMethod` 落空 `return false`，**语义产出为 0**。即现状验证的是「帧能解、能按 side 路由」，不是「游戏逻辑被驱动」。下行 EntityEnter/PropertySync/EntityEvent 仅计数不解析，bot 纯开环。

**剧本化**：无。18 键 INI 白名单（key=value），全员共享同一配置仅账号名递增；无时间轴/分组/概率/峰值概念，仓库内连 .ini 样例文件都没有。

**面板**：无（tools/bots 内 panel/dashboard/http/metrics 零命中；stdout 单行 `spawned/in_game/msg_in/msg_out/rtt_avg/...`）。引擎侧 `OpsServer` 的 `/metrics`（Prometheus）与 `/dashboard`（内嵌单页）已存在（src/ops/OpsServer.cpp:124,132-134）——面板集成有现成挂载点，不必另起统计源。

**对账**：无。E2E 最接近的一处是 `baseApp.runtime().entityCount() == 3`（实体数计数相等）；流量断言是 `messagesIn>6 && messagesOut>6` 宽松下界（双方计数差 100% 也不 FAIL）。

**规模模型**：单线程串行轮询（main 一循环 tick 全员 + sleep tickInterval），每 tick O(n) 两三趟遍历；`total_count` 解析上限 10 万但**无并发实现支撑**，有效加压频率被 tick_interval 钳制。另：本机从未构建过（无 build 目录、0 可执行文件），`--help` 实现在但二进制不存在。

## 2. apollo（轻量 instance 引擎）

**已有面**：`tools/bots/apollo_bots.py`（444 行，stdlib-only）+ README。真 wire：link Frame（16B 头 magic/ver/flags/seq/len/CRC32C）+ MessageHeader 28B + 软件 CRC32C；TCP/WS 双入口（WS 每消息一帧）。会话链：PING 建会话→GATEWAY_ASSIGN 票据准入→CELL_CREATE_ENTITY 入场（带 spaceId）→CELL_ENTITY_MOVE 圆周巡走+PING 心跳；有界指数退避重连（首包携旧会话号=保留窗 resume）。RTT p50/p95/min/max。**孤儿**：tests/、scripts/、CI、todo 全仓零引用（唯一命中是它自己的 README），todo.md 无任何 bot 条目。

**协议覆盖缺口**：发送面 5 个动词。`T_LOGIN_REQUEST` 定义了从未发送；`CELL_CROSS_BORDER/DESTROY_ENTITY/SKILL_CAST/ENTITY_PROPERTY`、party 族服务端支持但 bot 一律不发。下行零内容校验（frames_rx 只计数）。

**拓扑边界（对「bots 能否在代次概念上工作」的回答）**：**当前不可表达**。bot 只能在 CREATE_ENTITY 那一刻选一次 Space；`CELL_ENTITY_MOVE` 不带 space/cell 字段；客户端协议完全没有 cell 寻址面（cellId 只存在于 gateway 内部 RouteSnapshot 且被 `(void)` 丢弃，gateway_router.cpp:82）。dev_fleet 多 cell/多 Space 声明对 bot 不产生寻路效果。要支持迁移剧本需先解 apollo todo 的 D03/D14/D15/D29/D30（全部 BLOCKED）：gateway 多端点选择面、移动意图携带 space/cell/代次、同 Space 跨 Cell 连续交接、下行版本/重定向帧。

**准入真实性**：`DefaultSessionAdmissionService` 现阶段「仅拒空票，非空票放行」（session_admission_service.cpp:31-41）——bot 的随机票据几乎必然放行，「登录成功」断言不验证真实票据。

**剧本化/对账/面板**：无脚本（硬编码圆周巡走）；对账仅退出码级 `logins_ok>=count && moves>0 && pongs>0`（「有流量发生了」而非 sent==delivered），README 明说对账=人肉对照 gateway dashboard 与 bot 统计。服务端观测面反而厚：gateway 六个 Prometheus Counter（intents_sent/acked/failed/timed_out/unforwardable/dropped）+ `/api/state` + 内嵌面板——**自动化对账的挂载点已存在，缺的只是接线**。

## 3. shield（Skynet 系 Actor+Lua 运行时）

**bots 面**：确认零。全仓词边界 grep 仅 6 处命中且全在 `third_party/crashpad/`（Chromium 上游注释；此前的「bot 词命中」是 `both`/`shutdown_both` 子串误报）。

**客户端协议（bot 若接入的真实成本）**：极低。TCP IPv4（WebSocket/UDP/KCP 明确 deferred）；仓库默认封套 `idlen` = `[route_id:2B 大端][length:2B 大端][JSON body]`，另有 lenprefix/typed_len/delimiter 三种可选；**无应用层握手**（连接即绑 auth 入口服务，epoch=0），认证是业务 RPC（route 1 `login` → Lua `shield.client.bind` → epoch CAS 切 target）；动作面=actors[].rpc.routes 静态声明（echo 示例无需认证即可压；hello_world 有 login→move→move_result 闭环）。参考实现现成：`scripts/client_demo.py`（82 行 stdlib，注释自述「可直接拷进你自己的客户端工程当协议参考」）+ `tests/acceptance/test_client_rpc_e2e.cpp`（真实 TCP 全闭环，帧构造四件套可直接改写成 CI smoke）。边界须知：max_frame_size（默认 16MiB）、max_connections(_per_ip)、rate_limit 超限**静默丢帧不断连**（压测被限流时客户端只能靠发出多收到少反推——建议错误注入模式显式加一条 rate-limit 可观测性用例，用 `/ops/metrics` 的 gateway_rate_limited 计数对账）。

**关键发现**：shield 自己的 todo.md:442 **已有 BOTS-001 [P1] 未勾条目**（2026-11-14 用户令）：tools/bots 独立压测目录（bot 客户端+plan TOML 压测模式+错误注入+示例 plan+CI smoke，验收=ctest 全绿含 smoke+真实实例压测出 P99）。即**不需要再立项，需要执行**。量级估计：Python 版 4 文件约 400-700 行（bot_client.py 由 client_demo.py 演进、asyncio 斜坡、chaos 注入、tomllib 3.11+ 标准库），纯客户端工具目录，零服务端改动。

## 4. mistlane（FOGS 系，research & learning）

**business_bot 真实性质（先验描述修正）**：双重身份——(a) ctest 用例 `business.bot`（夹具内嵌于 src/test_cli.cpp:25-157，契约/宿主脚本以字面量写临时目录）；(b) 被驱动的 `mistlane bot --scenario business` 是**已发布的一等 CLI 子命令**（main.cpp:199 派发，src/bot.cpp 296 行含完整 --help），参考宿主对（examples/business.lua + examples/contracts/business.def）随仓发布，两条命令即可脱离测试独立复跑。「只是测试夹具」的说法不完全准确。

**协议覆盖**：完整且有逐字节文档钉死（docs/business-bot.md 与源码核过）：TCP u32 大端长度前缀帧（1MiB 上限）、握手族 MLS1/MLC2/MLM1/MLC1、MLE1 39 字节 envelope + sproto。bot 的 business 面覆盖 TCP+MLS1+MLE1 一条路：每连接 MLS1 服务握手→spawn→login→joinRoom→move/heartbeat 交替，四连接确定性剧本；测试另加裸腿守卫探针 + dashboard/metrics 一致性断言（`mistlane_script_gauge{rooms}==2`、`entity_results_total{ok}==32`、`messages_total rx==tx==43`）。

**规模/深度上限**：`--clients`≤128（一连接一线程）、总请求≤10 万、`--interval-ms`≤60s、`--ramp-ms`≤600s；方法集硬编码四个名字（契约里的 leaveRoom 从不被调用）；business 模式**不采集延迟**（p50/p95/p99 恒 0，echo 分支才采）；无重连 churn、无时长驱动、无消息体分布；报告仅两行 stdout。

**定位核查**：README/docs/PRODUCT 三处一致 self-position 为 research & learning、非生产推荐、"no new production capacity claims"；`docs/cross-server.md:71` 自述 bot 是 functional-run 驱动器、**持续生产级压测与运营指标管线明确列为开放项**，todo.md 的压测/容量条目全部 BLOCKED（缺真实压测集群与环境）。最小 bots 能力已在树内：CLI 压力驱动器 + 内嵌 dashboard（--dashboard-listen）+ Prometheus /metrics + 脚本 gauge。

## 5. 与 chorus BOTS 的可复用性评估

chorus BOTS 的四件套对照四仓：

| chorus 件 | theseed | apollo | shield | mistlane |
|---|---|---|---|---|
| SimulatorPlatform（模拟平台挂真实链路） | 不适用（无平台事件概念，bot 直连游戏协议） | 不适用（同左） | 不适用（同左） | 不适用（同左） |
| 剧本 TOML（时间轴/概率/峰值） | 缺，可加（INI→分组剧本） | 缺，可加（参数→脚本） | 已规划（BOTS-001 plan TOML） | 部分（CLI 参数化，方法集硬编码） |
| 断言对账库（发 N→到账 N，偏差 FAIL） | 缺（宽松下界） | 缺（退出码级；服务端 Counter 挂载点已备） | 未建（smoke 断言往返即可起步） | 部分（metrics rx==tx 一致性已是自动断言） |
| 控制面板 | 缺（OpsServer 挂载点已备） | 缺（服务端 dashboard 厚、bot 侧无） | 未建（BOTS-001 未含面板） | 已有最小形态（内嵌 dashboard） |

**剧本 TOML 跨仓复用：语义层可映射，传输层不可复用。** chorus 的剧本是「平台事件时间轴」（弹幕/礼物/SC/舰长 按 t+N、概率分布、峰值脉冲），其余四仓是「游戏协议动作时间轴」。可共享的是三层语义骨架：

| TOML 语义层 | chorus 含义 | 游戏仓映射 |
|---|---|---|
| `[timeline]` t+N 事件条目 | 发弹幕/礼物/SC 概率与脉冲段 | 发 move/skill/chat/login 等协议动作的速率与序列 |
| `[groups]` 行为分组 | 主播 bot vs 观众 bot 池 | theseed/apollo 的 bot 分组行为分化（现状全员同行为） |
| `[pulse]` 峰值脉冲段 | 压力脉冲（验证切面/队列） | 断连洪水/移动风暴/心跳风暴 |

不可共享的是每仓的 wire 编码与动作词汇（chorus=HTTP JSON 平台事件；shield=idlen+JSON RPC；apollo=二进制 link Frame；theseed=EntityDef 方法；mistlane=MLE1+sproto）。**结论：各仓播放器把共享语义层映射到各自协议，格式上保持 chorus 的 TOML schema 简洁并有文档，映射即机械；不建跨仓通用多方言 bot 工具**（shield 侧审计同判：等格式稳定再评估抽公共包）。对账库同理——读面完全仓内化（chorus=input.delivery/game.frame/game.result/settlement.grant；theseed=服务端 Action 回传计数；apollo=gateway intents Counter；mistlane=metrics rx==tx），没有可抽取的公共运行时，可共享的只有「sent-vs-delivered 偏差即 FAIL」这条断言纪律本身。

## 6. 明确建议清单（等用户拍板后另行派单）

### 6.1 theseed —— 立项（建议 P1，量级中）

理由：bots 骨架与 E2E 已在且是真协议栈，缺的三块（剧本化/对账/面板）都有明确挂载点；但不补「服务端 handler 绑定」前置，一切语义动作仍是静默无操作。

> todo 条目文案：
> - [ ] BOTS-001 [P1] bots 剧本化+对账+面板补齐（2026-11-14 审计 docs/analysis/bots-audit-2026-11-14.md，chorus 仓）：
>   ①场景剧本（18 键 INI 兼容升级为分组剧本：bot 分组、每组动作时间轴/概率/峰值段，替换全员同行为）；
>   ②服务端对账（sent==received 逐类断言，偏差 FAIL，替换 messagesIn>6 宽松下界；**前置：生产 EntityDef
>   method handler 绑定**——现状 onDamage/onHeal 落 Entity::dispatchMethod 未命中静默无操作，语义产出 0；
>   未绑前置则对账只到「服务端解帧计数」层）；③面板接线（renderStatsLine 进既有 OpsServer /metrics
>   +/dashboard，不造第二套统计）。验收：E2E 含分组剧本与 sent==received 断言；/metrics 出 bot 指标。

### 6.2 apollo —— 立项（建议 P1，量级小；拓扑剧本明确不做）

理由：wire bot 已在且协议栈真实，缺脚本/自动对账/CI 收编三件，且服务端 Counter 挂载点现成；但 space/cell 迁移剧本被自家 todo 的 BLOCKED 代次拓扑挡死，本期不做。

> todo 条目文案：
> - [ ] BOTS-001 [P1] wire bots 升级：剧本化+自动对账+孤儿收编（2026-11-14 审计 docs/analysis/bots-audit-2026-11-14.md，chorus 仓）：
>   ①场景脚本（JSON/TOML 时间轴替代硬编码圆周巡走：分组/概率/峰值段，动作仍限现有五动词）；
>   ②自动对账（bot sent 与 gateway intents_{sent,acked} Counter / dashboard /api/state 脚本化比对，
>   替换退出码级「有流量」断言与 README 人肉对账口径）；③CI 收编（apollo_bots 现状 tests/CI/todo
>   零引用，挂一条 CI 冒烟）。边界：space/cell 迁移剧本依赖 D03/D14/D15/D29/D30（BLOCKED），本期
>   不做拓扑剧本；admission 现容忍随机票据，「登录成功」断言不验证真实票据（如实登记）。
>   验收：脚本驱动 E2E+对账断言绿；bots 进 CI。

### 6.3 shield —— 已立项，不重复立项

shield todo.md:442 已有 BOTS-001 [P1]（2026-11-14 用户令），交付形态（bot 客户端+plan TOML+错误注入+CI smoke）与验收（ctest 全绿+真实实例 P99）已写好。审计只补两点执行建议：(a) 语言取 Python（client_demo.py 82 行现成参考，bot 是外部工具无需共享编译体系；C++ 仅在需要数千连接线速时才值）；(b) 错误注入模式显式加一条 **rate-limit 静默丢弃可观测性**用例（超限丢帧不断连，需以 /ops/metrics gateway 计数对账，防压测被限流而客户端无感）。

### 6.4 mistlane —— 不立项

理由：① 仓定位 research & learning、明示非生产推荐与 "no new production capacity claims"（README.md:8,13、PRODUCT.md:19）；② 最小 bots 能力已在树内（CLI + 内嵌 dashboard + /metrics + rx==tx 自动断言），再建平台是重复；③ 容量验证类目标被自家 todo BLOCKED 于环境（缺真实压测集群），立了也产不出承诺的证据；④ chorus 式 BOTS 的服务对象（长期在线压测运营）与教学/考古受众不重叠。`business.bot` 用例原样保留（已是 80/80 门禁内的端到端冒烟）。可选非项目小增强（各几十行，不立项、将来顺手）：business 模式补延迟分位（修 completed=0 失真）、补 leaveRoom 步骤、加 WS 场景。

### 6.5 跨仓纪律

- 四仓互不依赖：各仓 bots 工具各自独立实现；剧本语义层若要跨仓复用，等 chorus BOTS-1 的 TOML schema 稳定并文档化后再评估抽公共包（届时另行立项）。
- 各仓立项派单时**必须携带本报告对应节**（协议事实/边界/BLOCKED 前置），不凭记忆重侦察。

## 附：审计证据抽查记录

- theseed：`tools/bots/` 7 文件 905 行清点；`grep -rni business.bot` 两树零命中（任务书先验「business_bot E2E」在 theseed 不存在，真实对应物为 `theseed_bots_e2e_test`）；`bindMethodHandler` 生产零绑定；OpsServer.cpp:124,132-134 /metrics+/dashboard 在案；tests/bots CMakeLists Linux 门控在案。
- apollo：`tools/bots/apollo_bots.py` 444 行；全仓引用 grep 零命中（除自身 README）；CLI 参数面无 `--scenario`（该词属 mistlane）；README.md:39-40 人肉对账原文在案。
- shield：全仓 bot 词边界 grep 仅 third_party/crashpad 6 处；`scripts/client_demo.py` 与 `tests/acceptance/test_client_rpc_e2e.cpp` 文件在案；todo.md:442 BOTS-001 原文在案。
- mistlane：src/main.cpp:199 bot 派发；src/bot.cpp `--scenario` 帮助文本；include/mistlane/dashboard.hpp 在案；test_cli.cpp dashboard/metrics 断言（script_gauge==2、rx==tx==43）在案；README.md:8,13 status 徽章与定位句在案。
