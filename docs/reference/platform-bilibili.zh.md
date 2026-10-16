---
title: 平台对接参考：B 站
---


<p align="center">
  <a href="./platform-bilibili.md">English</a> | 简体中文
</p>

# 平台对接参考：B 站（bilibili）

> 路由：`POST /ingress/{platform}`（`platform=bilibili`，单体与 Gateway 同构）。
> 验签 fail-closed；去重走 `EventDeduplicator`（`msg_id` + TTL）。
> 与抖音/快手不同，**B 站 open-live 的协议细节拿到了核实源**（blivedm 开源实现，逐字段 ✅ 原文核实），
> 本文记录已核实事实与当前实现契约，真实联调等主播授权凭据。

## 1. 官方口径（open-live 开放平台，✅ 原文核实）

核实源：[blivedm](https://github.com/xfgryujk/blivedm)（同时对接 web 逆向与官方 open-live 的开源实现，
设计文档调研期逐字段核对 `clients/open_live.py` / `clients/ws_base.py` / `models/open_live.py`）。

### 1.1 接入与鉴权

- 凭据四件套：`access_key_id` / `access_key_secret` / `app_id` / `room_owner_auth_code`
  （主播本人授权，走配置注入，不进仓不进日志）。
- `POST https://live-open.biliapi.com/v2/app/start`，体 `{"code":<auth_code>,"app_id":<app_id>}`
  → `data.websocket_info.wss_link`（数组，取第一个）+ `data.websocket_info.auth_body`（字符串）
  + `data.game_info.game_id`。
- HTTP 签名（`app/start` / `app/heartbeat` / `app/end` 通用）：
  六个头 `x-bili-accesskeyid` / `x-bili-content-md5`（体 MD5 小写 hex）/
  `x-bili-signature-method: HMAC-SHA256` / `x-bili-signature-nonce`（uuid hex）/
  `x-bili-signature-version: 1.0` / `x-bili-timestamp`（epoch 秒字符串）；
  签名串 = 六头按字母序拼 `key:value` 行、`\n` 连接；
  `Authorization` = HMAC-SHA256(access_key_secret, 签名串) 小写 hex（Authorization 自身不参与签名串）。
- 心跳：项目心跳 `POST /v2/app/heartbeat` 体 `{"game_id":...}` 每 20s；WS 心跳每 30s。
- 结束：`POST /v2/app/end` 体 `{"game_id":...}`。

### 1.2 WS 帧协议（`wss_link`）

- **二进制帧**，16 字节大包头 `>I2H2I`：pack_len / raw_header_size=16 / ver / operation / seq_id=1。
- operation：`2`=心跳（体 `{}`）、`3`=心跳回复、`5`=业务消息、`7`=AUTH（体 = auth_body 原文）、
  `8`=AUTH_REPLY（体 JSON `{"code":0}` 通过，非 0 拒绝）。
- 业务帧一帧可含多包；包体 ver：`0`=明文 JSON、`2`=zlib deflate（开放平台在用）、`3`=brotli；
  压缩包体解压后**递归再分包**。
- 事件信封 `{"cmd":...,"data":{...},"msg_id":...}`；归一化（P1+P2）：`DANMU_MSG`→comment、
  `SEND_GIFT`/`SEND_GIFT_V2`（灰度容忍）→gift、`INTERACT_WORD_V2`/`LIVE_ROOM_ENTER`→enter、
  `LIVE_START`/`LIVE_END`→roomLifecycle（均 `data.open_id`/`msg_id` 扁平键）。
- 协议仍在演进：未知 cmd 不崩，按 Unknown 丢弃（SUPER_CHAT_MESSAGE / GUARD_BUY / USER_TOAST_MSG_V2
  归一化待定，金测驱动）。

## 2. chorus 实现（todo §12 P1，已落地）

```
B 站 open-live wss ──▶ BilibiliOpenLiveClient（app_start/认证/心跳/断线重连）
                          │  op-5 业务消息原样 + x-bili-* 签名
                          ▼
                   POST /ingress/bilibili（BilibiliPlatformEventVerifier 验签 fail-closed）
                          │  BilibiliPlatformEventMapper：弹幕/礼物/进场/生命周期 → AudienceEvent
                          ▼
                   PlatformIngressFace（限流/去重/Tell）→ SessionActor → 快照
```

- `src/Chorus.Adapters.Platforms/Bilibili/BilibiliSigner.cs`：签名/验签共用同一套规范化。
- `BilibiliOpenLiveProtocol.cs`：帧编解码（包头/分包/deflate/brotli/递归）。
- `BilibiliOpenLiveClient.cs`：连接状态机。断线重连指数退避（默认 1s→2s→…→30s 封顶，
  认证通过过的会话断开从最小退避起重连），**重连时重取 wss_link**（app_start 可重入）；
  传输缝（`HttpMessageInvoker` / ws connect）可注入，仓内离线金测覆盖。
- `BilibiliPlatformEventMapper.cs`：事件归一化（弹幕/礼物/进场/生命周期，P1+P2）；会话绑定走配置（单绑定，多房间属控制面 todo §14）。
- `BilibiliIngressBridgeHostedService.cs`：凭据齐全时随主机启动桥（Server/Gateway 均注册）。
- 不硬编码绕过平台风控：不模拟 web 登录、不伪造设备指纹、不并发多连接压测。

## 3. 配置

见 [配置参考](../reference/config.zh.md) 的 `Ingress:Bilibili:*`。凭据缺失时 `/ingress/bilibili`
全部拒绝（fail-closed），桥不启动；`SessionId` 缺失时事件映射失败（400 unmapped）。

## 4. 联调清单（等凭据）

1. `app_start` 的 code/app_id 响应码形状（`code:0` 顶层确认）。
2. AUTH_REPLY 错误码全集（已知 `-101` token 错、7003 项目心跳超时）。
3. `game_id` 类型（当前按字符串容忍数字）。
4. 未知 cmd 实测流量占比（决定 P2 归一化优先级）。

## 5. 相关页面

- [适配器契约](../reference/adapter-contract.zh.md)
- [B 站弹幕源适配器设计](../design/bilibili-danmaku-adapter.zh.md)
- [部署与安全](../reference/deployment.zh.md)
