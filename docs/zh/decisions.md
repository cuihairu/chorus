## 2026-10-12

### D1 平台适配器收进共享工程 `Chorus.Adapters.Platforms`

- **背景**：`Ingress/*` 三件套（`AllowAllPlatformEventVerifier` / `EventDeduplicator` / `TestPlatformEventMapper`）
  在 `Chorus.Server` 与 `Chorus.Gateway` 各存一份（todo §8 P1 已列「合并重复代码」）。
- **决策**：新增 `src/Chorus.Adapters.Platforms`（引用 `Chorus.Protocol` 与 `Chorus.Adapters.Abstractions`，均不含 Engine/Server/Gateway），平台适配器与 ingress 共用件单份实现，
  Server / Gateway 改为引用；同时删除两处重复副本。
- **影响**：旧命名空间 `Chorus.Server.Ingress` / `Chorus.Gateway.Ingress` 移除，测试同步更新。
  平台私有逻辑不进核心（外部评审 §10 口径）。回滚面：恢复两目录副本即可。

### D2 验签按平台分发：named verifier + router，无兜底即 fail-closed

- **背景**：ingress 原本只有一个 `IPlatformEventVerifier` 单例无条件放行；新增按平台 verifier 需要分发机制。
- **决策**：
  - `IPlatformNamedEventVerifier`（绑定平台名）注册进 `PlatformEventVerifierRouter`；
  - 有平台名 → 走对应 verifier；无 → 走 dev 兜底（`AllowAllPlatformEventVerifier`，阶段一③改为显式开关）；
  - 两者皆无 → **拒绝（fail-closed）** 并打 WARN。
- **影响**：新平台接入 = 注册一个 named verifier + 一个 mapper，路由零改动。

### D3 两平台官方口径未核实部分：占位契约 + TODO，不做猜测实现

- **背景**：抖音官方文档站登录墙 + 反爬，签名算法/事件字段/推送通道形态均未核实（详见
  [platform-douyin.md §1](./reference/platform-douyin.md)）；快手拿到官方《消息加解密和签名验证》正文，
  签名（sha1Hex(body+Token) / kwaisign）与 AES 信封按官方实现，直播事件内层 JSON 官方未给
  （详见 [platform-kuaishou.md §1](./reference/platform-kuaishou.md)）。
- **决策**：
  - 抖音：验签按 HMAC-SHA256(appSecret, body) 占位、事件信封用 Chorus 自定义占位字段；
  - 快手：验签与信封解密按官方算法实现，仅内层事件字段用占位契约（与抖音同构，便于联调时同点替换）；
  - 占位部分在代码注释与平台参考文档全部明示「待联调」；替换点收敛到单文件
    （verifier 的 ComputeSignature / mapper 的字段抽取）。
- **影响**：联调时改动面 = 每平台 1 个 verifier 方法 + 1 个 mapper 文件 + 配置 header 名；管道、路由、测试结构不变。

### D4 两平台凭据走配置，不硬编码

- **决策**：`Ingress:Douyin:AppSecret`、`Ingress:Kuaishou:VerifyToken` / `Ingress:Kuaishou:MsgEncryptKey`
  （及 header 名）从 `IConfiguration` 注入；缺密钥 → fail-closed。
- **影响**：生产密钥经环境变量/配置中心下发；仓库不落真实密钥。

### D5 单体内先做 listener 代码分层（platform-ingress / streamer-ws），不拆进程

- **背景**：[process-topology.md](./design/process-topology.md) 定了「角色先分、进程后拆」。
- **决策**：新增共享工程 `Chorus.Gateway.Core` 承载两 face 策略组件：
  - `platform-ingress` 面：限流（每来源固定窗口）、请求体上限（413）、请求超时（504）、按平台验签、去重，
    落在两个 host 的 `POST /ingress/{platform}` 处理器 + `/faces` 计数端点；
  - `streamer-ws` 面：鉴权、ACK 限频、消息体上限，落在 `/ws` 处理器（2026-10-12 已落地，见 D6）；
  - 两面配置分道（`Faces:PlatformIngress:*` / `Faces:StreamerWs:*`），日志分类按面
    （`Chorus.StreamerWs`；Ingress 面暂无独立日志类别），计数器经 `GET /faces` 分面暴露。
- **影响**：单体仍是默认部署形态；物理拆分等触发条件（worker CPU>60% 或单机数百房间），见拓扑文档 §4。

