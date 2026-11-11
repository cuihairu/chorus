---
title: "Platform Unity SDKs: Douyin and Kuaishou product forms and admission matrix"
---

# Platform Unity SDKs: Douyin and Kuaishou product forms and admission matrix

> FIX-U3D-01 deliverable (todo.md 22.18). Foundation research: [Douyin and Kuaishou 2026-10-30](/research/platforms/douyin-kuaishou-2026-10-30); this page re-verifies the load-bearing official pages on **2026-11-11 (Asia/Shanghai)** and turns the result into the product-form confirmation and admission matrix the U3D tasks gate on. Status words follow the [integration matrix](./platform-integration-matrix): **confirmed** means the official public page supports the statement as of the visit date; **needs permission** means an authorized developer account must complete it; **unverified** means current evidence is insufficient. Public pages and a filled-in AppID never imply review is waived or capabilities are granted.

## 1. Douyin: an official Unity SDK exists (BGDT + LiveOpenSDK)

All items below re-verified 2026-11-11 against the official interaction docs.

| Aspect | Confirmed product form |
| --- | --- |
| Distribution | The Unity SDK is distributed through BGDT (ByteGame Develop Tools): install the `com.bytedance.bgdt-cp.unitypackage` BGDT package, select LiveOpenSDK in the BGDT menu (channel `cp`), and the SDK lands at `Packages/com.bytedance.liveopensdk`. |
| Compile surface | Game code compiles against the `ByteDance.LiveOpenSdk.Api` assembly inside that package. |
| In-package samples | App id configuration, Douyin cloud environment (EnvId / ServiceId / debug endpoints) and command direct-push — the three legs the supported modes need. |
| Internal-test menu | The BGDT Apply menu offers the internal-test SDK version and needs platform permission before it is usable. |
| Server links | The overview page confirms three supported modes: Douyin Cloud hosting with the cloud long-connection gateway (server-side API), Douyin Cloud hosting with command direct-push, and serverless with command direct-push. A newer log entry also lists dual push to both the SDK and the server. Chorus must pick one mode per deployment and treat the server leg as the only authoritative input. |
| Latest public version | The update log's newest entry is still **2.7.4 (2025-08-06)** (dual push + magic gifts); history: 1.3.0 (2024-05-08, SDK released) → 2.0.0 (2024-06-13, direct-push) → 2.1.0 (2024-09-03, gift advanced interaction + results leaderboards). No newer public entry on 2026-11-11. |
| Capability set | Live info API (room_id, streamer open_id); gift advanced interaction (match state and factions); user results and leaderboards — live-room scope, no global leaderboard; traffic-guidance and cloud-launch components. In development: co-streaming (host-host, host-fan) and quick faction join. |
| Fulfillment ACK | The overview page states the fulfillment ACK reporting API **must** be integrated and that the platform will later force rejection at review for games without it. This is a server-side duty of the Chorus platform layer, not an optional client nicety. |

## 2. Kuaishou: companion-launched exe with an IPC library — no official Unity gift SDK

Client access page and server basics page both carry "updated 2024-11-22" and were re-verified 2026-11-11. The product is a standalone Windows exe launched by the PC companion (download page `live.kuaishou.com/live-partner`), rendered vertical fullscreen beside the streamer's camera. **Hot-updating the game package is forbidden; package updates go through the platform backend.**

### 2.1 Client contract

| Aspect | Confirmed form |
| --- | --- |
| Launch parameters | `-c` carries the roomCode (present only after the stream has started; starting the exe before the stream means the code arrives later over IPC); `-pos` carries the suggested window rect (first launch uses it, later launches remember the last window rect); `-ipc` carries the IPC name — present only when `ks_manifest.json` sets `ipcSetCode` or `ipcQuit`. |
| Package manifest | `ks_manifest.json` (UTF-8) names the launcher (the file must be named after the appId, e.g. `ks1234567890123456.exe`, at the package root), the worker and kill process lists the companion supervises, capture parameters and optional rule images (vertical 240px wide max 424px tall; horizontal max 748px wide, 124px tall). |
| Capture | Unity and UE games support `game` and `window` capture (window class e.g. `UnityWndClass`); Cocos supports `window` capture only. A virtual-camera capture type exists for beauty-filter pipelines. |
| IPC library | The companion ships a native IPC library (public binary link `ipc_8fd4d4d.rar`, the same `8fd4d4d` build the 2026-10-30 research recorded): C functions `InitIpc` / `ReleaseIpc` / `SendData` plus data/connected/disconnect/log callbacks, a class wrapper, and a C# integration sample. Open with type CLIENT and the `-ipc` name; JSON messages. Disconnect means the companion exited and the game should exit too. `ipcSetCode` (the companion pushes the match code after stream start) and `ipcQuit` (companion-initiated restart) are per-manifest opt-ins. |
| What the IPC library is not | It is a companion-cooperation channel only — it does not carry gifts, comments or likes. All interaction data arrives on the server push leg (below); any client that "reads gifts from IPC" would be inventing a protocol the platform does not offer. |

