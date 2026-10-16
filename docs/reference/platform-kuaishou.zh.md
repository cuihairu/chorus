---
title: 平台对接参考：快手
---


<p align="center">
  <a href="./platform-kuaishou.md">English</a> | 简体中文
</p>

# 平台对接参考：快手（kuaishou）

> 路由：`POST /ingress/{platform}`（`platform=kuaishou`，单体与 Gateway 同构）。
> 验签 fail-closed；去重走 `EventDeduplicator`（msgId + TTL）。
> 本文记录 2026-10-12 的官方口径调研结果与当前实现契约，供联调时对照替换。

## 1. 官方口径调研结论（2026-10-12）

与抖音不同，快手的**消息加解密与签名验证拿到了官方文档正文**：

- 官方文档：《消息加解密和签名验证》
  `https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html`（页面标注更新 2023-02-08）。
- 开放平台控制台需开发者登录，控制台内具体配置入口未核实（不阻塞实现）。

### 官方签名算法（已核实，非占位）

```
sha1Hex(rawBody + 消息验证Token) == 请求头 kwaisign
```

- Token 来自开发者控制台的「消息验证Token」（配置项 `Ingress:Kuaishou:VerifyToken`）。
- 实现落点：`KuaishouPlatformEventVerifier.ComputeSignature`。

### 官方消息信封（已核实，非占位）

```json
{
  "encryptedMsg": "<base64 密文>",
  "msgId": "平台侧唯一消息 ID",
  "componentAppId": "组件 AppId",
  "timestamp": 1700000000000
}
```

- 密文算法（官方）：AES-256-CBC / PKCS7；Key = base64 解码后的「消息加密解密KEY」（32 字节），
  **IV = Key 前 16 字节**（官方口径如此）。
- `msgId` 为平台唯一消息 ID，本实现直接用作去重 EventId。
- 官方文档同时说明：推送失败会重试（重试 3 次）→ 依赖去重幂等，TTL 建议覆盖重试窗口
  （`Ingress:DedupTtlSeconds`，默认 600s）。

### 已知风险（联调时优先确认）

1. **直播事件是否走此信封未确认**：官方文档描述的是开放平台「消息推送」的通用加解密壳；
   直播（弹幕/礼物/点赞）事件是否复用该信封、内层 JSON 结构是什么，文档未给出。
   当前内层按占位契约（同抖音字段），联调时只替换 `KuaishouPlatformEventMapper.TryMap` 一处。
2. **应答体格式未严格核实**：官方建议收到消息后应答成功（本文按 `200` 处理即视为成功）；
   是否必须返回特定 JSON（如 `{"result":1,"message_id":...}`）待联调确认。
3. **直播事件推送通道形态**（HTTP 回调 vs 平台侧 WS 推送）未确认，与抖音同风险：
   若为平台侧 WS 推送，`/ingress/kuaishou` 保留为转发目标，接口不变。

## 2. 当前实现契约

### 2.1 回调地址与路由

- 快手侧配置回调地址指向本服务：`https://<host>/ingress/kuaishou`（待联调：控制台实际配置项名称）。
- 单体部署时由 `platform-ingress` listener face 承接（限流 / 请求体上限 / 超时，
  见 `docs/reference/config.md` 的 `Faces:PlatformIngress` 与 `docs/design/process-topology.md`）。

### 2.2 验签（`KuaishouPlatformEventVerifier`，官方算法）

| 项 | 值 | 说明 |
|---|---|---|
| 算法 | `sha1Hex(rawBody + VerifyToken)` 小写 hex | **官方**（见 §1） |
| 签名 header | `kwaisign`（可配 `Ingress:Kuaishou:SignatureHeader`） | 官方 header 名 |
| Token | `Ingress:Kuaishou:VerifyToken`（配置注入，不硬编码） | 未配置或为空 → **全部拒绝（fail-closed）**，并打 ERROR 日志 |
| 比对 | `string.Equals(Ordinal)`（sha1hex 对比） | 缺 header / 空 header 一律拒绝 |
| 平台名 | 仅接受 `platform == "kuaishou"`（大小写不敏感） | 其他平台名 fail-closed |

验收口径：签名正确→通过；篡改 body→拒绝；换 Token→拒绝；缺 Token/缺 header→拒绝（见
`KuaishouPlatformVerifierTests` 与 `KuaishouIngressEndpointTests`）。

### 2.3 事件信封（`KuaishouPlatformEventMapper`）

外层为官方信封（§1），`encryptedMsg` 用 `Ingress:Kuaishou:MsgEncryptKey` 解密（AES-256-CBC，IV=Key[0..16]）。
内层 JSON 为**占位契约**（同抖音，待联调替换）：

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

| 占位事件名 | `AudienceEventKind` |
|---|---|
| `comment` | `Comment`（弹幕） |
| `gift` | `Gift`（礼物） |
| `like` | `Like`（点赞） |
| 其他/缺失 | `Unknown`（**不崩**：入库但被 reducer passthrough，回调返回 200） |

失败路径（均返回 `400 unmapped_event` 或 `401`，不影响服务）：

- 解密失败 / 缺 `MsgEncryptKey` / 密文非 base64 / 内层非法 JSON → `400`；
- 缺 `msgId` → `400`（去重键缺失时拒绝投递）。

### 2.4 去重

- 复用 `EventDeduplicator`（键 `kuaishou:<msgId>`，TTL `Ingress:DedupTtlSeconds`，默认 600s）。
- 重复事件返回 `200 {"ok":true,"duplicated":true}`。
- 官方重试 3 次 → TTL 需覆盖重试窗口（600s 足够，联调时按实际间隔核对）。
- 去重原子化（评审 P0-8，已修复 78f2597）：`EventDeduplicator.TryReserve` 用 `ConcurrentDictionary.TryAdd` 原子占位（`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:63-95`），投递失败走 `Rollback` 释放占位（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:114`），平台重试是全新投递而非误判重复。

## 3. 待联调清单（全部明示，不做猜测实现）

- [ ] 直播事件（弹幕/礼物/点赞）是否走 encryptedMsg 信封，内层 JSON 官方字段名。
- [ ] 平台后台配置回调地址与消息 Token / 加密 KEY 的实际入口（需开发者登录控制台）。
- [ ] 回调成功应答的严格格式（是否要求特定 JSON body）。
- [ ] 推送通道形态：HTTP 回调 vs 平台侧 WS 推送。
- [ ] 重试次数/间隔的现行值 → 核对去重 TTL。
- [ ] `session_id` 的映射来源（直播间/房间号 → Chorus session 的映射表或规则）。
- [ ] 事件时间字段（平台侧时间）是否提供，用于乱序归位。
- [ ] 礼物价值单位与连击（合并）语义。

## 4. 验收测试落点

| 用例 | 测试 |
|---|---|
| 签名正确通过 / 篡改拒绝 / 未知类型不崩 / 重复去重 | `KuaishouIngressEndpointTests`（HTTP 端到端，TestHost，AES 信封在测试内构造） |
| 验签算法（sha1Hex 口径）与 fail-closed 细项 | `KuaishouPlatformVerifierTests` |
| 信封解密与映射细项 | `KuaishouPlatformMapperTests` |
| platform-ingress 面限流 / 请求体上限 / 超时 | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
