---
title: 平台对接参考：抖音
---

# 平台对接参考：抖音（douyin）

> 路由：`POST /ingress/{platform}`（`platform=douyin`，单体与 Gateway 同构）。
> 验签 fail-closed；去重走 `EventDeduplicator`（platformEventId + TTL）。
> 本文记录 2026-10-12 的官方口径调研结果与当前实现契约，供联调时对照替换。

## 1. 官方口径调研结论（2026-10-12）

调研尝试（均受阻，未获得正文）：

- `developer.open-douyin.com`（抖音开放平台文档站）：`/docs/page/live` 可达（HTTP 200），
  但正文为 JS 渲染 + 安全脚本（argus/secsdk），静态抓取拿不到内容；
  文档内容 API（`/api/docs/v1/doc/get_doc_content`）返回 `401 {"login":false}`，需要登录态。
- 搜索引擎（Bing/DuckDuckGo/360/Baidu）：均被反爬拦截或返回无关结果；
  GitHub API 匿名额度耗尽，代码检索不可用。
- **结论：回调地址配置入口、签名校验算法、事件类型与字段、事件推送通道形态（HTTP 回调 vs 平台侧 WS 推送）
  四项官方细节均未核实。** 全部以下文「联调占位契约」实现，明示非官方。

### 已知风险（联调时优先确认）

1. **推送通道形态未确认**：抖音直播事件的实时下发存在「HTTP 回调」与「开发者主动连接的 WS 推送」两种可能的官方形态。
   若官方只提供 WS 推送（开发者作为客户端连抖音），则 `/ingress/douyin` 需改为由一个推送客户端进程转发进来，
   当前 HTTP 端点保留为转发目标即可，接口不变。
2. **签名算法未确认**：当前按 HMAC-SHA256 占位，联调时若算法不同，只需替换
   `DouyinPlatformEventVerifier.ComputeSignature`（单方法）与 header 名配置。
3. **事件字段未确认**：字段抽取集中在 `DouyinPlatformEventMapper`，联调时替换该文件即可，管道不动。

## 2. 当前实现契约（联调占位，非官方）

### 2.1 回调地址与路由

- 抖音侧配置回调地址指向本服务：`https://<host>/ingress/douyin`（待联调：平台后台实际配置项名称）。
- 单体部署时由 `platform-ingress` listener face 承接（限流 / 请求体上限 / 超时，
  见 `docs/reference/config.md` 的 `Faces:PlatformIngress` 与 `docs/design/process-topology.md`）。

### 2.2 验签（`DouyinPlatformEventVerifier`）

| 项 | 值 | 说明 |
|---|---|---|
| 算法 | `HMAC-SHA256(appSecret, rawBody)` 输出小写 hex | **占位**，待联调替换 |
| 签名 header | `X-Douyin-Signature`（可配 `Ingress:Douyin:SignatureHeader`） | **占位**，待联调确认官方 header 名 |
| 密钥 | `Ingress:Douyin:AppSecret`（配置注入，不硬编码） | 未配置或为空 → **全部拒绝（fail-closed）**，并打 ERROR 日志 |
| 比对 | hex 解码后 `CryptographicOperations.FixedTimeEquals` | 防时序侧信道；非 hex / 缺 header / 空 header 一律拒绝 |
| 平台名 | 仅接受 `platform == "douyin"`（大小写不敏感） | 其他平台名 fail-closed |

验收口径：签名正确→通过；篡改 body→拒绝；换密钥→拒绝；缺密钥/缺 header→拒绝（见
`DouyinPlatformVerifierTests` 与 `DouyinIngressEndpointTests`）。

### 2.3 事件信封（`DouyinPlatformEventMapper`，占位契约）

```json
{
  "event": "comment | gift | like | <其他>",
  "event_id": "平台事件唯一 ID（缺失时生成 douyin:<guid> 占位）",
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

| 官方事件（占位名） | `AudienceEventKind` |
|---|---|
| `comment` | `Comment`（弹幕） |
| `gift` | `Gift`（礼物） |
| `like` | `Like`（点赞） |
| 其他/缺失 | `Unknown`（**不崩**：入库但被 reducer passthrough，回调返回 200） |

非法 JSON 或缺 `session_id` → 返回 `400 unmapped_event`，不影响服务。

### 2.4 去重

- 复用 `EventDeduplicator`（键 `douyin:<event_id>`，TTL `Ingress:DedupTtlSeconds`，默认 600s）。
- 重复事件返回 `200 {"ok":true,"duplicated":true}`。
- 已知限制（评审 P0-8）：当前 `TryMark` 非原子、发布失败会被记为重复；在阶段一④统一原子化与四态投递。

## 3. 待联调清单（全部明示，不做猜测实现）

- [ ] 平台后台配置回调地址的实际入口与字段名（需开发者资质/登录）。
- [ ] 官方签名算法与签名 header 名；确认是否含 timestamp/nonce（重放保护）。
- [ ] 官方事件类型枚举与 payload 字段名（弹幕/礼物/点赞/关注/进场）。
- [ ] 推送通道形态：HTTP 回调 vs 平台侧 WS 推送（决定是否加推送客户端）。
- [ ] 回调重试语义（哪些状态码触发重试、重试次数/间隔）→ 影响去重 TTL 取值。
- [ ] `session_id` 的映射来源（建议：直播间/房间号 → Chorus session 的映射表或规则）。
- [ ] 事件时间字段（平台侧时间）是否提供，用于乱序归位。
- [ ] 礼物价值单位与连击（合并）语义。

## 4. 验收测试落点

| 用例 | 测试 |
|---|---|
| 签名正确通过 / 篡改拒绝 / 未知类型不崩 / 重复去重 | `DouyinIngressEndpointTests`（HTTP 端到端，TestHost） |
| 验签算法与 fail-closed 细项 | `DouyinPlatformVerifierTests` |
| 信封映射细项 | `DouyinPlatformMapperTests` |
| platform-ingress 面限流 / 请求体上限 / 超时 | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
