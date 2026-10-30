---
title: B 站弹幕源适配器（调研 + 接入方案）
---


# B 站弹幕源适配器（调研 + 接入方案）

本文回答两个问题：**B 站是否支持弹幕互动游戏**（调研，带来源），以及 **chorus 如何接入**（方案——文档先行，本轮未写代码）。

> 状态：**方案先行成文，本轮未写代码**。调研结论按"支持 / 不支持 / 部分支持"分类并逐条给来源；没查到的一律标"未找到公开来源"——不编造。协议细节附来源；没有为绕开平台风控硬编码任何东西；敏感凭据永不进仓。

---

## 1. 调研：B 站是否支持弹幕互动游戏

### 1.1 结论：**部分支持**

| 侧 | 支持度 | 说明 |
| --- | --- | --- |
| **输入侧**（弹幕/礼物/进场事件进游戏事件流） | **支持** | 官方直播开放平台提供弹幕/礼物/进场/开播/下播事件；各游戏模块自行决定哪些种类影响状态，且这是目前**唯一法律干净**的通道 |
| **输出侧**（游戏画面回投直播间） | **不支持（无官方通道）** | 官方平台不提供"把游戏画面推回直播间弹幕/评论区"的 API；社区做法是 OBS 浏览器源叠加（本地截屏，不是平台通道） |
| **反向互动指令**（游戏动作 → 直播间动作） | **不支持** | 官方平台无此通道；任何反向操作只能在平台明确允许之后做 |

### 1.2 输入侧：官方 open-live 开放平台

