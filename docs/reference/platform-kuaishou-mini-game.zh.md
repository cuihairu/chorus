---
title: 平台对接参考：快手小游戏
---


<p align="center">
  <a href="./platform-kuaishou-mini-game.md">English</a> | 简体中文
</p>

# 平台对接参考：快手小游戏（kuaishou mini-game）

> 适用范围：运行在快手小游戏容器内的客户端与服务端适配半边。
> 与快手直播回调接入（[platform-kuaishou.md](platform-kuaishou.zh.md)）是**两条不同的链路**。
> 本文记录 2026-10-14 的官方公开文档调研结果与当前实现契约。

## 1. 官方口径调研结论（2026-10-14）

**结论：快手公开文档中未找到「小游戏」板块——全部能力「未找到公开资料(待核)」。**

- 官方文档站 `open.kuaishou.com/docs` 的入口与正文页均为「**快手小程序**」
  （`/docs/develop/guide/introduction` 等），未见小游戏板块，未见 `ks.login` 类小游戏 API。
- 已核实到的小程序侧能力（**不是小游戏**，仅作参照，不得当作小游戏能力使用）：
  - code2Session：`POST https://open.kuaishou.com/oauth2/mp/code2session`，
    `application/x-www-form-urlencoded`，字段 `js_code` / `app_id` / `app_secret`，
    响应 `result`（1=成功）/ `session_key`（24h）/ `open_id`
    （同一开发者主体下的小程序统一，依据社会信用代码），`js_code` 有效期 10 分钟。
    来源：`https://open.kuaishou.com/docs/develop/server/code2Session.html`
  - 消息加解密与签名：`sha1Hex(rawBody + 消息验证Token) == 请求头 kwaisign`，
    AES-256-CBC/PKCS7（Key = base64 解码的消息加密解密KEY，IV = Key 前 16 字节）。
    来源：`https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html`
    （已落地为直播回调验签与信封解密，见 `KuaishouPlatformEventVerifier` /
    `KuaishouPlatformEventMapper`）
- 控制台具体配置入口需开发者登录，未核实（不阻塞实现）。

### 已核实 vs 待核

| 能力 | 状态 | 依据 |
|---|---|---|
| 客户端登录取 code（小游戏） | **未找到公开资料(待核)** | 公开文档仅覆盖小程序 |
| 服务端 code2session（小游戏） | **未找到公开资料(待核)** | 仅核实到小程序端点，是否复用未公开 |
| 容器内 WebSocket 实时通道 | **未找到公开资料(待核)** | 公开文档未见小游戏 WebSocket 能力 |
| 开放数据域 | **未找到公开资料(待核)** | 公开文档未见小游戏开放数据域 |
| 好友排行 / 云存储 | **未找到公开资料(待核)** | 公开文档未见小游戏排行/云存储能力 |

## 2. 当前实现契约（`Chorus.Adapters.Platforms.MiniGame`）

- `MiniGamePlatformCapabilities.Kuaishou`：全部能力标记 `NotFoundPublicDocs`，
  `Supports()` 对任何能力均返回 false——**不假设快手小游戏存在任何未公开的能力**。
- `MiniGameLoginExchangeConfig.KuaishouMiniProgram(appId, appSecret)`：
  提供的是**已核实的小程序** code2Session 官方形态（端点 + 字段名），
  供联调时直接复用；小游戏是否复用该端点 **未找到公开资料(待核)**。
- 排行榜投影：`Project("kuaishou", ...)` 恒返回 null（能力未核实）。

## 3. 待外部清单（凭据与真机联调，明示不做猜测）

- [ ] 小游戏平台入口本身是否独立于小程序平台存在（需开发者后台 / 登录态确认）
- [ ] 小游戏 AppID / AppSecret / 消息验证 Token 等凭据（待外部提供）
- [ ] 登录、实时通信、排行榜三段的 SDK 能力口径（待官方文档公开后调研）
- [ ] 真机 / 开发者工具联调验证

## 4. 验收测试落点

| 用例 | 测试 |
|---|---|
| 快手全部能力待核、`Supports` 恒 false | `MiniGamePlatformCapabilitiesTests` |
| 已核实的小程序 code2Session 形态（端点/字段名） | `MiniGameLoginExchangeTests` |
| 排行榜投影对快手返回 null | `MiniGameLeaderboardProjectorTests` |

相关：[platform-kuaishou.md](platform-kuaishou.zh.md)（直播回调）、[adapter-contract.md](adapter-contract.zh.md)（适配器契约）。
