---
title: "Platform Integration Reference: Douyin Mini-Game"
---


# Platform Integration Reference: Douyin Mini-Game (douyin mini-game)

> Scope: the client running inside the Douyin mini-game container (`tt.*` JS API) and the server-side integration half.
> This is a **separate link** from live callback integration ([platform-douyin.md](platform-douyin.md)):
> the live link is "the platform pushes events to this service"; the mini-game link is "client login + server-side identity exchange +
> leaderboard projection". This document records the official public documentation research findings of 2026-10-14 and the current implementation contract.

## 1. Official Findings (2026-10-14)

- Official documentation site: `developer.open-douyin.com/docs/resource/zh-CN/mini-game`,
  with three sections — **Quick Start / Development / Operations**; navigation and index accessible anonymously.
- The official **JavaScript API index page** (statically crawlable) has confirmed that the following API **names exist**:
  - Login: `tt.login` (`/develop/api/open-capacity/log-in/tt-login`), `tt.checkSession`
  - Open data domain: `tt.getOpenDataContext`, `tt.onMessage`
  - Open data / cloud storage: `tt.setUserCloudStorage`, `tt.getUserCloudStorage`,
    `tt.getCloudStorageByRelation`, `tt.removeUserCloudStorage`, `tt.getSharedCanvas`
  - Game leaderboards: `tt.setImRankData`, `tt.getImRankList`, `tt.getImRankData`, `tt.setImRankDataInOpenContext`
  - Network: `tt.getNetworkType`, `tt.onNetworkStatusChange`, `tt.offNetworkStatusChange`
- **The individual API detail pages under the index are JS-rendered**; public crawling only retrieves the
  navigation shell and cannot retrieve the signature / parameter / return structure / error code content — so
  only "API existence" is confirmed; fields and semantics are all **not found in public documentation (pending verification)**.
- **Server-side OpenAPI details obtained on re-verification 2026-10-14** (first round blocked by JS rendering):
  getAccessToken / code2Session / player rank upload — the three endpoints, parameters, responses, and error
  codes are all confirmed, and have landed in
  `src/Chorus.Adapters.Platforms/Douyin/` (`DouyinMiniGameAuthClient` etc., see
  [platform-douyin.md](platform-douyin.md) §2) — the capability table entry
  `ServerSessionExchange` has been upgraded to VerifiedOfficial.
- **The full server-side OpenAPI menu has been seen** (credentials / login / data cache / QR code / other /
  subscription messages / tournament ×2): **there is no server-side push channel for live audience events**;
  live interaction is client-side only — `tt.getLiveManager` + LiveManager — do not fabricate push clients.
- **In-container real-time channel (WebSocket / connectSocket) is not listed in the official index**
  (the network category has only the 3 APIs above) → the real-time communication capability is **not found in
  public documentation (pending verification)**; the integration layer must not assume the mini-game container
  provides WebSocket.

### Verified vs. Pending Verification

| Capability | Status | Basis |
|---|---|---|
| Client login to get code (`tt.login`) | **Verified** (API name) | Official JS API index page |
| Server-side code2session to exchange session | **Verified** (2026-10-14 details) | [code-2-session official documentation](https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/server/log-in/code-2-session); landed, see [platform-douyin.md](platform-douyin.md) §2.2 |
| In-container WebSocket real-time channel | **Not found in public documentation (pending verification)** | Official index network category has no socket API; server-side OpenAPI has no push channel (confirmed on re-verification) |
| Open data domain (`tt.getOpenDataContext` / `tt.onMessage`) | **Verified** (API name) | Official JS API index page |
| Friend leaderboards / cloud storage / game leaderboard APIs | **Verified** (API name) | Official JS API index page |

## 2. Current Implementation Contract (`Chorus.Adapters.Platforms.MiniGame`)

The integration layer takes the **capability table** as the single source of truth; unverified capabilities are
unavailable — to prevent fabricating platform capabilities:

