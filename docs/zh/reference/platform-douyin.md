---
title: "平台接入参考：抖音"
---


# 平台接入参考：抖音

> 路由：`POST /ingress/{platform}`（`platform=douyin`，单机与 Gateway 同构）。
> 签名校验 fail-closed；去重走 `EventDeduplicator`（platformEventId + TTL）。
> 本文记录两轮官方事实调研与当前实现契约——2026-10-12（受阻）与 2026-10-14（服务端 OpenAPI 已核实）——供联调时交叉核对替换。

## 1. 官方事实调研结论

**2026-10-14 复核（当前状态）**：文档站点正文可达（首轮被安全脚本挡住的页面本轮拿到内容）。
小游戏的**三个服务端 OpenAPI 已核实并落地**（getAccessToken / code2Session / 玩家排行上报，
见 §2）；**直播间观众事件推送确认不在小游戏服务端 OpenAPI 菜单里**（菜单已全部看到：
凭证/登录/数据缓存/二维码/其他/订阅消息/事件×2；直播互动只有客户端 JS API
`tt.getLiveManager` + LiveManager）。因此入口侧（回调地址/签名/事件字段/推送形态）
仍完全未核实，实现继续按 §3 联调占位契约，不虚构推送客户端。

**2026-10-12 首轮（受阻记录）**：

- `developer.open-douyin.com` 文档站点正文为 JS 渲染 + 安全脚本（argus/secsdk），
  静态抓取拿不到内容；文档内容 API 返回 `401 {"login":false}`（需登录态）。
- 搜索引擎（Bing/DuckDuckGo/360/Baidu）被反爬拦截；匿名 GitHub API 配额耗尽。
- 当时四个入口细节（回调入口/签名算法/事件字段/推送通道形态）全部未核实——
  该结论对入口半侧仍然成立，见 §3/§4。

### 已知风险（入口半侧，联调时先确认）

1. **推送通道形态未确认**：抖音直播事件实时下发有两种官方可能形态："HTTP 回调"与"开发者主动连入的 WS 推送"。
   若官方只提供 WS 推送（开发者作为客户端连抖音），则 `/ingress/douyin` 需要一个推送客户端进程转发进来；
   当前 HTTP 端点可保留为转发目标，接口不变。
2. **签名算法未确认**：当前为 HMAC-SHA256 占位；若联调时算法不同，只需替换
   `DouyinPlatformEventVerifier.ComputeSignature`（单方法）与请求头名配置。
3. **事件字段未确认**：字段提取集中在 `DouyinPlatformEventMapper`；联调时替换该文件；管线不动。

## 2. 小游戏服务端 OpenAPI（2026-10-14 官方核实并落地）

三个 API 按官方文档核实并落地（`src/Chorus.Adapters.Platforms/Douyin/`），
含金标测试 `DouyinMiniGameClientTests` / `DouyinRankUploadAdapterTests`（离线假后端，
`HttpMessageInvoker` 缝）。**无生产接线**（同 RoomAffinity 先例）：Server/Gateway
Program.cs 不注册默认实例；接线等真实小游戏凭证与事件配置。

### 2.1 getAccessToken（`DouyinAccessTokenProvider`）

- `POST https://minigame.zijieapi.com/mgplatform/api/apps/v2/token`，
  JSON body `{appid, secret, grant_type:"client_credential"}`。
- 响应 `{err_no, err_tips, data:{access_token, expires_in}}`（expires_in 单位秒，
  如 7200）；err_no != 0 抛 InvalidOperationException（40015 错误 appid /
  40017 错误 secret / 40020 错误 grant_type / -1 系统错误）。
- token 有效期 2 小时；重复获取把旧 token 压缩到 5 分钟（官方建议每小时刷新）：
  实现为进程内单例缓存 + SemaphoreSlim 单飞（防并发重复获取）+ 5 分钟提前刷新余量；
  短于余量的 token 不复用（金标测试覆盖）。
- secret 不进错误消息（金标测试断言）。

### 2.2 code2Session（`DouyinMiniGameAuthClient`，登录/身份映射）