### D6 主播 WS 鉴权 fail-closed + Ingress:AllowAll 默认关（demo 靠 Development 配置显式打开）

- **背景**：评审 P0-4（生产默认拒绝未验证 Ingress）与 P1-10（客户端消息边界/授权流程）；
  `/ws` 此前无凭据任何人都可连，`AllowAllPlatformEventVerifier` 无条件注册等于永远放行。
- **决策**：
  - `/ws` hello 携带 `token`，服务端校验（`Faces:StreamerWs:AuthTokens`，支持 `token@unixMs过期`）；
    缺失/未知/过期 → 回 error 后以 `PolicyViolation` 关连接；**未配置 token 且未开匿名 → 全部拒绝（fail-closed）**；
  - `Ingress:AllowAll`（默认 `false`）才注册未签名验签兜底；无真实验证器的平台在生产配置下直接 401；
  - 消息体上限（默认 64KiB，超限 `MessageTooBig` 关连接）与 ACK 限频（默认 600/连接/分钟，超限丢弃并计数）；
  - **本地 demo 影响已消化**：appsettings.Development.json（Server/Gateway）显式 `Ingress:AllowAll=true` +
    `Faces:StreamerWs:AllowAnonymous=true` 并注明仅开发用；生产配置不带这两项，启动日志对两者打醒目告警。
- **影响**：直接 `dotnet run`（Development）demo 行为不变；生产/测试环境默认全拒绝，需配 token 或回调验签密钥。
  回滚面：恢复无条件注册 AllowAll + 去掉 hello 校验即可。

## 2026-10-16

### D7 UDS socket 文件生命周期自管（已代决）

- **背景**：滚动重启竞态——.NET 6 起 `Socket.Dispose` 无条件 unlink 用 `UnixDomainSocketEndPoint`
  绑定的路径（dotnet/runtime#52103，无 opt-out），旧进程 dispose 时新进程若已删-绑抢走同名路径，
  被 unlink 的是新进程 socket 的文件入口：内核 socket 未死但路径没了，后续连接全 ENOENT
  （压测曾靠手工 `rm -f /tmp/chorus/*.sock` 绕过）。
- **决策**：服务端 bind 改用自定义 `RawUnixEndPoint`（sockaddr 布局照抄 BCL，不进入其删除跟踪），
  路径删除权回归总线；关闭时按 bind 身份守卫删除（Unix `CreationTimeUtc` = st_ctime，bind 后不再变化），
  身份未知或文件已换主一律不删，异常退出的残留由下次 `StartServer` 删-绑兜底。删除动作挂 `DisposeAsync`
  而非 accept 循环尾部（带取消 token 的 `Task.Run` 可能从未被调度，循环尾部守卫会整体跳过）。
  设计细节见 [docs/design/ipc.md](./design/ipc.md)「socket 文件生命周期」。
- **影响**：线协议、通道名、配置全部不变；客户端 `ConnectAsync` 仍用 BCL 端点（从不 bind，不进跟踪）。
  回滚面：bind 端点改回 `UnixDomainSocketEndPoint` 并恢复原 `File.Delete` 即回到 BCL 管理（竞态重现）。

### D8 帧层流水线读 + 合并写（已代决）

- **背景**：拆分模式 eng→gw 单 socket 在 800 房 churn 下打满 21.5k msg/s 上限、发布失败 ~3.7%
  （todo.md §10 复核）。瓶颈是每消息的 I/O 次数而非带宽：写侧 `WriteFrameAsync` 分两次
  `WriteAsync`（header + payload）+ `Flush`，读侧每帧两次独立 `ReadAsync`（header + payload），
  且 JSON 序列化压在 `_clientLock` 临界区内，所有发布者排队。快照路径已做广播去重（每 session
  每 tick 一条），无更粗的合并点。
- **决策**：
  - 读侧新增 `FrameReader`（带缓冲、按帧边界解析）：一次 `ReadAsync` 可解出多条帧；帧超过缓冲时
    payload 直接读入目标数组，缓冲不随帧大小增长。三个流传输（UDS/TCP/NamedPipe）共用；
  - 写侧小帧（≤16KiB）header+payload 合并单次 `WriteAsync`（接收端是字节流，看不到写边界，不影响分帧）；
    大帧保持两次写，避免为省一次 syscall 复制整份 payload；
  - UDS 发布侧把 `JsonEnvelopeCodec.Serialize` 移出 `_clientLock` 临界区。
