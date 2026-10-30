---
title: "Platform Integration Reference: Douyin"
---


# Platform Integration Reference: Douyin

> Routing: `POST /ingress/{platform}` (`platform=douyin`, monolith and Gateway are isomorphic).
> Signature verification is fail-closed; deduplication goes through `EventDeduplicator` (platformEventId + TTL).
> This document records two rounds of official-fact research and the current implementation contract — 2026-10-12 (blocked) and 2026-10-14 (server-side OpenAPI verified) — for cross-reference replacement during joint debugging.

## 1. Official-Fact Research Conclusions

**2026-10-14 re-verification (current state)**: the documentation site body is reachable (pages blocked by the security script in the first round got content this round).
The mini game's **three server-side OpenAPIs are verified and landed** (getAccessToken / code2Session / player rank reporting,
see §2); **live audience event push is confirmed not to be in the mini game server-side OpenAPI menu** (the menu is fully seen:
credentials/login/data cache/QR code/other/subscription messages/events×2; live interaction only has the client-side JS API
`tt.getLiveManager` + LiveManager). Therefore the ingress side (callback address/signature/event fields/push form)
remains entirely unverified, and implementation continues per the §3 joint-debugging placeholder contract, without fabricating a push client.

**2026-10-12 first round (blocked record)**:

- The `developer.open-douyin.com` documentation site body is JS-rendered + a security script (argus/secsdk),
  static scraping cannot get content; the document content API returns `401 {"login":false}` (login state required).
- Search engines (Bing/DuckDuckGo/360/Baidu) were blocked by anti-crawling; the anonymous GitHub API quota was exhausted.
- At that time, the four ingress details (callback entry/signature algorithm/event fields/push channel form) were all unverified —
  this conclusion still holds for the ingress half, see §3/§4.

### Known Risks (ingress half, confirm first during joint debugging)

1. **Push channel form unconfirmed**: Douyin live events' real-time delivery has two possible official forms: "HTTP callback" and "WS push actively connected by the developer".
   If the official side only provides WS push (the developer connects to Douyin as a client), then `/ingress/douyin` needs to be forwarded in by a push client process;
   the current HTTP endpoint can be kept as the forwarding target with an unchanged interface.
2. **Signature algorithm unconfirmed**: currently a placeholder with HMAC-SHA256; if the algorithm differs during joint debugging, only
   `DouyinPlatformEventVerifier.ComputeSignature` (single method) and the header name configuration need to be replaced.
3. **Event fields unconfirmed**: field extraction is concentrated in `DouyinPlatformEventMapper`; replace that file during joint debugging; the pipeline stays untouched.

## 2. Mini Game Server-Side OpenAPI (officially verified and landed on 2026-10-14)

The three APIs are verified per the official documentation and landed (`src/Chorus.Adapters.Platforms/Douyin/`),
with golden tests `DouyinMiniGameClientTests` / `DouyinRankUploadAdapterTests` (offline fake backend,
`HttpMessageInvoker` seam). **No production wiring** (same as the RoomAffinity precedent): Server/Gateway
Program.cs does not register default instances; wiring waits for real mini game credentials and event configuration.

### 2.1 getAccessToken (`DouyinAccessTokenProvider`)

- `POST https://minigame.zijieapi.com/mgplatform/api/apps/v2/token`,
  JSON body `{appid, secret, grant_type:"client_credential"}`.
- Response `{err_no, err_tips, data:{access_token, expires_in}}` (expires_in in seconds,
  e.g. 7200); err_no != 0 throws InvalidOperationException (40015 wrong appid /
  40017 wrong secret / 40020 wrong grant_type / -1 system error).
- The token is valid for 2 hours; repeated fetching compresses the old token to 5 minutes (official suggestion: refresh hourly):
  implemented as an in-process singleton cache + SemaphoreSlim single-flight (preventing concurrent repeated fetches) + a 5-minute early refresh margin;
  tokens shorter than the margin are not reused (covered by golden tests).
- The secret does not enter error messages (asserted by golden tests).

### 2.2 code2Session (`DouyinMiniGameAuthClient`, login/identity mapping)

- `GET https://minigame.zijieapi.com/mgplatform/api/apps/jscode2session?appid&secret&code|anonymous_code`
  (code = tt.login non-anonymous credential, anonymous_code = anonymous credential; at least one must be passed; both are valid only once).
- Response `{error, errcode, errmsg|message, openid, session_key, unionid, anonymous_openid}`;
  error==0 means success (40014 missing parameter / 40015 appid / 40017 secret / 40018 wrong code /
  40019 wrong anonymous code / -1 system error). openid/unionid are only returned with code,
  anonymous_openid is only returned with anonymous_code.
