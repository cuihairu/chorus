---
home: true
title: Chorus
heroImage: /logo.svg
heroText: Chorus
tagline: 面向直播弹幕/礼物驱动的互动对战游戏框架（服务器权威 Tick + 状态快照）
actions:
  - text: 快速开始
    link: /guide/getting-started.html
    type: primary
  - text: 架构设计
    link: /design/architecture.html
    type: secondary
features:
  - title: 服务器权威 Tick
    details: 服务器持续推进逻辑帧，不再"等最慢端"，客户端以快照对齐。
  - title: 弹幕/礼物输入缓冲
    details: 通过 inputDelayTicks 抵抗平台事件抖动/乱序，并提供降采样与合并策略。
  - title: 可观测性
    details: 内置会话与客户端 lag/ack 指标，定位"不同步"问题可解释、可复现。
footer: Apache-2.0 License
---

<p align="center">
  <a href="README.md">English</a> | 简体中文
</p>

## 文档入口

- 新接入项目：从 [快速开始](./guide/getting-started.zh.md) 开始
- 先理解架构与取舍（为什么这么设计）：看 [架构说明](./architecture.zh.md)
- 用对战 / 匹配 / 排行榜三类能力：看 [对战](./guide/battle.zh.md) /
  [匹配](./guide/matchmaking.zh.md) / [排行榜](./guide/leaderboard.zh.md)
- 理解当前架构（进程拓扑 + 组件索引）：看 [当前架构总览](./design/architecture-overview.zh.md)
- 理解整体设计：看 [整体架构](./design/architecture.zh.md) 和 [规格草案](./design/spec.zh.md)
- 关注帧同步：看 [帧同步设计](./design/frame-sync.zh.md)
- 处理"追同步两边都追不上"：看 [帧同步最佳实践](./design/frame-sync-best-practices.zh.md)
- 设计客户端恢复：看 [客户端追同步状态机](./design/client-resync-state-machine.zh.md)
- 调整线上参数：看 [参数调优](./reference/tuning.zh.md)
- 线上排障定位：看 [故障排查](./reference/troubleshooting.zh.md)
- 对照消息字段实现：看 [协议参考](./reference/protocol.zh.md)
- 接入直播平台：看 [平台对接参考：抖音](./reference/platform-douyin.zh.md) /
  [平台对接参考：快手](./reference/platform-kuaishou.zh.md)
- 复核代理决策：看 [决策记录](./decisions.zh.md)
