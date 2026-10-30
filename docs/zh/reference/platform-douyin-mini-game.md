---
title: "平台接入参考：抖音小游戏"
---


# 平台接入参考：抖音小游戏（douyin mini-game）

> 范围：跑在抖音小游戏容器内的客户端（`tt.*` JS API）与服务端接入半侧。
> 它与直播回调接入（[platform-douyin.md](platform-douyin.md)）是**独立链路**：
> 直播链路是"平台把事件推给本服务"；小游戏链路是"客户端登录 + 服务端身份交换 +
> 排行榜投影"。本文记录 2026-10-14 官方公开文档的调研发现与当前实现契约。

## 1. 官方发现（2026-10-14）

- 官方文档站：`developer.open-douyin.com/docs/resource/zh-CN/mini-game`，
  分**快速开始 / 开发 / 运营**三栏；导航与索引可匿名访问。
- 官方 **JavaScript API 索引页**（可静态抓取）确认下列 API **名字存在**：
  - 登录：`tt.login`（`/develop/api/open-capacity/log-in/tt-login`）、`tt.checkSession`
  - 开放数据域：`tt.getOpenDataContext`、`tt.onMessage`
  - 开放数据 / 云存储：`tt.setUserCloudStorage`、`tt.getUserCloudStorage`、
    `tt.getCloudStorageByRelation`、`tt.removeUserCloudStorage`、`tt.getSharedCanvas`
  - 游戏排行榜：`tt.setImRankData`、`tt.getImRankList`、`tt.getImRankData`、`tt.setImRankDataInOpenContext`
  - 网络：`tt.getNetworkType`、`tt.onNetworkStatusChange`、`tt.offNetworkStatusChange`
- **索引下的各 API 详情页是 JS 渲染**；公开抓取只能拿到
  导航壳，拿不到签名 / 参数 / 返回结构 / 错误码内容——因此
  只确认了"API 存在"；字段与语义均为**公开文档未找到（待核）**。
- **服务端 OpenAPI 细节已在 2026-10-14 复核中取得**（首轮被 JS 渲染挡住）：
  getAccessToken / code2Session / 玩家排行上报——三个端点、参数、响应、错误
  码全部确认，并已落地在
  `src/Chorus.Adapters.Platforms/Douyin/`（`DouyinMiniGameAuthClient` 等，见
  [platform-douyin.md](platform-douyin.md) §2）——能力表条目
  `ServerSessionExchange` 已升级为 VerifiedOfficial。
- **服务端 OpenAPI 菜单已全部看到**（凭证 / 登录 / 数据缓存 / 二维码 / 其他 /
  订阅消息 / 赛事 ×2）：**没有直播观众事件的服务端推送通道**；
  直播互动只有客户端侧——`tt.getLiveManager` + LiveManager——不虚构推送客户端。
- **容器内实时通道（WebSocket / connectSocket）不在官方索引内**
  （网络类只有上面 3 个 API）→ 实时通信能力**公开文档未找到（待核）**；接入层不得假设小游戏容器
  提供 WebSocket。

### 已核实与待核对照

| 能力 | 状态 | 依据 |
|---|---|---|
| 客户端登录取 code（`tt.login`） | **已核实**（API 名） | 官方 JS API 索引页 |
| 服务端 code2session 换 session | **已核实**（2026-10-14 细节） | [code-2-session 官方文档](https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/server/log-in/code-2-session)；已落地，见 [platform-douyin.md](platform-douyin.md) §2.2 |
| 容器内 WebSocket 实时通道 | **公开文档未找到（待核）** | 官方索引网络类无 socket API；服务端 OpenAPI 无推送通道（复核确认） |
| 开放数据域（`tt.getOpenDataContext` / `tt.onMessage`） | **已核实**（API 名） | 官方 JS API 索引页 |
| 好友排行 / 云存储 / 游戏排行榜 API | **已核实**（API 名） | 官方 JS API 索引页 |

## 2. 当前实现契约（`Chorus.Adapters.Platforms.MiniGame`）

接入层以**能力表**为唯一事实来源；未核实的能力不可用——防止虚构平台能力：