### 2.2 Server contract

| Aspect | Confirmed form |
| --- | --- |
| Credentials | Portal onboarding grants app_id and app_secret; the developer server exchanges them for an access_token (48 h validity, refresh recommended hourly, retry on failure; old and new tokens both valid until expiry). Every open API call passes app_id and access_token as query parameters, never in the body. |
| Launch-required set | The basics page marks three capabilities as required to go live: **bind**, **gift top**, and **interaction-message push**. Not integrating them means the game cannot go live — the Kuaishou mirror of Douyin's mandatory fulfillment ACK. |
| Bind | `POST /openapi/developer/live/smallPlay/bind`, module `bind`, action `start` / `stop` / `status`. Body: roomCode, millisecond timestamp, data as a JSON string, and `sign` (core fields sorted, app_id included, app_secret appended last). Limit 10 calls/s per app_id. The server must bind immediately after the streamer launches the game, or no interaction data is pushed. `status` answers 1 (open) or 2 (disconnected). A callback URL override exists for joint debugging only. |
| Round | `POST .../smallPlay/round`, module `round`, action `start` / `stop`. roundId is unique per roomCode and consistent across the phases of one round, shorter than 64 chars; roundType is `singleGroup` (single streamer with factions), `singleNotGroup` (single streamer, whole room), `double` or `multiple`; stop carries the result (winning faction / winning user / win-lose-tie). Optional when the game has no round concept. |
| Gift top | `POST .../smallPlay/gift`, module `gift`, action `top`, data carries the comma-separated gift id list plus an extend-info string. |
| Push protocol | The platform POSTs interaction messages to the developer callback registered offline. **Ordering across gift/comment/like is explicitly not guaranteed** — the game must tolerate a gift arriving before its comment. Idempotency key: `data.unique_message_id` (the outer `message_id` is not idempotent). Success is HTTP 200 plus `result=1`; anything else makes the platform retry, with the retry count configured per event name. |
| Test traffic | Stress traffic rides event `LIVE_INTERACTION_DATA_TEST` with `stress_` prefixes on `unique_message_id` and on user ids in the payload. The page states stress messages **must** be handled specially (no data reporting, no leaderboard participation) or acceptance is blocked — Chorus's tenant/test isolation must classify them, not ingest them as real users. |
| Payload types | `giftSend` (uniqueNo per transaction, gift id/name/count/unit price in Kuaishou coins — 10 per CNY — total price, user info; only gifts the developer subscribed to are pushed), `liveComment` (keyword exact-match list agreed offline), `liveLike` (anti-fraud; effective limits 60/min and 500/h per user), `follow`, `share`, `fansGroupJoin`, plus paid tier messages (`noble`, super-fan groups). |
| Error codes | 21 (access-token check failed), 220361 (system error), 220362 (too frequent), 220363 (app rate-limited), 220365 (no gameplay record), 220368 (appId does not match the gameplay record). |

## 3. Three layers the U3D work must keep separate

This is the conditional-compilation and configuration-wizard plan the foundation research's checklist asked for. Every U3D deliverable places each piece of code and configuration in exactly one layer:

| Layer | Contents | Where it lives |
| --- | --- | --- |
| 1. Platform official SDK | Douyin: the LiveOpenSDK unitypackage and the `ByteDance.LiveOpenSdk.Api` assembly, behind a Douyin conditional-compilation symbol. Kuaishou: the companion IPC native library and its C# sample wrapper, behind a Kuaishou symbol that is Windows-only. | Per-platform Unity package folders; never referenced by shared game code directly. |
| 2. Chorus SDK | The unified Unity interface (FIX-U3D-02): platform adapter contracts (FIX-U3D-05 Douyin, FIX-U3D-06 Kuaishou), mount/login to the streamer gateway, snapshot and delta application, intent sending, reconnect and catch-up, leaderboard reads. | Chorus's own Unity package, platform-neutral; no platform namespace leaks into it. |
| 3. Server-side secrets | app_secret, application private keys, server access_tokens, SaaS keys. | Chorus server configuration only. Never in Unity Resources, never in the built package, never in the browser console or research documents. The Unity side holds only public configuration. |

