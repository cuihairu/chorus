# 抖音、快手直播互动玩法：物料与架构兼容性核查

> 核查日期：**2026-10-30（Asia/Shanghai）**。范围为直播小玩法的 PC/Unity 客户端与开发者后端，不将小游戏容器 API 作为同一产品的证据。仅收集资料与实施前置条件；不表示已获得权限、下载 SDK 或完成生产接入。目前接入范围仍只有抖音、快手。
>
> 状态：**已确认**指官方公开正文能够支持；**需权限**指实际后台、包体或开通结果需要获授权账号；**待核实**指当前证据不足。网页核查日期与网页发布日期分别记录；版权年份和搜索引擎抓取时间均不是发布日期。

## 1. 两平台对照

| 项目 | 抖音 | 快手 |
|---|---|---|
| 正确产品入口 | [直播玩法文档指引](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/introduction/userGuide/userguide)；公开介绍与 Unity SDK 均已确认 | [直播小玩法介绍](https://open.kuaishou.com/miniPlayDocs/introduction/Barragegameplay/introduction.html)；虽然站点标题写“小程序文档”，正文描述 PC 伴侣启动 exe 的直播玩法，已确认 |
| 主播端启动 | 伴侣或移动端云启动传入 token，再由玩法服务端兑换直播身份，已确认 | 伴侣以 `-c` 传 roomCode，或开播后通过 IPC 补发/更新，已确认 |
| 互动消息链路 | 官方提供抖音云、SDK 指令直推等模式；公开更新日志还列出 SDK/服务器双推能力，不能强行统一为一种链路 | 服务端调用直播间绑定后，平台向开发者 HTTPS 回调地址 POST，已确认 |
| Unity 集成物料 | BGDT 分发 LiveOpenSDK；实际可下载版本需权限 | 官方公布伴侣 IPC 库和 C# 接入说明；不能把 IPC 库写成完整的 Unity 礼物 SDK |
| Chorus 兼容判断 | 服务端入站模式与 PlatformGateway 职责可对应；直推/双推模式需先确定权威输入和履约责任 | 服务端回调与 PlatformGateway 职责可对应；绑定状态、roomCode 轮换及回调幂等需明确建模 |

表中启动、推送与 SDK 的依据见下文逐项链接。兼容判断属于对 [Chorus 目标架构](../../architecture/architecture.md) 的推论，不是平台对 Chorus 的认证。

## 2. 抖音（核查日期：2026-10-30）

### 2.1 官方入口、物料和权限

| 物料 / 能力 | 本次核查结果 | 时间与来源 |
|---|---|---|
| 创建玩法、能力申请 | 已确认：先创建直播玩法和 AppID，提案通过后申请评论、点赞、礼物、exe 包体等能力；云启动为另一项可选能力。真实开通情况需权限 | 页面未展示可独立确认的发布日期；[创建直播玩法](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/introduction/zn/toybox-start) |
| 提案、视频、开播说明 | 已确认存在提案、上线文档与视频等材料要求；后台当前模板、图标/图片规格及字段必填性需权限后逐项存档，不能凭经验填规格 | 准入规则正文列有历次规则日期，含 2025-05-01；这不是本次全部页面的更新时间。[准入规则](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/introduction/live-play/creation)、[创建玩法](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/introduction/zn/toybox-start) |
| Unity SDK 获取 | 已确认：先安装 BGDT 插件，选择 LiveOpenSDK；企业/渠道/内部测试版本受权限控制。样例含 AppId 及抖音云环境参数 | 页面未显示发布日期；[Unity SDK 接入](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/unity-sdk/unity-sdk-access) |
| 引擎要求 | 已确认：文档列 Unity 2020+ LTS、Mono/IL2CPP、C# 8+。不能据此保证任意 Unity 6 版本已联调 | 同上；SDK 接入页 |
| SDK 版本证据 | 已确认：公开日志首项为 **2.7.4 / 2025-08-06**，记录 SDK 与服务器同时接收指令。此处只记录公开日志，不声称 2026-10-30 企业渠道最新包就是该版本；实际包、校验值和许可需权限 | [SDK 更新日志](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/unity-sdk/update-log) |

### 2.2 生命周期与能力边界

| 环节 | 已确认事实 / 未决项 | 官方依据 |
|---|---|---|
| 启动和身份绑定 | 伴侣/云启动将 token 交给玩法，玩法转交服务端，服务端换取直播间与主播信息；该启动 token 文档有效期为 30 分钟。不能将它当作 Chorus 长期重连票据 | [获取直播信息](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/server/live-room-scope/audience-play/get-live-info) |
| 评论、点赞、礼物 | 官方 SDK 列出抖音云接收与指令直推模式；具体选定模式的消息 ID、批次、重放、回调验签与补拉契约需逐 API 核验。不能从“支持直推”推出必须把客户端上报当权威事实 | [Unity SDK 概览](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/unity-sdk/overview) |
| 履约 ACK | SDK 概览明确要求接入履约上报；传输已收取、Chorus 持久接收、房间执行和画面呈现是不同节点。具体 ACK 对应节点、超时与多接收端责任需权限联调，不能在 HTTP 接收成功时伪称已生效 | [Unity SDK 概览](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/unity-sdk/overview) |
| 对局 / 阵营、战绩与榜单 | SDK 提供对局/阵营能力及战绩榜单；官方明确榜单是直播间维度，不支持世界榜。开始、结束、分数写入的实际 API 字段与幂等恢复仍需专项核验 | [Unity SDK 概览](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/unity-sdk/overview) |
| 服务端鉴权 | 直播玩法专属指南规定 SHA256-RSA2048，应用私钥由开发者保存，平台公钥用于验签，涉及回调的请求也须验签。须按具体能力接口使用，不能把小游戏签名替换进来 | [签名及验签指南](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/server/live-room-scope/signature-guide) |
| 云同步 | 官方定义为同一云 Unity 进程处理多路输入和多画面，公开说明最多两路输出。不是 Chorus 多 GameServer 所有权恢复或客户端追赶算法；本地/云启动限制也需按该模式单独评估 | [云同步能力概览](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/unity-sdk/cloud-sync/cloud-sync-overview) |

除上表单独标明的版本日志日期外，以上能力页本次可读正文未显示可确认的发布日期；核查时间统一为 2026-10-30。

## 3. 快手（核查日期：2026-10-30）

### 3.1 产品与 SDK 物料

| 物料 / 能力 | 本次核查结果 | 来源原文时间与链接 |
|---|---|---|
| 直播玩法形态 | 已确认：介绍页描述主播通过 PC 伴侣启动独立 exe，观众以评论、点赞和礼物互动。其旧版场景限制不能覆盖后来新增的双人连屏能力 | 介绍页更新时间 2023-09-25；[直播小玩法介绍](https://open.kuaishou.com/miniPlayDocs/introduction/Barragegameplay/introduction.html) |
| 提案物料 | 已确认：要求开发者/团队、玩法说明和 demo；视频展示开始、互动与结算及主播露出。资料模板是否更新需获授权对接核查 | 更新时间 2023-09-22；[玩法提案](https://open.kuaishou.com/miniPlayDocs/introduction/Accessprocess/material/playmethod) |
| 客户端包 | 已确认：PC 包含 `ks_manifest.json`、启动 exe、进程/捕获配置，可配置规则图片；伴侣传入 `-c`、`-ipc` 等参数。IPC 会更新 roomCode，也通知退出 | 更新时间 2024-11-22；[客户端接入说明](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/clientside) |
| SDK 获取与版本 | 已确认：该页公开提供伴侣 IPC 二进制库及 C# 示例入口，版本表列 **8fd4d4d / 2023-05-12**。它负责本机进程通信；不能宣称它是完整 Unity 网络/礼物 SDK，或是当前最新包。实际许可、二进制与 Unity 平台兼容性待核实 | [客户端接入说明及版本表](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/clientside) |
| 底部栏 / 可操作扩展 | 已确认存在支持 Unity 等引擎的附属玩法能力，部分接口须白名单；这是附属玩法集成资料，不能代替主播 PC 主程序的服务端绑定契约 | 更新时间 2025-02-06；[底部栏面板接入](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/dibulan) |

### 3.2 服务端链路与生命周期

公开 [玩法技术基础能力](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/localdevelopment.html) 标记更新时间 **2024-11-22 14:42:27**，确认了以下直播专属契约：

| 环节 | 公开契约摘要 | 对后续实现的含义 |
|---|---|---|
| 服务端授权 | 服务端用 app_id/app_secret 换 access_token；API 需 scope | 凭证集中管理；存在 API 文档不代表本应用已获权限 |
| 绑定 / 解绑 / 查状态 | `smallPlay/bind` 的 start/stop/status；客户端启动后需服务端绑定，才开始互动推送 | 直播绑定与 Chorus RoomId 分开；roomCode 改变需重新核验，不能沿用旧路由 |
| 开始 / 结束对局 | `smallPlay/round` 的 start/stop，roundId 在同一 roomCode 下不得跨局复用，支持不同对局类型 | 结束对局与解绑是两个动作；不能用一个泛化“结束”吞掉差异 |
| 礼物 / 评论 / 点赞推送 | HTTP POST；支持订阅礼物、匹配评论与点赞数据；平台明确允许乱序 | 游戏规则必须处理到达乱序，不假定先选队后送礼 |
| 幂等与成功回执 | `data.unique_message_id` 用于幂等；外层 message_id 不作该用途；HTTP 200 且 result=1 才确认成功 | 平台接收 ACK 与游戏执行完成分开，不能套旧占位 msgId |
| 测试流量 | 文档规定识别 stress_ 测试数据，避免进入真实排行榜等 | 验收样本与真实资产、积分隔离 |

上述均来自同一份 [直播基础接口正文](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/localdevelopment.html)，是协议核对摘要，不是已实现声明。重试次数、回调超时、乱序窗口、补拉与玩法履约 ACK 的完整契约仍须逐项形成联调证据。

该直播正文**明确链接** [附录 2：回调方式和策略](https://open.kuaishou.com/docs/develop/server/epay/appendix.html)，附录给出 `kwaisign = MD5(原始 HTTP body + app_secret)`。附录自身是支付文档，采用它的依据是直播正文的直接引用；需用平台直播签名样本再次核验。不可用另一个 SaaS 产品的 SHA1/Token/AES 协议替代。附录本次正文未显示可确认发布日期。

双主播视频能力另有 [双人连屏文档](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/Linkedscreen.html)，更新时间 **2024-11-22**：区分玩法发起与伴侣发起连线，前者与 PK 生命周期关联，并有心跳与 stop。该视频连线不是游戏帧同步；逻辑暂停时是否继续心跳、故障恢复是否重建连线需专项联调。普通继续一局策略不能擅自停止平台保活。

排行榜：本次已读直播基础资料不足以确认可直接复用的全局榜单服务或与抖音相同的榜单 API；标记**待核实**，不把未找到解释为平台不支持。Chorus 内部排行榜可以规划，但不能宣称已能自动写平台榜单。

## 4. Chorus 架构兼容性与凭证边界

以下属于基于官方契约与 [目标架构](../../architecture/architecture.md) 的设计判断，不新增平台承诺：

| 方面 | 判断与实施约束 |
|---|---|
| 身份与房间路由 | 使用平台、AppID、主播 openId、直播/绑定代次建立映射，再关联 Chorus 参与方、RoomId、GameServer 所有者及房间 epoch。roomCode、平台直播间 ID、平台 roundId、RoomId、Tick、epoch 不能混作同一标识。平台不会天然知道玩家在哪个 GameServer |
| 服务端互动入站 | PlatformGateway 验证来源、解析平台事实、按 ControlServer 已确认的路由投递 GameServer/RoomActor。ControlServer 负责路由发布与确认；DataServer 只处理持久读写，不承担平台事件解析 |
| 抖音直推 / 双推 | 先选定链路。客户端收到 SDK 指令不等于服务器已经获得可验证事实；双推必须按同一平台事件去重，并明确谁上报履约 ACK。禁止两条链路各执行一次游戏逻辑；具体可信证明机制尚待核验 |
| 主播实时链路 | U3D SDK 处理官方启动/IPC，再通过 StreamerGateway 登录、重连、订阅权威状态；游戏帧数据走既定实时通道。StreamerServer 处理主播身份业务，不为平台 IPC 或帧同步凭空增加 RPC 中继 |
| 关闭顺序 | 先确定最终权威边界与 PlatformGateway 停投/排空确认，再由协调流程通知平台结束对局/解绑；外部响应未知时保持待确认状态。平台 stop/解绑、内部路由关闭和 Actor 销毁分别记录，不能提前销毁后补发 |
| 平台状态兼容 | 快手 bind 与 round 分开；双人视频连线也有独立保活。抖音不同启动/推送模式应配置化。一个主播断线不直接等同关播或解除平台绑定；游戏暂停规则不能隐式关闭平台生命周期 |
| 跨平台匹配 | 本次资料不构成跨平台账号合并、礼物价值换算、跨平台 PK 展示或收益规则的授权证据。继续遵循同平台匹配默认策略 |

| 配置 / 物料 | 存放边界 |
|---|---|
| AppID、平台选择、公开接入地址、玩法包与审核展示图、规则说明 | 可成为客户端公开配置或审核物料；配置公开不等于能力已开通 |
| app_secret、应用私钥、服务端 access_token、SaaS 密钥 | 仅服务端秘密管理；不得写入 Unity Resources、包体、前端控制台或本研究文档。不同协议的密钥不得混用 |
| 启动 token、roomCode、IPC 参数、重连票据 | 属于会话上下文；即使经命令行交付也不应完整记录到日志。服务器按平台核验，再签发 Chorus 自己的会话授权 |
| SDK 包、版本、许可、校验值、后台 scope 截图 | 需获授权后补档，敏感项脱敏；不将账号权限页面或包体链接长期有效作为假设 |

## 5. 后续 agent 应完成的独立核验

- [ ] 用获授权账号记录抖音实际 LiveOpenSDK 版本、许可、校验值与当前能力开通项。
- [ ] 在抖音云、SDK 直推、SDK/服务器双推中选定一期模式，并保存对应真实签名事件及事件 ID 样本。
- [ ] 记录抖音每种事件的接收回执、履约 ACK 节点、重试、重复、补拉、停推和平台结束结果查询契约。
- [ ] 记录快手当前 IPC 包、许可、Unity 兼容范围，验证启动后开播与开播后启动两种 roomCode 流程。
- [ ] 用快手真实直播样本验证被直播文档引用的 MD5 回调验签，禁止沿用 SaaS 封装作验收依据。
- [ ] 核对快手推送重试/超时/乱序、停绑后尾包、批次与礼物单号幂等的完整规则。
- [ ] 分别确认两平台结束对局、结束玩法、解绑与视频连线的关闭先后、可查询状态和未知结果恢复方式。
- [ ] 分别补齐平台榜单的实际 API、作用域、写入权限、结算幂等及测试数据隔离证据。
- [ ] 保存两平台后台最新提案/包体/图标/视频/规则图模板，逐项记录规格、来源时间和权限条件。
- [ ] 输出 U3D 条件编译与配置向导计划，区分平台官方 SDK、Chorus SDK、服务端秘密配置；不把“只填 AppID”当作免审核承诺。

## 6. 旧文档使用限制

本次只新增资料，不修改旧文档。后续整理应保留它们的历史时间和产品范围，避免作为直播接入的最终证据：

| 旧文件 | 应注意的问题 |
|---|---|
| [platform-douyin.md](../../reference/platform-douyin.md)、[platform-douyin-mini-game.md](../../reference/platform-douyin-mini-game.md) | 2026-10-12/14 的小游戏 code2Session、tt API 调研与直播占位契约，不能推出直播玩法没有服务端链路或 Unity SDK |
| [platform-kuaishou.md](../../reference/platform-kuaishou.md) | SaaS kwaisign/AES 包络与直播事件内层占位契约，不能作为本次直播基础协议的已验证实现 |
| [platform-kuaishou-mini-game.md](../../reference/platform-kuaishou-mini-game.md) | “未找到小游戏文档”只描述当次范围，不否定已公开的 miniPlayDocs 直播 PC 产品 |
| [直播互动重设计基线](../live-interactive-redesign-baseline.md) | 已正确提示产品范围与旧占位风险；本次新增 SDK 版本、快手直播回调和客户端物料证据，后续应与具体契约核验共同使用 |