- **影响**：线协议字节不变、失败语义不变（干净 EOF 返 null、截断 header/payload 抛 IOException、
  非法长度拒绝），对端零感知；TCP/NamedPipe 的「锁内序列化」未动（本轮只优化实测瓶颈的 UDS 路径，
  无实测需求不扩散改动面）。回滚面：三个传输的 `FrameReader` 换回 `LengthPrefixedStream.ReadFrameAsync`、
  合并写回退为两次写。多 socket 分片与写合并泵（帧批写入队列）留待门控复测后再定。

### D9 门控复测取基线：②（多 socket 分片 / 写合并泵）维持暂缓（已代决，2026-10-16）

- **背景**：D8 落地（帧层流水线读 + 合并写 + 序列化出锁）时把「多 socket 按 session 分片」与
  「写合并泵」留待门控复测取新基线再定参数——缺基线而不是外部条件。本轮补齐该基线：同一拓扑
  （EngineHost + platform-gw + streamer-gw，`Ipc:GatewayIds=platform-gw,streamer-gw`、
  `Session:MaxSessions=1000`）同参数，在 D8 前树 `e74291c` 与 D8 后树 HEAD 各跑一轮
  （两树分别 `dotnet build -c Debug -o` 到独立目录，避免中途切换工作树）。主证据是
  churn-free 组（800 房 ×1 客户端、`--disconnect-every 0`、128 producers、60s），其工作量匹配
  （送达 107,794 vs 108,085，差 0.3%）；churn 组（800 房 ×3 客户端 = 2,400 WS）驱动侧工作量
  不匹配（送达 53,105 vs 32,657），仅作旁证。
- **测量结果（churn-free 主证据）**：`ipc_publish_failures` 4,324→1,425（−67%；按
  `snapshots_sent` 折算 0.69%→0.21%）、`snapshots_sent` +11.0%、引擎 `client_queue_dropped`
  −97%、网关 `ws_snapshot_dropped` −95%、引擎峰值 CPU 30.32→27.08（−10.7%，14 核 4.25→3.79 核）、
  无计划重连 4,775→1,678（−65%）、双侧零事件损失（零 503/429/401）、`maxLagTicks` 13 双侧不变、
  错误类别一致。**唯一恶化项** `tick_overruns`（`TickTimeoutMs=250`）13→38（+2.9×）。
- **恶化项结构定位**（未插桩计时）：tick 体内快照广播是 await 的
  （`SessionActor.cs:636/671/703`），经 `EngineBusSessionClient.SendSnapshotAsync` → UDS
  `PublishAsync` 在单一 `_clientLock` 上串行写单一 socket；D8 每帧 I/O 变便宜后交付量 +11%，
  同一 socket 的写阻塞暴露面随之上升（churn 组同向：1→15）。故残余瓶颈是「tick 内联 await 发布」
  与「单 socket 串行扇出」这一对，不是带宽也不是每帧 I/O 次数。合并写缓冲的每帧分配
  （`WriteFrameAsync` 对 ≤16KiB 帧 `new byte[4+n]`，实测快照均值 2,748 B）不构成 250ms 级停摆来源。
- **决定**：② 维持暂缓，理由从「缺基线」更新为「有基线且触发线未达」——三组峰值 21.67–30.32%
  （3.0–4.25 核）距 60% 触发线（8.4 核）2.0–2.8 倍余量；且写合并泵会把 `PublishAsync` 的失败语义
  从同步变异步（`snapshots_sent_total` 从「已写」变「已入队」、`SafePublishAsync` 与
  `WelcomePublisher` 的取舍需重排、welcome 延迟预算改动），而其目标工作负载（30k rps）仍被
  LoadTool 单进程驱动卡住（本轮 churn-free 仅 1,797 rps；churn 下上限 ~862 rps）。
- **触发条件（明确化）**：满足任一即重开 ②——引擎峰值 CPU > 60%（8.4 核）；或 LoadTool 驱动改造
  （异步发送 / 多进程驱动）落地后能把 800 房 churn 组的送达推到目标速率并复现 `tick_overruns`
  随交付量上升的趋势。两者都不满足前不为不可达负载改关键路径语义。
- **方法学注记**：驱动吞吐随机器负载波动（本轮 churn-free 1,797 rps vs R1015D 记录 4,706 rps），
  绝对速率不可跨轮比较；跨树比较只能用同轮 A/B 的归一化计数（每 `snapshots_sent`、每重连）。
