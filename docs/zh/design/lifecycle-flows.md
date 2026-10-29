# 生命周期流程图

以下展示已确认的目标生命周期，不代表全部流程已实现。玩家视角指通过直播平台互动的普通观众；直接连接游戏的嘉宾／玩家需要另行定义认证会话流程。

## 主播客户端启动生命周期

![主播客户端启动生命周期](/images/flow-launch-zh.svg)

[SVG](/images/flow-launch-zh.svg)

平台依据于 2026-10-29 复核：[抖音获取直播信息](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/server/live-room-scope/audience-play/get-live-info)明确伴侣／云启动传入 token，再交由玩法服务端兑换；[快手玩法技术基础能力](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/localdevelopment.html)明确客户端启动后由服务端绑定，绑定后才推送互动数据。该依据确认凭据与绑定流程，不代表两平台具有相同的启动器 UI、安装方式或可执行文件参数格式。

异常分支：凭据无效／过期不得返回 LoginReady，日志不记录秘密；重复拉起应恢复或接管既有会话和对局，不能另建房；重连使用 Chorus 凭据。平台绑定可能在匹配前就启动回调，未开局事件必须有局外处置。关闭游戏客户端、关播和网络断开分别处理。

## 一笔礼物消息的生命周期

![一笔礼物消息的生命周期](/images/flow-gift-zh.svg)

[SVG](/images/flow-gift-zh.svg)

异常分支：伪造签名拒绝；无有效路由按已声明策略暂存或明确拒绝；StaleOwner 只刷新同一房间的 owner；投递结果未知保持原 EventId 重试；重复事件返回原回执。迟到礼物沿用固定局归属并完成明确终态处置。只有具有已验证稳定标识的礼物能力才声明可靠去重；玩法补偿不等于平台支付退款。

## 一次点赞消息的生命周期

![一次点赞消息的生命周期](/images/flow-like-zh.svg)

[SVG](/images/flow-like-zh.svg)

点赞数量取决于平台能力：平台可能不逐次回调，也可能延迟或批量上报。分别记录已接受、合并、已应用计数和明确配置的 BestEffort 丢弃量。不能伪造事件 ID，也不能用相同计数或 payload 哈希误去重。持久输入不能在过载时静默降级为可丢输入。

## 房间全生命周期

本图把创建房间、注册路由、安装确认、开始战斗、持续状态刷新、关闭路由和销毁逐步展开。待开战 RoomActor 必须在路由确认屏障完成后才开始 Tick。此为目标契约；当前 PrepareRoom 中提前激活并启动 Tick 的实现需要迁移。

![房间全生命周期](/images/lifecycle-room-zh.svg)

[SVG](/images/lifecycle-room-zh.svg)

## 主播生命周期

主播实体独立于连接和单局房间。

![主播生命周期](/images/lifecycle-streamer-zh.svg)

打开完整 SVG：[SVG](/images/lifecycle-streamer-zh.svg)

## 玩家／观众生命周期

基线：观众通过直播平台互动，无需连接或登录 Chorus。

![玩家／观众生命周期](/images/lifecycle-player-zh.svg)

打开完整 SVG：[SVG](/images/lifecycle-player-zh.svg)

## 直播平台对接生命周期

平台回调、局结束及解绑按对应适配器能力契约执行。

![直播平台对接生命周期](/images/lifecycle-platform-zh.svg)

打开完整 SVG：[SVG](/images/lifecycle-platform-zh.svg)

房间结束的确认顺序：冻结结果 → 关闭投递并确认全部有效授权 Gateway → 授权平台调用 → 核实平台结果并完成结算 → 最终释放与销毁。平台失败或结果未知时阻止下一局；普通换局不得擅自解绑整场直播。

详见[架构及结束顺序契约](/architecture/architecture)。

[GameServer 当前线程与消息模型](/zh/design/gameserver-threading-current)独立记录现有实现，与本页目标生命周期区分。
