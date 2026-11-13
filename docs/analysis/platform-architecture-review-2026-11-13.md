# 平台适配层架构深度审核（2026-11-13）

- 工具：`codex exec --profile luna -s read-only`（只读审核：未修改文件、未运行构建/测试）
- 审核基线：HEAD `1cd55b8`
- 纪律：发现带 file:line、按 Critical/Major/Minor 分级、末尾 tokens used、不凑数
- 性质：静态调用链核对结果；代码中的「官方已核实」视为仓库声明，未独立复核外部协议
- 处置：**本报告只落账（发现已同步 todo.md），不做修复**（修复另派）

## 结论速览

平台事件、启动身份和履约任务在接口层已有基本分工，但**应用身份绑定、任务路由和 B 站桥投递尚未形成一致的端到端边界**。快手属于「公共 HTTP 入口已接线、直播协议仍待完成」，当前整体不能仅凭配置校验和文件齐全判定生产就绪。

| 分级 | 数量 |
|---|---|
| Critical | 3 |
| Major | 7 |
| Minor | 2 |

## Critical

### C1. 应用身份未进入验签信任边界，可改变去重命名空间

- 位置：`src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:105`、`src/Chorus.DataServer/RedesignInputEventStore.cs:165`
- 问题：入口直接信任调用方的 `X-Chorus-App-Id`；三个生产平台 verifier 均未将它与凭据对应的应用绑定。存储按 `(platform, app_id, source_dedup_key)` 去重，但归属查询只使用 `platform + canonical_live_session_key`（`RedesignInputEventStore.cs:199`），没有应用条件。
- 影响：同一份有效签名报文更换应用头后，可以作为另一条事件入库，并仍归属原绑定。具备合法报文重放条件的调用方，可绕过去重，污染事件来源及计费/礼物等数据。
- 修复方向：验签返回受信任的应用身份；入口拒绝头与凭据身份不一致；去重和归属统一使用该身份。

### C2. 任务队列没有平台/应用隔离，执行器可能调用错误平台

- 位置：`src/Chorus.PlatformGateway/TaskReconciler.cs:47`、`src/Chorus.DataServer/RedesignPlatformTaskStore.cs:507`、`src/Chorus.Rpc/Protos/m1.proto:382`
- 问题：宿主通过 `CHORUS_PLATFORM_ADAPTER` 选择唯一适配器；`TaskListRequest` 只有 `max`，SQL 从全局 `live.task` 取任务，返回契约也没有平台/应用字段。执行器直接使用自身适配器处理所有取到的任务。结束任务的 `DesiredJson` 同样没有平台/应用身份（`EndOperationCoordinatorService.cs:352`）。
- 影响：混合平台或同平台多应用共用数据面时，抖音、快手乃至 simulator 执行器会争抢彼此任务；可能错误上报外部接口，或把真实履约任务标为已完成。
- 修复方向：从绑定解析平台/应用，贯穿任务存储、发现、租约和执行前检查，并按适配器凭据作用域筛选。

### C3. B 站桥与新入口契约不兼容，且转发失败直接丢弃事件

- 位置：`src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveClient.cs:365`、`src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:105`
- 问题：桥的 `SignedRequest` 只添加 `BilibiliSigner.BuildHeaders` 返回的头（`BilibiliOpenLiveClient.cs:417`），没有新入口必需的 `X-Chorus-App-Id`。直连当前 PlatformGateway 时必然返回 `400 missing_app_id`。此外，非成功响应及网络异常只增加 `PostFailures`、记录日志，随后接收下一条消息，没有重试或持久待投递记录。
- 影响：配置完整、桥启动成功并不代表数据能进入新管线；契约错误会持续丢事件，短暂 503 也会造成不可恢复缺口。
- 修复方向：补齐应用身份契约；将桥转发改为可持久恢复的投递流程，保持事件标识不变。现有 `tests/.../BilibiliEndToEndTests.cs:20` 测的是旧 `Chorus.Server`，不能证明这个组合可用。

## Major

### M1. 随机生成事件 ID，使合法重试变成新事件

- 位置：`src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventMapper.cs:60`、`src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventMapper.cs:66`
- 问题：缺少事件 ID 时使用 `Guid.NewGuid()`；同样行为还存在于 test/simulator mapper。与之相对，快手缺少 `msgId` 就拒绝映射（`KuaishouPlatformEventMapper.cs:55`）。随机 ID 与抖音注释「Keep dedup stable」相矛盾。
- 影响：同一签名报文每次映射产生不同 ID，旧内存去重和新持久去重都失效，礼物等事件可能重复生效。
- 修复方向：生产平台要求可信稳定 ID；若协议确实无 ID，明确设计稳定来源键，不能简单把所有相同内容事件合并。

