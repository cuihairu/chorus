---
title: Operations and Observability
---

<p align="center">
  English | <a href="operations.zh.md">简体中文</a>
</p>

# Operations and Observability

Related docs:

- [Observability](./observability.md)
- [Parameter Tuning](./tuning.md)
- [Troubleshooting](./troubleshooting.md)
- [Frame Sync Best Practices](../design/frame-sync-best-practices.md)
- [Client Resync State Machine](../design/client-resync-state-machine.md)

## Metrics Endpoints

- `GET /sessions`: returns session and client metrics (tick, event counts, lag, ack age, resync count)
- `GET /matches`: returns a match-level aggregate view (currently includes the session list under a match, faction list, cumulative event count, last event time)

Key fields correspond to the current `SessionDiagnostics` / `SessionClientDiagnostics`:

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
- `totalResyncRequests` (since 2026-10-13, total of client-initiated `resync` pulls)
- `totalResyncReplays` (refill window hit and replayed)
- `totalResyncWindowMisses` (window start already evicted; latest frame returned + `resync-gap`)
- `totalResyncThrottled` (rejected within the cooldown)
- `frameHistoryBytes` (bytes currently used by this session's frame history)
- `lastTickProcessMs`
- `clients[].clientId`
- `clients[].connectedTickId`
- `clients[].lastAckTickId`
- `clients[].lagTicks`
- `clients[].lastAckAgeMs`
- `clients[].resyncCount`
- `clients[].lastResyncAgeMs`

When troubleshooting, prioritize:

- whether `lastTickProcessMs` is approaching `tickDurationMs`
- whether `clients[].lagTicks` keeps growing
- whether `clients[].lastAckAgeMs` rises abnormally
- whether `totalMergedEvents` rises significantly at peak
- whether `totalDroppedEvents` increases rapidly at peak

When you need to confirm "whether multiple streamers/multiple sessions actually landed in the same match", additionally check `GET /matches`:

- `match.matchId`
- `match.sessionIds[]`
- `match.factions[].factionId`
- `match.factions[].sessionIds[]`
- `totalEvents`
- `lastEventTimeMs`

## Ingress (Platform Event Entry)

- `POST /ingress/{platform}`: the unified platform event entry (currently provides the `test` sample mapper)
- The default sample rules support configuring "danmaku keyword → faction" and "gift ID → faction" mappings in `Ingress:FactionRules`; when an event does not explicitly carry a `factionId`, it is completed automatically per the rules

## CI / Coverage

- GitHub Actions: `.github/workflows/ci.yml`
- Coverage: CI generates `coverage.cobertura.xml` and uploads it to Codecov (shown in the README badge)

## Release (Tag)

Pushing a `v*` tag triggers automatic creation of a GitHub Release:

```bash
git tag v0.1.0
git push origin v0.1.0
```

## Related Docs

- [Troubleshooting](./troubleshooting.md)
- [Parameter Tuning](./tuning.md)
- [Protocol Reference](./protocol.md)
