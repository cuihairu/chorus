---
title: B 站弹幕源适配器（调研 + 接入方案）
---

# B 站弹幕源适配器（调研 + 接入方案）

本文回答两个问题：**B 站是否支持弹幕互动游戏**（调研，带出处），以及**chorus 怎么接入**（方案，文档先行不本轮写码）。

> 状态：**方案文档先行，未本轮写码**。调研结论分「支持 / 不支持 / 部分支持」并逐条给出处；查不到的写「未找到公开资料」，不编。协议细节给出处，不硬编码绕过平台风控，敏感凭据不进仓。

---

## 1. 调研：B 站是否支持弹幕互动游戏

### 1.1 结论：**部分支持**

| 侧 | 支持情况 | 说明 |
| --- | --- | --- |
| **输入侧**（弹幕/礼物/进场 → 游戏状态） | **支持** | 官方 open-live 开放平台提供完整事件（弹幕/礼物/进场/开播/下播），是当前**唯一法律干净**的通道 |
| **输出侧**（游戏画面回播到直播间） | **不支持（无官方通道）** | 官方未提供「把游戏画面推回直播间弹幕/评论区」的 API；社区做法是 OBS 浏览器源叠加（本地画面采集，非平台通道） |
| **反向互动指令**（游戏操作 → 直播间动作） | **不支持** | 官方无此通道；任何反向操作都需平台明确允许后才可做 |

### 1.2 输入侧：官方 open-live 开放平台

