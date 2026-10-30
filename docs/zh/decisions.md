# 决策日志（供评审）

> 委托决策已获用户授权（自 2026-10-12）。每条目附背景与影响；若评审改向，回滚面在此注明。

## 2026-10-12

### D1 平台适配器移入共享项目 `Chorus.Adapters.Platforms`

- **背景**：`Ingress/*` 三件套（`AllowAllPlatformEventVerifier` / `EventDeduplicator` /
  `TestPlatformEventMapper`）在 `Chorus.Server` 与 `Chorus.Gateway` 各存在一份
  （todo §8 P1 已列"合并重复代码"）。
- **决策**：新增 `src/Chorus.Adapters.Platforms`（引用 `Chorus.Protocol` 与
  `Chorus.Adapters.Abstractions`，两者都不引用 Engine/Server/Gateway）；平台
  适配器与共享入口部分得到单一实现，Server / Gateway 改为引用它；两份重复拷贝删除。
- **影响**：旧命名空间 `Chorus.Server.Ingress` / `Chorus.Gateway.Ingress` 移除，
  测试更新。平台私有逻辑不进核心（外部评审 §10）。回滚面：恢复两份目录拷贝。

### D2 签名校验按平台路由：具名校验器 + 路由器，无回退 = fail-closed

- **背景**：入口原本只有一个 `IPlatformEventVerifier` 单例放行一切；按平台校验器需要分发机制。
- **决策**：
  - `IPlatformNamedEventVerifier`（绑定平台名）注册进
    `PlatformEventVerifierRouter`；
  - 平台名存在 → 匹配校验器；不存在 → 开发回退
    （`AllowAllPlatformEventVerifier`，一期 ③ 显式开关化）；
  - 都没有 → **拒绝（fail-closed）** 并记 WARN。
- **影响**：接入新平台 = 注册一个具名校验器 + 一个映射器；路由不动。

### D3 两平台官方规格未核实部分：占位契约 + TODO，不做猜测实现

- **背景**：抖音文档站点在登录墙 + 反爬之后，故签名算法 / 事件字段 / 推送通道形态未核实（细节见
  [platform-douyin.md §1](./reference/platform-douyin.md)）；快手有官方
  "消息加解密与签名校验"文本，签名
  （sha1Hex(body+Token) / kwaisign）与 AES 信封按官方规格实现，但直播事件的内部 JSON 未指明（细节见
  [platform-kuaishou.md §1](./reference/platform-kuaishou.md)）。
- **决策**：
  - 抖音：校验用 HMAC-SHA256(appSecret, body) 占位；事件信封用 Chorus 定义占位字段；
  - 快手：校验与信封解密按官方算法；仅内部事件字段用占位契约（形态同抖音，两者在接入同一时点切换）；
  - 占位部分在代码注释与平台参考文档中显式标记"待接入"；切换点各自收敛到单个文件（校验器的 ComputeSignature /
    映射器的字段提取）。
- **影响**：接入期变更面 = 每平台 1 个校验器方法 + 1 个映射器文件 + 配置请求头名；管线、路由与测试结构不变。

### D4 两平台凭证来自配置，不硬编码

- **决策**：`Ingress:Douyin:AppSecret`、`Ingress:Kuaishou:VerifyToken` /
  `Ingress:Kuaishou:MsgEncryptKey`（及请求头名）从 `IConfiguration` 注入；缺键 → fail-closed。
- **影响**：生产密钥经环境变量 / 配置中心注入；仓库中无真实密钥。

### D5 先在单体内拆分监听代码（platform-ingress / streamer-ws），不拆进程

- **背景**：[process-topology.md](./design/process-topology.md) 定了"角色先拆、进程后拆"。
- **决策**：新增共享项目 `Chorus.Gateway.Core` 承载面策略组件：
  - `platform-ingress` 面：限流（每源固定窗）、请求体上限（413）、
    请求超时（504）、按平台校验、去重——落到两个宿主的
    `POST /ingress/{platform}` 处理器 + `/faces` 计数端点；
  - `streamer-ws` 面：认证、ACK 限流、消息大小上限，落到 `/ws` 处理器
    （2026-10-12 交付，见 D6）；
  - 两面有独立配置路径（`Faces:PlatformIngress:*` / `Faces:StreamerWs:*`）、
    每面日志类目（`Chorus.StreamerWs`；入口面暂无独立日志类目）、经 `GET /faces` 每面暴露计数。