### M2. 生产准入机制在 launch、ingest、task 三面不一致

- 位置：`src/Chorus.Platform/NotProductionReadyLaunchAdapters.cs:20`、`src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventVerifier.cs:11`、`src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:77`
- 问题：launch 有网关准入检查，未完成 stub 永远拒绝；ingest 仅校验配置键存在，仍可挂载占位的抖音签名/schema、快手直播 schema；task 的 `ProductionReady=false` 在宿主和执行器中没有被检查。
- 影响：补齐任意凭据即可启用尚未验证的 ingest/task 实现；「配置完整」被误当成「协议可生产使用」。
- 修复方向：按平台与 surface 声明准入状态，在生产宿主统一执行；保留明确的开发启用开关。

### M3. AllowAll 是全未知平台 fallback，环境隔离仅靠提示

- 位置：`src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs:40`、`src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:97`、`src/Chorus.Server/Program.cs:56`
- 问题：`Ingress:AllowAll=true` 没有 Development 环境限制，fallback 接受所有未注册平台；新宿主同时挂载 test/simulator mapper，旧宿主始终挂载 test mapper。旧宿主的 `appsettings.Development.json` 默认开启此开关。
- 影响：生产误配后可以提交自选会话、礼物等测试事件；未来新增 mapper 却漏注册 verifier 时，也可能静默接受未验签数据。
- 修复方向：开发 verifier 只绑定 `test/simulator`，禁止通用 fallback；非开发环境拒绝开启。
- 边界澄清（审核原文强调）：**已注册平台验签失败不会进入 fallback**，不能把 AllowAll 描述为三平台的无条件绕过。

### M4. Reserved 被当作已投递重复，存在提前成功确认窗口

- 位置：`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:79`、`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:98`
- 问题：`TryReserve` 对未过期的 `Reserved` 和 `Delivered` 都返回 false；入口统一返回 `200 duplicated=true`。触发序列：A 占位、B 重试得到成功确认、A 随后投递失败并 rollback。
- 影响：B 的成功确认没有任何成功投递事实支撑，可能使上游停止重试。该问题属于旧 `PlatformIngressFace`，新入口使用持久回执，不使用此类。
- 修复方向：区分处理中和已提交；处理中等待结果或返回可重试状态，仅已提交记录允许成功去重确认。

### M5. 各平台防重放强度不等，去重不能替代完整的新鲜度检查

- 位置：`src/Chorus.Adapters.Platforms/Kuaishou/KuaishouPlatformEventVerifier.cs:73`、`src/Chorus.Adapters.Platforms/Bilibili/BilibiliSigner.cs:80`、`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:134`
- 问题：快手仅校验 `SHA1(rawBody + token)`，不消费外层 `timestamp`；抖音仅签 body，也无新鲜度检查。B 站检查 ±300 秒，但 nonce 只参与签名、不记录已使用状态。旧去重仅为进程内 `(platform,eventId)`，默认 600 秒，可因重启、TTL 或容量淘汰失效。
- 影响：旧管线中抖音/快手的历史有效报文在去重失效后仍能通过；不同应用/房间 ID 若非全局唯一还可能互相抑制。
- 修复方向：按实际协议增加受签名保护的新鲜度约束，并设计应用/来源作用域的持久防重放策略；不能擅自修改官方签名算法。

### M6. 模块声明与实际注册没有一致性校验，编译器无法发现漏接

- 位置：`src/Chorus.Adapters.Abstractions/IPlatformEventMapper.cs:5`、`src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs:25`、`src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:87`
- 问题：verifier 有 `PlatformName`，mapper 没有对应声明，通过顺序遍历取首个结果。模块可以声明 `EventIngest` 却不注册 mapper/verifier；能力表仍会生成。router 的 `named[name] = verifier` 还会静默覆盖重复注册。
- 影响：漏注册表现为运行时 401/400，重复注册可能改变安全实现；模块能力表不能证明接线完整。
- 修复方向：mapper 也绑定平台身份，启动时校验事件模块与 mapper/verifier 一一对应，重复身份直接报错。

### M7. 抖音 code2session 存在两套实现，模块化尚未覆盖 launch/task

