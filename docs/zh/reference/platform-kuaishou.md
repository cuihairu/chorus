---
title: "平台接入参考：快手"
---


# 平台接入参考：快手（kuaishou）

> 路由：`POST /ingress/{platform}`（`platform=kuaishou`，单机与 Gateway 同构）。
> 签名校验 fail-closed；去重走 `EventDeduplicator`（msgId + TTL）。
> 本文记录 2026-10-12 的官方事实调研与当前实现契约，供联调时对照替换。

## 1. 官方事实（2026-10-12）

与抖音不同，快手的**消息加密解密与验签有官方文档内容**：

- 官方文档："消息加密解密与验签"
  `https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html`（页面标注 2023-02-08 更新）。
- 开放平台控制台需开发者登录；控制台内的具体配置项未确认（不阻塞实现）。

### 官方签名算法（已核实，非占位）

```
sha1Hex(rawBody + 消息验证Token) == 请求头 kwaisign
```

- Token 来自开发者控制台的"消息验证Token"（配置 `Ingress:Kuaishou:VerifyToken`）。
- 实现位置：`KuaishouPlatformEventVerifier.ComputeSignature`。

### 官方消息信封（已核实，非占位）

```json
{
  "encryptedMsg": "<base64 密文>",
  "msgId": "平台侧唯一消息 ID",
  "componentAppId": "组件 AppId",
  "timestamp": 1700000000000
}
```

- 密文算法（官方）：AES-256-CBC / PKCS7；Key = base64 解码的"消息加密解密 KEY"（32 字节），
  **IV = Key 的前 16 字节**（依官方文档）。
- `msgId` 是平台侧唯一消息 ID；本实现直接用作去重 EventId。
- 官方文档另载：推送失败会重试（3 次）→ 依赖去重幂等；TTL 建议覆盖重试窗口（`Ingress:DedupTtlSeconds`，默认 600s）。

### 已知风险（联调时先确认）

1. **直播事件是否用此信封未确认**：官方文档描述的是开放平台"消息推送"的通用加解密外壳；直播（弹幕/礼物/点赞）事件是否复用该信封、内层 JSON 结构是什么，文档没有给出。内层当前按占位契约（与抖音同字段）；联调时只替换 `KuaishouPlatformEventMapper.TryMap`。
2. **响应体格式未严格核实**：官方指引是收到消息后回成功（本文按 `200` 视为成功）；是否必须返回特定 JSON（如 `{"result":1,"message_id":...}`）待联调确认。
3. **直播事件推送通道形态**（HTTP 回调 vs 平台侧 WS 推送）未确认，与抖音同风险：若是平台侧 WS 推送，`/ingress/kuaishou` 仍是转发目标，接口不变。

### Launch 半侧设计说明（未实装，fail-closed 占位保留）

与抖音不同（抖音 code2Session 官方口径已于 2026-10-14 核实并实装，见 platform-douyin.md §2.2/§2.5），**快手的服务端登录/主播启动校验 API 尚无任何已核实的官方口径**（端点、鉴权方式、凭证类型、官方文档链接均缺），故不做投机实现。StreamerServer 继续注册 fail-closed 占位 stub `KuaishouLaunchAdapter`（拒绝一切登录，不校验任何凭证）。要生产化需要先补齐：官方端点与协议版本、鉴权方案、凭证类型与脱敏样本、签名测试向量（对应 FIX-ENV-06 待用户提供）；核实后按抖音同款模式实装（真实适配器 + HTTP 桩金测 + 环境变量注入凭据）。

## 2. 当前实现契约

### 2.1 回调地址与路由

- 快手侧回调地址配置指向本服务：`https://<host>/ingress/kuaishou`（待联调：控制台里实际的配置项名）。
- 单机部署由 `platform-ingress` 监听面服务（限流 / 请求体限制 / 超时，见 `docs/reference/config.md` 的 `Faces:PlatformIngress` 与 `docs/design/process-topology.md`）。

