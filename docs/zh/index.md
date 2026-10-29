---
layout: home

hero:
  name: Chorus
  text: 跨机多房间实时协作服务端
  tagline: 基于 WebSocket 的房间编排、状态同步与故障切换
  actions:
    - theme: brand
      text: 快速开始
      link: /architecture
    - theme: alt
      text: GitHub
      link: https://github.com/cuihairu/chorus

features:
  - title: 多房间编排
    details: 支持跨机器的房间分配、容量保护与生命周期管理。
  - title: 状态同步
    details: 房间内状态变更实时广播，支持顺序与一致性校验。
  - title: 故障切换
    details: 节点失联时自动迁移房间，最小化中断。
---

## 进程架构与房间管理

七类进程角色；一个 GameServer 管理多个房间，每个 RoomActor 串行推进自己的 Tick。图示为重设计架构，网关集成及生产验收仍在推进。

PlatformGateway 根据直播挂载、对局归属及所有权目录，将事件直接推送到目标 GameServer，由 RoomRuntime 投递给对应 RoomActor。GameServer 通过 DataServer 完成持久化与恢复。此为用户确认的目标链路，现有拉取实现的迁移见 TODO §22.9。

StreamerGateway 直接连接 GameServer：上行战斗输入，下行已提交帧与快照。StreamerServer 处理登录和个人业务，不中转战斗帧；ControlServer 向 StreamerGateway 提供房间 owner endpoint 与 epoch 路由。图示为目标关系，现有实现差异见配套文档。

![Chorus 七类进程架构，以及 GameServer 内的 RoomRuntime 和多个 RoomActor](/images/architecture-zh.svg)

[打开完整 SVG](/images/architecture-zh.svg) · [架构设计与实现边界](/architecture/architecture)

## 生命周期流程图

查看[主播、玩家／观众和直播平台三个视角的生命周期](/zh/design/lifecycle-flows)，包含重连、事件接受、PlatformGateway 停止投递确认及房间销毁。

独立流程：[直播伴侣拉起客户端](/zh/design/lifecycle-flows#主播客户端启动生命周期) · [一笔礼物消息](/zh/design/lifecycle-flows#一笔礼物消息的生命周期) · [一次点赞消息](/zh/design/lifecycle-flows#一次点赞消息的生命周期)。

[帧同步、断线追赶与全员离线策略](/zh/design/authoritative-sync) · [开箱即用能力与排行榜](/zh/design/developer-capabilities)
