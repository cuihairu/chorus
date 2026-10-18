---
title: 平台对接参考：抖音小游戏
---

# 平台对接参考：抖音小游戏（douyin mini-game）

> 适用范围：运行在抖音小游戏容器内的客户端（`tt.*` JS API）与服务端适配半边。
> 与直播回调接入（[platform-douyin.md](platform-douyin.md)）是**两条不同的链路**：
> 直播链路是「平台推事件到本服务」，小游戏链路是「客户端登录 + 服务端换身份 +
> 排行榜投影」。本文记录 2026-10-14 的官方公开文档调研结果与当前实现契约。

## 1. 官方口径调研结论（2026-10-14）

- 官方文档站：`developer.open-douyin.com/docs/resource/zh-CN/mini-game`，
  下分 **快速入门 / 开发 / 运营** 三板块，可匿名访问导航与索引。
- 官方 **JavaScript API 索引页**（静态可抓取）已核实下列 API **名称存在**：
  - 登录：`tt.login`（`/develop/api/open-capacity/log-in/tt-login`）、`tt.checkSession`
  - 开放数据域：`tt.getOpenDataContext`、`tt.onMessage`
  - 开放数据 / 云存储：`tt.setUserCloudStorage`、`tt.getUserCloudStorage`、
    `tt.getCloudStorageByRelation`、`tt.removeUserCloudStorage`、`tt.getSharedCanvas`
  - 游戏排行：`tt.setImRankData`、`tt.getImRankList`、`tt.getImRankData`、`tt.setImRankDataInOpenContext`
  - 网络：`tt.getNetworkType`、`tt.onNetworkStatusChange`、`tt.offNetworkStatusChange`
- **索引页下的各 API 正文页为 JS 渲染**，公开抓取只能拿到导航壳，
  拿不到签名 / 参数 / 返回结构 / 错误码正文——因此只有「API 存在」被核实，
  字段与语义一律 **未找到公开资料(待核)**。
- **服务端 OpenAPI 正文 2026-10-14 复核已拿到**（首轮 JS 渲染受阻）：getAccessToken /
  code2Session / 选手排名上报三项端点、参数、响应与错误码全部核实，并已落地
  `src/Chorus.Adapters.Platforms/Douyin/`（`DouyinMiniGameAuthClient` 等，
  详见 [platform-douyin.md](platform-douyin.md) §2）——能力表
  `ServerSessionExchange` 已升级 VerifiedOfficial。
- **服务端 OpenAPI 菜单已见全**（凭证/登录/数据缓存/二维码/其它/订阅消息/赛事×2）：
  **无直播观众事件服务端推送通道**，直播互动仅客户端
  `tt.getLiveManager` + LiveManager——不伪造推送客户端。
- **容器内实时通道（WebSocket / connectSocket）在官方索引中未列出**
  （网络分类仅上述 3 个 API）→ 实时通信能力 **未找到公开资料(待核)**，
  适配层不得假设小游戏容器提供 WebSocket。

### 已核实 vs 待核

| 能力 | 状态 | 依据 |
|---|---|---|
| 客户端登录取 code（`tt.login`） | **已核实**（API 名） | 官方 JS API 索引页 |
| 服务端 code2session 换 session | **已核实**（2026-10-14 正文） | [code-2-session 官方文档](https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/server/log-in/code-2-session)；已落地，见 [platform-douyin.md](platform-douyin.md) §2.2 |
| 容器内 WebSocket 实时通道 | **未找到公开资料(待核)** | 官方索引网络分类无 socket API；服务端 OpenAPI 无推送通道（复核确认） |
| 开放数据域（`tt.getOpenDataContext` / `tt.onMessage`） | **已核实**（API 名） | 官方 JS API 索引页 |
| 好友排行 / 云存储 / 游戏排行 API | **已核实**（API 名） | 官方 JS API 索引页 |

## 2. 当前实现契约（`Chorus.Adapters.Platforms.MiniGame`）

适配层以**能力表**为唯一事实源，未核实的能力不可用——防止编造平台能力：