- **平台存在且事件齐全**：[open-live.bilibili.com](https://open-live.bilibili.com/)（官方直播开放平台）。
- **接入流程**（blivedm `clients/open_live.py` 实现，✅ 对照原文核实）：
  1. 四件套凭证：`access_key_id` / `access_key_secret` / `app_id` / `room_owner_auth_code`。
  2. `POST https://live-open.biliapi.com/v2/app_start` → 返回 `websocket_info.wss_link` + `auth_body`。
  3. HTTP 头签名：`x-bili-accesskeyid` / `x-bili-content-md5` / `x-bili-signature-method: HMAC-SHA256` / `x-bili-signature-nonce` / `x-bili-signature-version: 1.0` / `x-bili-timestamp` → `Authorization`。
  4. 心跳：项目心跳 20s / WS 30s；错误码 7003。
- **事件类型**（blivedm `blivedm/handlers.py`，✅ 对照原文核实）：`DANMU_MSG`、`SEND_GIFT`（2026-07 起灰度 `SEND_GIFT_V2` protobuf）、`GUARD_BUY`、`USER_TOAST_MSG_V2`、`SUPER_CHAT_MESSAGE` / `SUPER_CHAT_MESSAGE_DELETE`、`INTERACT_WORD_V2`、`LIVE_ROOM_ENTER`、`LIVE_START`、`LIVE_END`、`LIVE_OPEN_PLATFORM_INTERACTION_END`。
- **平台仍在演进**：`SEND_GIFT_V2` 灰度说明官方协议会变；适配器必须容忍未知事件类型（不崩溃、可丢弃）。

### 1.3 输入侧：Web 逆向通道（有法律先例，不作主推荐）

- **协议**（pskdje/bilibili-API-collect 镜像，✅ 对照原文核实，`docs/live/message_stream.md`）：wss `/sub`，5 秒内必须发鉴权包否则掉线，token 经 `getDanmuInfo`（Wbi 签名），心跳 30s/60s 掉线，16 字节包头，op 2/3/5/7/8，未登录 mid=0 且用户名打码。
- **⚠️ 法律先例**：bilibili-API-collect（20.2k★）**2026-01-28 收到 B 站律师函，2026-01-30 归档/删库**（原仓库 README 可查）。→ Web 逆向通道有真实的法律追责先例，因此 **chorus 只主推 open-live**，不捆绑 Web 逆向实现。
- **隐私**：未登录用户 mid=0、用户名打码；适配器不推断用户身份。

### 1.4 输出侧：无官方通道

- 官方平台**没有**"把游戏画面推回直播间"的 API。社区做法（如竞品 open-block-war）是 **OBS 浏览器源**：本地跑一个渲染游戏画面的网页，OBS 采集推流——这是**画面叠加**，不是平台通道，游戏本身无感知。
- 结论：输出侧**不支持**；回投只能走 OBS 本地叠加，chorus 不捆绑（属用户侧接入）。

### 1.5 竞品做法（公开来源）

| 项目 | 做法 | 备注 |
| --- | --- | --- |
| [KeJunMao/open-block-war](https://github.com/KeJunMao/open-block-war) | React + Phaser"点点乐"，B 站官方 API + 非官方 ws，OBS 浏览器源回投 | 已停止维护 |
| [MrXiaoM/bilibili-open-live](https://github.com/MrXiaoM/bilibili-open-live) | Minecraft 插件，基于 open-live | 与 chorus 架构最接近（open-live 接入 + 游戏映射） |
| [seriserendipia/gojyuon_danmaku_game](https://github.com/seriserendipia/gojyuon_danmaku_game) | 依赖 blivedm | — |
| [mcube-12139/danmaku-game](https://github.com/mcube-12139/danmaku-game) | 弹幕游戏 | — |
| [PaticoBar/WhacAMole_Live](https://github.com/PaticoBar/WhacAMole_Live) | 直播打地鼠 | — |
| [zerocraft/bilirs](https://github.com/zerocraft/bilirs) | Rust 实现 | — |
| [fython/bili-open-live-go](https://github.com/fython/bili-open-live-go) | Go 实现 | — |
| [tangclQaQ/BOpenliveSDK-UE4](https://github.com/tangclQaQ/BOpenliveSDK-UE4) | UE4 SDK | — |
| [CXHGT/BilibiliSdkDemo](https://github.com/CXHGT/BilibiliSdkDemo) | Android demo | — |

**共同点**：都用 open-live 或 Web 逆向拿弹幕，且**没有一个**把游戏画面推回直播间（OBS 除外）。没有公开过"帧同步 + 弹幕互动"的架构文章。

### 1.6 未找到公开来源（诚实标注）

- "允许/禁止第三方弹幕互动游戏"的官方条款原文。
- 官方 open-live 文档的**正文**（页面 JS 渲染；只确认页面存在）与**频控/连接数限制**。
- 直播伴侣互动玩法的官方文档页。
- 用户服务条款中"第三方/自动化"条款原文。
- 直播房间管理规范的可达 URL。
- "弹幕精灵"作为 B 站弹幕互动游戏产品的官方文档。
- open-live 账号风控/封禁的公开文档。
- 官方"弹幕游戏品类"的确切名称。
- 羞狗夜总会官方技术文章的直接 URL。
- 个人开发者认证门槛（**⚠️ 只有第三方教程；官方原文无法核实**）。

---

## 2. 接入方案：B 站弹幕源 → chorus 帧同步管线

### 2.1 架构位置（代码为准）

chorus 已有平台接入缝：**每个平台 = 一对 `IPlatformEventMapper` + 一个验签实现**。验签侧由 `IPlatformNamedEventVerifier` + `PlatformEventVerifierRouter` 按平台名分派（无匹配验证器即 fail-closed），经 `POST /ingress/{platform}` 进入 `PlatformIngressFace`（限流/大小/超时/验签/映射/去重 → 恰一次 Tell），再走 `ISessionActorRouter` → `SessionActor`（tick 调度 → 快照）。证据：

- 接口：`src/Chorus.Adapters.Abstractions/`（`IPlatformEventMapper` / `IPlatformEventVerifier` / `IPlatformNamedEventVerifier` / `IAudienceFactionRule`）
- 验签分派：`src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs`（具名验证器 + AllowAll fallback，默认不注册）
- 面：`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs`（含 `ISessionActorRef.TellAsync` 三态）
- 路由：`src/Chorus.Server/Program.cs:22-23`（单机 `InProcSessionActorRouter`）
- 既有平台：`DouyinPlatformEventMapper` + `DouyinPlatformEventVerifier`、`KuaishouPlatformEventMapper` + `KuaishouPlatformEventVerifier`（`Program.cs:51-68`）

**B 站适配器 = 同一缝上的第三个平台插件**：新增 `BilibiliPlatformEventMapper` + `BilibiliPlatformEventVerifier`，挂到 `POST /ingress/bilibili`。**不需要动 tick 循环**——帧号是引擎 tick 层的概念（[帧同步方案](frame-sync.md) §4.1：帧号 ≡ tickId，`SessionActor.cs:391`）；适配器只负责把 B 站事件归一化为 `AudienceEvent`，帧映射由既有管线自动完成。

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

### 2.2 连接与重连（P1）

- 四件套凭证走**配置**（`Ingress:Bilibili:AccessKeyId` / `AccessKeySecret` / `AppId` / `RoomOwnerAuthCode`），**永不进仓**。
- `app_start` 取 `wss_link` + `auth_body` → 连 wss → 用 §1.2 签名头鉴权 → 心跳（项目 20s / WS 30s）。
- 重连：**指数退避**（如 1s→2s→4s→…→上限 30s）；重连时**重取 `wss_link`**（`app_start` 可重入；旧 wss 可能已失效）。
- 7003 等错误码：记日志 + 退避重试；连续失败 → 上报指标（`chorus_ingress_bilibili_*`）。
- **不硬编码绕过平台风控**：不模拟网页登录、不伪造设备 id、不对平台做并发多连接压测。

### 2.3 事件归一化（P1 弹幕，P2 礼物/进场）

| B 站事件 | 归一化 `AudienceEvent` 字段 | 阶段 |
| --- | --- | --- |
| `DANMU_MSG` | `kind=comment`、`userId`、`text`、`platformEventId` | P1 |
| `SEND_GIFT` | `kind=gift`、`userId`、`giftId`、`count` | P2 |
| `SEND_GIFT_V2`（protobuf 灰度） | 同 `SEND_GIFT`，容忍新格式 | P2 |
| `INTERACT_WORD_V2` / `LIVE_ROOM_ENTER` | `kind=enter`、`userId` | P2 |
| `LIVE_START` / `LIVE_END` | 信息性 `RoomLifecycle` 观众事件；不自动迁移 ControlHost 房间状态 | P2 |
| `SUPER_CHAT_MESSAGE` / `GUARD_BUY` / `USER_TOAST_MSG_V2` | 待定（金测驱动） | P2 |
| 未知事件类型 | **不崩溃**，丢弃 + 计数（平台协议仍在演进） | P1 |

- `userId` 用 B 站 mid（未登录 mid=0 → anonymous，不推断身份）。
- `platformEventId` 用 B 站消息 id → 复用既有 `EventDeduplicator`（`Program.cs:73-77`，TTL 去重）。

### 2.4 频控与合规

- **不刷量**：适配器对直播间**只读**、不写入任何东西（输出侧无官方通道，见 §1.4）。
- **遵守平台规则**：事件协议仍在演进（`SEND_GIFT_V2` 灰度）；未知事件类型容忍并丢弃。
- **用户侧显式授权**：`room_owner_auth_code` 由**主播个人**授权取得；凭据经配置注入，**永不进仓**、永不进日志。
- **不硬编码绕过平台风控**：不模拟网页登录、不伪造设备指纹、不做并发压测。

### 2.5 分期与验收标准

| 阶段 | 内容 | 验收标准 |
| --- | --- | --- |
| **P1** | open-live 接入 + 弹幕归一化 + 重连 + `/ingress/bilibili` | **已完成（2026-10-14）**：金测（鉴权通过 / 篡改拒绝 / 重复 `msg_id` 去重 / 未知事件不崩溃，`BilibiliIngressEndpointTests` 7 例）+ e2e（弹幕 → `AudienceEvent` → 帧 → 客户端快照，`BilibiliEndToEndTests`）；实现见[平台参考](../reference/platform-bilibili.md)；签名/帧协议对照 blivedm 原文核实（六个头以 `\n` 拼接 HMAC、16 字节大端包头 op 5/7/8、ver 2 deflate 递归子包） |
| **P2** | 礼物 / 进场 / 房间生命周期归一化（含 `SEND_GIFT_V2` 灰度） | **已完成（2026-10-14）**：`AudienceEventKind` 扩展 `Enter`/`RoomLifecycle`（编码值追加、不改既有编号）；金测覆盖 `SEND_GIFT`（gift_id/gift_num/price）、`SEND_GIFT_V2` 灰度容忍（缺字段不丢也不崩）、`INTERACT_WORD_V2`/`LIVE_ROOM_ENTER`→enter、`LIVE_START`/`LIVE_END`→roomLifecycle、SUPER_CHAT 等 P2 待定项保持 Unknown；e2e 礼物 → 帧（`totals.gifts`）。SUPER_CHAT/GUARD_BUY/USER_TOAST_MSG_V2 归一化仍待定（金测驱动；P2 待定项顺延） |
| **P3** | 反向写入 / 互动指令 | **仅当平台允许**——当前无官方通道，显式不做；平台开放后再评估。 |

---

## 3. 相关页面

- [帧同步方案](frame-sync.md) —— 帧号语义 / 帧回填 / 帧追赶（本适配器产出的 `AudienceEvent` 由该管线调度）
- [API 参考](../reference/api.md) —— `/ingress/{platform}` 端点、错误码、curl 示例
- [协议参考](../reference/protocol.md) —— WS 协议消息
- [版本策略](../reference/versioning.md) —— 兼容性规则
- [适配器契约](../reference/adapter-contract.md) —— 四个抽象 + `/ingress/{platform}` 处理顺序与验收模板