### 2.2 签名校验（`KuaishouPlatformEventVerifier`，官方算法）

| 项 | 值 | 说明 |
|---|---|---|
| 算法 | `sha1Hex(rawBody + VerifyToken)` 小写 hex | **官方**（见 §1） |
| 签名头 | `kwaisign`（经 `Ingress:Kuaishou:SignatureHeader` 配置） | 官方头名 |
| Token | `Ingress:Kuaishou:VerifyToken`（配置注入，非硬编码） | 未配置或空 → **全部拒绝（fail-closed）**，并记 ERROR 日志 |
| 比较 | `string.Equals(Ordinal)`（sha1hex 比较） | 缺头 / 空头一律拒绝 |
| 平台名 | 只接受 `platform == "kuaishou"`（大小写不敏感） | 其他平台名 fail-closed |

验收标准：正确签名 → 通过；篡改 body → 拒绝；错误 Token → 拒绝；缺 Token / 缺头 → 拒绝（见 `KuaishouPlatformVerifierTests` 与 `KuaishouIngressEndpointTests`）。

### 2.3 事件信封（`KuaishouPlatformEventMapper`）

外层是官方信封（§1）；`encryptedMsg` 用 `Ingress:Kuaishou:MsgEncryptKey` 解密（AES-256-CBC，IV=Key[0..16]）。内层 JSON 是**占位契约**（与抖音同，待联调替换）：

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
| 其他 / 缺失 | `Unknown`（**不崩溃**：已存储但 reducer 直通，回调返回 200） |

失败路径（一律返回 `400 unmapped_event` 或 `401`，不影响服务）：

- 解密失败 / 缺 `MsgEncryptKey` / 密文非 base64 / 内层 JSON 无效 → `400`；
- 缺 `msgId` → `400`（去重键缺失即拒收投递）。

### 2.4 去重

- 复用 `EventDeduplicator`（键 `kuaishou:<msgId>`，TTL `Ingress:DedupTtlSeconds`，默认 600s）。
- 重复事件返回 `200 {"ok":true,"duplicated":true}`。
- 官方重试 3 次 → TTL 必须覆盖重试窗口（600s 足够；联调时按实际间隔复核）。
- 去重是原子的（评审 P0-8，78f2597 修复）：`EventDeduplicator.TryReserve` 用 `ConcurrentDictionary.TryAdd` 做原子占位（`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:63-95`）；投递失败经 `Rollback` 释放占位（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:114`）；平台重试是一次全新投递而非误判重复。

## 3. 联调待办清单（全部显式，不做投机实现）

- [ ] 直播事件（弹幕/礼物/点赞）是否走 encryptedMsg 信封，以及内层 JSON 的官方字段名。
- [ ] 配置回调地址与消息 Token / 加密 KEY 的控制台实际入口（需开发者登录控制台）。
- [ ] 回调成功的响应严格格式（是否必须返回特定 JSON body）。
- [ ] 推送通道形态：HTTP 回调 vs 平台侧 WS 推送。
- [ ] 重试次数 / 间隔的实际取值 → 校验去重 TTL。
- [ ] `session_id` 的映射来源（直播间/房号 → Chorus 会话的映射表或规则）。
- [ ] 是否提供事件时间字段（平台侧时间），用于乱序放置。
- [ ] 礼物价值单位与连击（合并）语义。

## 4. 验收测试位置

| 用例 | 测试 |
|---|---|
| 正确签名通过 / 篡改拒绝 / 未知类型不崩溃 / 重复去重 | `KuaishouIngressEndpointTests`（HTTP 端到端，TestHost，测试内构造 AES 信封） |
| 验签算法（sha1Hex 标准）与 fail-closed 细节 | `KuaishouPlatformVerifierTests` |
| 信封解密与映射细节 | `KuaishouPlatformMapperTests` |
| platform-ingress 面限流 / 请求体限制 / 超时 | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