- **平台存在且事件齐全**：[open-live.bilibili.com](https://open-live.bilibili.com/)（官方直播开放平台）。
- **接入流程**（blivedm `clients/open_live.py` 实现，✅ 原文核实）：
  1. 凭据四件套：`access_key_id` / `access_key_secret` / `app_id` / `room_owner_auth_code`。
  2. `POST https://live-open.biliapi.com/v2/app_start` → 返回 `websocket_info.wss_link` + `auth_body`。
  3. HTTP 头签名：`x-bili-accesskeyid` / `x-bili-content-md5` / `x-bili-signature-method: HMAC-SHA256` / `x-bili-signature-nonce` / `x-bili-signature-version: 1.0` / `x-bili-timestamp` → `Authorization`。
  4. 心跳：项目心跳 20s / WS 30s；错误码 7003。
- **事件类型**（blivedm `blivedm/handlers.py`，✅ 原文核实）：`DANMU_MSG`、`SEND_GIFT`（2026-07 起灰度 `SEND_GIFT_V2` protobuf）、`GUARD_BUY`、`USER_TOAST_MSG_V2`、`SUPER_CHAT_MESSAGE` / `SUPER_CHAT_MESSAGE_DELETE`、`INTERACT_WORD_V2`、`LIVE_ROOM_ENTER`、`LIVE_START`、`LIVE_END`、`LIVE_OPEN_PLATFORM_INTERACTION_END`。
- **平台仍在演进**：`SEND_GIFT_V2` 灰度说明官方协议会变，适配器须容忍未知事件类型（不崩、可丢弃）。

### 1.3 输入侧：web 逆向通道（有法律先例，不主推）

- **协议**（镜像 pskdje/bilibili-API-collect，✅ 原文核实，`docs/live/message_stream.md`）：wss `/sub`、认证包 5 秒内发送否则断、token 走 `getDanmuInfo`（Wbi 签名）、心跳 30s/60s 断、16 字节包头、op 2/3/5/7/8、未登录 mid=0 用户名打码。
- **⚠️ 法律先例**：bilibili-API-collect（20.2k★）**2026-01-28 收到 B 站律师函、2026-01-30 归档删库**（原仓 README 可证）。→ web 逆向通道有被法律行动的现实先例，**chorus 只主推 open-live**，不内置 web 逆向实现。
- **隐私**：未登录用户 mid=0、用户名打码；适配器不做用户身份推断。

### 1.4 输出侧：无官方通道

- 官方**没有**「游戏画面推回直播间」的 API。社区做法（如竞品 open-block-war）是 **OBS 浏览器源**：本地跑一个网页渲染游戏画面，OBS 采集后推流——这是**画面叠加**，不是平台通道，游戏本身不感知。
- 结论：输出侧**不支持**；要做回播只能走 OBS 本地叠加，chorus 不内置（属用户侧集成）。

### 1.5 竞品做法（公开资料）

| 项目 | 做法 | 备注 |
| --- | --- | --- |
| [KeJunMao/open-block-war](https://github.com/KeJunMao/open-block-war) | React + Phaser「弹弹乐」，B 站官方接口 + 野生 ws，OBS 浏览器源回播 | 已停更 |
| [MrXiaoM/bilibili-open-live](https://github.com/MrXiaoM/bilibili-open-live) | Minecraft 插件，基于 open-live | 与 chorus 架构最接近（open-live 接入 + 游戏映射） |
| [seriserendipia/gojyuon_danmaku_game](https://github.com/seriserendipia/gojyuon_danmaku_game) | 依赖 blivedm | — |
| [mcube-12139/danmaku-game](https://github.com/mcube-12139/danmaku-game) | 弹幕游戏 | — |
| [PaticoBar/WhacAMole_Live](https://github.com/PaticoBar/WhacAMole_Live) | 直播打地鼠 | — |
| [zerocraft/bilirs](https://github.com/zerocraft/bilirs) | Rust 实现 | — |
| [fython/bili-open-live-go](https://github.com/fython/bili-open-live-go) | Go 实现 | — |
| [tangclQaQ/BOpenliveSDK-UE4](https://github.com/tangclQaQ/BOpenliveSDK-UE4) | UE4 SDK | — |
| [CXHGT/BilibiliSdkDemo](https://github.com/CXHGT/BilibiliSdkDemo) | Android demo | — |

**共同点**：都走 open-live 或 web 逆向拿弹幕，都**不**把游戏画面推回直播间（OBS 除外）。无一家实现「帧同步 + 弹幕互动」的公开架构文章。

### 1.6 未找到公开资料（诚实标注）

- 官方明文「允许/禁止第三方弹幕互动游戏」的条款原文。
- open-live 官方文档**正文**（页面 JS 渲染，仅确认页面存在）与**频控/连接数上限**。
- 直播姬互动玩法官方文档页。
- 用户使用协议中「第三方/自动化」条款原文。
- 直播间管理规范的可访问 URL。
- 「弹幕精灵」作为 B 站弹幕互动游戏产品的官方说明。
- open-live 账号风控/封禁的公开说明。
- 官方「弹幕游戏分区」的确切名称。
- 修勾夜店与官方技术文章的直接 URL。
- 个人开发者认证门槛（**⚠️ 仅第三方教程，官方原文未能核实**）。

---

## 2. 接入方案：B 站弹幕源 → chorus 帧同步管线

### 2.1 架构位置（以码为准）

chorus 已有的平台接入缝：**每个平台 = 一对 `IPlatformEventMapper` + 验签实现**。验签侧经 `IPlatformNamedEventVerifier` + `PlatformEventVerifierRouter` 按平台名分发（无匹配验证器则 fail-closed），经 `POST /ingress/{platform}` 进入 `PlatformIngressFace`（限流/体积上限/超时/验签/映射/去重 → 恰好一次 Tell），再进 `ISessionActorRouter` → `SessionActor`（tick 调度 → 快照）。证据：

- 接口：`src/Chorus.Adapters.Abstractions/`（`IPlatformEventMapper` / `IPlatformEventVerifier` / `IPlatformNamedEventVerifier` / `IAudienceFactionRule`）
- 验签分发：`src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs`（具名验证器 + AllowAll 兜底，默认不注册）
- 门面：`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs`（含 `ISessionActorRef.TellAsync` 三态）
- 路由：`src/Chorus.Server/Program.cs:22-23`（Monolith `InProcSessionActorRouter`）
- 现有平台：`DouyinPlatformEventMapper` + `DouyinPlatformEventVerifier`、`KuaishouPlatformEventMapper` + `KuaishouPlatformEventVerifier`（`Program.cs:51-68`）

**B 站适配器 = 同一缝上的第三个平台插件**：新增 `BilibiliPlatformEventMapper` + `BilibiliPlatformEventVerifier`，挂 `POST /ingress/bilibili`。**不需要改 tick 循环**——帧号是引擎 tick 层的概念（[帧同步方案](frame-sync.md) §4.1：帧号 ≡ tickId，`SessionActor.cs:391`），适配器只负责把 B 站事件归一化成 `AudienceEvent`，帧映射由现有管线自动完成。

```
B 站 open-live wss
  │  DANMU_MSG / SEND_GIFT / INTERACT_WORD_V2 / ...
  ▼
BilibiliOpenLiveClient（连接/鉴权/心跳/断线重连）   ← 新增（P1）
  ▼  归一化
BilibiliPlatformEventMapper → AudienceEvent           ← 新增（P1）
  ▼  POST /ingress/bilibili（验签 fail-closed）
PlatformIngressFace（限流/去重/Tell）                ← 已有
  ▼
SessionActor（tick 调度 → 快照 → 客户端）            ← 已有
```

### 2.2 连接与断线重连（P1）

- 凭据四件套走**配置**（`Ingress:Bilibili:AccessKeyId` / `AccessKeySecret` / `AppId` / `RoomOwnerAuthCode`），**不进仓**。
- `app_start` 取 `wss_link` + `auth_body` → 连 wss → 按 §1.2 签名头鉴权 → 心跳（项目 20s / WS 30s）。
- 断线重连：**指数退避**（如 1s→2s→4s→…→上限 30s），重连时**重取 `wss_link`**（`app_start` 可重入，旧 wss 可能失效）。
- 错误码 7003 等：记录 + 退避重试；连续失败 → 上报指标（`chorus_ingress_bilibili_*`）。
- **不硬编码绕过平台风控**：不模拟 web 登录、不伪造 device id、不并发多连接压测平台。

### 2.3 事件归一化（P1 弹幕，P2 礼物/进场）

| B 站事件 | 归一化 `AudienceEvent` 字段 | 期次 |
| --- | --- | --- |
| `DANMU_MSG` | `kind=comment`、`userId`、`text`、`platformEventId` | P1 |
| `SEND_GIFT` | `kind=gift`、`userId`、`giftId`、`count` | P2 |
| `SEND_GIFT_V2`（protobuf 灰度） | 同 `SEND_GIFT`，容忍新格式 | P2 |
| `INTERACT_WORD_V2` / `LIVE_ROOM_ENTER` | `kind=enter`、`userId`（注：当前 `AudienceEventKind` 仅 `Unknown/Comment/Like/Gift`，需先扩 `Enter` 成员——`src/Chorus.Protocol/Models/AudienceEvent.cs:3-9`） | P2 |
| `LIVE_START` / `LIVE_END` | 房间生命周期（开启/结束会话） | P2 |
| `SUPER_CHAT_MESSAGE` / `GUARD_BUY` / `USER_TOAST_MSG_V2` | 待定（金测驱动） | P2 |
| 未知事件类型 | **不崩**，丢弃 + 计数（平台协议仍在演进） | P1 |

- `userId` 取 B 站 mid（未登录 mid=0 → 匿名，不推断身份）。
- `platformEventId` 取 B 站消息 id → 复用现有 `EventDeduplicator`（`Program.cs:73-77`，TTL 去重）。

### 2.4 频控与合规

- **不刷屏**：适配器**只读**直播间，不向直播间写任何内容（输出侧无官方通道，见 §1.4）。
- **遵守平台规范**：事件协议仍在演进（`SEND_GIFT_V2` 灰度），未知事件类型容忍丢弃。
- **用户侧明示授权**：`room_owner_auth_code` 由**主播本人**授权获取，凭据走配置注入，**不进仓**、不进日志。
- **不硬编码绕过平台风控**：不模拟 web 登录、不伪造设备指纹、不并发压测。

### 2.5 分期与验收口径

| 期次 | 内容 | 验收口径 |
| --- | --- | --- |
| **P1** | open-live 接入 + 弹幕归一化 + 断线重连 + `/ingress/bilibili` | 金测：鉴权正确通过 / 篡改拒绝 / 重复 event id 去重 / 未知事件类型不崩；e2e：弹幕 → `AudienceEvent` → 帧 → 客户端快照 |
| **P2** | 礼物 / 进场 / 房间生命周期归一化（含 `SEND_GIFT_V2` 灰度） | 金测覆盖各事件类型映射；`SEND_GIFT_V2` 新格式容忍；e2e：礼物 → 帧 |
| **P3** | 反向写入 / 互动指令 | **需平台允许才做**——当前无官方通道，明确不做；平台开放后评估 |

---

## 3. 相关页面

- [帧同步方案](frame-sync.md) — 帧号语义 / 补帧 / 追帧（本适配器产出的 `AudienceEvent` 由该管线调度）
- [接口参考](../reference/api.md) — `/ingress/{platform}` 端点、错误码、curl 示例
- [协议参考](../reference/protocol.md) — WS 协议消息
- [版本策略](../reference/versioning.md) — 兼容性规则
- [适配器契约](../reference/adapter-contract.md) — 四抽象 + `/ingress/{platform}` 处理顺序与验收模板
