---
title: "平台接入参考：快手小游戏"
---


# 平台接入参考：快手小游戏（kuaishou mini-game）

> 范围：跑在快手小游戏容器内的客户端与服务端接入半侧。
> 它与快手直播回调接入（[platform-kuaishou.md](platform-kuaishou.md)）是**独立链路**。
> 本文记录 2026-10-14 官方公开文档的调研发现与当前实现契约。

## 1. 官方发现（2026-10-14）

**结论：快手公开文档没有找到"小游戏"专区——一切能力均为"公开文档未找到（待核）"。**

- 官方文档站 `open.kuaishou.com/docs` 的入口与内容页全是"**快手小程序**"
  （`/docs/develop/guide/introduction` 等）；未见小游戏专区，也未见 `ks.login` 式小游戏 API。
- 已核实的小程序侧能力（**不是小游戏**，仅作参考，不得当作小游戏能力使用）：
  - code2Session：`POST https://open.kuaishou.com/oauth2/mp/code2session`，
    `application/x-www-form-urlencoded`，字段 `js_code` / `app_id` / `app_secret`，
    响应 `result`（1 = 成功）/ `session_key`（24h）/ `open_id`
    （同一开发者主体下小程序间统一，按统一社会信用代码）；`js_code` 10 分钟内有效。
    来源：`https://open.kuaishou.com/docs/develop/server/code2Session.html`
  - 消息加密解密与签名：`sha1Hex(rawBody + 消息验证Token) == 请求头 kwaisign`，
    AES-256-CBC/PKCS7（Key = base64 解码的消息加密解密 KEY，IV = Key 前 16 字节）。
    来源：`https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html`
    （已落地为直播回调验签与信封解密，见 `KuaishouPlatformEventVerifier` /
    `KuaishouPlatformEventMapper`）
- 控制台里的具体配置项需开发者登录，未确认（不阻塞实现）。

### 已核实与待核对照

| 能力 | 状态 | 依据 |
|---|---|---|
| 客户端登录取 code（小游戏） | **公开文档未找到（待核）** | 公开文档只覆盖小程序 |
| 服务端 code2session（小游戏） | **公开文档未找到（待核）** | 只核实了小程序端点；小游戏是否复用未公开 |
| 容器内 WebSocket 实时通道 | **公开文档未找到（待核）** | 公开文档无小游戏 WebSocket 能力 |
| 开放数据域 | **公开文档未找到（待核）** | 公开文档无小游戏开放数据域 |
| 好友排行 / 云存储 | **公开文档未找到（待核）** | 公开文档无小游戏排行榜 / 云存储能力 |

## 2. 当前实现契约（`Chorus.Adapters.Platforms.MiniGame`）

- `MiniGamePlatformCapabilities.Kuaishou`：所有能力都标 `NotFoundPublicDocs`，
  `Supports()` 对一切能力返回 false——**不假设快手小游戏有任何未公开能力**。
- `MiniGameLoginExchangeConfig.KuaishouMiniProgram(appId, appSecret)`：
  提供**已核实的小程序** code2Session 官方形态（端点 + 字段名），
  供联调时直接复用；小游戏是否复用该端点**公开文档未找到（待核）**。
- 排行榜投影：`Project("kuaishou", ...)` 恒返回 null（能力未核实）。

## 3. 待外部清单（凭据与真机联调，显式不猜）

- [ ] 小游戏平台入口本身是否独立于小程序平台存在（需开发者后台 / 登录态确认）
- [ ] 小游戏 AppID / AppSecret / 消息验证 Token 等凭据（待外部提供）
- [ ] 登录、实时通信、排行榜的 SDK 能力规格（待官方文档发布后调研）
- [ ] 真机 / 开发者工具联调验证

## 4. 验收测试位置

| 用例 | 测试 |
|---|---|
| 快手所有能力待核、`Supports` 恒 false | `MiniGamePlatformCapabilitiesTests` |
| 已核实的小程序 code2Session 形态（端点 / 字段名） | `MiniGameLoginExchangeTests` |
| 快手排行榜投影返回 null | `MiniGameLeaderboardProjectorTests` |

相关：[platform-kuaishou.md](platform-kuaishou.md)（直播回调）、[adapter-contract.md](adapter-contract.md)（适配器契约）。
