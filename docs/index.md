---
layout: home

hero:
  name: Chorus
  text: Danmaku game framework
  tagline: Turns live-stream chat and gift events into structured gameplay input, driving server-authoritative matches with multi-room orchestration and failover
  actions:
    - theme: brand
      text: Get started
      link: /architecture
    - theme: alt
      text: GitHub
      link: https://github.com/cuihairu/chorus

features:
  - title: From danmaku to gameplay input
    details: Live-stream chat, gifts and likes are verified, normalized and deduplicated into bounded, ordered input for authoritative matches.
  - title: Multi-room match orchestration
    details: Cross-machine orchestration of matches across many live rooms, with assignment, capacity protection and lifecycle management.
  - title: Authoritative match simulation
    details: Server-authoritative tick simulation per match, with ordered state broadcast and consistency checks.
  - title: Failover
    details: A failed node is fenced off and its matches recover on another node, minimizing disruption.
---

## Process architecture and room management

Start with the [glossary: rooms, rounds, ticks and ownership epochs](/reference/glossary). For failure detection, fencing, replay, route barriers and client catch-up, read the [complete room recovery design](/design/room-recovery). Implementation gaps are stated separately.

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
