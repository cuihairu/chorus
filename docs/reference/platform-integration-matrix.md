---
title: "Multi-platform live integration: materials and architecture compatibility"
---

# Multi-platform live integration: materials and architecture compatibility

**Research date: 2026-10-30 (Asia/Shanghai).** This page records public-material research; it does not mean developer permissions, SDK commercial licenses, or real platform joint debugging have been obtained. Source publish/modify dates and this visit date are kept separately in the evidence attachments below; an unknown source date is recorded as unknown, not inferred to be the latest version.

The current implementation scope remains **Douyin and Kuaishou**. The next phase focuses research on Douyu; Huya, Bilibili and other platforms are research/compatibility only. Third-party coverage lists, user scale, revenue-share percentages, keyword or gift restrictions are not treated as facts without an applicable product and official evidence.

## Evidence attachments

| Attachment | Research date | Content |
| --- | --- | --- |
| [Douyin and Kuaishou](/research/platforms/douyin-kuaishou-2026-10-30) | 2026-10-30 | Official live-mini-game entry, Unity/companion launch materials, server events, fulfillment and lifecycle; differences from legacy mini-game/SaaS materials |
| [Douyu, Huya and Bilibili](/research/platforms/douyu-huya-bilibili-2026-10-30) | 2026-10-30 | Next-phase official channels, permission gaps, SDK/examples, legacy adapter migration and per-platform compatibility |
| [Other candidates and aggregators](/research/platforms/candidates-and-aggregators-2026-10-30) | 2026-10-30 | Domestic/overseas candidates item by item, vendor identity and claims, unconfirmed capabilities and follow-up materials |

Each attachment stores official links and visit results. "Publicly confirmed" here confirms only what the source explicitly writes; "needs permission" means material can only be completed after platform authorization; "unconfirmed" means this round did not obtain enough first-hand evidence and cannot infer the platform does not support it at all.

## Platform index

Every row below was researched on **2026-10-30**; product form, version, source date and specific links are in the corresponding attachment. Existing code registration and historical protocol research do not equal production integration.

| Platform | Scope and product | Material conclusion / integration prerequisite | Evidence |
| --- | --- | --- | --- |
| Douyin | Current; live mini-game | Official Unity SDK and server material are searchable; specific mode, version, app permission and real joint debugging still need locking; mini-game login API does not replace live event protocol | Douyin and Kuaishou |
| Kuaishou | Current; live mini-game | Official PC companion launch, roomCode/IPC, binding and match APIs are searchable; actual package, test streamer and app permission still to obtain | Douyin and Kuaishou |
| Douyu | Next-phase focus; danmaku interaction | Official creator entry and ordinary danmaku material must be separated; current gameplay SDK, trusted event and commercial access need permission evidence | Douyu, Huya and Bilibili |
| Huya | Later research; legacy open API/danmaku gameplay | Official legacy open API has a 2026-10-31 stop-support notice and cannot be a stable new integration route; replacement channel to confirm, not inferred as whole-platform shutdown | Douyu, Huya and Bilibili |
| Bilibili | Later research; live open platform | Official open platform and examples searchable; a C# basic example is not a complete Unity SDK; legacy bridge/code registration needs migration review | Douyu, Huya and Bilibili |
| Channels (WeChat Video) | Domestic candidate | Must prove a Video-channel live event channel; mini-program live capability cannot substitute | Other candidates and aggregators |
| Taobao Live | Domestic candidate | E-commerce tool capability and gameplay access must be verified separately | Same |
| Pinduoduo Live | Domestic candidate | Official gameplay event protocol required by this project is unconfirmed; needs developer channel and authorization material | Same |
| Xiaohongshu Live | Domestic candidate | Open-platform entry does not prove open live gift/danmaku gameplay | Same |
| JD Live | Domestic candidate | Merchant/e-commerce API and live-gameplay data authorization verified separately | Same |
| Meituan Live | Domestic candidate | Needs official live event, game use and test access evidence | Same |
| Alipay Live | Domestic candidate | Mini-program/live product and real-time gameplay event capability verified separately | Same |
| AcFun | Domestic candidate | Needs official permission and event contract; third-party danmaku library is not authorization proof | Same |
| QQ Music / Fan Live | Domestic candidate | Historical first-hand gameplay interaction leads; 2026 current channel, SDK and protocol unconfirmed | Same |
| TikTok | Overseas candidate | Separate from domestic Douyin; public developer API does not equal LIVE gift-event opening | Same |
| Twitch | Overseas candidate; EventSub | Official chat/Cheer/subscription-gift events searchable; authorization, dedup and game semantics need independent adaptation, not equal to domestic gift/like | Same |
| YouTube | Overseas candidate; Live Streaming API | Official liveChatMessages includes paid interaction, supports streamList; OAuth, quota and live-session lifecycle need adaptation | Same |
| Bigo | Overseas candidate | Public official event contract required by this project is unconfirmed; needs business/developer material | Same |
| Nimo | Overseas candidate | Public official event contract required by this project is unconfirmed; needs current service and opening-scope evidence | Same |

