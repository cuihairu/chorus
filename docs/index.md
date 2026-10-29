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

PlatformGateway resolves the room and its owner, then pushes events directly to the target GameServer. RoomRuntime delivers each event to the matching RoomActor. GameServer uses DataServer for persistence and recovery. This is the agreed target flow; migration from the existing pull implementation is tracked in TODO §22.9.

StreamerGateway connects directly to GameServer: battle inputs go upstream and committed frames/snapshots go downstream. StreamerServer handles login and personal business requests; it does not relay battle frames. ControlServer supplies StreamerGateway with room owner endpoints and epochs. These are target connections; current implementation differences remain documented.

StreamerServer registers live bindings and submits match intent to ControlServer; ControlServer coordinates participation and lifecycle updates back to StreamerServer. GameServer registers its process endpoint and reports status to ControlServer, which assigns and manages rooms.

StreamerServer uses DataServer for profile reads/writes and asset transactions. StreamerServer owns business decisions; DataServer performs the requested storage operations and enforces transaction/idempotency constraints.

![Seven Chorus process roles, with RoomRuntime and multiple RoomActors inside GameServer](/images/architecture-en.svg)

[Open full SVG](/images/architecture-en.svg) · [Architecture design and implementation scope (Chinese)](/architecture/architecture)

## Lifecycle flows

Follow the [streamer, player/audience and live-platform lifecycle flows](/design/lifecycle-flows), including reconnects, event acceptance, PlatformGateway delivery-close confirmation and room destruction.

Independent flows: [client launch](/design/lifecycle-flows#streamer-client-launch-lifecycle) · [one gift event](/design/lifecycle-flows#lifecycle-of-one-gift-event) · [a like event](/design/lifecycle-flows#lifecycle-of-a-like-event).

[Frame synchronization, reconnect and all-offline policy](/design/authoritative-sync) · [Built-in capabilities and leaderboards](/design/developer-capabilities)
