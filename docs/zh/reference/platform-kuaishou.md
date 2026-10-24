---
title: "Platform Integration Reference: Kuaishou"
---


# Platform Integration Reference: Kuaishou (kuaishou)

> Route: `POST /ingress/{platform}` (`platform=kuaishou`, same shape in single-process and Gateway).
> Signature verification is fail-closed; deduplication goes through `EventDeduplicator` (msgId + TTL).
> This document records the official findings research of 2026-10-12 and the current implementation contract, for reference and replacement during joint debugging.

## 1. Official Findings (2026-10-12)

Unlike Douyin, Kuaishou's **message encryption/decryption and signature verification have official documentation content**:

- Official documentation: "Message Encryption/Decryption and Signature Verification"
  `https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html` (page marked updated 2023-02-08).
- The open platform console requires developer login; the specific configuration entries inside the console are not confirmed (does not block implementation).

### Official Signature Algorithm (Verified, Not a Placeholder)

```
sha1Hex(rawBody + 消息验证Token) == 请求头 kwaisign
```

- Token comes from the "Message Verification Token" in the developer console (config `Ingress:Kuaishou:VerifyToken`).
- Implementation location: `KuaishouPlatformEventVerifier.ComputeSignature`.

### Official Message Envelope (Verified, Not a Placeholder)

```json
{
  "encryptedMsg": "<base64 密文>",
  "msgId": "平台侧唯一消息 ID",
  "componentAppId": "组件 AppId",
  "timestamp": 1700000000000
}
```

- Ciphertext algorithm (official): AES-256-CBC / PKCS7; Key = base64-decoded "Message Encryption/Decryption KEY" (32 bytes),
  **IV = first 16 bytes of Key** (per official documentation).
- `msgId` is the platform-unique message ID; this implementation uses it directly as the deduplication EventId.
- The official documentation also states: failed pushes are retried (3 retries) → relies on deduplication idempotency; the TTL is recommended to cover the retry window (`Ingress:DedupTtlSeconds`, default 600s).

### Known Risks (Confirm First During Joint Debugging)

1. **Whether live events use this envelope is unconfirmed**: the official documentation describes the general encryption/decryption shell for the open platform's "message push"; whether live-streaming (danmaku/gift/like) events reuse this envelope and what the inner JSON structure is, is not given in the documentation. The inner layer currently follows the placeholder contract (same fields as Douyin); during joint debugging only replace `KuaishouPlatformEventMapper.TryMap`.
2. **Response body format not strictly verified**: the official guidance is to respond success after receiving a message (this document treats `200` as success); whether a specific JSON must be returned (e.g. `{"result":1,"message_id":...}`) is pending joint debugging confirmation.
3. **Live event push channel form** (HTTP callback vs. platform-side WS push) is unconfirmed, same risk as Douyin: if it is platform-side WS push, `/ingress/kuaishou` remains the forwarding target, and the interface is unchanged.

## 2. Current Implementation Contract

### 2.1 Callback Address and Route

- Configure the callback address on the Kuaishou side to point to this service: `https://<host>/ingress/kuaishou` (pending joint debugging: the actual configuration entry name in the console).
- In single-process deployment it is handled by the `platform-ingress` listener face (rate limiting / request body limit / timeout, see `Faces:PlatformIngress` in `docs/reference/config.md` and `docs/design/process-topology.md`).

### 2.2 Signature Verification (`KuaishouPlatformEventVerifier`, Official Algorithm)

| Item | Value | Notes |
|---|---|---|
| Algorithm | `sha1Hex(rawBody + VerifyToken)` lowercase hex | **Official** (see §1) |
| Signature header | `kwaisign` (configurable via `Ingress:Kuaishou:SignatureHeader`) | Official header name |
| Token | `Ingress:Kuaishou:VerifyToken` (config-injected, not hardcoded) | Not configured or empty → **reject all (fail-closed)**, and write ERROR log |
| Comparison | `string.Equals(Ordinal)` (sha1hex comparison) | Missing header / empty header always rejected |
| Platform name | Only accepts `platform == "kuaishou"` (case-insensitive) | Other platform names fail-closed |

Acceptance criteria: correct signature → pass; tampered body → reject; wrong Token → reject; missing Token / missing header → reject (see `KuaishouPlatformVerifierTests` and `KuaishouIngressEndpointTests`).

### 2.3 Event Envelope (`KuaishouPlatformEventMapper`)

The outer layer is the official envelope (§1); `encryptedMsg` is decrypted with `Ingress:Kuaishou:MsgEncryptKey` (AES-256-CBC, IV=Key[0..16]). The inner JSON is a **placeholder contract** (same as Douyin, pending replacement during joint debugging):

```json
{
  "event": "comment | gift | like | <其他>",
  "session_id": "Chorus 会话 ID（必填，缺失返回 400）",
  "user_id": "观众 ID（默认 anonymous）",
  "content": "弹幕内容（comment）",
  "gift_id": "礼物 ID（gift）",
  "gift_count": 1,
  "gift_value": 10,
  "match_id": "可选，事件归属对战",
  "faction_id": "可选，事件归属阵营"
}
```

| Placeholder Event Name | `AudienceEventKind` |
|---|---|
| `comment` | `Comment` (danmaku) |
| `gift` | `Gift` (gift) |
| `like` | `Like` (like) |
| Other / missing | `Unknown` (**does not crash**: stored but passthrough in the reducer, callback returns 200) |

Failure paths (all return `400 unmapped_event` or `401`, do not affect the service):

- Decryption failure / missing `MsgEncryptKey` / non-base64 ciphertext / invalid inner JSON → `400`;
- Missing `msgId` → `400` (reject delivery when the deduplication key is missing).

### 2.4 Deduplication

- Reuses `EventDeduplicator` (key `kuaishou:<msgId>`, TTL `Ingress:DedupTtlSeconds`, default 600s).
- Duplicate events return `200 {"ok":true,"duplicated":true}`.
- Official retry is 3 times → TTL must cover the retry window (600s is sufficient; verify against actual interval during joint debugging).
- Deduplication is atomic (review P0-8, fixed in 78f2597): `EventDeduplicator.TryReserve` uses `ConcurrentDictionary.TryAdd` for atomic reservation (`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:63-95`); delivery failure goes through `Rollback` to release the reservation (`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:114`); platform retries are fresh deliveries, not misjudged duplicates.

## 3. Joint Debugging Pending List (All Explicit, No Speculative Implementation)

- [ ] Whether live events (danmaku/gift/like) go through the encryptedMsg envelope, and the official field names of the inner JSON.
- [ ] The actual console entries for configuring the callback address and message Token / encryption KEY (requires developer login to the console).
- [ ] Strict format of the successful callback response (whether a specific JSON body is required).
- [ ] Push channel form: HTTP callback vs. platform-side WS push.
- [ ] Current values of retry count / interval → verify deduplication TTL.
- [ ] Mapping source for `session_id` (mapping table or rules from live room / room number → Chorus session).
- [ ] Whether the event time field (platform-side time) is provided, for out-of-order placement.
- [ ] Gift value units and combo (merging) semantics.

## 4. Acceptance Test Locations

| Case | Test |
|---|---|
| Correct signature passes / tampered rejected / unknown type does not crash / duplicate deduplication | `KuaishouIngressEndpointTests` (HTTP end-to-end, TestHost, AES envelope constructed inside the test) |
| Signature verification algorithm (sha1Hex standard) and fail-closed details | `KuaishouPlatformVerifierTests` |
| Envelope decryption and mapping details | `KuaishouPlatformMapperTests` |
| platform-ingress face rate limiting / request body limit / timeout | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