The configuration wizard (FIX-U3D-04) validates layer-3 separation explicitly: it checks the public configuration set (appId, platform selection, public endpoints, package and review materials, Kuaishou manifest fields) and refuses a build whose Unity project contains a secret-shaped key. The wizard's success message states what remains open on the platform side — capability application, gift subscription, review submission — and never presents a filled AppID as a review waiver.

## 4. Admission matrix

Rows are the admission steps a Chorus deployment must clear before real traffic. Dates are the evidence dates; the product-form rows were re-verified on 2026-11-11.

| Admission step | Douyin | Kuaishou |
| --- | --- | --- |
| Developer onboarding, app_id | Confirmed (public docs and console); real account **needs permission** | Confirmed (portal onboarding guide); real account **needs permission** |
| Live gameplay product form | Confirmed 2026-11-11 (interaction docs + Unity SDK access guide) | Confirmed 2026-11-11 (miniPlayDocs introduction + client access, updated 2024-11-22) |
| Unity SDK material | Confirmed (BGDT + LiveOpenSDK public pages); actual package version, license and hash **need permission** (internal-test Apply menu) | Confirmed absent as a gift SDK; IPC library public (binary link unchanged since the 2023 `8fd4d4d` build); license and Unity compatibility **need permission** / **unverified** on real Windows + Unity |
| Capability application | **Needs permission** (Apply internal-test, capability activation in console) | **Needs permission** (interface scope granted before joint debugging, per the basics page's scope note) |
| Gift allowlist / subscription | **Needs permission** (gift configuration lives in the authorized console) | Confirmed mechanism (only subscribed gifts are pushed); the actual subscription list **needs permission** |
| Signing material | Confirmed guide (SHA256-RSA2048); real keys **need permission** | Confirmed scheme (sorted-field signature, app_secret appended last; callback verification per the epay appendix); real secret **needs permission** |
| Fulfillment / status reporting duty | Confirmed mandatory (overview page: review rejection without the fulfillment ACK) | Confirmed required to go live (bind + gift top + push marked launch-required) |
| Test-traffic isolation | **Needs permission** (sandbox samples are a §5 checklist item of the foundation research) | Confirmed mechanism (`LIVE_INTERACTION_DATA_TEST` event, `stress_` prefixes, special handling required for acceptance); live stress samples **need permission** |
| Proposal, package and review materials | **Needs permission** (templates in the authorized backend) | **Needs permission** (package upload and review in the authorized backend) |
| Real-link acceptance (FIX-U3D-17 / U3D-19) | Blocked until the needs-permission rows above clear | Same |

## 5. What this gates

- **FIX-U3D-02 (unified interface)** — unblocked. The asymmetry is now a pinned fact the interface must absorb: Douyin exposes an SDK event surface inside the Unity process, Kuaishou exposes nothing client-side but a companion-cooperation IPC, and both platforms deliver interaction facts to the Chorus server, where the game's authoritative inputs live.
- **FIX-U3D-03 (package and conditional compilation)** — unblocked. Two symbols with distinct shapes: Douyin's is a managed assembly reference; Kuaishou's is a native library plus Windows-only build target.
- **FIX-U3D-04 (configuration wizard)** — unblocked. Its check list is the layer-3 rule and the public configuration set in section 3.
- **FIX-U3D-05 / U3D-06 (platform adapters)** — unblocked at design level; actually downloading the Douyin SDK package and running the Kuaishou IPC library on Windows need the needs-permission rows.
- **FIX-U3D-14 (fulfillment and status reporting)** — the contract duties are confirmed on both platforms (Douyin fulfillment ACK; Kuaishou bind + gift top + push); real-traffic verification rides the same needs-permission rows.
- **FIX-U3D-17 / U3D-19 (real-link acceptance)** — blocked until an authorized account, activated capabilities, a test streamer and a subscribed gift list exist. This is the honest reading of the matrix: the code work can finish first, the acceptance cannot start without credentials.

The foundation research's §5 checklist items that need credentials (real SDK version and license, real signature samples, roomCode both flows, MD5 verification on real callbacks, retry and timeout full rules, shutdown ordering, leaderboard write APIs, proposal templates) stay open and are not re-listed here.
