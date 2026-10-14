---
title: 平台对接参考：抖音
---

# 平台对接参考：抖音（douyin）

> 路由：`POST /ingress/{platform}`（`platform=douyin`，单体与 Gateway 同构）。
> 验签 fail-closed；去重走 `EventDeduplicator`（platformEventId + TTL）。
> 本文记录 2026-10-12（受阻）与 2026-10-14（服务端 OpenAPI 核实）两轮官方口径
> 调研与当前实现契约，供联调时对照替换。

## 1. 官方口径调研结论

**2026-10-14 复核（当前口径）**：文档站正文可达（首轮被安全脚本挡住的页面这轮拿到内容）。
小游戏**服务端 OpenAPI 三项已核实并落地**（getAccessToken / code2Session / 选手排名上报，
详见 §2）；**直播观众事件推送确认不在小游戏服务端 OpenAPI 菜单内**（菜单已见全：
凭证/登录/数据缓存/二维码/其它/订阅消息/赛事×2；直播互动只有客户端 JS API
`tt.getLiveManager` + LiveManager）。因此 ingress 侧（回调地址/签名/事件字段/推送形态）
仍全部未核实，继续按 §3 联调占位契约实现，不伪造推送客户端。

**2026-10-12 首轮（受阻记录）**：

- `developer.open-douyin.com` 文档站正文为 JS 渲染 + 安全脚本（argus/secsdk），
  静态抓取拿不到内容；文档内容 API 返回 `401 {"login":false}`（需登录态）。
- 搜索引擎（Bing/DuckDuckGo/360/Baidu）被反爬拦截；GitHub API 匿名额度耗尽。
- 当时四项 ingress 细节（回调入口/签名算法/事件字段/推送通道形态）均未核实——
  该结论对 ingress 半边仍然成立，见 §3/§4。

### 已知风险（ingress 半边，联调时优先确认）

1. **推送通道形态未确认**：抖音直播事件的实时下发存在「HTTP 回调」与「开发者主动连接的 WS 推送」两种可能的官方形态。
   若官方只提供 WS 推送（开发者作为客户端连抖音），则 `/ingress/douyin` 需改为由一个推送客户端进程转发进来，
   当前 HTTP 端点保留为转发目标即可，接口不变。
2. **签名算法未确认**：当前按 HMAC-SHA256 占位，联调时若算法不同，只需替换
   `DouyinPlatformEventVerifier.ComputeSignature`（单方法）与 header 名配置。
3. **事件字段未确认**：字段抽取集中在 `DouyinPlatformEventMapper`，联调时替换该文件即可，管道不动。

## 2. 小游戏服务端 OpenAPI（2026-10-14 官方核实并落地）

三项 API 按官方文档核实并落地（`src/Chorus.Adapters.Platforms/Douyin/`），
金测 `DouyinMiniGameClientTests` / `DouyinRankUploadAdapterTests`（离线假后端，
`HttpMessageInvoker` 缝）。**无生产接线**（同 RoomAffinity 先例）：Server/Gateway
Program.cs 不注册默认实例，等真实小游戏凭据与赛事配置出现再接。

### 2.1 getAccessToken（`DouyinAccessTokenProvider`）

- `POST https://minigame.zijieapi.com/mgplatform/api/apps/v2/token`，
  JSON body `{appid, secret, grant_type:"client_credential"}`。
- 响应 `{err_no, err_tips, data:{access_token, expires_in}}`（expires_in 秒，
  例 7200）；err_no != 0 抛 InvalidOperationException（40015 appid 错 /
  40017 secret 错 / 40020 grant_type 错 / -1 系统错）。
- token 有效期 2 小时、重复获取会把旧 token 压到 5 分钟（官方建议按小时刷新）：
  实现为进程内单例缓存 + SemaphoreSlim 单飞（防并发重复获取）+ 5 分钟提前刷新余量；
  短于余量的 token 不复用（金测覆盖）。
- secret 不进错误消息（金测断言覆盖）。

### 2.2 code2Session（`DouyinMiniGameAuthClient`，登录/身份映射）

- `GET https://minigame.zijieapi.com/mgplatform/api/apps/jscode2session?appid&secret&code|anonymous_code`
  （code = tt.login 非匿名凭证、anonymous_code = 匿名凭证；至少传一个，均单次有效）。
- 响应 `{error, errcode, errmsg|message, openid, session_key, unionid, anonymous_openid}`；
  error==0 成功（40014 缺参 / 40015 appid / 40017 secret / 40018 code 错 /
  40019 匿名 code 错 / -1 系统错）。openid/unionid 仅随 code 返回，
  anonymous_openid 仅随 anonymous_code 返回。
- error==0 但无任何身份字段 → 按失败处理（fail-closed，不猜成功）。
- 平台错误返回失败结果（错误码 + 消息）不抛异常——错误码是正常业务面；
  两者都为空是契约违反，抛 ArgumentException 且不发请求。
- **session_key 敏感**：只存结果对象、不进日志、不下发客户端（官方安全要求：
  泄露可能导致小游戏被下架）。
- **身份映射**：`DouyinIdentity.ToChorusUserId(openid)` = `douyin:{openid}`，
  确定性、无状态、跨进程一致；openid 同一小游戏内稳定，跨小游戏归并需 unionid
  （上层独立决策）。

### 2.3 选手排名上报（`DouyinRankUploadAdapter`，排行榜适配）

- `POST https://minigame.zijieapi.com/mgplatform/api/apps/competition/rank/upload`，
  headers `access-token` + `content-type: application/json`；body
  `{app_id, competition_id, schedule_id, rank_status, ranks:[{open_id, nickname, player_source, rank, score}]}`
  （player_source：1=抖音用户 2=微信用户；官方限 QPS < 10）。