- `GET https://minigame.zijieapi.com/mgplatform/api/apps/jscode2session?appid&secret&code|anonymous_code`
  （code = tt.login 非匿名凭证，anonymous_code = 匿名凭证；至少传一个；两者都只能一次性有效）。
- 响应 `{error, errcode, errmsg|message, openid, session_key, unionid, anonymous_openid}`；
  error==0 表示成功（40014 缺参数 / 40015 appid / 40017 secret / 40018 错误 code /
  40019 错误 anonymous code / -1 系统错误）。openid/unionid 只随 code 返回，
  anonymous_openid 只随 anonymous_code 返回。
- error==0 但完全没有身份字段 → 按失败处理（fail-closed，不猜成功）。
- 平台错误返回失败结果（错误码 + 消息）而不抛——错误码是正常业务面；
  两者都空是契约违例；抛 ArgumentException 且不发请求。
- **session_key 是敏感数据**：只存结果对象，从不进日志、从不发给客户端（官方安全要求：
  泄露可能导致小游戏下架）。
- **身份映射**：`DouyinIdentity.ToChorusUserId(openid)` = `douyin:{openid}`，
  确定性、无状态、跨进程一致；openid 在同一小游戏内稳定，跨小游戏合并需要 unionid
  （由上层独立决定）。

### 2.3 玩家排行上报（`DouyinRankUploadAdapter`，排行榜适配器）

- `POST https://minigame.zijieapi.com/mgplatform/api/apps/competition/rank/upload`，
  请求头 `access-token` + `content-type: application/json`；body
  `{app_id, competition_id, schedule_id, rank_status, ranks:[{open_id, nickname, player_source, rank, score}]}`
  （player_source：1=抖音用户，2=微信用户；官方限 QPS < 10）。
- 响应 `{err_no, err_msg, log_id}`；28001003 token 无效 / 28001008 token 过期 /
  28001007 参数错误 / 28001005 平台内部错误。28001008 → 失效、重取、恰好重试一次；
  其余不盲目重放（平台幂等语义未文档化；重复上报比失败更糟）。
- 实现 `IPlatformLeaderboardAdapter`（Platform="douyin"，接到 `PlatformLeaderboardRegistry`）：
  每项 `TrySubmit` 走按 scope 的替换投影账本 + 比赛名次（1 + 严格更高分的条目数，
  并列共享 1,2,2,4，语义同 LeaderboardStore）逐项上报；每 scope 超限参与者
  拒绝（fail-visible，Uploads/Rejections 计数可观测）。
- **赛程冻结批量上报**：官方 API 上报最终名次；批量喂料应改用 LeaderboardScope.Query
  快照（生产接线决策；适配器不提供投机性批量 API）。
- `rank_status` 官方枚举值未文档化：默认 1，可作构造函数参数调整，联调时确认。
- 昵称无独立来源时回退 participantId。

### 2.4 配置与凭证（永不进仓库或日志）

| 键 | 描述 |
|---|---|
| `DouyinMiniGameOptions.AppId` / `AppSecret` | 小游戏凭证（用于 token + code2Session），经配置注入 |
| `DouyinMiniGameOptions.ApiBaseUrl` | 仅测试桩替换；生产恒用官方域名 `minigame.zijieapi.com` |
| `competition_id` / `schedule_id` / `rank_status` | 赛事标识，适配器构造函数参数（由赛事后端分配） |

## 3. 事件入口实现契约（联调占位，非官方）

### 3.1 回调地址与路由

- 抖音侧回调地址配置指向本服务：`https://<host>/ingress/douyin`（待联调：平台后端中实际配置项名）。
- 单机部署由 `platform-ingress` 监听面服务（限流 / 请求体限制 / 超时，
  见 `Faces:PlatformIngress` 于 `docs/reference/config.md` 与 `docs/design/process-topology.md`）。

### 3.2 签名校验（`DouyinPlatformEventVerifier`）