```
src/Chorus.Adapters.Platforms/MiniGame/
├── MiniGamePlatformCapabilities.cs   // 能力表（已核实/待核 + 待外部）
├── MiniGameLoginExchange.cs          // 服务端 code2session（配置驱动，fail-closed）
├── MiniGameIdentityBinder.cs         // 登录 session → Chorus Player 身份
└── MiniGameLeaderboardProjector.cs   // Chorus 排行行 → 平台中性 payload
```

The three verified Douyin server-side OpenAPI items (token / code2Session / rank upload) are a
**dedicated implementation** in `src/Chorus.Adapters.Platforms/Douyin/`
(`DouyinAccessTokenProvider` / `DouyinMiniGameAuthClient` / `DouyinRankUploadAdapter`, 20 golden tests) — the
generic layer (configuration-driven) is left to unverified platforms, without duplicating wiring with the
dedicated implementation; see [platform-douyin.md](platform-douyin.md) §2.

### 2.1 Capability Table (`MiniGamePlatformCapabilities`)

- `Supports(capability)` returns true only for **VerifiedOfficial** capabilities;
  capabilities not found in public documentation (pending verification) are never available.
- Credentials / real-device joint debugging items are not capabilities; they are recorded separately in
  `ExternalPending` (marked "pending external").
- Unknown platform `TryGet` returns null (fail-visible).

### 2.2 Login Session Exchange (`HttpMiniGameLoginExchange`)

- Endpoint, AppID, AppSecret, form field names, success flag field, account field names are **all
  configuration-injected**; no platform-private field names are hardcoded (unverified platforms are explicitly
  configured by the caller).
- Missing credentials or endpoint → **reject everything (fail-closed)**: no request sent, ERROR log written
  (only once).
- Platform success flag mismatch / missing account id / non-2xx / malformed JSON / network exception →
  return null.
- Request body limit 16KB (over-limit bodies are not read or parsed).
- HttpClient is injected by the caller; this implementation does not dispose it.

### 2.3 Identity Mapping (`MiniGameIdentityBinder`)

- Login session → the **Player** identity in `PlatformIdentityRegistry` (§14); the default identity id is
  deterministically derived as `<platform>:<platformAccountId>` (idempotent on re-login).
- If the same platform account is already bound to another Chorus identity → **fail-visible** (report the
  existing mapping, do not implicitly overwrite), to prevent a hijacked login code from taking over someone
  else's identity.

### 2.4 Leaderboard Projection (`MiniGameLeaderboardProjector`)

- Input is the engine-side authoritative leaderboard rows (ranks are given by the caller; projection
  **does not re-rank**).
- Platform with unverified leaderboard capability → return null (do not assume the platform has a
  leaderboard API).
- Output is a **platform-neutral row payload** (rank / participantId / score);
  drawing inside the open data domain sandbox is platform-side JavaScript, **not in this repository**
  (pending external).

## 3. External Pending List (Credentials and Real-Device Joint Debugging, Explicitly No Guessing)

- [ ] Mini-game AppID / AppSecret (credentials, to be provided externally, never written into code)
- [x] Server-side code2session endpoint and parameters (verified against official documentation 2026-10-14 and landed, see [platform-douyin.md](platform-douyin.md) §2.2)
- [ ] Real-device / developer-tool joint debugging to verify whether an in-container real-time channel (Socket) exists
- [ ] Leaderboard drawing script inside the open data domain sandbox (platform-side JS, not in this repository)

## 4. Acceptance Test Locations

| Case | Test |
|---|---|
| Capability table verified / pending verification / pending external | `MiniGamePlatformCapabilitiesTests` |
| code2session success / failure / fail-closed / field names / size limit | `MiniGameLoginExchangeTests` |
| Douyin server-side OpenAPI three items (token / code2Session / rank upload) | `DouyinMiniGameClientTests` / `DouyinRankUploadAdapterTests` |
| Identity mapping determinism / idempotency / conflict fail-visible | `MiniGameIdentityBinderTests` |
| Leaderboard projection capability gating / truncation / input validation | `MiniGameLeaderboardProjectorTests` |

Related: [platform-douyin.md](platform-douyin.md) (live callback), [adapter-contract.md](adapter-contract.md) (adapter contract).
