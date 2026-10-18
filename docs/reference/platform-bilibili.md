---
title: "Platform Integration Reference: Bilibili"
---


# Platform Integration Reference: Bilibili

> Routing: `POST /ingress/{platform}` (`platform=bilibili`, monolith and Gateway are isomorphic).
> Signature verification is fail-closed; deduplication goes through `EventDeduplicator` (`msg_id` + TTL).
> Unlike Douyin/Kuaishou, **the Bilibili open-live protocol details have a verified source** (the blivedm open-source implementation, field-by-field ✅ verified against the original),
> this document records verified facts and the current implementation contract; real joint debugging awaits the streamer's authorized credentials.

## 1. Official Facts (open-live open platform, ✅ verified against the original)

Verification source: [blivedm](https://github.com/xfgryujk/blivedm) (an open-source implementation integrating both the web reverse-engineering and the official open-live;
during the design document research period, field-by-field verification was done against `clients/open_live.py` / `clients/ws_base.py` / `models/open_live.py`).

### 1.1 Integration and Authentication

- The four-piece credential set: `access_key_id` / `access_key_secret` / `app_id` / `room_owner_auth_code`
  (authorized by the streamer personally, injected via configuration, never in the repo or logs).
- `POST https://live-open.biliapi.com/v2/app/start`, body `{"code":<auth_code>,"app_id":<app_id>}`
  → `data.websocket_info.wss_link` (array, take the first) + `data.websocket_info.auth_body` (string)
  + `data.game_info.game_id`.
- HTTP signature (common to `app/start` / `app/heartbeat` / `app/end`):
  six headers `x-bili-accesskeyid` / `x-bili-content-md5` (body MD5 lowercase hex) /
  `x-bili-signature-method: HMAC-SHA256` / `x-bili-signature-nonce` (uuid hex) /
  `x-bili-signature-version: 1.0` / `x-bili-timestamp` (epoch seconds string);
  the signature string = the six headers sorted alphabetically joined into `key:value` lines, connected with `\n`;
  `Authorization` = HMAC-SHA256(access_key_secret, signature string) lowercase hex (Authorization itself does not participate in the signature string).
- Heartbeat: project heartbeat `POST /v2/app/heartbeat` with body `{"game_id":...}` every 20s; WS heartbeat every 30s.
- Ending: `POST /v2/app/end` with body `{"game_id":...}`.

### 1.2 WS Frame Protocol (`wss_link`)

- **Binary frames**, 16-byte big-endian header `>I2H2I`: pack_len / raw_header_size=16 / ver / operation / seq_id=1.
- operation: `2`=heartbeat (body `{}`), `3`=heartbeat reply, `5`=business message, `7`=AUTH (body = auth_body verbatim),
  `8`=AUTH_REPLY (body JSON `{"code":0}` passes; non-0 rejected).
- A business frame may contain multiple packets; packet body ver: `0`=plaintext JSON, `2`=zlib deflate (used by the open platform), `3`=brotli;
  after decompressing a compressed packet body, **recursively re-split the packets**.
- Event envelope `{"cmd":...,"data":{...},"msg_id":...}`; normalization (P1+P2): `DANMU_MSG`→comment,
  `SEND_GIFT`/`SEND_GIFT_V2` (gray-release tolerance)→gift, `INTERACT_WORD_V2`/`LIVE_ROOM_ENTER`→enter,
  `LIVE_START`/`LIVE_END`→roomLifecycle (all with flattened `data.open_id`/`msg_id` keys).
- The protocol is still evolving: unknown cmds do not crash and are dropped as Unknown (SUPER_CHAT_MESSAGE / GUARD_BUY / USER_TOAST_MSG_V2
  normalization is pending, driven by golden tests).

## 2. chorus Implementation (todo §12 P1, landed)

```
B 站 open-live wss ──▶ BilibiliOpenLiveClient（app_start/认证/心跳/断线重连）
                          │  op-5 业务消息原样 + x-bili-* 签名
                          ▼
                   POST /ingress/bilibili（BilibiliPlatformEventVerifier 验签 fail-closed）
                          │  BilibiliPlatformEventMapper：弹幕/礼物/进场/生命周期 → AudienceEvent
                          ▼
                   PlatformIngressFace（限流/去重/Tell）→ SessionActor → 快照
```

- `src/Chorus.Adapters.Platforms/Bilibili/BilibiliSigner.cs`: signing and signature verification share the same normalization.
- `BilibiliOpenLiveProtocol.cs`: frame encode/decode (header / packet splitting / deflate / brotli / recursion).
- `BilibiliOpenLiveClient.cs`: connection state machine. Reconnect with exponential backoff on disconnect (default 1s→2s→…→30s cap;
  a session that has passed authentication reconnects from the minimum backoff after disconnecting), **re-fetch the wss_link on reconnect** (app_start is re-entrant);
  the transport seams (`HttpMessageInvoker` / ws connect) are injectable, covered by offline golden tests in the repo.
- `BilibiliPlatformEventMapper.cs`: event normalization (danmaku/gift/enter/lifecycle, P1+P2); session binding goes through configuration (single binding; multiple rooms belong to control-plane todo §14).
- `BilibiliIngressBridgeHostedService.cs`: starts the bridge with the host when the credentials are complete (registered in both Server/Gateway).
- No hard-coded bypassing of platform risk control: no web login simulation, no device fingerprint forgery, no concurrent multi-connection load testing.

## 3. Configuration

See the `Ingress:Bilibili:*` entries in [Configuration Reference](../reference/config.md). When the credentials are missing, `/ingress/bilibili`
rejects everything (fail-closed) and the bridge does not start; when `SessionId` is missing, event mapping fails (400 unmapped).

## 4. Joint Debugging Checklist (awaiting credentials)

1. The shape of the `app_start` code/app_id response codes (`code:0` confirmed at the top level).
2. The complete set of AUTH_REPLY error codes (known: `-101` token wrong, 7003 project heartbeat timeout).
3. The type of `game_id` (currently tolerated as a string for numbers).
4. The measured traffic share of unknown cmds (decides P2 normalization priority).

## 5. Related Pages

- [Adapter Contract](../reference/adapter-contract.md)
- [Bilibili Danmaku Source Adapter Design](../design/bilibili-danmaku-adapter.md)
- [Deployment and Security](../reference/deployment.md)