- 位置：`src/Chorus.Platform/DouyinMiniGameLaunchAdapter.cs:82`、`src/Chorus.Adapters.Platforms/Douyin/DouyinMiniGameAuthClient.cs:38`、`src/Chorus.PlatformGateway/PlatformModules/MiniGamePlatformModule.cs:32`
- 问题：两处分别构造相同 OpenAPI 请求、解析成功及身份字段；launch 不复用已有 auth client。`minigame` 模块只注册 auth client，实际 launch 由 StreamerServer 独立装配，task 又由 PlatformGateway 的独立 switch 装配。
- 影响：协议修改和异常处理容易漂移；「启用 minigame 模块」并不等于启用完整登录链。
- 修复方向：抽取共享平台客户端，让 launch 适配器只做请求/结果转换，并明确各宿主负责的 surface。
- 目录判断（审核原文）：**不建议仅为目录整齐把 launch 类直接搬入现有 Adapters.Platforms**——后者还依赖 Engine/Data；应先确定共享客户端的依赖边界。

## Minor

### m1. PlatformNames 只是常量容器，并非统一注册中心；名称规则也不一致

- 位置：`src/Chorus.Adapters.Platforms/PlatformNames.cs:3`、`src/Chorus.Platform/RedesignPlatformGateway.cs:11`、`src/Chorus.Adapters.Platforms/MiniGame/MiniGamePlatformCapabilities.cs:79`
- 问题：五个常量之外没有枚举、启用状态或能力约束；launch/fulfillment/task switch、MiniGame 能力表、LoadTool 均散落平台字符串。verifier 使用大小写不敏感，launch 使用 `Ordinal`，新入口还把原始路径平台名传给数据面。
- 影响：增加常量不会自动启用平台；同名大小写可能通过验签却无法匹配下游，增加部署排错成本。
- 修复方向：在低依赖契约层共享平台标识并统一入口规范化；保持 `minigame` 这一模块名与平台 ID 的语义区别。

### m2. 旧宿主重复注册能力表，接口及日志说明存在误导

- 位置：`src/Chorus.Server/Program.cs:80`、`src/Chorus.Adapters.Abstractions/IPlatformNamedEventVerifier.cs:5`、`src/Chorus.PlatformGateway/Program.cs:183`
- 问题：能力表在 80、88 行重复注册；接口注释称缺 verifier 总是 fail closed，但 router 支持 fallback；启动提示「platform signatures are not required」又容易被理解为已注册平台也免验签。
- 影响：维护注册时容易只改一份；运维对实际安全行为产生误判。
- 修复方向：删除重复注册，准确描述「仅未注册平台进入开发 fallback」。

## 五平台信任链对照

| 项目 | bilibili | kuaishou | douyin | test / simulator |
|---|---|---|---|---|
| 数据入口 | OpenLive WSS → 自有桥 → 通用 ingress；也接受符合签名的 POST | 通用 `/ingress/kuaishou` HTTP 入口 | 通用 HTTP 入口 | 通用 HTTP 入口 |
| 签名 | 六个规范化头的 HMAC-SHA256，含 body MD5 | verifier 内置 `SHA1(body + token)`，无需独立 signer 文件 | body HMAC-SHA256，占位协议 | 无专属事件 verifier，依靠 AllowAll |
| 凭据绑定 | 检查 AccessKeyId；未绑定 Chorus 应用头 | Token；未核对 `componentAppId` | Secret；未绑定应用身份 | 无 |
| 时间/nonce | ±300 秒；nonce 无消费记录 | 不检查 envelope 时间 | 无 | 无 |
| 比较方式 | `FixedTimeEquals` | `string.Equals` | `FixedTimeEquals` | 恒真 |
| body 处理 | MD5 与签名绑定；JSON mapper | 先验外层签名，再 AES-CBC 解密；内层直播 schema 占位 | JSON schema 占位 | 自定义测试 schema |
| 来源 ID | 缺失随机生成 | 必须有 `msgId` | 缺失随机生成 | 缺失随机生成 |

两点审核判断：

1. B 站签名在这里证明的是**持有共享密钥的桥/调用方发出了报文**，并非平台对每条 WSS 消息提供了独立签名。上游信任仍依赖 OpenLive 会话、TLS 和桥进程。
2. **「快手两文件是否半成品」的明确判断**：没有独立 ingress/signer 文件是合理的结构差异——HTTP 接收在宿主公共入口，签名算法在 verifier 内。但直播接入本身仍是**明确未完成的集成实现**：mapper 第 18 行承认内层协议占位，`docs/zh/reference/platform-kuaishou.md:47` 还承认直播是否复用此信封、响应格式和通道形态未确认。因此不能认定「缺四个文件就是缺四项能力」，也不能认定它已经达到 B 站接入完成度；B 站六文件同样存在上述端到端断点（C3）。