Huya status is based on the [official open API protocol page](https://dev.huya.com/docs/miniapp/dev/open/proto/); Twitch on the [official EventSub subscription types](https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/); YouTube on the [official LiveChatMessages](https://developers.google.com/youtube/v3/live/docs/liveChatMessages). A single public API or a third-party library is never used to infer whole-platform game compatibility.

## Third-party tools

Aggregator claims are separate from the platform authorization they depend on. A vendor claiming to support a platform is not proof of that platform's authority, trusted source or idempotency; client-side reporting cannot be an authoritative gift source.

| Tool | Current material status | To complete before integration |
| --- | --- | --- |
| Edge Hacker live interaction assistant | Vendor self-report; see candidate attachment | Authorization scope, version, trusted server entry, event ID, replay/duplicate semantics, commercial license |
| AIOBS | Vendor self-report; see candidate attachment | Same; verify actual product and protocol, not support by platform icon |
| Hurricane platform | Publisher-repo lead found; product ownership unconfirmed | First confirm whether it is the vendor the user means, then obtain formal protocol and license |
| ShiMengLive | No first-hand source attributable this round | Exact product address, publisher, documentation and authorization evidence |

## Unified material registration fields

| Material group | Required fields |
| --- | --- |
| Source and scope | Official/vendor/third party, URL, visit date, source publish/modify date, product form, version, region and confirmed/needs-permission/unconfirmed |
| Access | Enterprise/developer certification, gameplay proposal, package review, operations/commercial terms entry, authorized use, test environment and streamer account |
| App and identity | AppId, project/gameplay ID, streamer authorization method, OpenId scope, live-session/room identifier, binding validity |
| Client and launch | Companion launch parameter, roomCode/IPC, official SDK/example download and license, Unity/OS version, conditional compilation and distribution restriction |
| Server credentials | Key/certificate/Token acquisition and rotation, signature/verification, callback domain; secrets only into the server credential system, not into Unity package or docs |
| Events | HTTP push/WS/streaming pull, subscription scope, raw packet vs single event, event key, user identifier, gift quantity/combo, like aggregation, rate limit, ordering, retry/replay and source proof |
| Lifecycle | Start, subscribe/heartbeat, stop delivery, end, unknown-result query/retry, fulfillment ACK trigger condition and sole executor |
| Business and operations | Match state, platform room leaderboard vs framework leaderboard, cross-room/PK permission, per-platform packet/event/gift quantity/reject/dedup/failure metrics |
| Compatibility | Affected process, reusable contract, extension needed, conflict/insufficient evidence, required change, verification method and blocking material |

## Architecture compatibility

This is a material assessment against the [current target architecture](/architecture/architecture), **not an implementation acceptance**. A platform may need additional protocol adaptation, but it does not add a new business process or push platform business into DataServer.

| Existing boundary | Reusable parts | Gaps/changes to confirm per platform |
| --- | --- | --- |
| StreamerServer + Unity | Streamer entity, authorized binding, match-intent submission; Unity shared connection and sync interface | Real identity scope, companion launch/authorization SDK, SDK version and thread callback; only Douyin/Kuaishou enter this phase's conditional compilation |
| PlatformGateway | Source verification, platform normalization, direct push to GameServer by ControlServer-confirmed route | HTTP/WS/stream receive adaptation, signature, subscription holder, platform event key and replay; having a client SDK does not make client reporting trustworthy |
| ControlServer | Binding generation, RoomId, owner/RoomEpoch, route install confirmation, match and wrap-up coordination | Platform live-session to game-match mapping, platform start/end caller and unknown-result recovery; must not guess a player's room |
| GameServer / RoomActor | Per-room Tick, authoritative execution, committed effects, reconnect catch-up | Gift combo/like aggregation business semantics; platform transport replay must not generate duplicate rewards; platform PK does not replace frame sync |
| DataServer | Explicit binding/input/idempotency/state storage transactions and ownership isolation | Data fields, key length, retention and transaction constraints; does not parse platform events, select target rooms, or orchestrate platform APIs |
| StreamerGateway | Streamer-client input and committed frames direct to GameServer | Does not carry platform gift source proof; client connection and platform event connection recover separately |
| NodeAgent / backend | Machine, process and resource monitoring; backend platform metrics aggregation | NodeAgent holds no room authority; leaderboard backend distinguishes platform vs framework leaderboard; packets, events and gifts counted separately |

The unified identity chain is: `Platform + AppId + verified streamer identity / live session → BindingId/Generation → RoomId → owner/RoomEpoch`. Platform audience events are first attributed to the verified live mount; never guess a GameServer from a nickname or platform room number. Cross-room PK and cross-platform matchmaking are not the same permission; cross-platform matching and identity merge are disabled by default.

The ending order keeps: commit final result → ControlServer closes delivery → PlatformGateway drains and confirms → notify platform per authorized responsibility and confirm result → settlement/disposal of remaining input → release eligibility → authorize room destruction. When a platform requires no end interface, evidence is required before marking NotRequired; an interface timeout is not a success. When a stable event key, trusted entry or close confirmation is missing, record a blocker and pending alternative.

Douyin and Kuaishou protocol differences are carried by platform adapters and lifecycle contracts, but still need real permissions and joint debugging. Douyu is temporarily "insufficient evidence" because key material is missing; Huya's legacy route has stop-support risk and needs replacement-channel confirmation first; Bilibili long-connection must be landed into the new PlatformGateway and lifecycle coordination. A single public API for other candidates cannot infer whole-game integration compatibility; per-item conclusions are in the attachment.

## Next-phase handoff

| Order | Independent task | Completion evidence |
| --- | --- | --- |
| Current | Lock Douyin authorized product, SDK version and real launch material | Developer-backend product/permission records and SDK version list |
| Current | Obtain Kuaishou test app, streamer and IPC/callback joint-debugging material | Legally obtained package version and test-environment record |
| 1 | Obtain current Douyu interaction-gameplay development documentation | Official product entry, version, permissions and test conditions |
| 1 | Determine Douyu event trusted source and stable event key | Official protocol, signature/authorization, duplicate/replay rules |
| 1 | Determine Douyu start, match start and close responsibility | Interface/SDK documentation and unknown-result handling basis |
| 2 | Verify alternative route after Huya legacy API stop | Current official announcement/product documentation and access conditions; do not implement before confirmation |
| 2 | Compare Bilibili official protocol against legacy adapter item by item | Lifecycle, signature, event key and new-route migration list |
| Candidate | Fill missing material per candidate attachment | First-hand protocol or authorized formal material; do not replace with aggregator coverage list |

Atomic task numbers remain in the repo `todo.md` §22.19 (FIX-PLAT) and §22.18 (FIX-U3D). This archive records public material; real credentials, SDK authorization, product approval, joint debugging, integration and launch are separately accepted.