- 响应 `{err_no, err_msg, log_id}`；28001003 token 无效 / 28001008 token 过期 /
  28001007 参数错 / 28001005 平台内部错。28001008 → 失效重取重试恰好一次；
  其余不盲重放（平台幂等语义未载明，重复计名次比失败更糟）。
- 实现 `IPlatformLeaderboardAdapter`（Platform="douyin"，接 `PlatformLeaderboardRegistry`）：
  逐条 `TrySubmit` 走 per-scope 替换式投影台账 + 竞赛名次（1 + 分数严格更高条目数，
  并列共享名次 1,2,2,4，与 LeaderboardStore 同语义）单条上报；每 scope 参与者超上限
  拒报（fail-visible，Uploads/Rejections 计数可观测）。
- **赛段冻结批量上报**：官方 API 是最终名次上报，整批喂入应改用 LeaderboardScope.Query
  快照（生产接线决策，适配器不提供投机批量 API）。
- `rank_status` 官方枚举值未在文档载明：默认 1，构造参数可调，联调确认。
- 昵称无独立来源时回退 participantId。

### 2.4 配置与凭据（不进仓不进日志）

| 键 | 说明 |
|---|---|
| `DouyinMiniGameOptions.AppId` / `AppSecret` | 小游戏凭据（token + code2Session 用），配置注入 |
| `DouyinMiniGameOptions.ApiBaseUrl` | 仅测试桩替换，生产恒用官方域名 `minigame.zijieapi.com` |
| `competition_id` / `schedule_id` / `rank_status` | 赛事标识，适配器构造参数（赛事后台分配） |

## 3. 事件入口实现契约（联调占位，非官方）

### 3.1 回调地址与路由

- 抖音侧配置回调地址指向本服务：`https://<host>/ingress/douyin`（待联调：平台后台实际配置项名称）。
- 单体部署时由 `platform-ingress` listener face 承接（限流 / 请求体上限 / 超时，
  见 `docs/reference/config.md` 的 `Faces:PlatformIngress` 与 `docs/design/process-topology.md`）。

### 3.2 验签（`DouyinPlatformEventVerifier`）

| 项 | 值 | 说明 |
|---|---|---|
| 算法 | `HMAC-SHA256(appSecret, rawBody)` 输出小写 hex | **占位**，待联调替换 |
| 签名 header | `X-Douyin-Signature`（可配 `Ingress:Douyin:SignatureHeader`） | **占位**，待联调确认官方 header 名 |
| 密钥 | `Ingress:Douyin:AppSecret`（配置注入，不硬编码） | 未配置或为空 → **全部拒绝（fail-closed）**，并打 ERROR 日志 |
| 比对 | hex 解码后 `CryptographicOperations.FixedTimeEquals` | 防时序侧信道；非 hex / 缺 header / 空 header 一律拒绝 |
| 平台名 | 仅接受 `platform == "douyin"`（大小写不敏感） | 其他平台名 fail-closed |

验收口径：签名正确→通过；篡改 body→拒绝；换密钥→拒绝；缺密钥/缺 header→拒绝（见
`DouyinPlatformVerifierTests` 与 `DouyinIngressEndpointTests`）。

### 3.3 事件信封（`DouyinPlatformEventMapper`，占位契约）

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

### 3.4 去重

- 复用 `EventDeduplicator`（键 `douyin:<event_id>`，TTL `Ingress:DedupTtlSeconds`，默认 600s）。
- 重复事件返回 `200 {"ok":true,"duplicated":true}`。
- 去重原子化（评审 P0-8，已修复 78f2597）：`EventDeduplicator.TryReserve` 用 `ConcurrentDictionary.TryAdd` 原子占位（`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:63-95`），投递失败走 `Rollback` 释放占位（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:114`），平台重试是全新投递而非误判重复。

## 4. 待联调清单（ingress 半边，全部明示，不做猜测实现）

- [ ] 平台后台配置回调地址的实际入口与字段名（需开发者资质/登录）。
- [ ] 官方签名算法与签名 header 名；确认是否含 timestamp/nonce（重放保护）。
- [ ] 官方事件类型枚举与 payload 字段名（弹幕/礼物/点赞/关注/进场）。
- [ ] 推送通道形态：HTTP 回调 vs 平台侧 WS 推送（决定是否加推送客户端）。
- [ ] 回调重试语义（哪些状态码触发重试、重试次数/间隔）→ 影响去重 TTL 取值。
- [ ] `session_id` 的映射来源（建议：直播间/房间号 → Chorus session 的映射表或规则）。
- [ ] 事件时间字段（平台侧时间）是否提供，用于乱序归位。
- [ ] 礼物价值单位与连击（合并）语义。

## 5. 验收测试落点

| 用例 | 测试 |
|---|---|
| 签名正确通过 / 篡改拒绝 / 未知类型不崩 / 重复去重 | `DouyinIngressEndpointTests`（HTTP 端到端，TestHost） |
| 验签算法与 fail-closed 细项 | `DouyinPlatformVerifierTests` |
| 信封映射细项 | `DouyinPlatformMapperTests` |
| platform-ingress 面限流 / 请求体上限 / 超时 | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
| token 获取/缓存/单飞/失效/错误码、code2Session 成功与错误码、身份映射 | `DouyinMiniGameClientTests`（离线假后端） |
| 排名上报 payload 形状、竞赛名次语义、错误码、token 过期重试、容量上限 | `DouyinRankUploadAdapterTests`（离线假后端） |
