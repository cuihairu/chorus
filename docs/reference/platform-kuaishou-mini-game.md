---
title: "Platform Integration Reference: Kuaishou Mini-Game"
---

<p align="center">
  English | <a href="platform-kuaishou-mini-game.zh.md">简体中文</a>
</p>

# Platform Integration Reference: Kuaishou Mini-Game (kuaishou mini-game)

> Scope: the client running inside the Kuaishou mini-game container and the server-side integration half.
> This is a **separate link** from Kuaishou live callback integration ([platform-kuaishou.md](platform-kuaishou.md)).
> This document records the official public documentation research findings of 2026-10-14 and the current implementation contract.

## 1. Official Findings (2026-10-14)

**Conclusion: no "mini-game" section was found in Kuaishou's public documentation — all capabilities are "not found in public documentation (pending verification)".**

- The entry and content pages of the official documentation site `open.kuaishou.com/docs` are all "**Kuaishou Mini Program**"
  (`/docs/develop/guide/introduction` etc.); no mini-game section was seen, and no `ks.login`-style mini-game API was seen.
- Mini-program-side capabilities verified (**not mini-games**, for reference only, must not be used as mini-game capabilities):
  - code2Session: `POST https://open.kuaishou.com/oauth2/mp/code2session`,
    `application/x-www-form-urlencoded`, fields `js_code` / `app_id` / `app_secret`,
    response `result` (1 = success) / `session_key` (24h) / `open_id`
    (unified across mini programs under the same developer entity, based on the unified social credit code); `js_code` valid for 10 minutes.
    Source: `https://open.kuaishou.com/docs/develop/server/code2Session.html`
  - Message encryption/decryption and signature: `sha1Hex(rawBody + 消息验证Token) == 请求头 kwaisign`,
    AES-256-CBC/PKCS7 (Key = base64-decoded message encryption/decryption KEY, IV = first 16 bytes of Key).
    Source: `https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html`
    (landed as live callback signature verification and envelope decryption, see `KuaishouPlatformEventVerifier` /
    `KuaishouPlatformEventMapper`)
- The specific console configuration entries require developer login and were not confirmed (does not block implementation).

### Verified vs. Pending Verification

| Capability | Status | Basis |
|---|---|---|
| Client login to get code (mini-game) | **Not found in public documentation (pending verification)** | Public documentation only covers mini programs |
| Server-side code2session (mini-game) | **Not found in public documentation (pending verification)** | Only the mini-program endpoint was verified; whether it is reused is not published |
| In-container WebSocket real-time channel | **Not found in public documentation (pending verification)** | Public documentation shows no mini-game WebSocket capability |
| Open data domain | **Not found in public documentation (pending verification)** | Public documentation shows no mini-game open data domain |
| Friend leaderboards / cloud storage | **Not found in public documentation (pending verification)** | Public documentation shows no mini-game leaderboard / cloud storage capability |

## 2. Current Implementation Contract (`Chorus.Adapters.Platforms.MiniGame`)

- `MiniGamePlatformCapabilities.Kuaishou`: all capabilities are marked `NotFoundPublicDocs`,
  `Supports()` returns false for any capability — **do not assume Kuaishou mini-games have any unpublished capabilities**.
- `MiniGameLoginExchangeConfig.KuaishouMiniProgram(appId, appSecret)`:
  provides the **verified mini program** code2Session official form (endpoint + field names),
  for direct reuse during joint debugging; whether mini games reuse this endpoint is **not found in public documentation (pending verification)**.
- Leaderboard projection: `Project("kuaishou", ...)` always returns null (capability unverified).

## 3. External Pending List (Credentials and Real-Device Joint Debugging, Explicitly No Guessing)

- [ ] Whether the mini-game platform entry itself exists independently of the mini-program platform (requires developer backend / login state to confirm)
- [ ] Mini-game AppID / AppSecret / message verification Token and other credentials (to be provided externally)
- [ ] SDK capability specifications for login, real-time communication, and leaderboards (to be researched once official documentation is published)
- [ ] Real-device / developer-tool joint debugging verification

## 4. Acceptance Test Locations

| Case | Test |
|---|---|
| All Kuaishou capabilities pending verification, `Supports` always false | `MiniGamePlatformCapabilitiesTests` |
| Verified mini-program code2Session form (endpoint / field names) | `MiniGameLoginExchangeTests` |
| Leaderboard projection returns null for Kuaishou | `MiniGameLeaderboardProjectorTests` |

Related: [platform-kuaishou.md](platform-kuaishou.md) (live callback), [adapter-contract.md](adapter-contract.md) (adapter contract).