## 扩展成本：新增平台触碰面清单

注册方式的直接答案：**没有 DI 扫描**。模块目录由手工列表选择，模块手工注册 `IPlatformNamedEventVerifier`，router 从 `GetServices<…>()` 构建字典；mapper 使用另一套顺序列表。`PlatformNames`、模块目录、事件能力表、launch 列表、task switch、MiniGame 能力 switch 是分离的维护点。

未知模块、未知 launch、未知 task adapter 默认明确拒绝，未发现这些路径默认选择某个真实平台。真正需要防范的静默点是 **AllowAll fallback、重复 verifier 覆盖、mapper 漏注册、MiniGame `_ => null` 和 LoadTool 不签名**。平台事件类型的 `_ => Unknown` 则是事件兼容策略，不是未知平台路由。

以下区分事件接入必需项与按能力选做项；**新增平台不必仿造 B 站六文件**。

| 文件 / 注册点 | 新增 douyu 时的动作 | 漏改结果 |
|---|---|---|
| `src/Chorus.Adapters.Platforms/PlatformNames.cs:9` | 增加平台常量 | 不会自动注册；使用字符串仍可编译 |
| **新增** `Douyu/DouyuPlatformEventVerifier.cs` | 实现 named verifier，使用真实协议 | 无 fallback 时 401；AllowAll 下有静默放行风险 |
| **新增** `Douyu/DouyuPlatformEventMapper.cs` | 实现映射、稳定来源 ID、房间标识转换 | 运行时 `unmapped_event` |
| **新增** `PlatformModules/DouyuPlatformModule.cs` | 声明 surface/事件能力，验证配置并注册 mapper/verifier | 类型存在也不会被自动发现 |
| `src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:25` | 加入 `AvailableModules()` 手工列表 | 配置 `douyu` 时启动明确报未知模块 |
| `src/Chorus.Engine/Routing/PlatformCapabilityRegistry.cs:30` | 若沿用现有静态声明方式，增加 Douyu 能力；也可在新模块直接构造 | 独立维护时容易与 mapper 不一致 |
| 部署配置 `Ingress:Platforms`、`Ingress:Douyu:*` | 显式启用并注入凭据 | 未列出就完全不挂载；列出但缺键则启动失败 |
| `src/Chorus.Server/Program.cs:60` | **若继续支持旧宿主**，另行注册具体 verifier、named verifier、mapper 及能力 | 新宿主支持不会同步到旧宿主 |
| **新增** Douyu client/protocol/bridge | **仅平台要求主动拉流/WSS 时**增加；HTTP 回调无需此组文件 | 拉流平台仅有 mapper/verifier 无法取得事件 |
| **新增** `src/Chorus.Platform/DouyuLaunchAdapter.cs` | **若支持主播登录/启动**，实现 launch；未验证时保持拒绝 | 不影响纯事件接入，但不能完成主播启动 |
| `src/Chorus.StreamerServer/Program.cs:54` | 把 launch adapter 加入手工列表及凭据配置 | `RedesignPlatformGateway` 明确返回 `platform-unknown` |
| **新增** `src/Chorus.Platform/DouyuFulfillmentAdapter.cs` | **若需平台履约**，实现调用及结果查询语义 | 平台义务不能由其他平台适配器代替 |
| `src/Chorus.PlatformGateway/Program.cs:68` | 在 task adapter switch 增加选择项 | 未知值明确抛错；但仍须先修复全局任务串平台问题（C2） |
| `src/Chorus.Adapters.Platforms/MiniGame/MiniGamePlatformCapabilities.cs:79` | **若支持小游戏面**，增加能力记录及 `TryGet` 分支 | `_ => null` 静默表现为不支持 |
| MiniGame 登录交换 / 新平台认证客户端、模块 | **按小游戏产品协议**实现并装配 | `MiniGamePlatformModule` 当前只处理抖音，不会自动支持 |
| `src/Chorus.LoadTool/IngressSigner.cs:24` | **若需要签名压测**，增加算法及选择分支 | 未知平台返回 null，不签名 |
| `src/Chorus.LoadTool/LoadToolOptions.cs:9` | 按需调整平台配置和事件生成 | 默认仍为抖音 |
| `src/Chorus.UnitySdk/Platforms/Douyu/*` | **若要求 Unity 客户端支持**，新增适配器/桥/程序集声明 | 服务端接入不会带来客户端支持 |
| `tests/Chorus.Engine.Tests/*`、平台参考文档 | 增加独立签名向量、模块装配、实际宿主端到端及重试证据 | 现有平台测试不能覆盖新平台注册完整性 |