- error==0 but no identity fields at all → treated as failure (fail-closed, do not guess success).
- Platform errors return a failure result (error code + message) without throwing — error codes are a normal business surface;
  both being empty is a contract violation; throws ArgumentException and sends no request.
- **session_key is sensitive**: only stored in the result object, never in logs, never delivered to clients (official security requirement:
  leakage may cause the mini game to be taken down).
- **Identity mapping**: `DouyinIdentity.ToChorusUserId(openid)` = `douyin:{openid}`,
  deterministic, stateless, consistent across processes; openid is stable within the same mini game, merging across mini games requires unionid
  (decided independently at the upper layer).

### 2.3 Player Rank Reporting (`DouyinRankUploadAdapter`, leaderboard adapter)

- `POST https://minigame.zijieapi.com/mgplatform/api/apps/competition/rank/upload`,
  headers `access-token` + `content-type: application/json`; body
  `{app_id, competition_id, schedule_id, rank_status, ranks:[{open_id, nickname, player_source, rank, score}]}`
  (player_source: 1=Douyin user, 2=WeChat user; official limit QPS < 10).
- Response `{err_no, err_msg, log_id}`; 28001003 token invalid / 28001008 token expired /
  28001007 parameter error / 28001005 platform internal error. 28001008 → invalidate, refetch, and retry exactly once;
  the others are not blindly replayed (platform idempotency semantics are not documented; duplicate ranking is worse than failure).
- Implements `IPlatformLeaderboardAdapter` (Platform="douyin", wired to `PlatformLeaderboardRegistry`):
  per-item `TrySubmit` goes through a per-scope replacement-projection ledger + competition rank (1 + number of entries with strictly higher scores,
  ties share ranks 1,2,2,4, same semantics as LeaderboardStore) reported per item; per scope, participants beyond the limit are
  refused (fail-visible, Uploads/Rejections counters observable).
- **Schedule-frozen batch reporting**: the official API reports final ranks; batch feeding should use LeaderboardScope.Query
  snapshots instead (production wiring decision; the adapter provides no speculative batch API).
- `rank_status`'s official enum values are not documented: defaults to 1, adjustable as a constructor parameter, to be confirmed during joint debugging.
- When the nickname has no independent source, fall back to participantId.

### 2.4 Configuration and Credentials (never in the repo or logs)

| Key | Description |
|---|---|
| `DouyinMiniGameOptions.AppId` / `AppSecret` | Mini game credentials (used for token + code2Session), injected via configuration |
| `DouyinMiniGameOptions.ApiBaseUrl` | Only replaced in test stubs; production always uses the official domain `minigame.zijieapi.com` |
| `competition_id` / `schedule_id` / `rank_status` | Event identifiers, adapter constructor parameters (assigned by the event backend) |

### 2.5 Redesign Launch Adapter (real code2Session calls, landed 2026-10-31)

StreamerServer's streamer login verification (contracts §5 `VerifyLaunch` / `RedesignPlatformGateway`) was previously served by the fail-closed stub (`DouyinLaunchAdapter`); it is now implemented for real by `DouyinMiniGameLaunchAdapter` (`src/Chorus.Platform/DouyinMiniGameLaunchAdapter.cs`), mapped to the official code2Session:

- **Credential encoding**: `LaunchCredential` carries the client login code — a bare value is a tt.login `code`; a value prefixed with `anonymous:` is an `anonymous_code` (a local client-side encoding convention, not an official protocol guess).
- **Identity and live key**: on success OpenId = `openid` (for anonymous logins `anonymous:anonymous_openid`, the prefix separating the two namespaces); `CanonicalLiveSessionKey` derives deterministically as `douyin-live:{appId}:{identity}` (one canonical live session per streamer per app). No official live-room binding API is verified, so no room key is invented. `session_key` is sensitive and dropped inside the adapter; it never leaves it.
- **Environment variables**: `CHORUS_DOUYIN_APP_ID` / `CHORUS_DOUYIN_APP_SECRET` (both must be set; one without the other refuses to start); `CHORUS_DOUYIN_API_BASE_URL` is only replaced in test stubs; production always uses the official domain. Without credentials the original fail-closed stub stays (rejecting every launch). Credentials arrive only via the environment, never in the repo or CI.
- **Basis for ProductionReady=true**: the code2Session official contract was verified 2026-10-14 (§2.2); golden tests `DouyinMiniGameLaunchAdapterTests` (HTTP stub, 10 cases). Real-credential joint debugging (FIX-ENV-05) and the event-ingress half remain open.
- **Error codes** (stable; messages never include the credential or secret): `malformed-credential` / `app-mismatch` (rejected locally, no request sent) / `platform-rejected` (carries the official error code, e.g. 40018) / `platform-unreachable` (non-2xx HTTP or network failure, retryable) / `platform-response-invalid`.

