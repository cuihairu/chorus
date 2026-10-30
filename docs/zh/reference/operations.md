---
title: 运维与可观测性
---


# 运维与可观测性

相关文档：

- [可观测性](./observability.md)
- [参数调优](./tuning.md)
- [故障排查](./troubleshooting.md)
- [帧同步最佳实践](../design/frame-sync-best-practices.md)
- [客户端重同步状态机](../design/client-resync-state-machine.md)

## 指标端点

- `GET /sessions`：返回会话与客户端指标（tick、事件计数、延迟、ack 龄期、重同步次数）
- `GET /matches`：返回对局级聚合视图（目前包含对局下的会话列表、阵营列表、累计事件数、最近事件时间）

关键字段对应当前的 `SessionDiagnostics` / `SessionClientDiagnostics`：

- `sessionId`
- `tickId`
- `tickDurationMs`
- `serverTimeMs`
- `totalIngestedEvents`
- `totalAppliedEvents`
- `totalMergedEvents`
- `totalDroppedEvents`
- `totalSnapshotsSent`
- `totalResyncSnapshotsSent`
- `totalResyncRequests`（自 2026-10-13 起，客户端发起的 `resync` 拉取总数）
- `totalResyncReplays`（命中补充窗口并重放的次数）
- `totalResyncWindowMisses`（窗口起点已被逐出；返回最新帧 + `resync-gap`）
- `totalResyncThrottled`（在冷却期内被拒绝的次数）
- `frameHistoryBytes`（该会话帧历史当前占用的字节数）
- `lastTickProcessMs`
- `clients[].clientId`
- `clients[].connectedTickId`
- `clients[].lastAckTickId`
- `clients[].lagTicks`
- `clients[].lastAckAgeMs`
- `clients[].resyncCount`
- `clients[].lastResyncAgeMs`

排查时优先关注：

- `lastTickProcessMs` 是否逼近 `tickDurationMs`
- `clients[].lagTicks` 是否持续增长
- `clients[].lastAckAgeMs` 是否异常升高
- `totalMergedEvents` 在高峰期是否显著上升
- `totalDroppedEvents` 在高峰期是否快速增长

当需要确认"多个主播/多个会话是否真的落到了同一场对局"时，额外检查 `GET /matches`：

- `match.matchId`
- `match.sessionIds[]`
- `match.factions[].factionId`
- `match.factions[].sessionIds[]`
- `totalEvents`
- `lastEventTimeMs`

## 入口（平台事件入口）

- `POST /ingress/{platform}`：统一的平台事件入口（目前提供 `test` 示例映射器）
- 默认示例规则支持在 `Ingress:FactionRules` 中配置"弹幕关键词 → 阵营"与"礼物 ID → 阵营"映射；当事件未显式携带 `factionId` 时，按规则自动补全

## CI / 覆盖率

- GitHub Actions：`.github/workflows/ci.yml`
- 覆盖率：CI 生成 `coverage.cobertura.xml` 并上传至 Codecov（在 README 徽章中显示）

## 发布（Tag）

推送 `v*` 标签会触发自动创建 GitHub Release：

```bash
git tag v0.1.0
git push origin v0.1.0
```

## 相关文档

- [故障排查](./troubleshooting.md)
- [参数调优](./tuning.md)
- [协议参考](./protocol.md)