- **影响**：单体仍是默认部署形态；物理拆分等触发条件（worker CPU>60% 或单机数百房间），见拓扑文档 §4。

### D6 主播 WS 认证 fail-closed + Ingress:AllowAll 默认关闭（演示经 Development 配置显式开启）

- **背景**：评审项 P0-4（生产必须默认拒绝未校验入口）与 P1-10（客户端消息边界/认证流）；此前 `/ws` 无凭证即接受任何人，
  且 `AllowAllPlatformEventVerifier` 无条件注册，即恒放行。
- **决策**：
  - `/ws` hello 携带 `token`，服务端校验（`Faces:StreamerWs:AuthTokens`，支持
    `token@unixMsExpiry`）；缺失/未知/过期 → 回 error 再以 `PolicyViolation` 关闭；
    **未配置 token 且未启用匿名 → 全部拒绝（fail-closed）**；
  - 仅 `Ingress:AllowAll`（默认 `false`）注册无签名校验回退；
    无真实校验器的平台在生产配置下直接 401；
  - 消息体上限（默认 64KiB，超限 `MessageTooBig` 关连接）与 ACK 限流（默认 600/连接/分钟，超限丢弃并计数）；
  - **本地演示影响已吸收**：appsettings.Development.json（Server/Gateway）显式设
    `Ingress:AllowAll=true` + `Faces:StreamerWs:AllowAnonymous=true`，标记仅开发；
    生产配置两者皆无，启动时对两者打响亮警告。
- **影响**：普通 `dotnet run`（Development）演示行为不变；生产/测试
  环境默认拒绝一切，需要 token 或回调校验密钥。回滚面：恢复无条件 AllowAll 注册 + 去掉 hello 检查。

## 2026-10-16

### D7 UDS socket 文件生命周期自管（委托决策）

- **背景**：滚动重启竞态——自 .NET 6 起，`Socket.Dispose` 无条件 unlink 用 `UnixDomainSocketEndPoint` 绑定的路径（dotnet/runtime#52103，无关闭选项）。若旧进程 dispose 时新进程已 delete-bind 同一路径，被 unlink 的是新进程的 socket 文件项：内核 socket 还活着但路径没了，后续连接全得 ENOENT（压测曾用手工 `rm -f /tmp/chorus/*.sock` 规避）。
- **决策**：服务端 bind 改用自定义 `RawUnixEndPoint`（sockaddr 布局照抄 BCL，在其删除追踪之外），路径删除权回到 bus；关闭时删除受 bind 身份守卫（Unix `CreationTimeUtc` = st_ctime，bind 后不变）——身份未知或文件已易主则绝不删除，崩溃进程的残留由下一次 `StartServer` 的 delete-then-bind 清理。删除挂在 `DisposeAsync` 上，而非 accept 循环尾巴（带取消令牌的 `Task.Run` 可能永不被调度，循环尾巴的守卫会被整体跳过）。
  设计细节：[docs/design/ipc.md](./design/ipc.md) "socket file lifecycle"。
- **影响**：线协议、通道名与配置全部不变；客户端 `ConnectAsync` 仍用 BCL 端点（从不 bind，从不被追踪）。回滚面：把 bind 端点换回 `UnixDomainSocketEndPoint` 并恢复原 `File.Delete` 交还 BCL 管理（竞态回来）。

### D8 流水线帧读取 + 合并写入（委托决策）

- **背景**：分离模式下 eng→gw 单 socket 在 800 房间抖动下饱和于 21.5k msg/s 上限，约 3.7% 发布失败（todo.md §10 评审）。瓶颈是每消息的 I/O 调用而非带宽：写侧每帧两次 `WriteAsync`（头 + 载荷）+ `Flush`；读侧每帧两次独立 `ReadAsync`（头 + 载荷）；且 JSON 序列化在 `_clientLock` 临界区内，所有发布者排队其后。快照路径已去重广播（每会话每 tick 一条消息），无更粗合并点。
- **决策**：
  - 读侧新增 `FrameReader`（缓冲，按帧边界解析）：一次 `ReadAsync` 可产出多帧；大于缓冲的帧直接读进目标数组，缓冲不随帧大小增长。三种流传输（UDS/TCP/NamedPipe）共享；
  - 写侧：小帧（≤16KiB）头 + 载荷合并成一次 `WriteAsync`（接收方是字节流，看不到写边界，成帧不受影响）；大帧保留两次写，避免为了省一次系统调用而整拷贝载荷；
  - UDS 发布者把 `JsonEnvelopeCodec.Serialize` 移出 `_clientLock` 临界区。