| 项 | 值 | 描述 |
|---|---|---|
| 算法 | `HMAC-SHA256(appSecret, rawBody)` 输出小写 hex | **占位**，联调时替换 |
| 签名头 | `X-Douyin-Signature`（经 `Ingress:Douyin:SignatureHeader` 配置） | **占位**，官方头名联调时确认 |
| 密钥 | `Ingress:Douyin:AppSecret`（经配置注入，非硬编码） | 未配置或空 → **全部拒绝（fail-closed）**，并记 ERROR |
| 比较 | hex 解码后 `CryptographicOperations.FixedTimeEquals` | 防时序侧信道；非 hex / 缺头 / 空头都拒绝 |
| 平台名 | 只接受 `platform == "douyin"`（大小写不敏感） | 其他平台名 fail-closed |

验收标准：正确签名 → 通过；篡改 body → 拒绝；不同密钥 → 拒绝；缺密钥/缺头 → 拒绝（见
`DouyinPlatformVerifierTests` 与 `DouyinIngressEndpointTests`）。

### 3.3 事件信封（`DouyinPlatformEventMapper`，占位契约）

```json
{
  "event": "comment | gift | like | <other>",
  "event_id": "平台事件唯一 ID（缺失时生成 douyin:<guid> 占位）",
  "session_id": "Chorus 会话 ID（必填；缺失返回 400）",
  "user_id": "观众 ID（默认 anonymous）",
  "content": "弹幕内容（comment）",
  "gift_id": "礼物 ID（gift）",
  "gift_count": 1,
  "gift_value": 10,
  "match_id": "可选，事件所属的比赛",
  "faction_id": "可选，事件所属的阵营"
}
```

| 官方事件（占位名） | `AudienceEventKind` |
|---|---|
| `comment` | `Comment`（弹幕） |
| `gift` | `Gift`（礼物） |
| `like` | `Like`（点赞） |
| 其他/缺失 | `Unknown`（**不崩溃**：摄取但 reducer 直通，回调返回 200） |

无效 JSON 或缺 `session_id` → 返回 `400 unmapped_event`，不影响服务。

### 3.4 去重

- 复用 `EventDeduplicator`（键 `douyin:<event_id>`，TTL `Ingress:DedupTtlSeconds`，默认 600s）。
- 重复事件返回 `200 {"ok":true,"duplicated":true}`。
- 去重是原子的（评审 P0-8，78f2597 修复）：`EventDeduplicator.TryReserve` 用 `ConcurrentDictionary.TryAdd` 做原子占位（`src/Chorus.Adapters.Platforms/EventDeduplicator.cs:63-95`），投递失败经 `Rollback` 释放占位（`src/Chorus.Gateway.Core/Faces/PlatformIngressFace.cs:114`），因此平台重试是一次全新投递而非误判重复。

## 4. 待联调清单（入口半侧，全部显式，不做投机实现）

- [ ] 平台后端配置回调地址的实际入口与字段名（需开发者资质/登录）。
- [ ] 官方签名算法与签名头名；确认是否带时间戳/nonce（重放防护）。
- [ ] 官方事件类型枚举与载荷字段名（弹幕/礼物/点赞/关注/进场）。
- [ ] 推送通道形态：HTTP 回调 vs 平台侧 WS 推送（决定是否加推送客户端）。
- [ ] 回调重试语义（哪些状态码触发重试、重试次数/间隔）→ 影响去重 TTL 取值。
- [ ] `session_id` 的映射来源（建议：直播房间/房号到 Chorus 会话的映射表或规则）。
- [ ] 是否提供事件时间字段（平台侧时间），用于乱序放置。
- [ ] 礼物价值单位与连击（合并）语义。

## 5. 验收测试位置

| 用例 | 测试 |
|---|---|
| 正确签名通过 / 篡改拒绝 / 未知类型不崩溃 / 重复去重 | `DouyinIngressEndpointTests`（HTTP 端到端，TestHost） |
| 签名校验算法与 fail-closed 细节 | `DouyinPlatformVerifierTests` |
| 信封映射细节 | `DouyinPlatformMapperTests` |
| platform-ingress 面限流 / 请求体限制 / 超时 | `IngressFaceGuardTests` + `IngressFaceEndpointTests` |
| token 获取/缓存/单飞/过期/错误码、code2Session 成功与错误码、身份映射 | `DouyinMiniGameClientTests`（离线假后端） |
| 排行上报载荷形态、比赛名次语义、错误码、token 过期重试、容量限制 | `DouyinRankUploadAdapterTests`（离线假后端） |
