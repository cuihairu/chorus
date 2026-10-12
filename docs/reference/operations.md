---
title: 运维与观测
---

# 运维与观测

相关文档：

- [可观测性](./observability.md)
- [参数调优](./tuning.md)
- [故障排查](./troubleshooting.md)
- [帧同步最佳实践](../design/frame-sync-best-practices.md)
- [客户端追同步状态机](../design/client-resync-state-machine.md)

## 指标端点

- `GET /sessions`：返回会话与客户端指标（tick、事件计数、lag、ack age、resync 次数）
- `GET /matches`：返回 match 级聚合视图（当前包含 match 下的 session 列表、阵营列表、累计事件数、最后事件时间）

关键字段对应当前 `SessionDiagnostics` / `SessionClientDiagnostics`：

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
- `lastTickProcessMs`
- `clients[].clientId`
- `clients[].connectedTickId`
- `clients[].lastAckTickId`
- `clients[].lagTicks`
- `clients[].lastAckAgeMs`
- `clients[].resyncCount`
- `clients[].lastResyncAgeMs`

排障时优先关注：

- `lastTickProcessMs` 是否逼近 `tickDurationMs`
- `clients[].lagTicks` 是否持续增长
- `clients[].lastAckAgeMs` 是否异常升高
- `totalMergedEvents` 是否在高峰期显著升高
- `totalDroppedEvents` 是否在高峰期快速增加

当需要确认“多个主播/多个 session 是否实际落在同一局”时，可额外检查 `GET /matches`：

- `match.matchId`
- `match.sessionIds[]`
- `match.factions[].factionId`
- `match.factions[].sessionIds[]`
- `totalEvents`
- `lastEventTimeMs`

## Ingress（平台事件入口）

- `POST /ingress/{platform}`：统一平台事件入口（当前提供 `test` 示例 mapper）
- 默认示例规则支持在 `Ingress:FactionRules` 中配置“弹幕口令 -> 阵营”和“礼物 ID -> 阵营”映射；当事件未显式带 `factionId` 时，会按规则自动补全

## CI / 覆盖率

- GitHub Actions：`.github/workflows/ci.yml`
- 覆盖率：CI 生成 `coverage.cobertura.xml` 并上传到 Codecov（README badge 展示）

## 发布（Tag）

推送 `v*` tag 会触发自动创建 GitHub Release：

```bash
git tag v0.1.0
git push origin v0.1.0
```

## 关联文档

- [故障排查](./troubleshooting.md)
- [参数调优](./tuning.md)
- [协议参考](./protocol.md)
