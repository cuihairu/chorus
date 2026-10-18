---
home: true
title: Chorus
heroImage: /logo.svg
heroText: Chorus
tagline: An interactive battle game framework driven by live-stream danmaku/gifts (server-authoritative tick + state snapshots)
actions:
  - text: Getting Started
    link: /guide/getting-started.html
    type: primary
  - text: Architecture
    link: /design/architecture.html
    type: secondary
features:
  - title: Server-authoritative tick
    details: The server keeps advancing logic frames instead of "waiting for the slowest peer"; clients align to snapshots.
  - title: Danmaku/gift input buffering
    details: inputDelayTicks absorbs platform event jitter/reordering, with downsampling and merge strategies.
  - title: Observability
    details: Built-in session and client lag/ack metrics make "out of sync" issues explainable and reproducible.
footer: Apache-2.0 License
---

## Documentation index

- New to the project: start with [Getting Started](./guide/getting-started.md)
- Understand the architecture and the trade-offs (why it is designed this way): read [Architecture](./architecture.md)
- Use the three capability areas — battle / matchmaking / leaderboard: read [Battle](./guide/battle.md) /
  [Matchmaking](./guide/matchmaking.md) / [Leaderboard](./guide/leaderboard.md)
- Understand the current architecture (process topology + component index): read [Current architecture overview](./design/architecture-overview.md)
- Understand the overall design: read [Overall architecture](./design/architecture.md) and the [Spec draft](./design/spec.md)
- Care about frame sync: read [Frame sync design](./design/frame-sync.md)
- Dealing with "neither side can catch up during resync": read [Frame sync best practices](./design/frame-sync-best-practices.md)
- Designing client recovery: read [Client resync state machine](./design/client-resync-state-machine.md)
- Tuning production parameters: read [Parameter tuning](./reference/tuning.md)
- Production troubleshooting: read [Troubleshooting](./reference/troubleshooting.md)
- Checking message fields against the implementation: read [Protocol reference](./reference/protocol.md)
- Integrating live-stream platforms: read [Platform integration: Douyin](./reference/platform-douyin.md) /
  [Platform integration: Kuaishou](./reference/platform-kuaishou.md)
- Reviewing proxy decisions: read [Decision log](./decisions.md)
