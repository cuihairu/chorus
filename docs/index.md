---
layout: home

hero:
  name: Chorus
  text: Cross-machine, multi-room real-time collaboration server
  tagline: WebSocket-based room orchestration, state synchronization, and failover
  actions:
    - theme: brand
      text: Get started
      link: /architecture
    - theme: alt
      text: GitHub
      link: https://github.com/cuihairu/chorus

features:
  - title: Multi-room orchestration
    details: Cross-machine room assignment, capacity protection, and lifecycle management.
  - title: State synchronization
    details: Real-time broadcast of state changes within a room, with ordering and consistency checks.
  - title: Failover
    details: Automatically migrate rooms when a node becomes unreachable, minimizing disruption.
---

## Process architecture and room management

Seven process roles. Each GameServer manages multiple rooms, with a serial tick loop per RoomActor. This diagram shows the target redesign; gateway integration and production validation are ongoing.

Platform events are durably accepted and assigned to a room inbox using gameplay bindings and round boundaries. Each RoomActor reads its own inputs on each tick and acknowledges consumption in the frame commit.

![Seven Chorus process roles, with RoomRuntime and multiple RoomActors inside GameServer](/images/architecture-en.svg)

[Open full SVG](/images/architecture-en.svg) · [Architecture design and implementation scope (Chinese)](/architecture/architecture)