```
src/Chorus.Adapters.Platforms/MiniGame/
├── MiniGamePlatformCapabilities.cs   // 能力表（已核实/待核 + 待外部）
├── MiniGameLoginExchange.cs          // 服务端 code2session（配置驱动，fail-closed）
├── MiniGameIdentityBinder.cs         // 登录 session → Chorus Player 身份
└── MiniGameLeaderboardProjector.cs   // Chorus 排行行 → 平台中性 payload
```

抖音服务端 OpenAPI 已核实的三项（token / code2Session / 排名上报）为**专项实现**，
在 `src/Chorus.Adapters.Platforms/Douyin/`（`DouyinAccessTokenProvider` /
`DouyinMiniGameAuthClient` / `DouyinRankUploadAdapter`，金测 20 例）——通用层
（配置驱动）留给未核实平台，不与专项实现重复接线，详见
[platform-douyin.md](platform-douyin.md) §2。

### 2.1 能力表（`MiniGamePlatformCapabilities`）

- `Supports(capability)` 仅对 **VerifiedOfficial** 的能力返回 true；
  未找到公开资料(待核)一律不可用。
- 凭据 / 真机联调项不是能力，单独记入 `ExternalPending`（标「待外部」）。
- 未知平台 `TryGet` 返回 null（fail-visible）。

### 2.2 登录换 session（`HttpMiniGameLoginExchange`）

- 端点、AppID、AppSecret、表单字段名、成功标志字段、账号字段名**全部配置注入**，
  不写死任何平台私有字段名（未核实的平台由调用方显式配置）。
- 凭据或端点缺失 → **全部拒绝（fail-closed）**，不发请求、打 ERROR 日志（只打一次）。
- 平台成功标志不匹配 / 缺账号 id / 非 2xx / 畸形 JSON / 网络异常 → 返回 null。
- 请求体上限 16KB（超限不读不解析）。
- HttpClient 由调用方注入、本实现不释放。

### 2.3 身份映射（`MiniGameIdentityBinder`）

- 登录 session → `PlatformIdentityRegistry`（§14）的 **Player** 身份，
  默认身份 id 确定性派生 `<platform>:<platformAccountId>`（重登幂等）。
- 同一平台账号已绑定到其他 Chorus 身份 → **fail-visible**（报告既有映射，
  不隐式覆盖），防止被截取的登录 code 劫持他人身份。

### 2.4 排行榜投影（`MiniGameLeaderboardProjector`）

- 输入为引擎侧权威的排行行（排名由调用方给出，投影**不重排**）。
- 平台未核实排行能力 → 返回 null（不假设平台有排行 API）。
- 输出为**平台中性行 payload**（rank / participantId / score）：
  开放数据域沙箱内的绘制是平台侧 JavaScript，**不在本仓**（待外部）。

## 3. 待外部清单（凭据与真机联调，明示不做猜测）

- [ ] 小游戏 AppID / AppSecret（凭据，待外部提供，绝不写进代码）
- [x] 服务端 code2session 端点与参数（2026-10-14 官方正文核实并落地，见 [platform-douyin.md](platform-douyin.md) §2.2）
- [ ] 真机 / 开发者工具联调验证容器内实时通道（Socket）能力是否存在
- [ ] 开放数据域沙箱内的排行绘制脚本（平台侧 JS，不在本仓）

## 4. 验收测试落点

| 用例 | 测试 |
|---|---|
| 能力表已核实/待核/待外部 | `MiniGamePlatformCapabilitiesTests` |
| code2session 成功/失败/fail-closed/字段名/体积上限 | `MiniGameLoginExchangeTests` |
| 抖音服务端 OpenAPI 三项（token/code2Session/排名上报） | `DouyinMiniGameClientTests` / `DouyinRankUploadAdapterTests` |
| 身份映射确定性/幂等/冲突 fail-visible | `MiniGameIdentityBinderTests` |
| 排行榜投影能力门控/截断/输入校验 | `MiniGameLeaderboardProjectorTests` |

相关：[platform-douyin.md](platform-douyin.md)（直播回调）、[adapter-contract.md](adapter-contract.md)（适配器契约）。