- **影响**：线字节不变、失败语义不变（干净 EOF 返回 null，截断头/载荷抛 IOException，非法长度拒绝）——对端零感知；TCP/NamedPipe 保留锁内序列化（本轮只优化实测的 UDS 瓶颈；无实测需要不扩散变更面）。回滚面：把三传输的 `FrameReader` 换回 `LengthPrefixedStream.ReadFrameAsync`，合并写换回两次写。多 socket 分片与写合并泵（批帧写队列）等门禁复测。

### D9 门禁复测基线已取：②（多 socket 分片 / 写合并泵）继续推迟（委托决策，2026-10-16）

- **背景**：D8 落地（流水线读 + 合并写 + 序列化移出锁）后，"每会话多 socket 分片"与"写合并泵"留待门禁复测以定参数——缺一个基线，而非外部条件。本轮补齐该基线：同一拓扑（EngineHost + platform-gw + streamer-gw，`Ipc:GatewayIds=platform-gw,streamer-gw`，`Session:MaxSessions=1000`）、同参数，在 D8 前树 `e74291c` 与 D8 后 HEAD 各跑一次（两树都用 `dotnet build -c Debug -o` 建到独立目录，避免运行中途切工作树）。主证据是无抖动组（800 房间 × 1 客户端，`--disconnect-every 0`，128 生产者，60s），其负载匹配（投递 107,794 vs 108,085，差 0.3%）；抖动组（800 房间 × 3 客户端 = 2,400 WS）驱动侧负载不匹配（投递 53,105 vs 32,657），仅作佐证。
- **测量（无抖动主证据）**：`ipc_publish_failures` 4,324→1,425（−67%；按 `snapshots_sent` 归一，0.69%→0.21%），`snapshots_sent` +11.0%，引擎 `client_queue_dropped` −97%，网关 `ws_snapshot_dropped` −95%，引擎峰值 CPU 30.32→27.08（−10.7%，14 核 4.25→3.79），未调度重连 4,775→1,678（−65%），两侧零事件丢失（零 503/429/401），两侧 `maxLagTicks` 13 不变，错误类目相同。**唯一回退**：`tick_overruns`（`TickTimeoutMs=250`）13→38（+2.9×）。
- **回退已结构性定位**（无计时插桩）：tick 内快照广播被 await（`SessionActor.cs:636/671/703`），经 `EngineBusSessionClient.SendSnapshotAsync` → UDS `PublishAsync` 在单一 `_clientLock` 下串行写单 socket；D8 让每帧 I/O 更便宜，投递上升 11%，同 socket 上的写阻塞暴露随之上升（抖动组同向：1→15）。故残余瓶颈是"tick 内 await 发布" + "单 socket 串行扇出"这对组合——不是带宽，不是每帧 I/O 调用数。合并写缓冲的每帧分配（`WriteFrameAsync` 对 ≤16KiB 帧做 `new byte[4+n]`；实测快照均值 2,748 B）不是 250ms 级停顿来源。
- **决策**：②继续推迟，理由从"缺基线"改为"基线已取、触发线未达"——三项峰值读数 21.67–30.32%（3.0–4.25 核）对 60% 触发线（8.4 核）保有 2.0–2.8× 余量；且写合并泵会把 `PublishAsync` 的失败语义从同步改为异步（`snapshots_sent_total` 从"已写"变"已入队"，`SafePublishAsync` / `WelcomePublisher` 的权衡需重排，welcome 延迟预算改变），而其目标负载（30k rps）仍被单进程 LoadTool 驱动封顶（本轮无抖动仅 1,797 rps；抖动下约 862 rps 上限）。
- **触发条件（显式化）**：满足其一即重开 ②——引擎峰值 CPU > 60%（8.4 核）；或 LoadTool 驱动重构（异步发送 / 多进程驱动）落地、能把 800 房间抖动组的投递推到目标速率、并在投递上复现 `tick_overruns` 上升。两者任一成立前，不为不可达负载做临界路径语义变更。
- **方法学注记**：驱动吞吐随机器负载波动（本轮无抖动 1,797 rps vs R1015D 记录的 4,706 rps）；绝对速率跨轮不可比；跨树比较必须用同轮 A/B 归一计数（按 `snapshots_sent`、按重连）。