```
src/Chorus.Adapters.Platforms/MiniGame/
├── MiniGamePlatformCapabilities.cs   // 能力表（已核实/待核 + 待外部）
├── MiniGameLoginExchange.cs          // 服务端 code2session（配置驱动，fail-closed）
├── MiniGameIdentityBinder.cs         // 登录 session → Chorus Player 身份
└── MiniGameLeaderboardProjector.cs   // Chorus 排行行 → 平台中性 payload
```

抖音已核实的三个服务端 OpenAPI（token / code2Session / 排行上报）是
`src/Chorus.Adapters.Platforms/Douyin/` 里的**专属实现**
（`DouyinAccessTokenProvider` / `DouyinMiniGameAuthClient` / `DouyinRankUploadAdapter`，20 个金测）——
通用层（配置驱动）留给未核实平台，不与专属实现重复接线；
见 [platform-douyin.md](platform-douyin.md) §2。

### 2.1 能力表（`MiniGamePlatformCapabilities`）

- `Supports(capability)` 只对 **VerifiedOfficial** 能力返回 true；
  公开文档未找到（待核）的能力永不可用。
- 凭据 / 真机联调项不是能力；它们单独记在
  `ExternalPending`（标"待外部"）。
- 未知平台 `TryGet` 返回 null（fail-visible）。

### 2.2 登录会话交换（`HttpMiniGameLoginExchange`）

- 端点、AppID、AppSecret、表单字段名、成功标志字段、账号字段名**全部配置注入**；
  不硬编码任何平台私有字段名（未核实平台由调用方显式配置）。
- 凭据或端点缺失 → **拒绝一切（fail-closed）**：不发请求、记 ERROR 日志
  （只记一次）。
- 平台成功标志不匹配 / 缺账号 id / 非 2xx / JSON 畸形 / 网络异常 →
  返回 null。
- 请求体上限 16KB（超限 body 不读不解析）。
- HttpClient 由调用方注入；本实现不 Dispose 它。

### 2.3 身份映射（`MiniGameIdentityBinder`）

- 登录 session → `PlatformIdentityRegistry`（§14）里的 **Player** 身份；默认身份 id 确定性推导为
  `<platform>:<platformAccountId>`（重登幂等）。
- 同一平台账号已绑定到别的 Chorus 身份 → **fail-visible**（报出
  既有映射，不隐式覆盖），防止被劫持的登录码接管他人身份。

### 2.4 排行榜投影（`MiniGameLeaderboardProjector`）

- 输入是引擎侧权威排行榜行（名次由调用方给出；投影**不重新排名**）。
- 排行能力未核实的平台 → 返回 null（不假设平台有
  排行榜 API）。
- 输出是**平台中性的行 payload**（rank / participantId / score）；
  开放数据域沙箱内的绘制是平台侧 JavaScript，**不在本仓**
  （待外部）。

## 3. 待外部清单（凭据与真机联调，显式不猜）

- [ ] 小游戏 AppID / AppSecret（凭据，待外部提供，永不写进代码）
- [x] 服务端 code2session 端点与参数（2026-10-14 对照官方文档核实并落地，见 [platform-douyin.md](platform-douyin.md) §2.2）
- [ ] 真机 / 开发者工具联调验证容器内是否存在实时通道（Socket）
- [ ] 开放数据域沙箱内的排行榜绘制脚本（平台侧 JS，不在本仓）

## 4. 验收测试位置

| 用例 | 测试 |
|---|---|
| 能力表已核实 / 待核 / 待外部 | `MiniGamePlatformCapabilitiesTests` |
| code2session 成功 / 失败 / fail-closed / 字段名 / 大小限制 | `MiniGameLoginExchangeTests` |
| 抖音服务端 OpenAPI 三项（token / code2Session / 排行上报） | `DouyinMiniGameClientTests` / `DouyinRankUploadAdapterTests` |
| 身份映射确定性 / 幂等 / 冲突 fail-visible | `MiniGameIdentityBinderTests` |
| 排行榜投影能力门控 / 截断 / 入参校验 | `MiniGameLeaderboardProjectorTests` |

相关：[platform-douyin.md](platform-douyin.md)（直播回调）、[adapter-contract.md](adapter-contract.md)（适配器契约）。
