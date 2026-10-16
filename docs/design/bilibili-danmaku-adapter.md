---
title: Bilibili Danmaku Source Adapter (Research + Integration Plan)
---

<p align="center">
  English | <a href="bilibili-danmaku-adapter.zh.md">简体中文</a>
</p>

# Bilibili Danmaku Source Adapter (Research + Integration Plan)

This document answers two questions: **whether Bilibili supports danmaku interactive games** (research, with sources), and **how chorus integrates** (plan — documentation first, no code written in this round).

> Status: **plan documented first, no code written in this round**. Research conclusions are categorized as "supported / not supported / partially supported" with a source for each item; anything not found is marked "no public sources found" — nothing is fabricated. Protocol details include sources; nothing hardcodes workarounds for platform risk control, and sensitive credentials are never committed to the repo.

---

## 1. Research: Does Bilibili Support Danmaku Interactive Games

### 1.1 Conclusion: **Partially Supported**

| Side | Support | Notes |
| --- | --- | --- |
| **Input side** (danmaku/gift/enter → game state) | **Supported** | The official open-live open platform provides complete events (danmaku/gift/enter/live start/live end); it is currently the **only legally clean** channel |
| **Output side** (game screen replayed to the live room) | **Not supported (no official channel)** | The official platform does not provide an API for "pushing the game screen back to the live room's danmaku/comment section"; the community approach is an OBS browser source overlay (local screen capture, not a platform channel) |
| **Reverse interaction commands** (game actions → live room actions) | **Not supported** | The official platform has no such channel; any reverse operation may only be done after the platform explicitly allows it |

### 1.2 Input Side: Official open-live Open Platform

- **The platform exists and events are complete**: [open-live.bilibili.com](https://open-live.bilibili.com/) (official live open platform).
- **Integration flow** (implemented in blivedm `clients/open_live.py`, ✅ verified against the original text):
  1. Credential set of four: `access_key_id` / `access_key_secret` / `app_id` / `room_owner_auth_code`.
  2. `POST https://live-open.biliapi.com/v2/app_start` → returns `websocket_info.wss_link` + `auth_body`.
  3. HTTP header signature: `x-bili-accesskeyid` / `x-bili-content-md5` / `x-bili-signature-method: HMAC-SHA256` / `x-bili-signature-nonce` / `x-bili-signature-version: 1.0` / `x-bili-timestamp` → `Authorization`.
  4. Heartbeat: project heartbeat 20s / WS 30s; error code 7003.
- **Event types** (blivedm `blivedm/handlers.py`, ✅ verified against the original text): `DANMU_MSG`、`SEND_GIFT` (gray-releasing `SEND_GIFT_V2` protobuf since 2026-07)、`GUARD_BUY`、`USER_TOAST_MSG_V2`、`SUPER_CHAT_MESSAGE` / `SUPER_CHAT_MESSAGE_DELETE`、`INTERACT_WORD_V2`、`LIVE_ROOM_ENTER`、`LIVE_START`、`LIVE_END`、`LIVE_OPEN_PLATFORM_INTERACTION_END`。
- **The platform is still evolving**: the `SEND_GIFT_V2` gray release shows the official protocol will change; the adapter must tolerate unknown event types (no crash, may drop).

### 1.3 Input Side: Web Reverse-Engineering Channel (Legal Precedent Exists, Not the Primary Recommendation)

- **Protocol** (mirror of pskdje/bilibili-API-collect, ✅ verified against the original text, `docs/live/message_stream.md`): wss `/sub`, auth packet must be sent within 5 seconds or the connection is dropped, token via `getDanmuInfo` (Wbi signature), heartbeat 30s/60s drop, 16-byte packet header, op 2/3/5/7/8, non-logged-in mid=0 with masked username.
- **⚠️ Legal precedent**: bilibili-API-collect (20.2k★) **received a lawyer's letter from Bilibili on 2026-01-28, and archived/deleted the repo on 2026-01-30** (verifiable in the original repo's README). → the web reverse-engineering channel has a real precedent of legal action, so **chorus only primarily recommends open-live** and does not bundle a web reverse-engineering implementation.
- **Privacy**: non-logged-in users have mid=0 and masked usernames; the adapter does not infer user identity.

### 1.4 Output Side: No Official Channel

- The official platform **does not have** an API for "pushing the game screen back to the live room". The community approach (e.g., competitor open-block-war) is the **OBS browser source**: run a web page locally that renders the game screen, capture it with OBS and push the stream — this is **screen overlay**, not a platform channel, and the game itself is unaware of it.
- Conclusion: the output side is **not supported**; replay can only be done via OBS local overlay, which chorus does not bundle (it is user-side integration).

### 1.5 Competitor Approaches (Public Sources)