## 3. Event Entry Implementation Contract (joint-debugging placeholder, not official)

### 3.1 Callback Address and Routing

- The Douyin-side callback address configuration points to this service: `https://<host>/ingress/douyin` (pending joint debugging: the actual configuration item name in the platform backend).
- In monolith deployment it is served by the `platform-ingress` listener face (rate limiting / request body limit / timeout,
  see `Faces:PlatformIngress` in `docs/reference/config.md` and `docs/design/process-topology.md`).

### 3.2 Signature Verification (`DouyinPlatformEventVerifier`)

| Item | Value | Description |
|---|---|---|
| Algorithm | `HMAC-SHA256(appSecret, rawBody)` output lowercase hex | **Placeholder**, to be replaced during joint debugging |
| Signature header | `X-Douyin-Signature` (configurable via `Ingress:Douyin:SignatureHeader`) | **Placeholder**, the official header name to be confirmed during joint debugging |
| Secret | `Ingress:Douyin:AppSecret` (injected via configuration, not hard-coded) | Not configured or empty → **reject everything (fail-closed)**, and log an ERROR |
| Comparison | `CryptographicOperations.FixedTimeEquals` after hex decoding | Prevents timing side channels; non-hex / missing header / empty header are all rejected |
| Platform name | Only accepts `platform == "douyin"` (case-insensitive) | Other platform names are fail-closed |

Acceptance criteria: correct signature → pass; tampered body → reject; different secret → reject; missing secret/missing header → reject (see
`DouyinPlatformVerifierTests` and `DouyinIngressEndpointTests`).

### 3.3 Event Envelope (`DouyinPlatformEventMapper`, placeholder contract)

```json
{
  "event": "comment | gift | like | <other>",
  "event_id": "Platform event unique ID (a douyin:<guid> placeholder is generated when missing)",
  "session_id": "Chorus session ID (required; returns 400 when missing)",
  "user_id": "Viewer ID (default anonymous)",
  "content": "Danmaku content (comment)",
  "gift_id": "Gift ID (gift)",
  "gift_count": 1,
  "gift_value": 10,
  "match_id": "Optional, the battle the event belongs to",
  "faction_id": "Optional, the faction the event belongs to"
}
```

| Official event (placeholder name) | `AudienceEventKind` |
|---|---|
| `comment` | `Comment` (danmaku) |
| `gift` | `Gift` (gift) |
| `like` | `Like` (like) |
| Other/missing | `Unknown` (**no crash**: ingested but reducer passthrough, callback returns 200) |

Invalid JSON or missing `session_id` → return `400 unmapped_event`, without affecting the service.

### 3.4 Deduplication

- Reuses `EventDeduplicator` (key `douyin:<event_id>`, TTL `Ingress:DedupTtlSeconds`, default 600s).
- Duplicate events return `200 {"ok":true,"duplicated":true}`.
- Deduplication is atomic (review P0-8, fixed in 78f2597): `EventDeduplicator.TryReserve` uses `ConcurrentDictionary.TryAdd` for an atomic placeholder (`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:63-95`), and delivery failure releases the placeholder via `Rollback` (`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:114`), so a platform retry is a completely new delivery rather than a misjudged duplicate.

## 4. Pending Joint Debugging Checklist (ingress half, all explicit, no speculative implementation)

- [ ] The actual entry and field name for configuring the callback address in the platform backend (requires developer qualification/login).
- [ ] The official signature algorithm and signature header name; confirm whether timestamp/nonce is included (replay protection).
- [ ] The official event type enum and payload field names (danmaku/gift/like/follow/enter).
- [ ] Push channel form: HTTP callback vs platform-side WS push (decides whether to add a push client).
- [ ] Callback retry semantics (which status codes trigger retries, retry count/interval) → affects the dedup TTL value.
- [ ] The mapping source for `session_id` (suggestion: a mapping table or rule from live room/room number to Chorus session).
- [ ] Whether an event time field (platform-side time) is provided, for out-of-order placement.
- [ ] The gift value unit and combo (merge) semantics.

## 5. Acceptance Test Locations

| Case | Test |
|---|---|
| Correct signature passes / tampered rejected / unknown type does not crash / duplicate dedup | `DouyinIngressEndpointTests` (HTTP end-to-end, TestHost) |
| Signature verification algorithm and fail-closed details | `DouyinPlatformVerifierTests` |
| Envelope mapping details | `DouyinPlatformMapperTests` |
| platform-ingress face rate limiting / request body limit / timeout | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
| Token fetch/cache/single-flight/expiration/error codes, code2Session success and error codes, identity mapping | `DouyinMiniGameClientTests` (offline fake backend) |
| Rank reporting payload shape, competition rank semantics, error codes, token expiration retry, capacity limits | `DouyinRankUploadAdapterTests` (offline fake backend) |