## 原子 TODO 拆分

以下每项应连同针对性回归证据作为一个独立 commit。已同步 todo.md（未勾选，修复另派）。

- [ ] 让事件验签产生受信任的应用身份并拒绝应用头冒用（`src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:105`）—— 调用方不能自行选择事件的应用及去重命名空间。
- [ ] 为事件归属查询加入受信任的应用作用域（`src/Chorus.DataServer/RedesignInputEventStore.cs:195`）—— 去重作用域与绑定归属必须一致。
- [ ] 为任务存储及 RPC 暴露从绑定解析的平台/应用身份（`src/Chorus.Rpc/Protos/m1.proto:386`）—— 执行器目前无法可靠识别任务所属凭据域。
- [ ] 按平台/应用筛选任务发现并在执行前检查适配器身份（`src/Chorus.PlatformGateway/TaskReconciler.cs:47`）—— 防止不同平台或应用执行器串领任务。
- [ ] 为 B 站桥转发补齐受信任的应用身份契约（`src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveClient.cs:365`）—— 当前直连新入口会被固定拒绝。
- [ ] 为 B 站桥增加持久待投递及失败恢复（`src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveClient.cs:361`）—— 接收过的 WSS 消息不能因一次 HTTP 失败直接丢弃。
- [ ] 移除抖音生产事件缺 ID 时的随机兜底（`src/Chorus.Adapters.Platforms/Douyin/DouyinPlatformEventMapper.cs:60`）—— 相同报文重试必须保持来源标识不变。
- [ ] 移除 B 站生产事件缺 ID 时的随机兜底（`src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventMapper.cs:66`）—— 当前兜底会绕过所有下游来源去重。
- [ ] 在事件模块装配阶段执行生产准入检查（`src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:77`）—— 占位协议不能仅凭存在配置键就进入生产。
- [ ] 在任务适配器装配阶段检查 `ProductionReady`（`src/Chorus.PlatformGateway/Program.cs:68`）—— 当前 readiness 属性没有执行效果。
- [ ] 将开发事件放行限定为明确的 test/simulator 身份并限制宿主环境（`src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs:40`）—— 消除未知平台注册缺口被 fallback 掩盖的问题。
- [ ] 区分处理中与已提交的去重结果（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:98`）—— 避免尚未投递就成功确认重复。
- [ ] 为抖音/快手补齐协议支持的持久防重放边界（`src/Chorus.Adapters.Platforms/Kuaishou/KuaishouPlatformEventVerifier.cs:73`）—— 有效签名不能被无限期当作新鲜事件使用。
- [ ] 让 mapper 声明所属平台并校验模块注册完整性（`src/Chorus.Adapters.Abstractions/IPlatformEventMapper.cs:5`）—— 编译通过不能掩盖 mapper/verifier 漏接。
- [ ] 对重复 named verifier 注册直接报错（`src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs:25`）—— 禁止按注册顺序静默替换安全实现。
- [ ] 抽取共享抖音认证客户端供 launch 使用（`src/Chorus.Platform/DouyinMiniGameLaunchAdapter.cs:82`）—— 消除相同 OpenAPI 请求和响应处理的双份实现。
- [ ] 统一共享平台标识及入口规范化规则（`src/Chorus.Adapters.Platforms/PlatformNames.cs:3`）—— 避免跨宿主字符串与大小写语义漂移。
- [ ] 删除旧宿主重复能力表注册（`src/Chorus.Server/Program.cs:88`）—— 保留单一有效装配位置。
- [ ] 修正缺 verifier 行为的接口注释及 AllowAll 启动提示（`src/Chorus.Adapters.Abstractions/IPlatformNamedEventVerifier.cs:5`）—— 文档应准确反映 fallback 及已注册平台仍须验签的行为。

## tokens used

125,482（codex exec 报告值；报告同时注明接口未提供精确用量统计，无法可靠报数，此为其给出的数值）