| Project | Approach | Notes |
| --- | --- | --- |
| [KeJunMao/open-block-war](https://github.com/KeJunMao/open-block-war) | React + Phaser "Dan Dan Le", Bilibili official API + unofficial ws, OBS browser source replay | No longer maintained |
| [MrXiaoM/bilibili-open-live](https://github.com/MrXiaoM/bilibili-open-live) | Minecraft plugin, based on open-live | Closest to chorus architecture (open-live integration + game mapping) |
| [seriserendipia/gojyuon_danmaku_game](https://github.com/seriserendipia/gojyuon_danmaku_game) | Depends on blivedm | — |
| [mcube-12139/danmaku-game](https://github.com/mcube-12139/danmaku-game) | Danmaku game | — |
| [PaticoBar/WhacAMole_Live](https://github.com/PaticoBar/WhacAMole_Live) | Live whac-a-mole | — |
| [zerocraft/bilirs](https://github.com/zerocraft/bilirs) | Rust implementation | — |
| [fython/bili-open-live-go](https://github.com/fython/bili-open-live-go) | Go implementation | — |
| [tangclQaQ/BOpenliveSDK-UE4](https://github.com/tangclQaQ/BOpenliveSDK-UE4) | UE4 SDK | — |
| [CXHGT/BilibiliSdkDemo](https://github.com/CXHGT/BilibiliSdkDemo) | Android demo | — |

**Common ground**: all use open-live or web reverse-engineering to get danmaku, and **none** push the game screen back to the live room (except OBS). None has published an architecture article on "frame sync + danmaku interaction".

### 1.6 No Public Sources Found (Honest Marking)

- The original official clause text "allowing/prohibiting third-party danmaku interactive games".
- The official open-live documentation's **body text** (page rendered by JS; only the page's existence is confirmed) and **rate limits/connection limits**.
- The official documentation page for live-assistant interactive gameplay.
- The original "third-party/automation" clause in the user terms of service.
- An accessible URL for the live room management rules.
- Official documentation for "Danmaku Elf" as a Bilibili danmaku interactive game product.
- Public documentation of open-live account risk control/bans.
- The exact name of the official "danmaku game category".
- A direct URL for the Xiu Gou Nightclub official technical article.
- Individual developer certification threshold (**⚠️ only third-party tutorials; the official original text could not be verified**).

---

## 2. Integration Plan: Bilibili Danmaku Source → chorus Frame Sync Pipeline

### 2.1 Architecture Position (Code Is Authoritative)

chorus already has the platform integration seam: **each platform = a pair of `IPlatformEventMapper` + a verification implementation**. The verification side is dispatched by platform name via `IPlatformNamedEventVerifier` + `PlatformEventVerifierRouter` (fail-closed when no matching verifier), enters `PlatformIngressFace` via `POST /ingress/{platform}` (rate limit/size cap/timeout/verification/mapping/deduplication → exactly-once Tell), then goes to `ISessionActorRouter` → `SessionActor` (tick scheduling → snapshot). Evidence:

- Interfaces: `src/Chorus.Adapters.Abstractions/`（`IPlatformEventMapper` / `IPlatformEventVerifier` / `IPlatformNamedEventVerifier` / `IAudienceFactionRule`）
- Verification dispatch: `src/Chorus.Adapters.Platforms/PlatformEventVerifierRouter.cs` (named verifiers + AllowAll fallback, not registered by default)
- Face: `src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs` (includes the three states of `ISessionActorRef.TellAsync`)
- Routing: `src/Chorus.Server/Program.cs:22-23` (Monolith `InProcSessionActorRouter`)
- Existing platforms: `DouyinPlatformEventMapper` + `DouyinPlatformEventVerifier`、`KuaishouPlatformEventMapper` + `KuaishouPlatformEventVerifier`（`Program.cs:51-68`）

**The Bilibili adapter = the third platform plugin on the same seam**: add `BilibiliPlatformEventMapper` + `BilibiliPlatformEventVerifier`, attach to `POST /ingress/bilibili`. **No changes to the tick loop are needed** — the frame number is a concept of the engine tick layer ([Frame Sync Plan](frame-sync.md) §4.1: frame number ≡ tickId, `SessionActor.cs:391`); the adapter is only responsible for normalizing Bilibili events into `AudienceEvent`, and frame mapping is done automatically by the existing pipeline.

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

### 2.2 Connection and Reconnect (P1)

- The credential set of four goes through **config** (`Ingress:Bilibili:AccessKeyId` / `AccessKeySecret` / `AppId` / `RoomOwnerAuthCode`), **never committed to the repo**.
- `app_start` fetches `wss_link` + `auth_body` → connect wss → authenticate with the §1.2 signature headers → heartbeat (project 20s / WS 30s).
- Reconnect: **exponential backoff** (e.g. 1s→2s→4s→…→cap 30s); on reconnect, **re-fetch `wss_link`** (`app_start` is re-entrant; the old wss may be invalid).
- Error codes like 7003: log + backoff retry; consecutive failures → report metrics (`chorus_ingress_bilibili_*`).
- **No hardcoding to bypass platform risk control**: no simulating web login, no forging device id, no concurrent multi-connection load testing against the platform.

### 2.3 Event Normalization (P1 Danmaku, P2 Gift/Enter)

| Bilibili Event | Normalized `AudienceEvent` Fields | Phase |
| --- | --- | --- |
| `DANMU_MSG` | `kind=comment`、`userId`、`text`、`platformEventId` | P1 |
| `SEND_GIFT` | `kind=gift`、`userId`、`giftId`、`count` | P2 |
| `SEND_GIFT_V2` (protobuf gray release) | Same as `SEND_GIFT`, tolerant of the new format | P2 |
| `INTERACT_WORD_V2` / `LIVE_ROOM_ENTER` | `kind=enter`、`userId`(note: `AudienceEventKind` currently has only `Unknown/Comment/Like/Gift`; the `Enter` member must be added first —`src/Chorus.Protocol/Models/AudienceEvent.cs:3-9`） | P2 |
| `LIVE_START` / `LIVE_END` | room lifecycle (session start/end) | P2 |
| `SUPER_CHAT_MESSAGE` / `GUARD_BUY` / `USER_TOAST_MSG_V2` | TBD (golden-test driven) | P2 |
| Unknown event types | **no crash**, dropped + counted (the platform protocol is still evolving) | P1 |

- `userId` uses the Bilibili mid (non-logged-in mid=0 → anonymous, no identity inference).
- `platformEventId` uses the Bilibili message id → reuse the existing `EventDeduplicator` (`Program.cs:73-77`, TTL deduplication).

### 2.4 Frequency Control and Compliance

- **No spamming**: the adapter is **read-only** on the live room and writes nothing to it (the output side has no official channel, see §1.4).
- **Comply with platform rules**: the event protocol is still evolving (`SEND_GIFT_V2` gray release); unknown event types are tolerated and dropped.
- **Explicit user-side authorization**: `room_owner_auth_code` is authorized and obtained by the **streamer personally**; credentials are injected via config and are **never committed to the repo** and never logged.
- **No hardcoding to bypass platform risk control**: no simulating web login, no forging device fingerprints, no concurrent load testing.

### 2.5 Phasing and Acceptance Criteria

| Phase | Content | Acceptance Criteria |
| --- | --- | --- |
| **P1** | open-live integration + danmaku normalization + reconnect + `/ingress/bilibili` | **Completed (2026-10-14)**: golden tests (authentication passes / tampering rejected / duplicate `msg_id` deduplication / unknown events don't crash, 7 cases in `BilibiliIngressEndpointTests`) + e2e (danmaku → `AudienceEvent` → frame → client snapshot, `BilibiliEndToEndTests`); implementation see [Platform Reference](../reference/platform-bilibili.md); signature/frame protocol verified against the blivedm original text (six headers joined with `\n` HMAC, 16-byte large packet header op 5/7/8, ver 2 deflate recursive sub-packaging) |
| **P2** | gift / entry / room-lifecycle normalization (including the `SEND_GIFT_V2` gray release) | **Completed (2026-10-14)**: `AudienceEventKind` extended with `Enter`/`RoomLifecycle` (code values appended without changing existing numbering); golden tests cover `SEND_GIFT` (gift_id/gift_num/price), `SEND_GIFT_V2` gray-release tolerance (missing fields neither dropped nor crashed), `INTERACT_WORD_V2`/`LIVE_ROOM_ENTER`→enter, `LIVE_START`/`LIVE_END`→roomLifecycle, P2 TBD items like SUPER_CHAT remain Unknown; e2e gift → frame (`totals.gifts`). SUPER_CHAT/GUARD_BUY/USER_TOAST_MSG_V2 normalization is still TBD (golden-test driven; P2 TBD items postponed) |
| **P3** | reverse write / interaction commands | **Only with the platform's permission** — there is currently no official channel, explicitly not doing it; evaluate once the platform opens up. |

---

## 3. Related Pages

- [Frame Sync Plan](frame-sync.md) — frame number semantics / frame backfill / frame catch-up (the `AudienceEvent` produced by this adapter is scheduled by that pipeline)
- [API Reference](../reference/api.md) — `/ingress/{platform}` endpoints, error codes, curl examples
- [Protocol Reference](../reference/protocol.md) — WS protocol messages
- [Versioning Policy](../reference/versioning.md) — compatibility rules
- [Adapter Contract](../reference/adapter-contract.md) — the four abstractions + `/ingress/{platform}` processing order and acceptance template
