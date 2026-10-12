# 决策记录（供复核）

> 用户授权代理拍板（2026-10-12 起）。每条含背景与影响；复核后如需改向，回滚面在此标注。

## 2026-10-12

### D1 平台适配器收进共享工程 `Cohort.Adapters.Platforms`

- **背景**：`Ingress/*` 三件套（`AllowAllPlatformEventVerifier` / `EventDeduplicator` / `TestPlatformEventMapper`）
  在 `Cohort.Server` 与 `Cohort.Gateway` 各存一份（todo §8 P1 已列「合并重复代码」）。
- **决策**：新增 `src/Cohort.Adapters.Platforms`（仅引用 Protocol），平台适配器与 ingress 共用件单份实现，
  Server / Gateway 改为引用；同时删除两处重复副本。
- **影响**：旧命名空间 `Cohort.Server.Ingress` / `Cohort.Gateway.Ingress` 移除，测试同步更新。
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
- **决策**：新增共享工程 `Cohort.Gateway.Core` 承载两 face 策略组件：
  - `platform-ingress` 面：限流（每来源固定窗口）、请求体上限（413）、请求超时（504）、按平台验签、去重，
    落在两个 host 的 `POST /ingress/{platform}` 处理器 + `/faces` 计数端点；
  - `streamer-ws` 面：鉴权（阶段批次 3）、ACK 限频、消息体上限，落在 `/ws` 处理器；
  - 两面配置分道（`Faces:PlatformIngress:*` / `Faces:StreamerWs:*`），日志分类按面
    （`Cohort.Ingress` / `Cohort.StreamerWs`），计数器经 `GET /faces` 分面暴露。
- **影响**：单体仍是默认部署形态；物理拆分等触发条件（worker CPU>60% 或单机数百房间），见拓扑文档 §4。
